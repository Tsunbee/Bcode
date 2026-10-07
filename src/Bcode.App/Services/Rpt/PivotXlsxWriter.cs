using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace Bcode.App.Services.Rpt;

/// <summary>
/// Ghi file Excel báo cáo pivot của Fast: workbook 2 sheet — "Main" (ẩn: dòng 1 = tên field, dòng 2 = <c>!2.field</c>, là nguồn của PivotTable)
/// và "Main - Pivot" (bố cục in + 1 PivotTable thật). Cấu trúc làm theo ~140 file mẫu zrpt_*/rpt*/hrpt* của Fast. ClosedXML dựng sheet/ô/style,
/// phần PivotTable (pivotCacheDefinition + pivotTable) tự chèn vào gói sau đó vì ClosedXML không sinh được đúng kiểu Fast dùng
/// (cache 1 dòng chứa chuỗi <c>!2.xxx</c>, refreshOnLoad, không có records).
/// </summary>
public class PivotXlsxWriter
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";

    private readonly ExcelTemplateWriter _sheets;

    public PivotXlsxWriter(ExcelTemplateWriter sheets) => _sheets = sheets;

    public void Write(SheetLayout layout, string filePath)
    {
        var spec = layout.Pivot ?? throw new InvalidOperationException("Chưa có cấu hình pivot.");
        Validate(spec);

        var tmp = filePath + ".tmp.xlsx";
        try
        {
            using (var wb = new XLWorkbook())
            {
                var main = wb.Worksheets.Add("Main");
                for (int i = 0; i < spec.Fields.Count; i++)
                {
                    main.Cell(1, i + 1).SetValue(spec.Fields[i].Name);
                    main.Cell(2, i + 1).SetValue(spec.Fields[i].Expr);
                }
                main.Visibility = XLWorksheetVisibility.Hidden;
                foreach (var ex in layout.ExtraSheets.Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.Exprs.Count > 0))
                {
                    var xs = wb.Worksheets.Add(ex.Name);
                    for (int i = 0; i < ex.Exprs.Count; i++) xs.Cell(1, i + 1).SetValue(ex.Exprs[i]);
                    xs.Visibility = XLWorksheetVisibility.Hidden;
                }

                var pv = wb.Worksheets.Add("Main - Pivot");
                _sheets.WriteSheet(pv, layout);
                pv.ShowGridLines = false;
                pv.PageSetup.SetRowsToRepeatAtTop(1, spec.TopRow + ColFieldCount(spec));
                pv.SetTabActive();
                wb.SaveAs(tmp);
            }
            Inject(tmp, spec);
            File.Copy(tmp, filePath, overwrite: true);
        }
        finally { try { File.Delete(tmp); } catch { /* dọn file tạm */ } }
    }

    private static void Validate(PivotSpec s)
    {
        if (s.Fields.Count == 0) throw new InvalidOperationException("Pivot chưa có field nào.");
        if (!s.Fields.Any(f => f.Role is PivotRole.Row or PivotRole.RowKey)) throw new InvalidOperationException("Pivot cần ít nhất 1 field dòng (Row).");
        if (!s.Fields.Any(f => f.Role is PivotRole.ColKey or PivotRole.ColHeader)) throw new InvalidOperationException("Pivot cần ít nhất 1 field cột (ColKey/ColHeader).");
        if (!s.Fields.Any(f => f.Role == PivotRole.Data)) throw new InvalidOperationException("Pivot cần ít nhất 1 data field.");
        if (s.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != s.Fields.Count)
            throw new InvalidOperationException("Tên field trên sheet Main bị trùng nhau — đổi lại tên cột (dòng 1).");
        if (string.IsNullOrWhiteSpace(s.LocationRef)) throw new InvalidOperationException("Thiếu vùng PivotTable (location).");
    }

    private static int DataCount(PivotSpec s) => s.Fields.Count(f => f.Role == PivotRole.Data);
    private static int ColFieldCount(PivotSpec s) => s.Fields.Count(f => f.Role is PivotRole.ColKey or PivotRole.ColHeader) + (DataCount(s) > 1 ? 1 : 0);

    // ---------- chèn PivotTable vào gói đã có ----------

    private static void Inject(string path, PivotSpec spec)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);

        var wbDoc = Load(zip, "xl/workbook.xml");
        var wbRoot = wbDoc.Root!;
        var pivotSheet = wbRoot.Element(Main + "sheets")!.Elements(Main + "sheet").First(x => (string?)x.Attribute("name") == "Main - Pivot");
        var sheetRid = (string)pivotSheet.Attribute(Rel + "id")!;

        // workbook.xml: <pivotCaches> đặt sau calcPr (đúng thứ tự schema); cacheId trùng pivotTable.
        var caches = new XElement(Main + "pivotCaches", new XElement(Main + "pivotCache", new XAttribute("cacheId", "1"), new XAttribute(Rel + "id", "rIdPivotCache1")));
        var anchor = wbRoot.Element(Main + "calcPr") ?? wbRoot.Element(Main + "definedNames") ?? wbRoot.Element(Main + "sheets")!;
        anchor.AddAfterSelf(caches);
        Save(zip, "xl/workbook.xml", wbDoc);

        // workbook.xml.rels
        var relsDoc = Load(zip, "xl/_rels/workbook.xml.rels");
        relsDoc.Root!.Add(Relationship("rIdPivotCache1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/pivotCacheDefinition", "pivotCache/pivotCacheDefinition1.xml"));
        var sheetTarget = relsDoc.Root.Elements(PkgRel + "Relationship").First(r => (string?)r.Attribute("Id") == sheetRid).Attribute("Target")!.Value.TrimStart('/');
        if (!sheetTarget.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) sheetTarget = "xl/" + sheetTarget;
        Save(zip, "xl/_rels/workbook.xml.rels", relsDoc);

        // rels của sheet pivot: thêm quan hệ tới pivotTable (tạo file nếu chưa có)
        var sheetFile = Path.GetFileName(sheetTarget);
        var sheetRelsPath = $"xl/worksheets/_rels/{sheetFile}.rels";
        var sheetRels = zip.GetEntry(sheetRelsPath) is null ? new XDocument(new XElement(PkgRel + "Relationships")) : Load(zip, sheetRelsPath);
        sheetRels.Root!.Add(Relationship("rIdPivotTable1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/pivotTable", "../pivotTables/pivotTable1.xml"));
        Save(zip, sheetRelsPath, sheetRels);

        // [Content_Types].xml
        var ct = Load(zip, "[Content_Types].xml");
        ct.Root!.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/xl/pivotCache/pivotCacheDefinition1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.pivotCacheDefinition+xml")));
        ct.Root.Add(new XElement(Ct + "Override", new XAttribute("PartName", "/xl/pivotTables/pivotTable1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.pivotTable+xml")));
        Save(zip, "[Content_Types].xml", ct);

        var dxfBase = AddDxfs(zip);
        Save(zip, "xl/pivotCache/pivotCacheDefinition1.xml", BuildCacheDefinition(spec));
        Save(zip, "xl/pivotTables/pivotTable1.xml", BuildPivotTable(spec, dxfBase));
        var ptRels = new XDocument(new XElement(PkgRel + "Relationships",
            Relationship("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/pivotCacheDefinition", "../pivotCache/pivotCacheDefinition1.xml")));
        Save(zip, "xl/pivotTables/_rels/pivotTable1.xml.rels", ptRels);
    }

    /// <summary>Thêm 2 định dạng vùng (dxf) vào styles.xml: [base] viền mảnh 4 phía, [base+1] chữ đậm + nền xám + căn giữa. Trả về chỉ số dxf đầu tiên.</summary>
    private static int AddDxfs(ZipArchive zip)
    {
        var doc = Load(zip, "xl/styles.xml");
        var root = doc.Root!;
        var dxfs = root.Element(Main + "dxfs");
        if (dxfs is null)
        {
            dxfs = new XElement(Main + "dxfs", new XAttribute("count", "0"));
            (root.Element(Main + "cellStyles") ?? root.Element(Main + "cellXfs")!).AddAfterSelf(dxfs);
        }
        var baseId = dxfs.Elements(Main + "dxf").Count();
        XElement Side(string n) => new(Main + n, new XAttribute("style", "thin"), new XElement(Main + "color", new XAttribute("indexed", "64")));
        dxfs.Add(new XElement(Main + "dxf", new XElement(Main + "border", Side("left"), Side("right"), Side("top"), Side("bottom"))));
        dxfs.Add(new XElement(Main + "dxf",
            new XElement(Main + "font", new XElement(Main + "b")),
            new XElement(Main + "fill", new XElement(Main + "patternFill", new XAttribute("patternType", "solid"),
                new XElement(Main + "fgColor", new XAttribute("rgb", "FFEDF5FF")), new XElement(Main + "bgColor", new XAttribute("rgb", "FFEDF5FF")))),
            new XElement(Main + "alignment", new XAttribute("horizontal", "center"), new XAttribute("vertical", "center"), new XAttribute("wrapText", "1"))));
        dxfs.Add(new XElement(Main + "dxf", new XElement(Main + "numFmt", new XAttribute("numFmtId", "164"), new XAttribute("formatCode", "dd/mm/yyyy"))));   // short date
        dxfs.SetAttributeValue("count", baseId + 3);
        Save(zip, "xl/styles.xml", doc);
        return baseId;
    }

    private static bool OnAxis(PivotField f) => f.Role is PivotRole.Row or PivotRole.RowKey or PivotRole.ColKey or PivotRole.ColHeader;

    /// <summary>Nguồn của PivotTable = sheet Main, hàng 1–2. Field nằm trên trục có sẵn 1 mục (chuỗi <c>!2.xxx</c>); field dữ liệu để trống như mẫu.</summary>
    private static XDocument BuildCacheDefinition(PivotSpec spec)
    {
        var fields = spec.Fields.Select(f => new XElement(Main + "cacheField", new XAttribute("name", f.Name), new XAttribute("numFmtId", "0"),
            OnAxis(f)
                ? new XElement(Main + "sharedItems", new XAttribute("count", "1"), new XElement(Main + "s", new XAttribute("v", f.Expr)))
                : new XElement(Main + "sharedItems")));
        var lastCol = ColLetters(spec.Fields.Count);
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(Main + "pivotCacheDefinition",
            new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName),
            new XAttribute("saveData", "0"), new XAttribute("refreshOnLoad", "1"), new XAttribute("refreshedBy", "Bcode"),
            new XAttribute("createdVersion", "3"), new XAttribute("refreshedVersion", "4"), new XAttribute("minRefreshableVersion", "3"), new XAttribute("recordCount", "1"),
            new XElement(Main + "cacheSource", new XAttribute("type", "worksheet"),
                new XElement(Main + "worksheetSource", new XAttribute("ref", $"A1:{lastCol}2"), new XAttribute("sheet", "Main"))),
            new XElement(Main + "cacheFields", new XAttribute("count", spec.Fields.Count), fields)));
    }

    private static XDocument BuildPivotTable(PivotSpec spec, int dxfBase)
    {
        var f = spec.Fields;
        var rowIdx = Enumerable.Range(0, f.Count).Where(i => f[i].Role is PivotRole.Row or PivotRole.RowKey).ToList();
        var colIdx = Enumerable.Range(0, f.Count).Where(i => f[i].Role is PivotRole.ColKey or PivotRole.ColHeader).ToList();
        var dataIdx = Enumerable.Range(0, f.Count).Where(i => f[i].Role == PivotRole.Data).ToList();
        var multi = dataIdx.Count > 1;

        var blank = 1; // tên field "ẩn" của cột khoá: toàn dấu cách, mỗi field một độ dài khác nhau (tên field phải khác nhau)
        var pivotFields = f.Select(x =>
        {
            var e = new XElement(Main + "pivotField");
            if (x.Role == PivotRole.ColKey) e.Add(new XAttribute("name", string.IsNullOrEmpty(x.DataName) ? new string(' ', ++blank + 1) : x.DataName));
            if (x.Role is PivotRole.Row or PivotRole.RowKey) e.Add(new XAttribute("axis", "axisRow"));
            else if (x.Role is PivotRole.ColKey or PivotRole.ColHeader) e.Add(new XAttribute("axis", "axisCol"));
            else if (x.Role == PivotRole.Data) e.Add(new XAttribute("dataField", "1"));
            if (x.Role == PivotRole.ColKey) e.Add(new XAttribute("showDropDowns", "0"));
            e.Add(new XAttribute("compact", "0"), new XAttribute("outline", "0"), new XAttribute("showAll", "0"));
            if (x.Role == PivotRole.ColKey) e.Add(new XAttribute("sortType", "ascending"));
            e.Add(new XAttribute("defaultSubtotal", "0"));
            if (OnAxis(x)) e.Add(new XElement(Main + "items", new XAttribute("count", "1"), new XElement(Main + "item", new XAttribute("x", "0"))));
            return e;
        });

        XElement Fields(string name, IEnumerable<int> idx, bool values = false)
        {
            var list = idx.Select(i => new XElement(Main + "field", new XAttribute("x", i))).ToList();
            if (values) list.Add(new XElement(Main + "field", new XAttribute("x", "-2")));
            return new XElement(Main + name, new XAttribute("count", list.Count), list);
        }
        XElement Xs(int n) => new(Main + "i", Enumerable.Range(0, n).Select(_ => new XElement(Main + "x")));

        var nCf = colIdx.Count + (multi ? 1 : 0);
        var colItems = new XElement(Main + "colItems", new XAttribute("count", multi ? dataIdx.Count : 1), Xs(nCf));
        if (multi)
            for (int k = 1; k < dataIdx.Count; k++)
                colItems.Add(new XElement(Main + "i", new XAttribute("r", nCf - 1), new XAttribute("i", k), new XElement(Main + "x", new XAttribute("v", k))));

        var dataFields = dataIdx.Select(i => new XElement(Main + "dataField", new XAttribute("name", string.IsNullOrWhiteSpace(f[i].DataName) ? "Sum of " + f[i].Name : f[i].DataName),
            new XAttribute("fld", i), new XAttribute("baseField", "0"), new XAttribute("baseItem", "0")));

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(Main + "pivotTableDefinition",
            new XAttribute("name", "PivotTable1"), new XAttribute("cacheId", "1"),
            new XAttribute("applyNumberFormats", "0"), new XAttribute("applyBorderFormats", "0"), new XAttribute("applyFontFormats", "0"),
            new XAttribute("applyPatternFormats", "0"), new XAttribute("applyAlignmentFormats", "0"), new XAttribute("applyWidthHeightFormats", "1"),
            new XAttribute("dataCaption", spec.DataCaption ?? " "), new XAttribute("showMissing", "0"), new XAttribute("updatedVersion", "4"),
            new XAttribute("minRefreshableVersion", "3"), new XAttribute("showCalcMbrs", "0"), new XAttribute("showDrill", "0"), new XAttribute("enableDrill", "0"),
            new XAttribute("rowGrandTotals", "0"), new XAttribute("colGrandTotals", "0"), new XAttribute("itemPrintTitles", "1"),
            new XAttribute("createdVersion", "3"), new XAttribute("indent", "0"), new XAttribute("compact", "0"), new XAttribute("compactData", "0"),
            new XElement(Main + "location", new XAttribute("ref", spec.LocationRef), new XAttribute("firstHeaderRow", "1"),
                new XAttribute("firstDataRow", spec.FirstDataRow), new XAttribute("firstDataCol", spec.FirstDataCol)),
            new XElement(Main + "pivotFields", new XAttribute("count", f.Count), pivotFields),
            Fields("rowFields", rowIdx),
            new XElement(Main + "rowItems", new XAttribute("count", "1"), Xs(rowIdx.Count)),
            Fields("colFields", colIdx, multi),
            colItems,
            new XElement(Main + "dataFields", new XAttribute("count", dataIdx.Count), dataFields),
            BuildFormats(rowIdx, colIdx, dataIdx, f, dxfBase),
            new XElement(Main + "pivotTableStyleInfo", new XAttribute("showRowHeaders", "1"),
                new XAttribute("showColHeaders", "1"), new XAttribute("showRowStripes", "0"), new XAttribute("showColStripes", "0"), new XAttribute("showLastColumn", "1"))));
    }

    /// <summary>Định dạng vùng PivotTable (giữ nguyên khi Excel làm mới): viền mảnh toàn bảng; nút field, nhãn header cột và tên data field in đậm + nền xám.</summary>
    private static XElement BuildFormats(List<int> rowIdx, List<int> colIdx, List<int> dataIdx, List<PivotField> f, int dxfBase)
    {
        int border = dxfBase, head = dxfBase + 1, date = dxfBase + 2;
        var list = new List<XElement>();
        XElement Area(params object[] c) => new(Main + "pivotArea", c);
        void Add(int dxf, XElement area) => list.Add(new XElement(Main + "format", new XAttribute("dxfId", dxf), area));
        XAttribute A(string n, object v) => new(n, v);

        Add(border, Area(A("type", "all"), A("dataOnly", "0"), A("outline", "0"), A("fieldPosition", "0")));
        Add(head, Area(A("type", "origin"), A("dataOnly", "0"), A("labelOnly", "1"), A("outline", "0"), A("fieldPosition", "0")));
        for (int k = 0; k < rowIdx.Count; k++)
            Add(head, Area(A("field", rowIdx[k]), A("type", "button"), A("dataOnly", "0"), A("labelOnly", "1"), A("outline", "0"), A("axis", "axisRow"), A("fieldPosition", k)));
        for (int k = 0; k < colIdx.Count; k++)
            Add(head, Area(A("field", colIdx[k]), A("type", "button"), A("dataOnly", "0"), A("labelOnly", "1"), A("outline", "0"), A("axis", "axisCol"), A("fieldPosition", k)));
        if (dataIdx.Count > 1)
        {
            Add(head, Area(A("field", "-2"), A("type", "button"), A("dataOnly", "0"), A("labelOnly", "1"), A("outline", "0"), A("axis", "axisValues"), A("fieldPosition", "0")));
            var refs = new XElement(Main + "reference", A("field", "4294967294"), A("count", dataIdx.Count), dataIdx.Select((_, i) => new XElement(Main + "x", A("v", i))));
            Add(head, Area(A("dataOnly", "0"), A("labelOnly", "1"), A("outline", "0"), A("fieldPosition", "0"), new XElement(Main + "references", A("count", "1"), refs)));
        }
        else
        {
            foreach (var i in colIdx.Where(i => f[i].Role == PivotRole.ColHeader))
                Add(head, Area(A("dataOnly", "0"), A("labelOnly", "1"), A("outline", "0"), A("fieldPosition", "0"),
                    new XElement(Main + "references", A("count", "1"), new XElement(Main + "reference", A("field", i), A("count", "0")))));
        }
        // nhãn dòng của field kiểu ngày: giữ định dạng ngày ngắn khi Excel làm mới PivotTable
        foreach (var i in rowIdx.Where(i => f[i].IsDate))
            Add(date, Area(A("dataOnly", "0"), A("labelOnly", "1"), A("outline", "0"), A("fieldPosition", "0"),
                new XElement(Main + "references", A("count", "1"), new XElement(Main + "reference", A("field", i), A("count", "0")))));
        return new XElement(Main + "formats", new XAttribute("count", list.Count), list);
    }

    // ---------- tiện ích gói zip ----------

    private static XElement Relationship(string id, string type, string target) =>
        new(PkgRel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", target));

    private static XDocument Load(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open();
        return XDocument.Load(s);
    }

    private static void Save(ZipArchive zip, string name, XDocument doc)
    {
        zip.GetEntry(name)?.Delete();
        using var s = zip.CreateEntry(name).Open();
        using var w = new StreamWriter(s, new UTF8Encoding(false));
        doc.Save(w);
    }

    private static string ColLetters(int c)
    {
        var s = "";
        while (c > 0) { var m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - m - 1) / 26; }
        return s;
    }
}
