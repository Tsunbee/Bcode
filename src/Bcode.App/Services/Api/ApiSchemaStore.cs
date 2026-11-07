using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bcode.App.Services.Api;

/// <summary>
/// Dữ liệu của "Tạo cấu trúc API" (ApiSchemaBuilderForm) lưu theo dự án tại %AppData%\Bcode\ApiSchemas\&lt;workspace&gt;.json — đóng form mở lại
/// vẫn còn các API đã tạo. Tên thuộc tính JSON camelCase = đúng model của trang apischema.html (bản Web gửi / nhận nguyên khối).
/// </summary>
public sealed class ApiSchemaState
{
    /// <summary>Xuất Word không có bảng / cột database nguồn (gửi đối tác): mỗi trường mô tả bằng tên tiếng Việt.</summary>
    public bool HideSource { get; set; }
    public int Active { get; set; }
    public List<ApiSchemaApi> Apis { get; set; } = new();
}

public sealed class ApiSchemaApi
{
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    /// <summary>Tên hàm API — tự đặt "Set" + controller Dir của bảng đầu tiên (<see cref="FuncAuto"/>); sửa tay thì không tự đổi nữa.</summary>
    public string Func { get; set; } = "";
    public bool FuncAuto { get; set; } = true;
    public string View { get; set; } = "";
    public List<ApiSchemaTable> Tables { get; set; } = new();
    public List<ApiSchemaColumn> Columns { get; set; } = new();
}

public sealed class ApiSchemaTable
{
    public string Db { get; set; } = "0";
    public string Table { get; set; } = "";
    /// <summary>Controller của bảng = sysid của menu wcommand dùng bảng (không thấy menu thì controller Dir trong source; "" = chưa tìm / không có).</summary>
    public string Controller { get; set; } = "";
    /// <summary>Menu ứng với <see cref="Controller"/> ("wmenu_id tên menu"; "" = controller lấy từ Dir, không qua menu).</summary>
    public string Menu { get; set; } = "";
}

public sealed class ApiSchemaColumn
{
    public bool Selected { get; set; } = true;
    public string ColName { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool IsPk { get; set; }
    public bool Nullable { get; set; } = true;
    public bool Identity { get; set; }
    public bool Required { get; set; }
    public string FieldDesc { get; set; } = "";
    public string LogicDesc { get; set; } = "";
    /// <summary>Tên tiếng Việt của trường — lấy từ tiêu đề field ở controller Dir của bảng (hoặc từ điển field FCode); sửa tay được.</summary>
    public string ViName { get; set; } = "";
    /// <summary>true = người dùng đã sửa tay tên tiếng Việt → không tự điền đè.</summary>
    public bool ViUser { get; set; }
    public string Tkey { get; set; } = "";
    public string Tname { get; set; } = "";
    public string Tdb { get; set; } = "";
    public string? PrevField { get; set; }
    /// <summary>Cột thêm tay ("＋ Thêm cột") — không có trong bảng database: Cột DB / Kiểu sửa được, Bảng nguồn = "(thêm tay)", xoá được.</summary>
    public bool Manual { get; set; }
}

public static class ApiSchemaStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // prevField vắng mặt (không phải null) thì trang mới coi là "chưa đổi camelCase".
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FilePath(string workspace)
    {
        var safe = string.IsNullOrWhiteSpace(workspace) ? "_no_workspace" : workspace.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        return Path.Combine(BcodePaths.AppData, "Bcode", "ApiSchemas", safe + ".json");
    }

    /// <summary>null = chưa lưu lần nào (hoặc file hỏng).</summary>
    public static ApiSchemaState? Load(string workspace)
    {
        try
        {
            var p = FilePath(workspace);
            if (!File.Exists(p)) return null;
            var s = JsonSerializer.Deserialize<ApiSchemaState>(File.ReadAllText(p), Json);
            return s is { Apis.Count: > 0 } ? s : null;
        }
        catch { return null; }
    }

