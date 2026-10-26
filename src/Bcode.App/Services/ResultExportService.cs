using System.Data;
using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Font = DocumentFormat.OpenXml.Spreadsheet.Font;

namespace Bcode.App.Services;

/// <summary>
/// Ghi 1 bảng kết quả SQL ra file — "Save as CSV…" / "Save as Excel (.xlsx)…" ở menu chuột phải tab Result (cả chế độ Web lẫn App, xem
/// Controls/ResultExport.cs). Chỉ đọc DataTable nên chạy được ở luồng nền.
///   • CSV: UTF-8 có BOM (Excel mở đúng tiếng Việt), dấu phẩy, ngoặc kép khi ô có , " xuống dòng hoặc khoảng trắng đầu/cuối; NULL = ô trống;
///     ngày dd/MM/yyyy (có giờ thì thêm HH:mm:ss) như lưới; số theo InvariantCulture (dấu chấm thập phân, không phân nhóm).
///   • XLSX: ghi kiểu SAX bằng DocumentFormat.OpenXml (đi kèm ClosedXML — không thêm package) nên hàng trăm nghìn dòng vẫn nhẹ RAM.
///     Dòng tiêu đề đậm + cố định + AutoFilter; số = ô số; ngày = ô ngày (dd/mm/yyyy, có giờ thì dd/mm/yyyy hh:mm:ss); chuỗi luôn là text
///     (mã 000123 / 0979… giữ số 0 đầu); bit = 1/0; số nguyên quá 15 chữ số (Excel làm tròn) = text.
/// </summary>
public static class ResultExportService
{
    /// <summary>Excel giới hạn 32.767 ký tự mỗi ô.</summary>
    private const int MaxXlsxCellChars = 32_767;

    // ------------------------------------------------------------------ CSV

