using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Tra ngược "bảng → menu" cho tab Table (dòng "Phiếu nhập kho (inctpnd.aspx)" dưới thanh nhập). Hai nguồn, gộp lại:
/// <list type="number">
/// <item>Bảng <c>dmct9</c> (App Data) — chứng từ: cột c$/m$/d$/i$ là tiền tố bảng (c74$, m74$, d74$, i74$ hay dmcc, phdm…), cột url là trang
/// (inctpnd.aspx?query=%s) → menu wcommand có link đúng trang đó.</item>
/// <item>Controller của menu (sysid) trong source: <c>Controllers\Dir\{sysid}.f</c> có &lt;dir table="…"&gt; và các Grid nó trỏ tới
/// (<see cref="CommandTableService.ResolveIn"/>) — danh mục dm…, chứng từ không khai ở dmct9.</item>
/// </list>
/// Tên bảng so theo "gốc": bỏ schema, bỏ đuôi kỳ $yyyymm / $000000 / $log (i74$202601 → i74). Bảng dmct9 + kết quả quét controller cache theo
/// workspace (server + App DB + Source Path); quét lại khi số menu trên cây đổi (cây nạp xong / đổi dự án) hoặc khi gọi <see cref="Forget"/>
/// (nút ⟳ Refresh database). Menu luôn lấy từ cây WCommand đang nạp nên bấm vào là mở đúng node.
/// </summary>
public sealed class TableMenuService
{
    /// <summary>1 kết quả. <paramref name="Menu"/> null = dmct9 có trang <paramref name="Aspx"/> nhưng không menu nào trỏ tới trang đó.</summary>
    public sealed record Hit(WCommandItem? Menu, string Path, string Aspx, string Via);

    private sealed class Index
    {
        public string Stamp = "";
        public int MenuCount;
        /// <summary>gốc bảng → (trang aspx, mã chứng từ) theo dmct9.</summary>
        public readonly Dictionary<string, List<(string Aspx, string MaCt)>> Vouchers = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>gốc bảng → (controller, true = bảng master của Dir / false = bảng của Grid chi tiết).</summary>
        public readonly Dictionary<string, List<(string Controller, bool Master)>> Controllers = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly DbConnectionService _connections;
    private readonly Func<List<(WCommandItem Item, string Path)>> _menus;
    private Task<Index>? _index;

    /// <param name="menus">Các menu lá của cây WCommand đang nạp (gọi trên luồng UI).</param>
    public TableMenuService(DbConnectionService connections, Func<List<(WCommandItem Item, string Path)>> menus)
    {
        _connections = connections;
        _menus = menus;
    }

    /// <summary>Quên dữ liệu đã nạp (dmct9 + controller) — lần tra sau đọc lại.</summary>
    public void Forget() => _index = null;

    private static readonly Regex PeriodSuffix = new(@"\$(\d+|log)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"dbo.[i74$202601]" → "i74"; "pos$phdc$" → "pos$phdc"; "dmkh" → "dmkh".</summary>
    public static string BaseName(string? table)
    {
        var t = (table ?? "").Trim().Replace("[", "").Replace("]", "");
        var dot = t.LastIndexOf('.');
        if (dot >= 0) t = t[(dot + 1)..];
        return PeriodSuffix.Replace(t.Trim(), "");
    }

    /// <summary>"Main/inctpnd.aspx?query=%s" → "inctpnd.aspx".</summary>
    public static string PageOf(string? link)
    {
        var s = (link ?? "").Trim();
        var q = s.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) s = s[..q];
        var slash = s.LastIndexOfAny(new[] { '/', '\\' });
        return slash >= 0 ? s[(slash + 1)..] : s;
    }

    /// <summary>Các menu ứng với bảng (đã bỏ trùng; nguồn dmct9 trước, rồi Dir master, rồi Grid). Rỗng khi không tra được.</summary>
    public async Task<List<Hit>> FindAsync(string table)
    {
        var key = BaseName(table);
        if (key.Length == 0 || _connections.Current is not { } ws) return new();
        List<(WCommandItem Item, string Path)> leaves;
        try { leaves = _menus(); } catch { leaves = new(); }

        var stamp = _connections.CurrentStamp(false) + "|" + ws.SourcePath;
        var task = _index;
        if (task is null || task.IsFaulted || task.IsCanceled
            || (task.IsCompletedSuccessfully && (task.Result.Stamp != stamp || task.Result.MenuCount != leaves.Count)))
            _index = task = BuildAsync(stamp, ws.SourcePath, leaves);
        Index idx;
        try { idx = await task; } catch { return new(); }
        if (idx.Stamp != stamp) return new();   // đổi workspace giữa chừng

        var hits = new List<Hit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(Hit h) { if (h.Menu is null || seen.Add(h.Menu.WMenuId)) hits.Add(h); }

        if (idx.Vouchers.TryGetValue(key, out var vouchers))
            foreach (var (aspx, maCt) in vouchers)
            {
                var found = false;
                foreach (var (item, path) in leaves)
                    if (PageOf(item.Link).Equals(aspx, StringComparison.OrdinalIgnoreCase))
                    { Add(new Hit(item, path, aspx, "dmct9: ma_ct " + maCt)); found = true; }
                if (!found && !hits.Any(h => h.Menu is null && h.Aspx.Equals(aspx, StringComparison.OrdinalIgnoreCase)))
                    hits.Add(new Hit(null, "", aspx, "dmct9: ma_ct " + maCt));
            }
        if (idx.Controllers.TryGetValue(key, out var controllers))
            foreach (var (controller, master) in controllers.OrderBy(c => c.Master ? 0 : 1).ThenBy(c => c.Controller, StringComparer.OrdinalIgnoreCase))
                foreach (var (item, path) in leaves)
                    if ((item.SysId ?? "").Trim().Equals(controller, StringComparison.OrdinalIgnoreCase))
                    {
                        var page = PageOf(item.Link);
                        Add(new Hit(item, path, page.Length > 0 ? page : controller, (master ? "Dir " : "Grid ") + controller));
                    }
        return hits;
    }

