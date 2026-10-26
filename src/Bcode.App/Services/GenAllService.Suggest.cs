using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>1 file gợi ý cho ô File Path: đường dẫn đầy đủ, đường dẫn tương đối trong Source Path, nhóm (Main / Dir / Grid / Filter / Include...)
/// và có nên tick sẵn không.</summary>
public sealed class SuggestFile
{
    public string Path { get; set; } = "";
    public string Rel { get; set; } = "";
    public string Group { get; set; } = "";
    public bool Pick { get; set; }
}

/// <summary>1 SQL Object gợi ý (procedure / function... được EXEC trong file của menu): database tìm thấy ("app" / "sys" / "" = không thấy).</summary>
public sealed class SuggestObject
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Db { get; set; } = "";
    public string From { get; set; } = "";
    public bool Pick { get; set; }
}

/// <summary>1 table gợi ý cho "Table liên quan": thông tin như nút Tải table + vai trò trong menu (bảng của Dir / Grid chi tiết).</summary>
public sealed class SuggestTable
{
    public RelatedTable Info { get; set; } = new();
    public string Role { get; set; } = "";
    public bool Pick { get; set; }
}

/// <summary>Script 1 menu (Gen Script Menu: DELETE/INSERT wcommand + command, chạy ở Sys). <see cref="Self"/> = menu đang chọn, còn lại là menu cha
/// (gốc trước); <see cref="Custom"/> = tên có (C) / (*) — menu làm riêng cho khách, đích thường chưa có nên tick sẵn.</summary>
public sealed class SuggestMenu
{
    public string WMenuId { get; set; } = "";
    public string Bar { get; set; } = "";
    public bool Self { get; set; }
    public bool Custom { get; set; }
    public string Script { get; set; } = "";
    public bool Pick { get; set; }
}

