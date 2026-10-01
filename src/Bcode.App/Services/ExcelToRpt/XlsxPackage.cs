using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>
/// Đọc thẳng XML của file .xlsx, KHÔNG qua ClosedXML — cố ý.
///
/// layout.json là hợp đồng với RptGenerator và phải khớp từng twip với bản Python
/// (excel2rpt_parser.py, dùng openpyxl). ClosedXML "chuẩn hoá" quá nhiều thứ mà openpyxl
/// để nguyên: ô không tồn tại thì ClosedXML trả style của dòng/cột (openpyxl trả style số 0),
/// ô gộp thì ClosedXML giữ viền của từng ô con (openpyxl thay bằng MergedCell chỉ mang viền
/// mép của ô đầu), số thì ClosedXML trả double (openpyxl phân biệt int/float), và
/// <c>&lt;col min=3 max=5&gt;</c> thì ClosedXML áp cho cả 3 cột (openpyxl chỉ gắn vào cột C,
/// D/E rơi về độ rộng mặc định). Mỗi chỗ lệch là một ô lệch toạ độ trong .rpt, nên ở đây
/// mô phỏng đúng hành vi của openpyxl 3.1 thay vì cố "sửa" nó.
/// </summary>
internal sealed class XlsxPackage
{
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    public XlsxSheet Sheet { get; private set; } = null!;
    /// <summary>XML thô của xl/theme/theme1.xml (openpyxl: wb.loaded_theme), hoặc null.</summary>
    public string? ThemeXml { get; private set; }

