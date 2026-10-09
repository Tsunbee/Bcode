using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>1 cột của bảng danh mục cho "Tạo nhanh danh mục" — thêm các cờ Lookup/Filter/Import so với <see cref="CatalogColumn"/>.</summary>
public class QuickListColumn : CatalogColumn
{
    public bool InLookup { get; set; }
    public bool InFilter { get; set; }
    public bool InImport { get; set; } = true;
    /// <summary>Kiểu SQL thật ("varchar(32)", "numeric(18,2)"...) — dùng dựng bảng tạm khi import chi tiết.</summary>
    public string SqlType { get; set; } = "";
    /// <summary>Tra cứu AutoComplete (từ header.xml) — null = ô nhập thường.</summary>
    public FieldLookup? Lookup { get; set; }
    /// <summary>Field khai trong fields (vẫn load/lưu) nhưng hidden="true": không có dòng trên form Dir, cột ẩn trên lưới.</summary>
    public bool Hidden { get; set; }
    /// <summary>Có lookup nhưng field tên đi kèm (ten_xxx%l) để hidden="true" — không hiện ô/cột tên.</summary>
    public bool HideName { get; set; }
}

public class QuickListSpec
{
    /// <summary>Tên controller = tên file Dir/Grid/Lookup/Filter (không đuôi).</summary>
    public string Controller { get; set; } = "";
    /// <summary>Tên file Main (không đuôi) → Main\{MainName}.aspx.</summary>
    public string MainName { get; set; } = "";
    public string Table { get; set; } = "";
    /// <summary>Các cột khoá (khoá ghép được).</summary>
    public List<string> Keys { get; set; } = new();
    public string Order { get; set; } = "";
    /// <summary>Cột tên — dùng cho name="" của Lookup.</summary>
    public string NameField { get; set; } = "";
    public string TitleV { get; set; } = "";
    public string TitleE { get; set; } = "";
    public string SubTitleV { get; set; } = "";
    public string SubTitleE { get; set; } = "";

    public bool FuncNew { get; set; } = true;
    public bool FuncEdit { get; set; } = true;
    public bool FuncDelete { get; set; } = true;
    public bool FuncFilter { get; set; } = true;

    public bool CreateLookup { get; set; } = true;
    /// <summary>Có form lọc khi mở danh mục (Filter\{Controller}.xml + FilterMode="true" ở Main).</summary>
    public bool CreateFilter { get; set; }
    public bool ImportData { get; set; }
    /// <summary>true = bộ import mới (list_import2262 / list2262), false = bộ cũ (list_import / list).</summary>
    public bool Import2262 { get; set; } = true;
    /// <summary>Khối [#Comment#] / [#Code4Comment#] — PostExtender (bình luận).</summary>
    public bool Comment { get; set; }
    /// <summary>Khối [#Irregular#] — kiểm tra ký tự đặc biệt trong mã.</summary>
    public bool Irregular { get; set; }
    /// <summary>Mẫu tên biến giá trị khoá CŨ trong Updating, {0} = tên cột.</summary>
    public string OldKeyFormat { get; set; } = QuickListService.DefaultOldKeyFormat;

    public List<QuickListColumn> Columns { get; set; } = new();

    public string ImportForm => Controller + "Import";

    // ---- Chi tiết (tuỳ chọn) ----
    /// <summary>Bảng chi tiết; trống = danh mục không có chi tiết.</summary>
    public string DetailTable { get; set; } = "";
    /// <summary>Tên controller lưới chi tiết = tên field lưới trong Dir = tên file Grid\{DetailId}.xml.</summary>
    public string DetailId { get; set; } = "";
    public string DetailTitleV { get; set; } = "";
    public string DetailTitleE { get; set; } = "";
    /// <summary>Khoá của bảng chi tiết (thường = cột liên kết + line_nbr).</summary>
    public List<string> DetailKeys { get; set; } = new();
    /// <summary>Cột liên kết chi tiết → danh mục (cùng tên ở 2 bảng, là khoá của danh mục).</summary>
    public List<string> LinkKeys { get; set; } = new();
    public string DetailOrder { get; set; } = "";
    public bool DetailImport { get; set; }
    public List<QuickListColumn> DetailColumns { get; set; } = new();

    public bool HasDetail => !string.IsNullOrWhiteSpace(DetailTable);
    public string DetailImportForm => DetailId + "Import";
}