    public static void WriteCsv(DataTable table, IReadOnlyList<DataRow>? rows, string path, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var list = rows ?? table.Rows.Cast<DataRow>().ToList();
        using var w = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16);
        var cols = table.Columns.Cast<DataColumn>().ToList();
        w.Write(string.Join(",", cols.Select(c => CsvQuote(c.ColumnName))));
        w.Write("\r\n");
        for (var r = 0; r < list.Count; r++)
        {
            var row = list[r];
            for (var c = 0; c < cols.Count; c++)
            {
                if (c > 0) w.Write(',');
                var text = CsvText(row[c]);
                if (text.Length > 0) w.Write(CsvQuote(text));
            }
            w.Write("\r\n");
            if ((r & 1023) == 1023) { ct.ThrowIfCancellationRequested(); progress?.Report(r + 1); }
        }
        progress?.Report(list.Count);
    }

    private static string CsvQuote(string s)
    {
        if (s.Length == 0) return s;
        var need = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]);
        return need ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string CsvText(object v) => v switch
    {
        DBNull or null => "",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : d.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("dd/MM/yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        double x => x.ToString("R", CultureInfo.InvariantCulture),
        float x => x.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    // ------------------------------------------------------------------ XLSX

    private const uint StyleHeader = 1, StyleDate = 2, StyleDateTime = 3;

    public static void WriteXlsx(DataTable table, IReadOnlyList<DataRow>? rows, string path, string sheetName, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var list = rows ?? table.Rows.Cast<DataRow>().ToList();
        var cols = table.Columns.Cast<DataColumn>().ToList();
        var letters = cols.Select((_, i) => ColumnLetter(i)).ToArray();
        var lastCol = cols.Count == 0 ? "A" : letters[^1];
        var lastRow = list.Count + 1;
        sheetName = SafeSheetName(sheetName);

        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        var stylesPart = wbPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStyles();
        stylesPart.Stylesheet.Save();
        var wsPart = wbPart.AddNewPart<WorksheetPart>();

        using (var w = OpenXmlWriter.Create(wsPart))
        {
            w.WriteStartElement(new Worksheet());
            w.WriteElement(new SheetViews(new SheetView(new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen },
                new Selection { Pane = PaneValues.BottomLeft, ActiveCell = "A2", SequenceOfReferences = new ListValue<StringValue> { InnerText = "A2" } })
                { TabSelected = true, WorkbookViewId = 0 }));
            if (cols.Count > 0)
            {
                var widths = new Columns();
                for (var c = 0; c < cols.Count; c++)
                    widths.Append(new Column { Min = (uint)(c + 1), Max = (uint)(c + 1), Width = GuessWidth(cols[c], list), CustomWidth = true });
                w.WriteElement(widths);
            }

            w.WriteStartElement(new SheetData());
            // Dòng tiêu đề
            w.WriteStartElement(new Row(), new[] { new OpenXmlAttribute("r", "", "1") });
            for (var c = 0; c < cols.Count; c++) WriteText(w, letters[c] + "1", cols[c].ColumnName, StyleHeader);
            w.WriteEndElement();

            for (var r = 0; r < list.Count; r++)
            {
                var rowNo = (r + 2).ToString(CultureInfo.InvariantCulture);
                w.WriteStartElement(new Row(), new[] { new OpenXmlAttribute("r", "", rowNo) });
                var row = list[r];
                for (var c = 0; c < cols.Count; c++) WriteValue(w, letters[c] + rowNo, row[c]);
                w.WriteEndElement();
                if ((r & 1023) == 1023) { ct.ThrowIfCancellationRequested(); progress?.Report(r + 1); }
            }
            w.WriteEndElement(); // sheetData

            if (cols.Count > 0) w.WriteElement(new AutoFilter { Reference = $"A1:{lastCol}{lastRow}" });
            w.WriteEndElement(); // worksheet
        }

        var wb = new Workbook();
        wb.Append(new Sheets(new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = sheetName }));
        if (cols.Count > 0)
            wb.Append(new DefinedNames(new DefinedName { Name = "_xlnm._FilterDatabase", LocalSheetId = 0, Hidden = true, Text = $"'{sheetName.Replace("'", "''")}'!$A$1:${lastCol}${lastRow}" }));
        wbPart.Workbook = wb;
        wbPart.Workbook.Save();
        progress?.Report(list.Count);
    }

    private static void WriteValue(OpenXmlWriter w, string cellRef, object v)
    {
        switch (v)
        {
            case DBNull or null: return;   // ô trống
            case bool b: WriteNumber(w, cellRef, b ? "1" : "0"); return;
            case byte or sbyte or short or ushort or int or uint:
                WriteNumber(w, cellRef, Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return;
            case long l:
                if (Math.Abs((decimal)l) > 999_999_999_999_999m) WriteText(w, cellRef, l.ToString(CultureInfo.InvariantCulture), 0);
                else WriteNumber(w, cellRef, l.ToString(CultureInfo.InvariantCulture));
                return;
            case ulong ul:
                if (ul > 999_999_999_999_999UL) WriteText(w, cellRef, ul.ToString(CultureInfo.InvariantCulture), 0);
                else WriteNumber(w, cellRef, ul.ToString(CultureInfo.InvariantCulture));
                return;
            case decimal m:
                if (m == decimal.Truncate(m) && Math.Abs(m) > 999_999_999_999_999m) WriteText(w, cellRef, m.ToString(CultureInfo.InvariantCulture), 0);
                else WriteNumber(w, cellRef, m.ToString(CultureInfo.InvariantCulture));
                return;
            case double d:
                if (double.IsFinite(d)) WriteNumber(w, cellRef, d.ToString("R", CultureInfo.InvariantCulture)); else WriteText(w, cellRef, d.ToString(CultureInfo.InvariantCulture), 0);
                return;
            case float f:
                if (float.IsFinite(f)) WriteNumber(w, cellRef, ((double)f).ToString("R", CultureInfo.InvariantCulture)); else WriteText(w, cellRef, f.ToString(CultureInfo.InvariantCulture), 0);
                return;
            case DateTime dt:
                // Excel không biểu diễn được ngày trước 1900 → để dạng chữ.
                if (dt.Year < 1900) { WriteText(w, cellRef, CsvText(dt), 0); return; }
                w.WriteStartElement(new Cell(), new[] { new OpenXmlAttribute("r", "", cellRef), new OpenXmlAttribute("s", "", (dt.TimeOfDay == TimeSpan.Zero ? StyleDate : StyleDateTime).ToString(CultureInfo.InvariantCulture)) });
                w.WriteElement(new CellValue(dt.ToOADate().ToString("R", CultureInfo.InvariantCulture)));
                w.WriteEndElement();
                return;
            case byte[] bytes:
                WriteText(w, cellRef, "0x" + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, (MaxXlsxCellChars - 2) / 2)), 0); return;
            default:
                WriteText(w, cellRef, CsvText(v), 0); return;
        }
    }

    private static void WriteNumber(OpenXmlWriter w, string cellRef, string invariant)
    {
        w.WriteStartElement(new Cell(), new[] { new OpenXmlAttribute("r", "", cellRef) });
        w.WriteElement(new CellValue(invariant));
        w.WriteEndElement();
    }

    /// <summary>Chuỗi ghi dạng inline string (t="inlineStr") — luôn là text nên mã 000123 không mất số 0 đầu.</summary>
    private static void WriteText(OpenXmlWriter w, string cellRef, string text, uint style)
    {
        text = CleanXml(text);
        if (text.Length > MaxXlsxCellChars) text = text[..MaxXlsxCellChars];
        var attrs = style == 0
            ? new[] { new OpenXmlAttribute("r", "", cellRef), new OpenXmlAttribute("t", "", "inlineStr") }
            : new[] { new OpenXmlAttribute("r", "", cellRef), new OpenXmlAttribute("s", "", style.ToString(CultureInfo.InvariantCulture)), new OpenXmlAttribute("t", "", "inlineStr") };
        w.WriteStartElement(new Cell(), attrs);
        var t = new Text(text);
        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))) t.Space = SpaceProcessingModeValues.Preserve;
        w.WriteElement(new InlineString(t));
        w.WriteEndElement();
    }

    /// <summary>Bỏ ký tự XML không cho phép (ký tự điều khiển, nửa cặp surrogate lẻ) — để nguyên thì ghi file lỗi / Excel báo hỏng file.</summary>
    private static string CleanXml(string s)
    {
        var ok = true;
        foreach (var ch in s) if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r' || char.IsSurrogate(ch) || ch is '￾' or '￿') { ok = false; break; }
        if (ok) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(ch).Append(s[++i]); continue; }
            if (char.IsSurrogate(ch) || ch is '￾' or '￿' || ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static double GuessWidth(DataColumn col, IReadOnlyList<DataRow> rows)
    {
        var t = col.DataType;
        var max = col.ColumnName.Length + 3;   // chừa chỗ nút AutoFilter
        if (t == typeof(DateTime))
        {
            var withTime = false;
            for (var r = 0; r < Math.Min(rows.Count, 500) && !withTime; r++) if (rows[r][col] is DateTime d && d.TimeOfDay != TimeSpan.Zero) withTime = true;
            max = Math.Max(max, withTime ? 20 : 11);
        }
        else
            for (var r = 0; r < Math.Min(rows.Count, 500); r++)
            {
                var v = rows[r][col];
                if (v is DBNull) continue;
                var len = CsvText(v).Length;
                if (len > max) max = len;
            }
        return Math.Min(Math.Max(max + 1, 6), 60);
    }

    private static string ColumnLetter(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    /// <summary>Tên sheet hợp lệ: bỏ : \ / ? * [ ], không bắt đầu/kết thúc bằng ', tối đa 31 ký tự.</summary>
    public static string SafeSheetName(string? name)
    {
        var s = new string((name ?? "").Where(ch => ch is not (':' or '\\' or '/' or '?' or '*' or '[' or ']') && !char.IsControl(ch)).ToArray()).Trim().Trim('\'');
        if (s.Length > 31) s = s[..31].TrimEnd();
        return s.Length == 0 ? "Result" : s;
    }

    private static Stylesheet BuildStyles()
    {
        CellFormat Fmt(uint numFmt, uint font, uint fill) => new()
        {
            NumberFormatId = numFmt, FontId = font, FillId = fill, BorderId = 0, FormatId = 0,
            ApplyNumberFormat = numFmt != 0 ? true : null, ApplyFont = font != 0 ? true : null, ApplyFill = fill != 0 ? true : null,
        };
        return new Stylesheet(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164, FormatCode = "dd/mm/yyyy" },
                new NumberingFormat { NumberFormatId = 165, FormatCode = "dd/mm/yyyy hh:mm:ss" }) { Count = 2 },
            new Fonts(
                new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" })) { Count = 2 },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFDDEBF7" }, new BackgroundColor { Indexed = 64 }) { PatternType = PatternValues.Solid })) { Count = 3 },
            new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 },
            new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
            new CellFormats(Fmt(0, 0, 0), Fmt(0, 1, 2), Fmt(164, 0, 0), Fmt(165, 0, 0)) { Count = 4 },
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });
    }
}