    public static XlsxPackage Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var pkg = new XlsxPackage();
        pkg.Read(zip);
        return pkg;
    }

    private static XDocument? ReadXml(ZipArchive zip, string part)
    {
        var entry = zip.GetEntry(part.TrimStart('/'));
        if (entry is null) return null;
        using var s = entry.Open();
        return XDocument.Load(s, LoadOptions.PreserveWhitespace);
    }

    private static string? ReadText(ZipArchive zip, string part)
    {
        var entry = zip.GetEntry(part);
        if (entry is null) return null;
        using var s = entry.Open();
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>Đích của một relationship, quy về đường dẫn trong zip.</summary>
    private static string ResolveTarget(string baseDir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in target.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != ".") parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static Dictionary<string, (string type, string target)> ReadRels(ZipArchive zip, string partPath)
    {
        var dir = partPath.Contains('/') ? partPath[..partPath.LastIndexOf('/')] : "";
        var name = partPath[(partPath.LastIndexOf('/') + 1)..];
        var relsPath = (dir.Length > 0 ? dir + "/" : "") + "_rels/" + name + ".rels";
        var result = new Dictionary<string, (string, string)>();
        var doc = ReadXml(zip, relsPath);
        if (doc?.Root is null) return result;
        foreach (var rel in doc.Root.Elements(PR + "Relationship"))
        {
            var id = (string?)rel.Attribute("Id");
            var target = (string?)rel.Attribute("Target");
            if (id is null || target is null) continue;
            if ((string?)rel.Attribute("TargetMode") == "External") continue;
            result[id] = ((string?)rel.Attribute("Type") ?? "", ResolveTarget(dir, target));
        }
        return result;
    }

    private void Read(ZipArchive zip)
    {
        var wbDoc = ReadXml(zip, "xl/workbook.xml") ?? throw new InvalidDataException("File .xlsx thiếu xl/workbook.xml");
        var wbRoot = wbDoc.Root!;
        var wbRels = ReadRels(zip, "xl/workbook.xml");

        // openpyxl: wb.worksheets[0] = trang tính ĐẦU TIÊN (bỏ qua chartsheet), tính cả sheet ẩn
        string? sheetPart = null;
        int sheetIndex = -1, idx = 0;
        foreach (var sh in wbRoot.Element(M + "sheets")?.Elements(M + "sheet") ?? Enumerable.Empty<XElement>())
        {
            var rid = (string?)sh.Attribute(R + "id");
            if (rid is not null && wbRels.TryGetValue(rid, out var rel) && rel.type.EndsWith("/worksheet"))
            {
                sheetPart = rel.target;
                sheetIndex = idx;
                break;
            }
            idx++;
        }
        if (sheetPart is null) throw new InvalidDataException("File .xlsx không có trang tính nào");

        var date1904 = XBool(wbRoot.Element(M + "workbookPr")?.Attribute("date1904"), false);

        string? printArea = null, printTitles = null;
        foreach (var dn in wbRoot.Element(M + "definedNames")?.Elements(M + "definedName") ?? Enumerable.Empty<XElement>())
        {
            if ((string?)dn.Attribute("localSheetId") != sheetIndex.ToString(CultureInfo.InvariantCulture)) continue;
            var name = (string?)dn.Attribute("name");
            if (name == "_xlnm.Print_Area") printArea = dn.Value;
            else if (name == "_xlnm.Print_Titles") printTitles = dn.Value;
        }

        ThemeXml = ReadText(zip, "xl/theme/theme1.xml");

        var styles = XlsxStyles.Read(ReadXml(zip, "xl/styles.xml"));
        var sst = ReadSharedStrings(ReadXml(zip, "xl/sharedStrings.xml"));

        var sheetDoc = ReadXml(zip, sheetPart) ?? throw new InvalidDataException("Không đọc được " + sheetPart);
        var sheetRels = ReadRels(zip, sheetPart);
        Sheet = XlsxSheet.Read(sheetDoc.Root!, styles, sst, date1904, sheetRels, p => ReadXml(zip, p));
        Sheet.PrintArea = printArea;
        Sheet.PrintTitles = printTitles;
    }

    private static List<string> ReadSharedStrings(XDocument? doc)
    {
        var list = new List<string>();
        if (doc?.Root is null) return list;
        foreach (var si in doc.Root.Elements(M + "si"))
            list.Add(TextContent(si).Replace("x005F_", ""));
        return list;
    }

    /// <summary>openpyxl Text.content: &lt;t&gt; trực tiếp + &lt;t&gt; của từng &lt;r&gt;, bỏ &lt;rPh&gt; (phiên âm).</summary>
    internal static string TextContent(XElement node)
    {
        var sb = new System.Text.StringBuilder();
        var plain = node.Element(M + "t");
        if (plain is not null) sb.Append(plain.Value);
        foreach (var r in node.Elements(M + "r"))
        {
            var t = r.Element(M + "t");
            if (t is not null) sb.Append(t.Value);
        }
        return sb.ToString();
    }

    /// <summary>Thuộc tính bool kiểu OOXML ("1"/"true"/"0"/"false").</summary>
    internal static bool XBool(XAttribute? a, bool whenMissing)
    {
        if (a is null) return whenMissing;
        var v = a.Value.Trim().ToLowerInvariant();
        return !(v is "0" or "false" or "f" or "n" or "no" or "off" or "");
    }

    internal static double? XDouble(XAttribute? a) =>
        a is not null && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    internal static int? XInt(XAttribute? a)
    {
        if (a is null) return null;
        if (int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        return double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : null;
    }

    internal static XNamespace Main => M;
    internal static XNamespace Rel => R;
}

/// <summary>Màu của openpyxl: đúng MỘT loại (indexed &gt; theme &gt; auto &gt; rgb) kèm tint.</summary>
internal sealed record XColor(string Type, string? Rgb, int Theme, int Indexed, double Tint)
{
    public static XColor? Read(XElement? el)
    {
        if (el is null) return null;
        var tint = XlsxPackage.XDouble(el.Attribute("tint")) ?? 0.0;
        if (XlsxPackage.XInt(el.Attribute("indexed")) is { } ix) return new XColor("indexed", null, 0, ix, tint);
        if (XlsxPackage.XInt(el.Attribute("theme")) is { } th) return new XColor("theme", null, th, 0, tint);
        if (el.Attribute("auto") is not null) return new XColor("auto", null, 0, 0, tint);
        return new XColor("rgb", (string?)el.Attribute("rgb") ?? "00000000", 0, 0, tint);
    }
}

internal sealed record XSide(string? Style, XColor? Color);

internal sealed class XFont
{
    public string? Name;
    public double? Size;
    public bool Bold, Italic;
    public string? Underline;
    public XColor? Color;
}

internal sealed class XFill
{
    public string? PatternType;
    public XColor? FgColor;
}

internal sealed class XBorder
{
    public XSide? Left, Right, Top, Bottom;

    public XSide? Get(string side) => side switch
    {
        "left" => Left, "right" => Right, "top" => Top, "bottom" => Bottom, _ => null,
    };

    public XBorder Clone() => new() { Left = Left, Right = Right, Top = Top, Bottom = Bottom };
}

internal sealed class XAlignment
{
    public static readonly XAlignment Default = new();
    public string? Horizontal, Vertical;
    public bool WrapText;
}

/// <summary>Style đã tra xong của một ô (openpyxl: cell.font / fill / border / alignment / number_format).</summary>
internal sealed class XCellStyle
{
    public XFont Font = new();
    public XFill Fill = new();
    public XBorder Border = new();
    public XAlignment Alignment = XAlignment.Default;
    public string NumberFormat = "General";
}

internal sealed class XlsxStyles
{
    private static readonly XNamespace M = XlsxPackage.Main;

    /// <summary>openpyxl BUILTIN_FORMATS.</summary>
    private static readonly Dictionary<int, string> Builtin = new()
    {
        [0] = "General", [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00",
        [5] = "\"$\"#,##0_);(\"$\"#,##0)", [6] = "\"$\"#,##0_);[Red](\"$\"#,##0)",
        [7] = "\"$\"#,##0.00_);(\"$\"#,##0.00)", [8] = "\"$\"#,##0.00_);[Red](\"$\"#,##0.00)",
        [9] = "0%", [10] = "0.00%", [11] = "0.00E+00", [12] = "# ?/?", [13] = "# ??/??",
        [14] = "mm-dd-yy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy",
        [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "m/d/yy h:mm",
        [37] = "#,##0_);(#,##0)", [38] = "#,##0_);[Red](#,##0)", [39] = "#,##0.00_);(#,##0.00)",
        [40] = "#,##0.00_);[Red](#,##0.00)", [41] = "_(* #,##0_);_(* \\(#,##0\\);_(* \"-\"_);_(@_)",
        [42] = "_(\"$\"* #,##0_);_(\"$\"* \\(#,##0\\);_(\"$\"* \"-\"_);_(@_)",
        [43] = "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)",
        [44] = "_(\"$\"* #,##0.00_)_(\"$\"* \\(#,##0.00\\)_(\"$\"* \"-\"??_)_(@_)",
        [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mmss.0", [48] = "##0.0E+0", [49] = "@",
    };

    private static readonly Regex StripRe = new("\".*?\"|\\[(?!hh?\\]|mm?\\]|ss?\\])[^\\]]*\\]");
    private static readonly Regex DateCharRe = new(@"(?<![_\\])[dmhysDMHYS]");
    private static readonly Regex TimedeltaRe = new(@"\[hh?\](:mm(:ss(\.0*)?)?)?|\[mm?\](:ss(\.0*)?)?|\[ss?\](\.0*)?", RegexOptions.IgnoreCase);

    private readonly List<XFont> _fonts = new();
    private readonly List<XFill> _fills = new();
    private readonly List<XBorder> _borders = new();
    private readonly List<XCellStyle> _xfs = new();
    private readonly List<bool> _isDate = new();
    private readonly List<bool> _isTimedelta = new();

    /// <summary>Style của ô mới tạo (openpyxl StyleArray() = toàn số 0), KHÁC cellXfs[0].</summary>
    public XCellStyle Blank { get; private set; } = new();

    public XCellStyle Xf(int id) => id >= 0 && id < _xfs.Count ? _xfs[id] : Blank;
    public bool IsDate(int id) => id >= 0 && id < _isDate.Count && _isDate[id];
    public bool IsTimedelta(int id) => id >= 0 && id < _isTimedelta.Count && _isTimedelta[id];

    public static bool IsDateFormat(string? fmt)
    {
        if (fmt is null) return false;
        fmt = fmt.Split(';')[0];
        fmt = StripRe.Replace(fmt, "");
        return DateCharRe.IsMatch(fmt);
    }

    private static bool IsTimedeltaFormat(string? fmt) =>
        fmt is not null && TimedeltaRe.IsMatch(fmt.Split(';')[0]);

    public static XlsxStyles Read(XDocument? doc)
    {
        var st = new XlsxStyles();
        var root = doc?.Root;
        var custom = new Dictionary<int, string>();
        if (root is not null)
        {
            foreach (var nf in root.Element(M + "numFmts")?.Elements(M + "numFmt") ?? Enumerable.Empty<XElement>())
                if (XlsxPackage.XInt(nf.Attribute("numFmtId")) is { } id)
                    custom[id] = (string?)nf.Attribute("formatCode") ?? "";

            foreach (var f in root.Element(M + "fonts")?.Elements(M + "font") ?? Enumerable.Empty<XElement>())
                st._fonts.Add(ReadFont(f));
            foreach (var f in root.Element(M + "fills")?.Elements(M + "fill") ?? Enumerable.Empty<XElement>())
                st._fills.Add(ReadFill(f));
            foreach (var b in root.Element(M + "borders")?.Elements(M + "border") ?? Enumerable.Empty<XElement>())
                st._borders.Add(ReadBorder(b));
        }

        XFont FontAt(int i) => i >= 0 && i < st._fonts.Count ? st._fonts[i] : new XFont();
        XFill FillAt(int i) => i >= 0 && i < st._fills.Count ? st._fills[i] : new XFill();
        XBorder BorderAt(int i) => i >= 0 && i < st._borders.Count ? st._borders[i] : new XBorder();

        st.Blank = new XCellStyle { Font = FontAt(0), Fill = FillAt(0), Border = BorderAt(0) };

        foreach (var xf in root?.Element(M + "cellXfs")?.Elements(M + "xf") ?? Enumerable.Empty<XElement>())
        {
            var numId = XlsxPackage.XInt(xf.Attribute("numFmtId")) ?? 0;
            // openpyxl _normalise_numbers + cell.number_format: định dạng tự khai thắng định
            // dạng dựng sẵn cùng id; id lạ không khai -> "General"
            string? code = custom.TryGetValue(numId, out var c) ? c : Builtin.GetValueOrDefault(numId);
            var al = xf.Element(M + "alignment");
            var style = new XCellStyle
            {
                Font = FontAt(XlsxPackage.XInt(xf.Attribute("fontId")) ?? 0),
                Fill = FillAt(XlsxPackage.XInt(xf.Attribute("fillId")) ?? 0),
                Border = BorderAt(XlsxPackage.XInt(xf.Attribute("borderId")) ?? 0),
                NumberFormat = code ?? "General",
                Alignment = al is null ? XAlignment.Default : new XAlignment
                {
                    Horizontal = NoneSet((string?)al.Attribute("horizontal")),
                    Vertical = NoneSet((string?)al.Attribute("vertical")),
                    WrapText = XlsxPackage.XBool(al.Attribute("wrapText"), false),
                },
            };
            st._xfs.Add(style);
            st._isDate.Add(IsDateFormat(code));
            st._isTimedelta.Add(IsTimedeltaFormat(code));
        }
        return st;
    }

    /// <summary>NoneSet của openpyxl: "none" (và rỗng) nghĩa là không khai.</summary>
    private static string? NoneSet(string? v) => string.IsNullOrEmpty(v) || v == "none" ? null : v;

    private static bool NestedBool(XElement? el) =>
        el is not null && XlsxPackage.XBool(el.Attribute("val"), true);

    private static XFont ReadFont(XElement f)
    {
        var u = f.Element(M + "u");
        return new XFont
        {
            Name = (string?)f.Element(M + "name")?.Attribute("val"),
            Size = XlsxPackage.XDouble(f.Element(M + "sz")?.Attribute("val")),
            Bold = NestedBool(f.Element(M + "b")),
            Italic = NestedBool(f.Element(M + "i")),
            // openpyxl Font.from_tree: <u/> không có val -> "single"
            Underline = u is null ? null : NoneSet((string?)u.Attribute("val") ?? "single"),
            Color = XColor.Read(f.Element(M + "color")),
        };
    }

    private static XFill ReadFill(XElement f)
    {
        var p = f.Element(M + "patternFill");
        if (p is null) return new XFill();          // gradientFill: không có patternType
        return new XFill
        {
            PatternType = NoneSet((string?)p.Attribute("patternType")),
            FgColor = XColor.Read(p.Element(M + "fgColor")),
        };
    }

    private static XBorder ReadBorder(XElement b)
    {
        XSide? Side(params string[] names)
        {
            foreach (var n in names)
            {
                var el = b.Element(M + n);
                if (el is not null)
                    return new XSide(NoneSet((string?)el.Attribute("style")), XColor.Read(el.Element(M + "color")));
            }
            return null;
        }
        return new XBorder
        {
            Left = Side("left", "start"),
            Right = Side("right", "end"),
            Top = Side("top"),
            Bottom = Side("bottom"),
        };
    }
}

internal sealed class XCell
{
    public int Row, Column;
    /// <summary>Giá trị kiểu Python (null, string, PyNumber, bool, PyText cho ngày giờ).</summary>
    public object? Value;
    /// <summary>Giá trị đã tính (openpyxl data_only=True) — chỉ khác Value ở ô công thức.</summary>
    public object? Cached;
    public XCellStyle Style = null!;
    public bool IsMerged;
}

/// <summary>Số đọc từ &lt;v&gt;: giữ nguyên kiểu int/float như openpyxl _cast_number.</summary>
internal sealed record PyNumber(string Text, bool IsInt, double Value)
{
    public override string ToString() => IsInt ? PyFormat.Int(Text) : PyFormat.Float(Value);
}

/// <summary>Chuỗi đã định dạng sẵn theo str() của Python (datetime, time, timedelta).</summary>
internal sealed record PyText(string Text)
{
    public override string ToString() => Text;
}

internal sealed record XMerge(int MinRow, int MinCol, int MaxRow, int MaxCol);

internal sealed class XlsxSheet
{
    private static readonly XNamespace M = XlsxPackage.Main;

    private readonly Dictionary<(int, int), XCell> _cells = new();
    private XlsxStyles _styles = null!;

    public int MaxRow { get; private set; } = 1;
    public int MaxColumn { get; private set; } = 1;
    public List<XMerge> Merges { get; } = new();
    /// <summary>openpyxl column_dimensions: khoá theo cột <c>min</c> của mỗi &lt;col&gt; (xem ghi chú lớp).</summary>
    public Dictionary<int, (double? width, bool hidden)> ColumnDims { get; } = new();
    public Dictionary<int, (double? height, bool hidden)> RowDims { get; } = new();
    public double? DefaultColWidth { get; private set; }
    public double DefaultRowHeight { get; private set; } = 15.0;
    public int? PaperSize { get; private set; }
    public string? Orientation { get; private set; }
    public int? Scale { get; private set; }
    // openpyxl PageMargins() mặc định khi sheet không khai <pageMargins>
    public double MarginLeft { get; private set; } = 0.75;
    public double MarginRight { get; private set; } = 0.75;
    public double MarginTop { get; private set; } = 1;
    public double MarginBottom { get; private set; } = 1;
    public List<(string name, string reference)> Tables { get; } = new();
    public string? PrintArea { get; set; }
    public string? PrintTitles { get; set; }

    /// <summary>openpyxl ws.cell(r, c): ô chưa có thì là ô trống mang style số 0.</summary>
    public XCell Cell(int row, int col)
    {
        if (_cells.TryGetValue((row, col), out var c)) return c;
        return new XCell { Row = row, Column = col, Style = _styles.Blank };
    }

    public bool HasCell(int row, int col) => _cells.ContainsKey((row, col));

    public static XlsxSheet Read(XElement root, XlsxStyles styles, List<string> sst, bool date1904,
        Dictionary<string, (string type, string target)> rels, Func<string, XDocument?> readXml)
    {
        var sh = new XlsxSheet { _styles = styles };

        var fmt = root.Element(M + "sheetFormatPr");
        if (fmt is not null)
        {
            sh.DefaultColWidth = XlsxPackage.XDouble(fmt.Attribute("defaultColWidth"));
            sh.DefaultRowHeight = XlsxPackage.XDouble(fmt.Attribute("defaultRowHeight")) ?? 15.0;
        }

        foreach (var col in root.Element(M + "cols")?.Elements(M + "col") ?? Enumerable.Empty<XElement>())
        {
            if (XlsxPackage.XInt(col.Attribute("min")) is not { } min) continue;
            sh.ColumnDims[min] = (XlsxPackage.XDouble(col.Attribute("width")),
                                  XlsxPackage.XBool(col.Attribute("hidden"), false));
        }

        var rowCounter = 0;
        foreach (var row in root.Element(M + "sheetData")?.Elements(M + "row") ?? Enumerable.Empty<XElement>())
        {
            rowCounter = XlsxPackage.XInt(row.Attribute("r")) ?? rowCounter + 1;
            // openpyxl chỉ tạo row_dimensions khi <row> có thêm thuộc tính ngoài r/spans
            if (row.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None
                                          && a.Name.LocalName is not ("r" or "spans")))
                sh.RowDims[rowCounter] = (XlsxPackage.XDouble(row.Attribute("ht")),
                                          XlsxPackage.XBool(row.Attribute("hidden"), false));

            var colCounter = 0;
            foreach (var c in row.Elements(M + "c"))
            {
                int r = rowCounter, cc;
                var coord = (string?)c.Attribute("r");
                if (coord is not null && TryParseCoord(coord, out var pr, out var pc)) { r = pr; cc = pc; }
                else cc = colCounter + 1;
                colCounter = cc;

                var styleId = XlsxPackage.XInt(c.Attribute("s")) ?? 0;
                var cell = new XCell { Row = r, Column = cc, Style = styles.Xf(styleId) };
                ReadValue(c, styleId, styles, sst, date1904, out cell.Value, out cell.Cached);
                sh._cells[(r, cc)] = cell;
            }
        }

        foreach (var mc in root.Element(M + "mergeCells")?.Elements(M + "mergeCell") ?? Enumerable.Empty<XElement>())
        {
            var reference = (string?)mc.Attribute("ref");
            if (reference is null) continue;
            var parts = reference.Split(':');
            if (!TryParseCoord(parts[0], out var r0, out var c0)) continue;
            int r1 = r0, c1 = c0;
            if (parts.Length > 1 && !TryParseCoord(parts[1], out r1, out c1)) continue;
            var m = new XMerge(Math.Min(r0, r1), Math.Min(c0, c1), Math.Max(r0, r1), Math.Max(c0, c1));
            sh.Merges.Add(m);
            sh.CleanMergeRange(m);
        }

        var ps = root.Element(M + "pageSetup");
        if (ps is not null)
        {
            sh.PaperSize = XlsxPackage.XInt(ps.Attribute("paperSize"));
            sh.Orientation = (string?)ps.Attribute("orientation");
            sh.Scale = XlsxPackage.XInt(ps.Attribute("scale"));
        }
        var pm = root.Element(M + "pageMargins");
        if (pm is not null)
        {
            sh.MarginLeft = XlsxPackage.XDouble(pm.Attribute("left")) ?? 0.75;
            sh.MarginRight = XlsxPackage.XDouble(pm.Attribute("right")) ?? 0.75;
            sh.MarginTop = XlsxPackage.XDouble(pm.Attribute("top")) ?? 1;
            sh.MarginBottom = XlsxPackage.XDouble(pm.Attribute("bottom")) ?? 1;
        }

        foreach (var tp in root.Element(M + "tableParts")?.Elements(M + "tablePart") ?? Enumerable.Empty<XElement>())
        {
            var rid = (string?)tp.Attribute(XlsxPackage.Rel + "id");
            if (rid is null || !rels.TryGetValue(rid, out var rel)) continue;
            var t = readXml(rel.target)?.Root;
            if (t is null) continue;
            sh.Tables.Add(((string?)t.Attribute("name") ?? (string?)t.Attribute("displayName") ?? "",
                           (string?)t.Attribute("ref") ?? ""));
        }

        if (sh._cells.Count > 0)
        {
            sh.MaxRow = sh._cells.Keys.Max(k => k.Item1);
            sh.MaxColumn = sh._cells.Keys.Max(k => k.Item2);
        }
        return sh;
    }

    /// <summary>
    /// openpyxl Worksheet._clean_merge_range + MergedCellRange.format(): mọi ô trừ ô đầu thành
    /// MergedCell (mất style riêng), rồi các ô nằm ở MÉP nhận cạnh viền tương ứng của ô đầu.
    /// </summary>
    private void CleanMergeRange(XMerge m)
    {
        // MergedCellRange._get_borders(): TRƯỚC khi dọn, ô đầu nhận cạnh phải/dưới của ô
        // cuối (góc dưới phải) nếu ô đó có trong file — Excel hay khai viền đáy ở ô dưới cùng
        // của vùng gộp chứ không ở ô đầu.
        var start0 = Cell(m.MinRow, m.MinCol);
        if (!HasCell(m.MinRow, m.MinCol)) _cells[(m.MinRow, m.MinCol)] = start0;
        if ((m.MaxRow != m.MinRow || m.MaxCol != m.MinCol) && _cells.TryGetValue((m.MaxRow, m.MaxCol), out var end))
        {
            var b = start0.Style.Border.Clone();
            b.Right = AddSide(b.Right, end.Style.Border.Right);
            b.Bottom = AddSide(b.Bottom, end.Style.Border.Bottom);
            start0.Style = WithBorder(start0.Style, b);
        }

        for (var r = m.MinRow; r <= m.MaxRow; r++)
            for (var c = m.MinCol; c <= m.MaxCol; c++)
                if (r != m.MinRow || c != m.MinCol)
                    _cells[(r, c)] = new XCell { Row = r, Column = c, Style = MergedStyle(), IsMerged = true };

        var start = Cell(m.MinRow, m.MinCol);
        if (!HasCell(m.MinRow, m.MinCol)) _cells[(m.MinRow, m.MinCol)] = start;

        foreach (var name in new[] { "top", "left", "right", "bottom" })
        {
            var side = start.Style.Border.Get(name);
            if (side is null || side.Style is null) continue;
            IEnumerable<(int, int)> edge = name switch
            {
                "top" => Enumerable.Range(m.MinCol, m.MaxCol - m.MinCol + 1).Select(c => (m.MinRow, c)),
                "bottom" => Enumerable.Range(m.MinCol, m.MaxCol - m.MinCol + 1).Select(c => (m.MaxRow, c)),
                "left" => Enumerable.Range(m.MinRow, m.MaxRow - m.MinRow + 1).Select(r => (r, m.MinCol)),
                _ => Enumerable.Range(m.MinRow, m.MaxRow - m.MinRow + 1).Select(r => (r, m.MaxCol)),
            };
            foreach (var (r, c) in edge)
            {
                if (r == m.MinRow && c == m.MinCol) continue;      // ô đầu cộng chính nó: không đổi
                var cell = _cells[(r, c)];
                var b = cell.Style.Border.Clone();
                var merged = AddSide(b.Get(name), side);
                switch (name)
                {
                    case "top": b.Top = merged; break;
                    case "bottom": b.Bottom = merged; break;
                    case "left": b.Left = merged; break;
                    default: b.Right = merged; break;
                }
                cell.Style = WithBorder(cell.Style, b);
            }
        }
    }

    /// <summary>Side.__add__ của openpyxl: thuộc tính nào bên trái chưa có thì lấy bên phải.</summary>
    private static XSide? AddSide(XSide? a, XSide? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new XSide(a.Style ?? b.Style, a.Color ?? b.Color);
    }

    private static XCellStyle WithBorder(XCellStyle s, XBorder b) => new()
    {
        Font = s.Font, Fill = s.Fill, Alignment = s.Alignment, NumberFormat = s.NumberFormat, Border = b,
    };

    private XCellStyle MergedStyle() => new()
    {
        Font = _styles.Blank.Font, Fill = _styles.Blank.Fill, Border = new XBorder(),
        Alignment = XAlignment.Default, NumberFormat = "General",
    };

    /// <summary>"AB12" -> (12, 28).</summary>
    public static bool TryParseCoord(string coord, out int row, out int col)
    {
        row = col = 0;
        var i = 0;
        coord = coord.Replace("$", "");
        while (i < coord.Length && char.IsAsciiLetter(coord[i]))
        {
            col = col * 26 + (char.ToUpperInvariant(coord[i]) - 'A' + 1);
            i++;
        }
        return i > 0 && int.TryParse(coord.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out row);
    }

    public static string ColumnLetter(int col)
    {
        var s = "";
        while (col > 0)
        {
            var rem = (col - 1) % 26;
            s = (char)('A' + rem) + s;
            col = (col - 1) / 26;
        }
        return s;
    }

    public static string Coordinate(int row, int col) => ColumnLetter(col) + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>openpyxl WorksheetReader.parse_cell, chạy cả hai chế độ (thường và data_only) một lượt.</summary>
    private static void ReadValue(XElement c, int styleId, XlsxStyles styles, List<string> sst, bool date1904,
        out object? value, out object? cached)
    {
        var t = (string?)c.Attribute("t") ?? "n";
        var vText = c.Element(M + "v")?.Value;
        if (string.IsNullOrEmpty(vText)) vText = null;

        object? Convert(string type)
        {
            if (type == "inlineStr")
            {
                var isEl = c.Element(M + "is");
                return isEl is null ? null : XlsxPackage.TextContent(isEl);
            }
            if (vText is null) return null;
            switch (type)
            {
                case "n":
                    var num = CastNumber(vText);
                    if (num is null) return vText;
                    if (styles.IsDate(styleId))
                        return PyFormat.FromExcel(num.Value, date1904, styles.IsTimedelta(styleId)) ?? (object)"#VALUE!";
                    return num;
                case "s":
                    return int.TryParse(vText, out var si) && si >= 0 && si < sst.Count ? sst[si] : null;
                case "b":
                    return vText.Trim() != "0";
                case "d":
                    return DateTime.TryParse(vText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                        ? new PyText(PyFormat.DateTime(d)) : vText;
                default:                                     // str, e
                    return vText;
            }
        }

        cached = Convert(t);
        var f = c.Element(M + "f");
        value = f is not null ? "=" + f.Value : cached;
    }

    private static PyNumber? CastNumber(string s)
    {
        s = s.Trim();
        if (s.Contains('.') || s.Contains('E') || s.Contains('e'))
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? new PyNumber(s, false, d) : null;
        return System.Numerics.BigInteger.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bi)
            ? new PyNumber(bi.ToString(CultureInfo.InvariantCulture), true, (double)bi) : null;
    }
}