    private async Task<Index> BuildAsync(string stamp, string? sourcePath, List<(WCommandItem Item, string Path)> leaves)
    {
        var idx = new Index { Stamp = stamp, MenuCount = leaves.Count };

        // 1) dmct9 — chứng từ. Bảng không có / thiếu cột thì bỏ qua nguồn này.
        try
        {
            await using var conn = _connections.CreateConnection(useSysDatabase: false);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("IF OBJECT_ID('dbo.dmct9') IS NOT NULL SELECT * FROM dbo.dmct9", conn) { CommandTimeout = 30 };
            await using var r = await cmd.ExecuteReaderAsync();
            int Col(string name) { for (var i = 0; i < r.FieldCount; i++) if (r.GetName(i).Equals(name, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
            var cols = new[] { "c$", "m$", "d$", "i$" }.Select(Col).Where(i => i >= 0).ToArray();
            int url = Col("url"), maCt = Col("ma_ct");
            if (url >= 0)
                while (await r.ReadAsync())
                {
                    var page = PageOf(r.IsDBNull(url) ? "" : Convert.ToString(r.GetValue(url)));
                    if (page.Length == 0) continue;
                    var ct = maCt >= 0 && !r.IsDBNull(maCt) ? Convert.ToString(r.GetValue(maCt))?.Trim() ?? "" : "";
                    foreach (var c in cols)
                    {
                        var b = BaseName(r.IsDBNull(c) ? "" : Convert.ToString(r.GetValue(c)));
                        if (b.Length == 0) continue;
                        if (!idx.Vouchers.TryGetValue(b, out var list)) idx.Vouchers[b] = list = new();
                        if (!list.Any(x => x.Aspx.Equals(page, StringComparison.OrdinalIgnoreCase))) list.Add((page, ct));
                    }
                }
        }
        catch { /* không có dmct9 / không kết nối được — còn nguồn controller */ }

        // 2) Controller của từng menu (sysid) trong source: Dir master + Grid chi tiết. Đọc file qua share có thể chậm → luồng nền, song song.
        if (!string.IsNullOrWhiteSpace(sourcePath) && leaves.Count > 0)
        {
            var controllersDir = Path.Combine(sourcePath, "App_Data", "Controllers");
            var sysIds = leaves.Select(l => (l.Item.SysId ?? "").Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var found = new ConcurrentBag<(string Table, string Controller, bool Master)>();
            await Task.Run(() =>
            {
                if (!Directory.Exists(Path.Combine(controllersDir, "Dir"))) return;
                Parallel.ForEach(sysIds, new ParallelOptions { MaxDegreeOfParallelism = 8 }, id =>
                {
                    try
                    {
                        var res = CommandTableService.ResolveIn(controllersDir, id);
                        // table="&Tag;" (Dir danh mục hay khai bảng qua entity) → tra <!ENTITY Tag "dmkh"> trong chính file Dir.
                        string? dirText = null;
                        string Expand(string t)
                        {
                            if (EntityRef.Match(t) is not { Success: true } m) return t;
                            dirText ??= ReadDir(controllersDir, id) ?? "";
                            var decl = Regex.Match(dirText, @"<!ENTITY\s+" + Regex.Escape(m.Groups[1].Value) + @"\s+""([^""&%]*)""");
                            return decl.Success ? decl.Groups[1].Value.Trim() : "";
                        }
                        if (Expand(res.Master) is { Length: > 0 } master) found.Add((master, id, true));
                        foreach (var d in res.Details) if (Expand(d) is { Length: > 0 } detail) found.Add((detail, id, false));
                    }
                    catch { /* file lỗi — bỏ qua menu này */ }
                });
            });
            foreach (var (table, controller, master) in found)
            {
                var b = BaseName(table);
                if (b.Length == 0) continue;
                if (!idx.Controllers.TryGetValue(b, out var list)) idx.Controllers[b] = list = new();
                if (!list.Any(x => x.Controller.Equals(controller, StringComparison.OrdinalIgnoreCase))) list.Add((controller, master));
            }
        }
        return idx;
    }

    private static readonly Regex EntityRef = new(@"^&(\w+);$", RegexOptions.Compiled);

    private static string? ReadDir(string controllersDir, string id)
    {
        foreach (var ext in new[] { ".f", ".xml" })
            try { var p = Path.Combine(controllersDir, "Dir", id + ext); if (File.Exists(p)) return File.ReadAllText(p); } catch { /* share rớt */ }
        return null;
    }
}