/// <summary>Gợi ý điền Note (New) cho 1 menu wcommand: controller (Gen All), file (File Path), SQL Object, Table liên quan.</summary>
public sealed class MenuSuggestion
{
    public string Menu { get; set; } = "";
    public List<string> Controllers { get; set; } = new();
    public List<SuggestFile> Files { get; set; } = new();
    public List<SuggestObject> Objects { get; set; } = new();
    public List<SuggestTable> Tables { get; set; } = new();
    /// <summary>Script menu (DELETE/INSERT wcommand + command, database Sys): chính menu + các menu cha.</summary>
    public List<SuggestMenu> Menus { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Phần người dùng đã chọn trong hộp gợi ý — gửi xuống trang advnote.html (advNote.applySuggest).
/// <see cref="TblList"/> = mọi table liên quan (để hiện ở bảng Table liên quan như sau khi bấm Tải table), <see cref="Tables"/> = table được tick Cấu trúc.</summary>
public sealed class MenuSuggestionPick
{
    public string Menu { get; set; } = "";
    public string NewName { get; set; } = "";
    public List<string> GenAll { get; set; } = new();
    public List<string> FilePaths { get; set; } = new();
    public List<string> Procedures { get; set; } = new();
    public List<string> ProceduresSys { get; set; } = new();
    public List<TableSelection> Tables { get; set; } = new();
    public List<RelatedTable> TblList { get; set; } = new();
    /// <summary>Script menu đã tick — thêm vào SQL Bottom Script của Sys (khối nào đã có trong ô thì bỏ qua).</summary>
    public List<string> MenuScripts { get; set; } = new();
}

public partial class GenAllService
{
    /// <summary>
    /// Gợi ý cho Note (New) theo 1 menu (BcodeNote.exe — bước "chọn menu cần gen"): controller của menu (sysid, Controller="" của trang Main\*.aspx,
    /// không có thì tên trang), các file của menu theo đúng cách File Lookup dò (BuildTreeForMenuItem) — tick sẵn như tập file Gen All đóng gói (Dir/Grid/Filter chỉ .f),
    /// procedure / function được EXEC trong Dir / Grid / Filter (tick sẵn cái có thật trong App / Sys), table liên quan (FindTablesAsync — tick sẵn
    /// bảng của Dir và Grid chi tiết). Chỉ đọc file / database, không ghi gì.
    /// </summary>
    /// <param name="ancestors">Các menu cha của <paramref name="menu"/> (gốc trước) — để gợi ý script menu cha customize (C) / (*).</param>
    public async Task<MenuSuggestion> SuggestForMenuAsync(Workspace ws, WCommandItem menu, IReadOnlyList<WCommandItem>? ancestors = null)
    {
        var s = new MenuSuggestion { Menu = string.IsNullOrWhiteSpace(menu.Bar) ? menu.WMenuId : menu.Bar.Trim() };
        var source = ws.SourcePath?.Trim() ?? "";
        var link = (menu.Link ?? "").Split('?')[0].Trim();
        var sysId = (menu.SysId ?? "").Trim();
        if (source.Length == 0) s.Warnings.Add("Dự án chưa khai báo Source Path — chỉ gợi ý được controller.");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var procs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // tên → file đầu tiên gọi nó
        (string Master, List<string> Details) own = ("", new());

        await Task.Run(() =>
        {
            var ctl = new List<string>();
            if (sysId.Length > 0) ctl.Add(sysId);
            if (source.Length > 0 && link.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
            {
                var main = Path.Combine(source, "Main", link);
                try { if (File.Exists(main)) ctl.AddRange(ControllerAttr.Matches(SafeRead(main)).Select(m => m.Groups[1].Value.Trim())); }
                catch { /* share rớt */ }
            }
            if (ctl.Count == 0 && link.Length > 0) ctl.Add(Path.GetFileNameWithoutExtension(link));
            s.Controllers = ctl.Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (source.Length == 0 || s.Controllers.Count == 0) return;

            var key = sysId.Length > 0 ? sysId : s.Controllers[0];
            Collect(_files.BuildTreeForMenuItem(source, link, key, onlyFInGridFilterDir: false), files);
            foreach (var c in s.Controllers)
                foreach (var aspx in FindMainPagesForController(source, c)) files.Add(aspx);

            var cache = FileParseCache.For(source);
            var session = new ReadSession(cache);
            foreach (var f in files.Where(f => (f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".f", StringComparison.OrdinalIgnoreCase))
                                               && (IsInFolder(source, f, "Filter") || IsInFolder(source, f, "Dir") || IsInFolder(source, f, "Grid")))
                                   .OrderBy(f => IsInFolder(source, f, "Filter") ? 0 : 1).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList())
            {
                try
                {
                    foreach (var p in cache.GetOrCompute("execprocs", f, session, rd => ExtractFilterProcs(f, rd)))
                        procs.TryAdd(p, Path.GetRelativePath(Path.Combine(source, "App_Data", "Controllers"), f));
                }
                catch { /* file không đọc được — bỏ qua */ }
            }
            cache.SaveInBackground();

            var r = CommandTableService.Resolve(source, key);
            own = (r.Master, r.Details);
        });

        foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var rel = IsUnder(source, f) ? Path.GetRelativePath(source, f) : f;
            var isMain = rel.StartsWith("Main" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            s.Files.Add(new SuggestFile
            {
                Path = f,
                Rel = rel,
                Group = GroupOf(source, f, isMain),
                // Gen All không tick sẵn (người dùng tự chọn file) → tick sẵn đúng tập file Gen All đưa vào gói: mọi file của menu, riêng Dir / Grid / Filter chỉ lấy .f.
                Pick = !RequiresF(source, f) || f.EndsWith(".f", StringComparison.OrdinalIgnoreCase),
            });
        }
        s.Files = s.Files.OrderBy(f => GroupRank(f.Group)).ThenBy(f => f.Rel, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var (name, from) in procs.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var o = new SuggestObject { Name = name, From = from };
            foreach (var sys in new[] { false, true })
            {
                try
                {
                    var hit = (await _sql.ListObjectsAsync(sys, name)).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) continue;
                    o.Name = hit.Name;
                    o.Db = sys ? "sys" : "app";
                    o.Kind = hit.Kind switch
                    {
                        SqlObjectKind.StoredProcedure => "procedure",
                        SqlObjectKind.Function => "function",
                        SqlObjectKind.View => "view",
                        SqlObjectKind.Trigger => "trigger",
                        _ => "table",
                    };
                    break;
                }
                catch (Exception ex) { s.Warnings.Add($"Tra \"{name}\" ({(sys ? "Sys" : "App")}): {ex.Message}"); break; }
            }
            o.Pick = o.Db.Length > 0;
            s.Objects.Add(o);
        }

        if (s.Controllers.Count > 0 && source.Length > 0)
        {
            try
            {
                var mainTables = new HashSet<string>(own.Details.Append(own.Master).Where(t => t.Length > 0), StringComparer.OrdinalIgnoreCase);
                foreach (var t in await FindTablesAsync(ws, s.Controllers))
                {
                    var role = string.Equals(t.Name, own.Master, StringComparison.OrdinalIgnoreCase) ? "Dir"
                        : own.Details.Contains(t.Name, StringComparer.OrdinalIgnoreCase) ? "Grid" : "";
                    s.Tables.Add(new SuggestTable { Info = t, Role = role, Pick = mainTables.Contains(t.Name) && (t.InApp || t.InSys) && !t.IsView });
                }
                s.Tables = s.Tables.OrderBy(t => t.Role.Length == 0 ? 1 : 0).ThenBy(t => t.Info.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) { s.Warnings.Add("Dò table liên quan lỗi: " + ex.Message); }
        }

        // Script menu (Sys): menu đang chọn (Gen All không tick sẵn nên không có sysmenu tự sinh) + menu cha customize (C) / (*) tick sẵn.
        // Dòng đọc từ bảng command (sản phẩm dạng APP, không có wcommand) thì không sinh.
        if (!menu.IsAppCommand)
        {
            try
            {
                var chain = (ancestors ?? Array.Empty<WCommandItem>()).Where(a => !a.IsAppCommand).Select(a => (Item: a, Self: false)).Append((Item: menu, Self: true)).ToList();
                foreach (var (item, self) in chain)
                {
                    var custom = WCommandService.IsCustom(item);
                    s.Menus.Add(new SuggestMenu
                    {
                        WMenuId = item.WMenuId,
                        Bar = item.Bar,
                        Self = self,
                        Custom = custom,
                        Script = $"-- Menu: {item.Bar} ({item.WMenuId})\r\n" + (await Task.Run(() => _wcommand.GenerateScriptForCurrentDb(item))).TrimEnd(),
                        Pick = self || custom,
                    });
                }
            }
            catch (Exception ex) { s.Warnings.Add("Sinh script menu lỗi: " + ex.Message); }
        }

        if (s.Controllers.Count == 0) s.Warnings.Add("Menu không có sysid / link nên không suy ra được controller.");
        else if (source.Length > 0 && s.Files.Count == 0) s.Warnings.Add($"Không thấy file nào của {string.Join(", ", s.Controllers)} trong Source Path.");
        return s;
    }

    private static readonly string[] SuggestGroups = { "Main", "Dir", "Grid", "Filter", "Lookup", "Report", "Include", "Templates" };

    private static int GroupRank(string g) { var i = Array.IndexOf(SuggestGroups, g); return i < 0 ? SuggestGroups.Length : i; }

    /// <summary>Nhóm hiển thị: Main, hoặc thư mục con đầu tiên dưới App_Data\Controllers (Dir, Grid, Filter...), còn lại "Khác".</summary>
    private static string GroupOf(string source, string file, bool isMain)
    {
        if (isMain) return "Main";
        try
        {
            var rel = Path.GetRelativePath(Path.Combine(source, "App_Data", "Controllers"), file);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            {
                var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (first.Length > 0 && !first.Contains('.')) return first;
            }
        }
        catch { /* đường dẫn lạ */ }
        return "Khác";
    }
}