/// <summary>
/// "Tạo nhanh danh mục" — sinh bộ file danh mục từ bộ source mẫu FCode
/// <c>Templates\fileSource\CreateList</c>. Mẫu dùng 2 loại đánh dấu:
/// <list type="bullet">
/// <item><c>[#TEN#]</c> viết HOA = giá trị (bảng, khoá, field, view...) — thay bằng nội dung code dựng.</item>
/// <item><c>[#Ten#]…[#Ten#]</c> theo cặp = khối tuỳ chọn — bật thì giữ nội dung (bỏ dấu), tắt thì xoá cả khối.</item>
/// </list>
/// Chi tiết: Dir nhúng lưới qua field <c>&lt;items style="Grid" controller="{DetailId}"/&gt;</c>; dữ liệu lưới về SQL dưới dạng
/// biến bảng <c>@{DetailId}</c> (đúng như mẫu Dmdemo: <c>update @dmdemo_detail set stt_rec = @stt_rec</c>) — Inserting/Updating
/// gán cột liên kết, Inserted chép sang bảng chi tiết, Updated xoá theo khoá cũ rồi chép lại, Deleting xoá chi tiết.
/// </summary>
public class QuickListService
{
    public const string DefaultTemplateFolderName = @"Templates\fileSource\CreateList";
    public const string DefaultOldKeyFormat = "@{0}_old";

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly Regex AnyMarker = new(@"\[#([A-Za-z0-9_]+)#\]", RegexOptions.Compiled);

    private const string DirTemplate = @"App_Data\Controllers\Dir\list.xml";
    private const string DetailGridTemplate = @"App_Data\Controllers\Grid\list_detail.xml";
    private const string DetailImportTemplate = @"App_Data\Controllers\Filter\detail_import.xml";
    private const string DetailUploadTemplate = @"App_Data\Controllers\Templates\Upload\detail.xml";