    public static void Save(string workspace, ApiSchemaState state)
    {
        var p = FilePath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var tmp = p + ".tmp";   // ghi file tạm rồi thay: tắt ngang không hỏng bản đang có
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>"Set" + controller (bỏ ký tự không hợp lệ cho tên hàm).</summary>
    public static string FuncName(string controllerOrTable)
    {
        var n = controllerOrTable ?? "";
        if (n.LastIndexOf('.') is var dot and >= 0) n = n[(dot + 1)..];
        n = new string(n.Replace("[", "").Replace("]", "").Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        return n.Length == 0 ? "" : "Set" + n;
    }

    /// <summary>Menu (wcommand) dùng bảng: <paramref name="SysId"/> = controller của menu (cột sysid), <paramref name="Label"/> = "wmenu_id tên menu",
    /// <paramref name="Others"/> = sysid các menu khác cũng dùng bảng.</summary>
    public sealed record MenuHit(string SysId, string Label, string[] Others);

    private static readonly System.Text.RegularExpressions.Regex VoucherTable = new(@"^([a-z])(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Menu của bảng theo <see cref="TableMenuService"/> (dmct9 c$/m$/d$/i$ → trang của menu, rồi Dir master / Grid chi tiết của từng sysid trên cây).
    /// Bảng chứng từ không khai trực tiếp (vd a91$000000) thì tra bảng cùng số (m91, d91, c91, i91). Ưu tiên: dmct9 → Dir master → Grid, rồi theo thứ tự
    /// wmenu_id (menu nhập chứng từ thường đứng trước menu duyệt / đóng). Phải gọi trên luồng UI (TableMenuService đọc cây WCommand). null = không thấy.</summary>
    public static async Task<MenuHit?> MenuForTableAsync(TableMenuService? menus, string table)
    {
        if (menus is null) return null;
        static bool HasSysId(TableMenuService.Hit h) => !string.IsNullOrWhiteSpace(h.Menu?.SysId);
        var hits = await menus.FindAsync(table);
        // Bảng chứng từ (chữ + số): menu có Dir master là m<số> mới là menu "của" chứng từ — Grid d<số> còn được Dir chứng từ khác (vd đơn hàng
        // kéo phiếu nhu cầu) trỏ tới, nên các menu đó xếp sau.
        var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (VoucherTable.Match(TableMenuService.BaseName(table)) is { Success: true } m)
        {
            var masterName = "m" + m.Groups[2].Value;
            var masterHits = masterName.Equals(TableMenuService.BaseName(table), StringComparison.OrdinalIgnoreCase) ? hits : await menus.FindAsync(masterName);
            foreach (var h in masterHits.Where(h => HasSysId(h) && !h.Via.StartsWith("Grid", StringComparison.OrdinalIgnoreCase))) owners.Add(h.Menu!.SysId.Trim());
            if (!hits.Any(HasSysId))
            {
                if (!ReferenceEquals(masterHits, hits)) hits.AddRange(masterHits);
                foreach (var p in new[] { "d", "c", "i" }.Where(p => !p.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
                    hits.AddRange(await menus.FindAsync(p + m.Groups[2].Value));
            }
        }
        int Rank(TableMenuService.Hit h) => h.Via.StartsWith("dmct9", StringComparison.OrdinalIgnoreCase) ? 0
            : h.Via.StartsWith("Dir", StringComparison.OrdinalIgnoreCase) || owners.Contains(h.Menu!.SysId.Trim()) ? 1 : 2;
        var ordered = hits.Where(h => !string.IsNullOrWhiteSpace(h.Menu?.SysId))
            .OrderBy(Rank).ThenBy(h => h.Menu!.WMenuId, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordered.Count == 0) return null;
        var best = ordered[0].Menu!;
        var others = ordered.Select(h => h.Menu!.SysId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !s.Equals(best.SysId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        return new MenuHit(best.SysId.Trim(), (best.WMenuId.Trim() + " " + (best.Bar ?? "").Trim()).Trim(), others);
    }

    /// <summary>Controller + tên tiếng Việt từng cột của 1 bảng. Controller = sysid của menu wcommand dùng bảng (<see cref="MenuForTableAsync"/>, khi có
    /// <paramref name="menus"/>); không thấy menu thì controller Dir khai &lt;dir table="…"&gt; đúng bảng như cũ. Tên tiếng Việt: tiêu đề field ở controller của
    /// source dự án (Workspace.SourcePath), cột không có trong controller thì lấy header.xml (từ điển field FCode). Source chưa đọc xong trong 20 giây → chỉ từ điển.</summary>
    public static async Task<(string Controller, string[] Others, Dictionary<string, string> Vi, string Menu)> TableInfoAsync(string? sourcePath, string table, IEnumerable<string> columns, TableMenuService? menus = null)
    {
        var vi = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string controller = "", menu = ""; string[] others = Array.Empty<string>();
        IReadOnlyDictionary<string, string>? heads = null;
        try
        {
            if (await MenuForTableAsync(menus, table) is { } hit) { controller = hit.SysId; menu = hit.Label; others = hit.Others; }
        }
        catch { /* không tra được menu — theo controller Dir */ }
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            try
            {
                var load = SourceFieldIndex.GetAsync(sourcePath);
                if (await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(20))) == load)
                {
                    var list = load.Result.ForTable(table);
                    if (controller.Length > 0)
                        heads = list.FirstOrDefault(c => c.Controller.Equals(controller, StringComparison.OrdinalIgnoreCase))?.HeadersV;
                    else if (list.Count > 0)
                    {
                        controller = list[0].Controller; heads = list[0].HeadersV;
                        others = list.Skip(1).Select(c => c.Controller).ToArray();
                    }
                }
            }
            catch { /* source không đọc được — dùng từ điển */ }
            if (heads is null && menu.Length > 0)
            {
                // Bảng không khai ở Dir của menu (a91 của menu PRTran…): lấy tiêu đề field ở chính Dir của sysid.
                var text = await Task.Run(() =>
                {
                    foreach (var ext in new[] { ".f", ".xml" })
                        try { var p = Path.Combine(sourcePath, "App_Data", "Controllers", "Dir", controller + ext); if (File.Exists(p)) return File.ReadAllText(p); } catch { }
                    return "";
                });
                if (text.Length > 0) heads = ApiMenuSchema.ParseFields(text).Values.Where(f => f.HeaderV.Length > 0).ToDictionary(f => f.Name, f => f.HeaderV, StringComparer.OrdinalIgnoreCase);
            }
        }
        foreach (var col in columns)
        {
            if (heads is not null && heads.TryGetValue(col, out var v) && v.Length > 0) vi[col] = v;
            else if (FieldDictionaryService.Instance.Header(col) is { V.Length: > 0 } h) vi[col] = h.V;
        }
        return (controller, others, vi, menu);
    }
}