    public static string DefaultTemplateDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultTemplateFolderName);

    /// <summary>(file mẫu tương đối trong thư mục mẫu, file đích tương đối trong thư mục source).</summary>
    public static IReadOnlyList<(string Template, string Target)> PlannedFiles(QuickListSpec s)
    {
        var c = s.Controller;
        var list = new List<(string, string)>
        {
            (DirTemplate, $@"App_Data\Controllers\Dir\{c}.xml"),
            (@"App_Data\Controllers\Grid\list.xml", $@"App_Data\Controllers\Grid\{c}.xml"),
        };
        if (s.HasDetail)
        {
            list.Add((DetailGridTemplate, $@"App_Data\Controllers\Grid\{s.DetailId}.xml"));
            if (s.DetailImport)
            {
                list.Add((DetailImportTemplate, $@"App_Data\Controllers\Filter\{s.DetailImportForm}.xml"));
                list.Add((DetailUploadTemplate, $@"App_Data\Controllers\Templates\Upload\{s.DetailId}.xml"));
            }
        }
        if (s.CreateLookup) list.Add((@"App_Data\Controllers\Lookup\list.xml", $@"App_Data\Controllers\Lookup\{c}.xml"));
        if (s.CreateFilter) list.Add((@"App_Data\Controllers\Filter\list.xml", $@"App_Data\Controllers\Filter\{c}.xml"));
        if (s.ImportData)
        {
            list.Add((s.Import2262 ? @"App_Data\Controllers\Filter\list_import2262.xml" : @"App_Data\Controllers\Filter\list_import.xml",
                $@"App_Data\Controllers\Filter\{s.ImportForm}.xml"));
            list.Add((s.Import2262 ? @"App_Data\Controllers\Templates\Upload\list2262.xml" : @"App_Data\Controllers\Templates\Upload\list.xml",
                $@"App_Data\Controllers\Templates\Upload\{c}.xml"));
        }
        list.Add((@"Main\list.aspx", $@"Main\{s.MainName}.aspx"));
        return list;
    }

    public static string? Validate(QuickListSpec s)
    {
        if (string.IsNullOrWhiteSpace(s.Controller)) return "Chưa nhập Controller (tên file).";
        if (s.Controller.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Controller chứa ký tự không hợp lệ cho tên file.";
        if (string.IsNullOrWhiteSpace(s.MainName)) return "Chưa nhập tên file Main.";
        if (s.MainName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Tên file Main chứa ký tự không hợp lệ.";
        if (string.IsNullOrWhiteSpace(s.Table)) return "Chưa chọn bảng.";
        if (s.Keys.Count == 0) return "Chưa chọn cột khoá.";
        var missing = Missing(s.Keys, s.Columns);
        if (missing != null) return $"Cột khoá '{missing}' không có trong bảng.";
        if (!s.Columns.Any(c => c.InGrid)) return "Chưa tick cột nào cho Grid.";
        if (s.Columns.Any(c => s.Keys.Contains(c.Name, StringComparer.OrdinalIgnoreCase) && !c.InForm)) return "Cột khoá phải được tick trong form Dir.";
        if (s.CreateLookup && string.IsNullOrWhiteSpace(s.NameField)) return "Tạo Lookup cần chọn cột tên (Name).";
        if (s.CreateFilter && !s.Columns.Any(c => c.InFilter)) return "Có form lọc nhưng chưa tick cột nào cho Filter.";
        if (s.ImportData && !s.Columns.Any(c => c.InImport)) return "Có import nhưng chưa tick cột nào cho Import.";
        if (!s.OldKeyFormat.Contains("{0}")) return "Mẫu biến khoá cũ phải chứa {0}.";

        if (!s.HasDetail) return null;
        if (string.IsNullOrWhiteSpace(s.DetailId)) return "Chưa nhập Controller của lưới chi tiết.";
        if (s.DetailId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Controller chi tiết chứa ký tự không hợp lệ cho tên file.";
        if (s.DetailId.Equals(s.Controller, StringComparison.OrdinalIgnoreCase)) return "Controller chi tiết phải khác controller danh mục (cùng thư mục Grid).";
        if (s.DetailTable.Equals(s.Table, StringComparison.OrdinalIgnoreCase)) return "Bảng chi tiết phải khác bảng danh mục.";
        if (s.DetailColumns.Count == 0) return "Chưa nạp cột của bảng chi tiết.";
        if (s.LinkKeys.Count == 0) return "Chưa chọn cột liên kết chi tiết → danh mục.";
        if ((missing = Missing(s.LinkKeys, s.DetailColumns)) != null) return $"Cột liên kết '{missing}' không có trong bảng chi tiết.";
        if ((missing = s.LinkKeys.FirstOrDefault(k => !s.Keys.Contains(k, StringComparer.OrdinalIgnoreCase))) != null)
            return $"Cột liên kết '{missing}' phải là khoá của danh mục.";
        if (s.DetailKeys.Count == 0) return "Chưa chọn khoá của bảng chi tiết.";
        if ((missing = Missing(s.DetailKeys, s.DetailColumns)) != null) return $"Khoá chi tiết '{missing}' không có trong bảng chi tiết.";
        if (!s.DetailColumns.Any(c => c.InGrid)) return "Chưa tick cột nào cho lưới chi tiết.";
        if (s.DetailImport && !s.DetailColumns.Any(c => c.InImport)) return "Có import chi tiết nhưng chưa tick cột nào cho Import.";
        return null;
    }

    private static string? Missing(IEnumerable<string> names, IEnumerable<CatalogColumn> cols)
    {
        var set = new HashSet<string>(cols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        return names.FirstOrDefault(n => !set.Contains(n));
    }

    /// <summary>Dựng nội dung toàn bộ file (chưa ghi). Leftovers = các [#...#] trong mẫu mà code không biết.</summary>
    public List<(string Target, string Content)> Render(QuickListSpec spec, string templateDir, out List<string> leftovers)
    {
        foreach (var c in spec.Columns) c.IsKey = spec.Keys.Contains(c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var c in spec.DetailColumns) c.IsKey = spec.DetailKeys.Contains(c.Name, StringComparer.OrdinalIgnoreCase);
        var toggles = BuildToggles(spec);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var result = new List<(string, string)>();

        foreach (var (template, target) in PlannedFiles(spec))
        {
            var templatePath = Path.Combine(templateDir, template);
            if (!File.Exists(templatePath)) throw new FileNotFoundException($"Thiếu file mẫu: {templatePath}");

            var text = FixTemplate(spec, template, File.ReadAllText(templatePath));
            foreach (var (name, on) in toggles) text = ApplyToggle(text, name, on);

            var values = IsDetailTemplate(template) ? BuildDetailValues(spec, template) : BuildValues(spec, template);
            text = AnyMarker.Replace(text, m =>
            {
                if (values.TryGetValue(m.Groups[1].Value, out var v)) return v;
                unknown.Add(m.Value);
                return "";
            });
            result.Add((target, CatalogCloneService.NormalizeNewLines(text)));
        }
        leftovers = unknown.ToList();
        return result;
    }

    public List<string> Write(IEnumerable<(string Target, string Content)> files, string sourceRoot)
    {
        var written = new List<string>();
        foreach (var (target, content) in files)
        {
            var path = Path.Combine(sourceRoot, target);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Utf8Bom);
            written.Add(path);
        }
        return written;
    }

    private static bool IsDetailTemplate(string template) =>
        template is DetailGridTemplate or DetailImportTemplate or DetailUploadTemplate;

    /// <summary>
    /// Sửa vài chỗ cứng trong mẫu chi tiết (chỉ khi còn đúng nguyên văn gốc — sửa mẫu rồi thì không đụng):
    /// list_detail mở form import tên cứng 'FilterImport'; detail_import lấy cột 'ma_vv'/'ma_kh' của mẫu gốc để dò dòng trống;
    /// Upload/detail insert vào #r thiếu 2 cột ref, stt.
    /// </summary>
    private static string FixTemplate(QuickListSpec s, string template, string text)
    {
        switch (template)
        {
            case DetailGridTemplate:
                return text.Replace("g.showForm('FilterImport')", $"g.showForm('{s.DetailImportForm}')");
            case DetailImportTemplate:
                var imp = s.DetailColumns.Where(c => c.InImport).Select(c => c.Name).ToList();
                var c0 = imp.FirstOrDefault() ?? "";
                var c1 = imp.Skip(1).FirstOrDefault() ?? c0;
                return text.Replace("g._getColumnOrder('ma_vv')", $"g._getColumnOrder('{c0}')")
                           .Replace("g._getColumnOrder('ma_kh')", $"g._getColumnOrder('{c1}')");
            case DetailUploadTemplate:
                return text.Replace("insert into #r([#DETAILFIELDS#])", "insert into #r([#DETAILFIELDS#], ref, stt)");
            default:
                return text;
        }
    }

    private static Dictionary<string, bool> BuildToggles(QuickListSpec s) => new()
    {
        ["Comment"] = s.Comment,
        ["Code4Comment"] = s.Comment,
        ["Irregular"] = s.Irregular,
        ["TabControl"] = false,
        ["FirstLoading"] = s.CreateFilter,
        ["ImportData"] = s.ImportData,
        ["ImportDataOld"] = s.ImportData && !s.Import2262,
        ["ImportData226"] = s.ImportData && s.Import2262,
        ["IMPORTDETAIL"] = s.HasDetail && s.DetailImport,
        ["FuncNew"] = s.FuncNew,
        ["FuncEdit"] = s.FuncEdit,
        ["FuncDelete"] = s.FuncDelete,
        ["FuncFilter"] = s.FuncFilter,
    };

    /// <summary>Khối [#Ten#]…[#Ten#] (ghép cặp lần lượt): bật = giữ nội dung, tắt = xoá cả khối.</summary>
    internal static string ApplyToggle(string text, string name, bool on)
    {
        var marker = Regex.Escape($"[#{name}#]");
        return Regex.Replace(text, marker + "(.*?)" + marker, m => on ? m.Groups[1].Value : "", RegexOptions.Singleline);
    }

    private static Dictionary<string, string> BuildValues(QuickListSpec s, string template)
    {
        var A = CatalogCloneService.Attr;
        var keys = s.Keys;
        var order = string.IsNullOrWhiteSpace(s.Order) ? string.Join(", ", keys) : s.Order;
        string Old(string k) => string.Format(s.OldKeyFormat, k);
        var formCols = s.Columns.Where(c => c.InForm).ToList();
        var gridCols = s.Columns.Where(c => c.InGrid).ToList();
        var importCols = s.Columns.Where(c => c.InImport).ToList();
        var folder = template.Split('\\').Reverse().Skip(1).FirstOrDefault() ?? "";
        var isImport = template.Contains("import", StringComparison.OrdinalIgnoreCase) || folder == "Upload";

        var v = new Dictionary<string, string>
        {
            ["CONTROLLER"] = A(isImport && !s.Import2262 ? s.ImportForm : s.Controller),
            ["CONTROLLERMAIN"] = A(s.Controller),
            ["TABLE"] = A(s.Table),
            ["KEY"] = A(string.Join(", ", keys)),
            ["ORDER"] = A(order),
            ["NAME"] = A(s.NameField),
            ["TITLE"] = A(s.TitleV),
            ["TITLE2"] = A(s.TitleE),
            ["SUBTITLE"] = A(s.SubTitleV),
            ["SUBTITLE2"] = A(s.SubTitleE),
            ["IMPORTFORM"] = A(s.ImportForm),

            // Dir — câu kiểm tra trùng khoá trong Inserting/Updating
            ["CURRENTKEY"] = string.Join(" and ", keys.Select(k => $"{k} = @{k}")),
            ["CURRENTKEYORG"] = string.Join(" and ", keys.Select(k => $"{k} = @{k}")),
            ["OLDKEY"] = string.Join(" and ", keys.Select(k => $"{k} = {Old(k)}")),
            ["OLDKEYDIFF"] = string.Join(" or ", keys.Select(k => $"@{k} <> {Old(k)}")),
            ["FOCUSKEY"] = keys[0],
            ["FOCUSTAB"] = $"'{keys[0]}'",

            // Import
            ["TXTKEY"] = keys[0],
            ["IMPORTKEY"] = string.Join(" and ", keys.Select(k => $"a.{k} = b.{k}")),
            ["IMPORTKEY_NOT"] = "a.stt <> b.stt",
            ["TEMPLATE"] = Join(importCols.Select((c, i) => UploadTemplateField(c, i))),

            // Filter
            ["FIELDS_FILTER"] = "",
            ["FIELDS_ADD"] = string.Join("\n\t\t\t", s.Columns.Where(c => c.InFilter).Select(c =>
                $"if (f.getItem('{c.Name}').value != '') k.push(['{c.Name}', f.getItem('{c.Name}').value]);")),
        };

        // Chi tiết trong Dir (trống nếu không có chi tiết).
        v["DETAILFIELDS"] = s.HasDetail ? DetailDirField(s) : "";
        v["UPDATEDETAILTMP"] = s.HasDetail ? $"update @{s.DetailId} set " + string.Join(", ", s.LinkKeys.Select(k => $"{k} = @{k}")) : "";
        v["INSERTDETAIL"] = s.HasDetail ? InsertDetailSql(s) : "";
        v["UPDATEDETAIL"] = s.HasDetail
            ? $"delete {s.DetailTable} where " + string.Join(" and ", s.LinkKeys.Select(k => $"{k} = {Old(k)}")) + "\n\t" + InsertDetailSql(s)
            : "";
        v["DELETEDETAIL"] = s.HasDetail ? $"delete {s.DetailTable} where " + string.Join(" and ", s.LinkKeys.Select(k => $"{k} = @{k}")) : "";
        v["GATHERDETAIL"] = s.HasDetail && s.DetailColumns.Any(c => c.Name.Equals("line_nbr", StringComparison.OrdinalIgnoreCase))
            ? $"var g = f.getItem('{s.DetailId}')._controlBehavior;\n\t\t\t\t\tg.setSequenceNumber('line_nbr');"
            : "";

        switch (folder)
        {
            case "Dir":
                var refs = References(s.Columns.Where(c => c.InForm), s.Columns);
                v["FIELDS"] = Join(s.Columns.Where(c => c.InForm).Select(DirField)
                    .Concat(refs.Values.Select(r => ReferenceField(r, null))));
                v["VIEW"] = DirView(formCols, s.HasDetail ? s.DetailId : null, refs);
                break;
            case "Grid":
                v["FIELDS"] = Join(gridCols.Select(c => c.Hidden ? Hide(CatalogCloneService.GridField(c)) : CatalogCloneService.GridField(c)));
                v["VIEW"] = Join(gridCols.Select(c => $"\t\t\t<field name=\"{A(c.Name)}\"/>"));
                break;
            case "Lookup":
                var lookupCols = s.Columns.Where(c => c.InLookup).ToList();
                if (lookupCols.Count == 0) lookupCols = s.Columns.Where(c => c.IsKey || c.Name.Equals(s.NameField, StringComparison.OrdinalIgnoreCase)).ToList();
                v["FIELDS"] = Join(lookupCols.Select(CatalogCloneService.LookupField));
                break;
            case "Filter":
                var filterCols = s.Columns.Where(c => c.InFilter)
                    .Select(c => new CatalogColumn { Name = c.Name, HeaderV = c.HeaderV, HeaderE = c.HeaderE, Type = c.Type, AllowNulls = true })
                    .ToList();
                v["FIELDS"] = Join(filterCols.Select(CatalogCloneService.DirField));
                v["VIEW"] = DirView(filterCols, null, new());
                break;
            case "Upload":
                v["FIELDS"] = Join(importCols.Select((c, i) => UploadField(c, i)));
                break;
            default:
                v["FIELDS"] = "";
                v["VIEW"] = "";
                break;
        }
        return v;
    }

    /// <summary>Giá trị cho 3 mẫu chi tiết: Grid\list_detail, Filter\detail_import, Templates\Upload\detail.</summary>
    private static Dictionary<string, string> BuildDetailValues(QuickListSpec s, string template)
    {
        var A = CatalogCloneService.Attr;
        var importCols = s.DetailColumns.Where(c => c.InImport).ToList();
        var order = string.IsNullOrWhiteSpace(s.DetailOrder)
            ? (s.DetailColumns.FirstOrDefault(c => c.Name.Equals("line_nbr", StringComparison.OrdinalIgnoreCase))?.Name ?? string.Join(", ", s.DetailKeys))
            : s.DetailOrder;

        var v = new Dictionary<string, string>
        {
            ["CONTROLLER"] = A(template == DetailGridTemplate ? s.Controller : s.DetailId),
            ["TABLE"] = A(s.DetailTable),
            ["TABLEDETAIL"] = A(s.DetailTable),
            ["KEY"] = A(string.Join(", ", s.DetailKeys)),
            ["ORDER"] = A(order),
            ["DETAILID"] = A(s.DetailId),
            ["DETAILTITLE"] = A(s.DetailTitleV),
            ["DETAILTITLE2"] = A(s.DetailTitleE),
            ["LOADING"] = DetailLoading(s),
            ["DETAILFIELDS"] = string.Join(", ", importCols.Select(c => c.Name)),
            ["DETAILFIELDSALIAS"] = string.Join(", ", importCols.Select(c => "a." + c.Name)),
            ["DETAILSTRUCT"] = string.Join(", ", importCols.Select(c => $"{c.Name} {(c.SqlType == "" ? "nvarchar(512)" : c.SqlType)}")),
            ["VIEW"] = Join(DetailGridLayout(s).Select(x => $"\t\t\t<field name=\"{A(x.Name)}\"/>")),
        };
        v["FIELDS"] = template switch
        {
            DetailGridTemplate => Join(DetailGridLayout(s).Select(x => x.Xml)),
            DetailUploadTemplate => Join(importCols.Select((c, i) => UploadField(c, i))),
            _ => "",
        };
        return v;
    }

    /// <summary>Cột của lưới chi tiết: các cột tick Grid + khoá/cột liên kết chưa tick (thêm vào dạng ẩn, để biến bảng @{DetailId} có đủ cột lưu).</summary>
    private static List<(QuickListColumn Col, bool Hidden)> DetailGridColumns(QuickListSpec s)
    {
        var must = new HashSet<string>(s.DetailKeys.Concat(s.LinkKeys), StringComparer.OrdinalIgnoreCase);
        var visible = s.DetailColumns.Where(c => c.InGrid && !s.LinkKeys.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
        var hidden = s.DetailColumns.Where(c => must.Contains(c.Name) && !visible.Contains(c));
        return visible.Select(c => (c, false)).Concat(hidden.Select(c => (c, true))).ToList();
    }

    private static string InsertDetailSql(QuickListSpec s)
    {
        var cols = string.Join(", ", DetailGridColumns(s).Select(c => c.Col.Name));
        return $"insert into {s.DetailTable}({cols}) select {cols} from @{s.DetailId}";
    }

    /// <summary>Field lưới chi tiết nhúng trong Dir (Dir đọc cột từ Grid\{DetailId}.xml).</summary>
    private static string DetailDirField(QuickListSpec s)
    {
        var A = CatalogCloneService.Attr;
        return $"<field name=\"{A(s.DetailId)}\" external=\"true\" allowContain=\"true\">\n" +
               $"\t\t\t<header v=\"{A(s.DetailTitleV)}\" e=\"{A(s.DetailTitleE)}\"></header>\n" +
               $"\t\t\t<items style=\"Grid\" controller=\"{A(s.DetailId)}\"/>\n" +
               "\t\t</field>";
    }

    private static string DetailGridField(QuickListColumn c, bool hidden)
    {
        var A = CatalogCloneService.Attr;
        var isInt = Regex.IsMatch(c.SqlType, @"^(int|bigint|smallint|tinyint)\b", RegexOptions.IgnoreCase);
        var attrs = new StringBuilder($"name=\"{A(c.Name)}\"");
        if (isInt && hidden) attrs.Append(" type=\"Int32\"");
        else if (c.Type != "") attrs.Append($" type=\"{c.Type}\"");
        if (c.IsKey) attrs.Append(" isPrimaryKey=\"true\"");
        if (!hidden && c.Type == "Decimal") attrs.Append(" dataFormatString=\"@quantityInputFormat\" clientDefault=\"0\"");
        if (!hidden && c.Type == "DateTime") attrs.Append(" dataFormatString=\"@datetimeFormat\"");
        if (!hidden && !c.AllowNulls) attrs.Append(" allowNulls=\"false\"");
        if (!hidden && c.ReadOnly) attrs.Append(" readOnly=\"true\"");
        attrs.Append(hidden ? " width=\"0\" hidden=\"true\"" : $" width=\"{c.Width}\"");
        attrs.Append(" aliasName=\"a\"");

        var sb = new StringBuilder($"\t\t<field {attrs}>\n");
        sb.Append(hidden ? "\t\t\t<header v=\"\" e=\"\"></header>\n" : $"\t\t\t<header v=\"{A(c.HeaderV)}\" e=\"{A(c.HeaderE)}\"></header>\n");
        if (!hidden && c.Lookup != null) sb.Append("\t\t\t" + Items(c.Lookup) + "\n");
        else if (!hidden && c.Type == "Decimal") sb.Append("\t\t\t<items style=\"Numeric\"/>\n");
        sb.Append("\t\t</field>");
        return sb.ToString();
    }

    // ---- Tra cứu (AutoComplete) ----------------------------------------------------------------

    /// <summary>
    /// Field tên đi kèm lookup (reference, vd <c>ten_bp%l</c>) của từng cột có lookup. Bỏ nếu trùng 1 cột thật của bảng
    /// (vd bảng đã có cột ten_bp) hoặc đã có cột trước dùng cùng reference (2 cột cùng tra Item → chỉ cột đầu có tên).
    /// </summary>
    private static Dictionary<string, (QuickListColumn Col, string Reference)> References(IEnumerable<QuickListColumn> cols, IEnumerable<QuickListColumn> all)
    {
        var real = new HashSet<string>(all.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, (QuickListColumn, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cols)
        {
            var r = c.Lookup?.Reference ?? "";
            if (r.Length == 0 || real.Contains(r) || real.Contains(r.Replace("%l", "")) || !used.Add(r)) continue;
            map[c.Name] = (c, r);
        }
        return map;
    }

    private static string Items(FieldLookup l)
    {
        var A = CatalogCloneService.Attr;
        var sb = new StringBuilder($"<items style=\"AutoComplete\" controller=\"{A(l.Controller)}\"");
        if (l.Reference != "") sb.Append($" reference=\"{A(l.Reference)}\"");
        if (l.Key != "") sb.Append($" key=\"{A(l.Key)}\"");
        if (l.Check != "") sb.Append($" check=\"{A(l.Check)}\"");
        if (l.Information != "") sb.Append($" information=\"{A(l.Information)}\"");
        return sb.Append("/>").ToString();
    }

    /// <summary>Field Dir: như Clone danh mục, cột có lookup thì thay &lt;items&gt; bằng AutoComplete.</summary>
    private static string DirField(QuickListColumn c)
    {
        var xml = CatalogCloneService.DirField(c);
        if (c.Hidden) xml = Hide(xml);
        if (c.Lookup == null) return xml;
        xml = Regex.Replace(xml, @"\t\t\t<items [^>]*/>\n", "");
        return xml.Insert(xml.LastIndexOf("\t\t</field>", StringComparison.Ordinal), "\t\t\t" + Items(c.Lookup) + "\n");
    }

    /// <summary>Thêm hidden="true" ngay sau name="..." của thẻ field (đã có hidden thì để nguyên).</summary>
    private static string Hide(string fieldXml) =>
        fieldXml.Contains(" hidden=\"", StringComparison.Ordinal) ? fieldXml
            : Regex.Replace(fieldXml, "^(\\s*<field name=\"[^\"]*\")", "$1 hidden=\"true\"");

    /// <summary>Field tên (external, chỉ đọc) đi cạnh ô mã. Trong lưới chi tiết có alias join + bề rộng + header.</summary>
    private static string ReferenceField((QuickListColumn Col, string Reference) r, string? alias)
    {
        var A = CatalogCloneService.Attr;
        var head = alias == null ? ("", "") : NameHeader(r.Reference);
        var extra = alias == null ? "" : $" width=\"250\" aliasName=\"{alias}\"";
        if (r.Col.HideName) extra = " hidden=\"true\"" + extra;
        return $"\t\t<field name=\"{A(r.Reference)}\" readOnly=\"true\" external=\"true\" defaultValue=\"''\" inactivate=\"true\"{extra}>\n" +
               $"\t\t\t<header v=\"{A(head.Item1)}\" e=\"{A(head.Item2)}\"></header>\n\t\t</field>";
    }

    private static (string, string) NameHeader(string reference) =>
        FieldDictionaryService.Instance.Header(reference.Replace("%l", "")) ?? ("Tên", "Name");

    /// <summary>Thứ tự field lưới chi tiết (fields và view dùng chung): mỗi cột, ngay sau cột có lookup là field tên của nó.</summary>
    private static List<(string Name, string Xml)> DetailGridLayout(QuickListSpec s)
    {
        var cols = DetailGridColumns(s);
        var refs = DetailReferences(s);
        var list = new List<(string, string)>();
        foreach (var (col, hidden) in cols)
        {
            list.Add((col.Name, DetailGridField(col, hidden || col.Hidden)));
            if (!hidden && !col.Hidden && refs.TryGetValue(col.Name, out var r)) list.Add((r.Reference, ReferenceField((col, r.Reference), r.Alias)));
        }
        return list;
    }

    /// <summary>Field tên trong lưới chi tiết chỉ dùng được khi biết bảng nguồn (từ information) để join — alias b1, b2...</summary>
    private static Dictionary<string, (string Reference, string Alias, string Table, string KeyColumn)> DetailReferences(QuickListSpec s)
    {
        var visible = DetailGridColumns(s).Where(x => !x.Hidden && !x.Col.Hidden).Select(x => x.Col);
        var result = new Dictionary<string, (string, string, string, string)>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var (name, (col, reference)) in References(visible, s.DetailColumns))
        {
            var (table, keyColumn) = col.Lookup!.Source;
            if (table == "" || keyColumn == "") continue;
            result[name] = (reference, $"b{++i}", table, keyColumn);
        }
        return result;
    }

    /// <summary>
    /// Query Loading của lưới chi tiết: join bảng tra cứu để lấy field tên. Bảng tra cứu bọc trong subquery, đổi tên cột mã
    /// (giống mẫu Dmdemo: <c>(select ma_vt as ma_sp, ten_vt, ten_vt2 from dmvt) b</c>) để @@whereClause không bị trùng tên cột.
    /// </summary>
    private static string DetailLoading(QuickListSpec s)
    {
        var joins = DetailReferences(s).Select(kv =>
        {
            var (reference, alias, table, keyColumn) = kv.Value;
            var name = reference.Replace("%l", "");
            var select = reference.Contains("%l") ? $"{name}, {name}2" : name;
            return $" left join (select {keyColumn} as {alias}_key, {select} from {table}) {alias} on a.{kv.Key} = {alias}.{alias}_key";
        });
        return "select @@fieldExternal from @@table a" + string.Concat(joins) + " where @@whereClause order by @@orderByClause";
    }

    /// <summary>
    /// View Dir: dòng bề rộng + mỗi field 1 dòng (cột có lookup: nhãn, mã, tên — tên ẩn thì chỉ nhãn, mã); field Hidden không có dòng;
    /// có chi tiết thì thêm 1 dòng lưới trải hết 13 cột.
    /// </summary>
    private static string DirView(IEnumerable<CatalogColumn> cols, string? detailId, Dictionary<string, (QuickListColumn Col, string Reference)> refs)
    {
        var A = CatalogCloneService.Attr;
        var sb = new StringBuilder($"<view id=\"Dir\">\n\t\t\t<item value=\"{CatalogCloneService.DirColumnWidths}\"/>\n");
        cols = cols.Where(c => c is not QuickListColumn { Hidden: true });
        sb.Append(string.Join("\n", cols.Select(c => refs.TryGetValue(c.Name, out var r) && !r.Col.HideName
            ? $"\t\t\t<item value=\"1100100000000: [{A(c.Name)}].Label, [{A(c.Name)}], [{A(r.Reference)}]\"/>"
            : CatalogCloneService.DirItem(c))));
        if (detailId != null) sb.Append($"\n\t\t\t<item value=\"1000000000000: [{CatalogCloneService.Attr(detailId)}]\"/>");
        sb.Append("\n\t\t</view>");
        return sb.ToString();
    }

    private static string Join(IEnumerable<string> parts) => string.Join("\n", parts).TrimStart();

    private static string ExcelColumn(int index)
    {
        var s = "";
        for (index++; index > 0; index = (index - 1) / 26) s = (char)('A' + (index - 1) % 26) + s;
        return s;
    }

    private static string UploadField(CatalogColumn c, int i)
    {
        var A = CatalogCloneService.Attr;
        var sb = new StringBuilder($"\t\t<field name=\"{A(c.Name)}\" column=\"{ExcelColumn(i)}\"");
        if (c.Type != "") sb.Append($" type=\"{c.Type}\"");
        if (c.IsKey || !c.AllowNulls) sb.Append(" allowNulls=\"false\"");
        sb.Append("/>");
        return sb.ToString();
    }

    private static string UploadTemplateField(CatalogColumn c, int i)
    {
        var A = CatalogCloneService.Attr;
        return $"\t\t\t<field name=\"{A(c.Name)}\" column=\"{ExcelColumn(i)}\">\n\t\t\t\t<header v=\"{A(c.HeaderV)}\" e=\"{A(c.HeaderE)}\"/>\n\t\t\t</field>";
    }
}
