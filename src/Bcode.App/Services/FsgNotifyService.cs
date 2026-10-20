using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Controls;
using Req = Bcode.App.Controls.FsgRequirementReportControl.Req;

namespace Bcode.App.Services;

/// <summary>
/// Thông báo y/c FSG (chuông 🔔 trên thanh trên): chạy NGẦM (cả khi không mở tab Yêu cầu FSG), định kỳ kéo lại y/c của chính người dùng
/// (ma_lt1 = tên lập trình đã khai — AppSettings.NoteProgrammer; tuỳ chọn cả ma_nv1) cho các dự án đã lưu trong fsg-requirements.json, so với lần lưu trước
/// theo khoá ma_da + fcode1 rồi ghi thông báo: ghi chú có dòng mới (bỏ dòng do chính mình ghi), đổi trạng thái, y/c mới giao cho mình, đổi hạn hoàn thành.
/// Chỉ ĐỌC database FSG (<see cref="FsgProjectLookupService.FetchRequirementReportAsync"/>). Lỗi mạng / VPN: im lặng, lần sau thử lại.
/// Kéo xong thì cập nhật luôn các dòng đó vào fsg-requirements.json (tab đang mở tự vẽ lại qua <see cref="StoreUpdated"/>); "Kéo về" bằng tay ở tab cũng được so
/// (<see cref="RecordFetch"/>) để thay đổi không bị nuốt mất. Cài đặt + lịch sử thông báo: %AppData%\Bcode\fsg-notify.json — dùng chung giữa các cửa sổ Bcode
/// (mỗi cửa sổ là 1 tiến trình): chỉ 1 tiến trình kéo mỗi chu kỳ (khoá file fsg-notify.lock + giờ kéo lưu trong file), các tiến trình khác thấy file đổi thì nạp lại.
/// </summary>
public sealed class FsgNotifyService : IDisposable
{
    public sealed class Settings
    {
        public bool Enabled { get; set; } = true;
        public int Minutes { get; set; } = 10;
        public bool Note { get; set; } = true;       // ghi chú có dòng mới
        public bool Status { get; set; } = true;     // đổi trạng thái
        public bool New { get; set; } = true;        // y/c mới giao cho mình
        public bool Due { get; set; } = true;        // đổi hạn hoàn thành
        public bool Deployer { get; set; }           // báo cả y/c mình là triển khai (ma_nv1)
        public bool Toast { get; set; } = true;      // toast Windows khi Bcode không ở trước
    }

    /// <summary>1 thông báo. Kind: note | status | new | due. Role: lt (mình là lập trình) | nv (mình là triển khai).</summary>
    public sealed class Item
    {
        public string Id { get; set; } = "";
        public string At { get; set; } = "";
        public string MaDa { get; set; } = "";
        public string Fcode1 { get; set; } = "";
        public string MenuId { get; set; } = "";
        public string TenMenu { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Who { get; set; } = "";
        public string Text { get; set; } = "";
        public string Role { get; set; } = "";
        public bool Seen { get; set; }
    }

    private sealed class FileData
    {
        public Settings Settings { get; set; } = new();
        public string LastPoll { get; set; } = "";      // lần thử kéo gần nhất (cả khi lỗi) — tính chu kỳ cho mọi tiến trình
        public string LastOk { get; set; } = "";        // lần kéo thành công gần nhất
        public string LastError { get; set; } = "";
        public List<Item> Items { get; set; } = new();
        /// <summary>Ô "Tự làm mới" của tab Yêu cầu FSG: kéo ngầm cả dữ liệu (theo điều kiện của lần "Kéo về" gần nhất) mỗi N phút, cả khi đóng tab; 0 = tắt.</summary>
        public int AutoPullMinutes { get; set; }
        public string LastAutoPull { get; set; } = "";
        public string AutoPullError { get; set; } = "";
    }

    private const int MaxItems = 500;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    private static string FilePath => Path.Combine(BcodePaths.AppData, "Bcode", "fsg-notify.json");
    private static readonly object FileLock = new();
    private static string LockPath => Path.Combine(BcodePaths.AppData, "Bcode", "fsg-notify.lock");

    /// <summary>Khoá giữa các tiến trình Bcode (chỉ 1 cửa sổ kéo mỗi lượt). Dùng file mở độc quyền thay cho Mutex: Mutex gắn với LUỒNG, mà sau await
    /// code chạy tiếp ở luồng khác → ReleaseMutex lỗi, mutex bị giữ mãi và các lượt sau không chạy. File tự nhả khi tiến trình thoát. null = đang bị giữ.</summary>
    private static FileStream? TryLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
            return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch { return null; }
    }
    /// <summary>Khoá đọc-sửa-ghi fsg-requirements.json trong tiến trình.</summary>
    internal static readonly object StoreLock = new();

    /// <summary>Bản đang chạy (1 MainForm mỗi tiến trình). Null khi chưa khởi động.</summary>
    public static FsgNotifyService? Instance { get; private set; }

    /// <summary>fsg-requirements.json vừa được lần kéo nền ghi lại — tab Yêu cầu FSG đang mở vẽ lại.</summary>
    public static event Action? StoreUpdated;
    /// <summary>Danh sách / cài đặt / số chưa xem đổi (gọi từ luồng nền).</summary>
    public event Action? Changed;
    /// <summary>Thông báo MỚI do chính tiến trình này phát hiện (để hiện toast — tránh mỗi cửa sổ Bcode toast 1 lần).</summary>
    public event Action<IReadOnlyList<Item>>? NewItems;

    private readonly Func<string> _programmer;
    private readonly Func<bool> _licensed;
    private readonly CancellationTokenSource _cts = new();
    private FileData _data = new();
    private DateTime _fileStamp;
    private int _polling;

    public FsgNotifyService(Func<string> programmer, Func<bool> licensed)
    {
        _programmer = programmer;
        _licensed = licensed;
        Reload();
        Instance = this;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (Instance == this) Instance = null;
    }

    // ---- Đọc / ghi file ----

    private void Reload()
    {
        lock (FileLock)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    _data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(FilePath), JsonOpts) ?? new FileData();
                    _fileStamp = File.GetLastWriteTimeUtc(FilePath);
                }
            }
            catch { /* file hỏng / đang ghi: giữ bản trong bộ nhớ */ }
            _data.Settings ??= new Settings();
            _data.Items ??= new List<Item>();
        }
    }

    /// <summary>Đọc lại file (tiến trình khác có thể vừa ghi), sửa, ghi xuống. Trả về bản sau khi sửa.</summary>
    private void Mutate(Action<FileData> change)
    {
        lock (FileLock)
        {
            Reload();
            change(_data);
            if (_data.Items.Count > MaxItems) _data.Items = _data.Items.OrderByDescending(i => i.At, StringComparer.Ordinal).Take(MaxItems).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, JsonOpts));
                _fileStamp = File.GetLastWriteTimeUtc(FilePath);
            }
            catch { /* không ghi được: lần sau ghi lại */ }
        }
        Changed?.Invoke();
    }

    public Settings CurrentSettings { get { lock (FileLock) return _data.Settings; } }
    public int AutoPullMinutes { get { lock (FileLock) return _data.AutoPullMinutes; } }

    /// <summary>Ô "Tự làm mới" của tab Yêu cầu FSG (0 = tắt). Bật thì lần kéo đầu chạy ở lượt kiểm tra kế (≤ 30 giây).</summary>
    public void SetAutoPull(int minutes)
    {
        minutes = Math.Clamp(minutes, 0, 24 * 60);
        Mutate(d => { if (d.AutoPullMinutes != minutes) { d.AutoPullMinutes = minutes; d.LastAutoPull = ""; } });
    }
    public int UnseenCount { get { lock (FileLock) return _data.Items.Count(i => !i.Seen); } }

    /// <summary>Ảnh chụp cho khung Thông báo.</summary>
    public object Snapshot()
    {
        lock (FileLock)
        {
            var projects = new List<string>();
            try { projects = FsgRequirementReportControl.LoadStore().Projects.Keys.Where(k => k.Length > 0).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(); } catch { }
            return new
            {
                settings = _data.Settings,
                items = _data.Items.OrderByDescending(i => i.At, StringComparer.Ordinal).ToList(),
                lastPoll = _data.LastPoll,
                lastOk = _data.LastOk,
                lastError = _data.LastError,
                programmer = _programmer() ?? "",
                licensed = _licensed(),
                projects,
                polling = _polling != 0,
            };
        }
    }

    public void SaveSettings(Settings s)
    {
        s.Minutes = Math.Clamp(s.Minutes, 1, 24 * 60);
        Mutate(d => d.Settings = s);
    }

    public void MarkSeen(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids);
        Mutate(d => { foreach (var i in d.Items) if (set.Contains(i.Id)) i.Seen = true; });
    }

    public void MarkAllSeen() => Mutate(d => { foreach (var i in d.Items) i.Seen = true; });

    /// <summary>Xoá các thông báo đã xem khỏi lịch sử.</summary>
    public void ClearSeen() => Mutate(d => d.Items.RemoveAll(i => i.Seen));

    public Item? Find(string id) { lock (FileLock) return _data.Items.FirstOrDefault(i => i.Id == id); }

    // ---- Chạy ngầm ----

    /// <summary>Vòng nền: mỗi 30 giây xem file có đổi (cửa sổ Bcode khác ghi) và tới chu kỳ thì kéo. Không chặn UI.</summary>
    public void Start()
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), token); } catch { return; }
            while (!token.IsCancellationRequested)
            {
                try { await TickAsync(); } catch { /* không để vòng nền chết */ }
                try { await Task.Delay(TimeSpan.FromSeconds(30), token); } catch { return; }
            }
        }, token);
    }

    private async Task TickAsync()
    {
        try
        {
            if (File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) != _fileStamp) { Reload(); Changed?.Invoke(); }
        }
        catch { }
        Settings s; string last; int auto; string lastAuto;
        lock (FileLock) { s = _data.Settings; last = _data.LastPoll; auto = _data.AutoPullMinutes; lastAuto = _data.LastAutoPull; }
        if (auto > 0 && !(DateTime.TryParse(lastAuto, out var la) && DateTime.Now - la < TimeSpan.FromMinutes(auto))) await AutoPullAsync();
        if (!s.Enabled) return;
        if (DateTime.TryParse(last, out var lp) && DateTime.Now - lp < TimeSpan.FromMinutes(Math.Max(1, s.Minutes))) return;
        await PollAsync(force: false);
    }

    /// <summary>Kéo ngay (nút "Kiểm tra ngay" hoặc tới chu kỳ). Trả về câu báo lỗi / lý do không kéo (null = kéo xong).</summary>
    public async Task<string?> PollAsync(bool force)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return "Đang kiểm tra…";
        FileStream? gate = null;
        try
        {
            Changed?.Invoke();
            var s = CurrentSettings;
            if (!force && !s.Enabled) return "Đang tắt thông báo.";
            if (!_licensed()) return "Cần key bản quyền (Báo cáo yêu cầu FSG).";
            var me = (_programmer() ?? "").Trim();
            if (me.Length == 0) return "Chưa khai tên lập trình — nhập ở ô Lập trình của khung Thông báo (hoặc Note (New)).";
            List<string> projects;
            lock (StoreLock) projects = FsgRequirementReportControl.LoadStore().Projects.Keys.Where(k => k.Trim().Length > 0).ToList();
            if (projects.Count == 0) return "Chưa có dự án nào đã lưu — mở Yêu cầu FSG, nhập Mã dự án rồi Kéo về một lần.";

            // Nhiều cửa sổ Bcode: chỉ 1 tiến trình kéo; tiến trình khác thấy LastPoll mới trong file thì bỏ lượt này.
            if ((gate = TryLock()) is null) return "Cửa sổ Bcode khác đang kiểm tra.";
            if (!force)
            {
                Reload();
                if (DateTime.TryParse(_data.LastPoll, out var lp) && DateTime.Now - lp < TimeSpan.FromMinutes(Math.Max(1, s.Minutes))) return null;
            }
            var startedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            Mutate(d => d.LastPoll = startedAt);

            // Chỉ y/c của mình: ma_lt1 = mình (hoặc ma_lt1 / ma_nv1 khi bật "cả triển khai") — nhẹ hơn kéo cả dự án. Mỗi lần ≤ 150 mã dự án.
            var fetched = new List<Req>();
            string? error = null;
            foreach (var chunk in projects.Chunk(150))
            {
                var (rows, err) = await new FsgProjectLookupService().FetchRequirementReportAsync(string.Join(",", chunk), "", s.Deployer ? "" : me, "", s.Deployer ? me : "");
                if (err is not null) { error = err; break; }
                fetched.AddRange(rows.Select(r => new Req(r.MaDa, r.MenuId, r.TenMenuNgan, r.TrangTlks, r.TlksYn, r.BpLt, r.Fcode1, r.NoiDung, r.MaLt1, r.MaNv1, r.TrangThai, r.NgayHt?.ToString("yyyy-MM-dd"), r.GhiChu)));
            }
            if (error is not null) { Mutate(d => d.LastError = error); return error; }   // mạng / VPN: im lặng, lần sau thử lại

            List<Item> found;
            var storeChanged = false;
            lock (StoreLock)
            {
                var st = FsgRequirementReportControl.LoadStore();
                found = Diff(st, fetched, me, s);
                foreach (var g in fetched.GroupBy(r => r.MaDa, StringComparer.OrdinalIgnoreCase))
                {
                    if (!st.Projects.TryGetValue(g.Key, out var pd)) continue;
                    var merged = Upsert(pd.Rows, g.ToList(), r => Eq(r.MaLt1, me) || (s.Deployer && Eq(r.MaNv1, me)));
                    if (!merged.SequenceEqual(pd.Rows)) { pd.Rows = merged; storeChanged = true; }
                }
                if (storeChanged) FsgRequirementReportControl.SaveStore(st);
            }
            var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            Mutate(d => { d.LastOk = now; d.LastError = ""; d.Items.AddRange(found); });
            if (storeChanged) StoreUpdated?.Invoke();
            if (found.Count > 0) NewItems?.Invoke(found);
            return null;
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            Mutate(d => d.LastError = msg);
            return msg;
        }
        finally
        {
            gate?.Dispose();
            Interlocked.Exchange(ref _polling, 0);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Kéo ngầm tự động (ô "Tự làm mới" của tab Yêu cầu FSG): kéo lại theo đúng điều kiện của lần "Kéo về" gần nhất (Filters trong fsg-requirements.json),
    /// ghép vào bản lưu như "Kéo về" (<see cref="FsgRequirementReportControl.ApplyFetched"/>), so để thông báo (<see cref="RecordFetch"/>), tab đang mở tự vẽ lại.
    /// Chạy cả khi không mở tab. Lỗi mạng / VPN: im lặng (AutoPullError), lần sau thử lại. Nhiều cửa sổ Bcode: chỉ 1 tiến trình kéo mỗi chu kỳ.
    /// </summary>
    private async Task AutoPullAsync()
    {
        if (!_licensed()) return;
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        FileStream? gate = null;
        try
        {
            Dictionary<string, string> f;
            lock (StoreLock) f = FsgRequirementReportControl.LoadStore().Filters;
            if (f.Count == 0) return;   // chưa "Kéo về" lần nào: không biết kéo gì
            if ((gate = TryLock()) is null) return;
            Reload();
            if (_data.AutoPullMinutes <= 0 || (DateTime.TryParse(_data.LastAutoPull, out var la) && DateTime.Now - la < TimeSpan.FromMinutes(_data.AutoPullMinutes))) return;
            var startedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            Mutate(d => d.LastAutoPull = startedAt);

            string F(string k) => f.TryGetValue(k, out var v) ? v ?? "" : "";
            var maDa = F("maDa");
            var (rows, error) = await new FsgProjectLookupService().FetchRequirementReportAsync(maDa, F("bpLt"), F("programmer"), F("deployer"), F("person"), F("fcode"), F("content"));
            if (error is not null) { Mutate(d => d.AutoPullError = error); return; }
            var data = rows.Select(FsgRequirementReportControl.ToReq).ToList();
            lock (StoreLock)
            {
                var st = FsgRequirementReportControl.LoadStore();
                RecordFetch(st, data);
                FsgRequirementReportControl.ApplyFetched(st, maDa, data, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
                FsgRequirementReportControl.SaveStore(st);
            }
            Mutate(d => d.AutoPullError = "");
            StoreUpdated?.Invoke();
        }
        catch (Exception ex) { var msg = ex.Message; Mutate(d => d.AutoPullError = msg); }
        finally
        {
            gate?.Dispose();
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>"Kéo về" bằng tay ở tab Yêu cầu FSG: so dữ liệu vừa kéo với dữ liệu đã lưu (gọi TRƯỚC khi ghi đè) — chỉ dự án đã có trong bản lưu.</summary>
    internal void RecordFetch(FsgRequirementReportControl.Store before, List<Req> fetched)
    {
        try
        {
            var s = CurrentSettings;
            var me = (_programmer() ?? "").Trim();
            if (!s.Enabled || me.Length == 0 || fetched.Count == 0) return;
            var found = Diff(before, fetched, me, s);
            if (found.Count == 0) return;
            Mutate(d => d.Items.AddRange(found));
            NewItems?.Invoke(found);
        }
        catch { /* thông báo là phụ — không làm hỏng "Kéo về" */ }
    }

    // ---- So sánh ----

    private static readonly Dictionary<string, string> StatusLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DD"] = "Đã duyệt", ["TH"] = "Đang thực hiện", ["DT"] = "Đang test", ["OK"] = "Đã test", ["UP"] = "Đã up", ["CH"] = "Chờ", ["KL"] = "Không làm",
    };

    private static string Label(string code) => code.Trim().Length == 0 ? "(trống)" : StatusLabel.TryGetValue(code.Trim(), out var l) ? $"{l} ({code.Trim()})" : code.Trim();
    private static string Date(string? d) => DateTime.TryParse(d, out var x) ? x.ToString("dd/MM/yyyy") : "(trống)";
    private static bool Eq(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    private static string Key(Req r) => (r.MaDa ?? "").Trim().ToUpperInvariant() + "|" + (r.Fcode1 ?? "").Trim().ToUpperInvariant();

    // "[TESTER]:nội dung" hoặc "[TESTER][trạng thái lỗi]:nội dung [LT:trả lời của lập trình]" (FsgProjectLookupService dựng từ nbctyc).
    private static readonly Regex Author = new(@"^\s*\[([^\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex LtReply = new(@"\s*\[LT:.*\]\s*$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static string NoteKey(string line) => Regex.Replace(LtReply.Replace(line, ""), @"\s+", " ").Trim().ToUpperInvariant();

    private static IEnumerable<string> NoteLines(string? ghiChu) => (ghiChu ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>
    /// So y/c vừa kéo (<paramref name="fetched"/>) với bản đã lưu (<paramref name="before"/>), theo ma_da + fcode1 (trùng khoá thì ghép theo thứ tự xuất hiện).
    /// Chỉ y/c của mình, chỉ dự án đã có trong bản lưu. Tránh báo ồ ạt lần đầu: dự án mà bản lưu chưa có y/c nào của mình (vd lần kéo trước lọc theo
    /// người khác) → không báo "y/c mới giao"; dự án mà bản lưu không có dòng ghi chú nào (kéo từ bản Bcode chưa lấy ghi chú) → không so ghi chú.
    /// </summary>
    internal static List<Item> Diff(FsgRequirementReportControl.Store before, IEnumerable<Req> fetched, string me, Settings s)
    {
        bool Mine(Req r) => Eq(r.MaLt1, me) || (s.Deployer && Eq(r.MaNv1, me));
        var oldByKey = new Dictionary<string, List<Req>>();
        var mineProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var noteProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // bản lưu có cột ghi chú (dự án kéo từ trước khi Bcode lấy ghi chú: trống hết)
        foreach (var (code, pd) in before.Projects)
            foreach (var r in pd.Rows)
            {
                var k = Key(r);
                if (!oldByKey.TryGetValue(k, out var l)) oldByKey[k] = l = new List<Req>();
                l.Add(r);
                if (Mine(r)) mineProjects.Add(code);
                if (!string.IsNullOrWhiteSpace(r.GhiChu)) noteProjects.Add(code);
            }

        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var seq = 0;
        var items = new List<Item>();
        var mineFetched = fetched.Where(n => Mine(n) && before.Projects.ContainsKey((n.MaDa ?? "").Trim())).ToList();
        var pairs = new List<(Req N, Req? O)>();
        foreach (var g in mineFetched.GroupBy(Key))
        {
            var fl = g.ToList();
            var ol = oldByKey.TryGetValue(g.Key, out var l0) ? l0 : new List<Req>();
            var map = Pair(ol, fl, Mine);
            for (var i = 0; i < fl.Count; i++) pairs.Add((fl[i], map[i] >= 0 ? ol[map[i]] : null));
        }
        foreach (var (n, o) in pairs)
        {
            var role = Eq(n.MaLt1, me) ? "lt" : "nv";
            Item Make(string kind, string who, string text) => new()
            {
                Id = DateTime.Now.ToString("yyyyMMddHHmmssfff") + "-" + (seq++) + "-" + kind,
                At = now, MaDa = n.MaDa ?? "", Fcode1 = n.Fcode1 ?? "", MenuId = n.MenuId ?? "", TenMenu = n.TenMenuNgan ?? "",
                Kind = kind, Who = who, Text = text.Length > 1500 ? text[..1500] + "…" : text, Role = role,
            };

            if (o is null || !Mine(o))
            {
                if (s.New && mineProjects.Contains((n.MaDa ?? "").Trim()))
                {
                    var first = NoteLines(n.NoiDung).FirstOrDefault() ?? "";
                    var prev = o is null ? "" : $" (trước: {(role == "lt" ? o.MaLt1 : o.MaNv1)})";
                    items.Add(Make("new", "", (role == "lt" ? "Giao bạn lập trình" : "Giao bạn triển khai") + prev + (first.Length > 0 ? " — " + first : "")));
                }
                continue;
            }

            if (s.Note && noteProjects.Contains((n.MaDa ?? "").Trim()))
            {
                var oldCount = new Dictionary<string, int>();
                foreach (var l in NoteLines(o.GhiChu)) { var nk = NoteKey(l); oldCount[nk] = oldCount.TryGetValue(nk, out var x) ? x + 1 : 1; }
                var added = new List<(string Who, string Text)>();
                foreach (var l in NoteLines(n.GhiChu))
                {
                    var nk = NoteKey(l);
                    if (oldCount.TryGetValue(nk, out var x) && x > 0) { oldCount[nk] = x - 1; continue; }
                    var m = Author.Match(l);
                    var who = m.Success ? m.Groups[1].Value.Trim() : "";
                    if (who.Length > 0 && Eq(who, me)) continue;      // dòng do chính mình ghi
                    var text = m.Success ? l[m.Length..].TrimStart() : l;
                    if (text.StartsWith(':')) text = text[1..].TrimStart();
                    added.Add((who, text));
                }
                if (added.Count > 0)
                    items.Add(Make("note", string.Join(", ", added.Select(a => a.Who).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)), string.Join("\n", added.Select(a => a.Text))));
            }
            if (s.Status && !Eq(o.TrangThai, n.TrangThai)) items.Add(Make("status", "", Label(o.TrangThai) + " → " + Label(n.TrangThai)));
            if (s.Due && !Eq(o.NgayHt, n.NgayHt)) items.Add(Make("due", "", "Hạn hoàn thành " + Date(o.NgayHt) + " → " + Date(n.NgayHt)));
        }
        return items;
    }

    /// <summary>Ghép y/c trùng khoá (ma_da + fcode1 có thể lặp): mỗi dòng mới lấy dòng cũ chưa dùng — giống hệt trước, rồi của mình cùng nội dung,
    /// rồi của mình bất kỳ, cuối cùng dòng bất kỳ (y/c chuyển sang mình); -1 = không có.</summary>
    private static int[] Pair(List<Req> old, List<Req> fresh, Func<Req, bool> mine)
    {
        var used = new bool[old.Count];
        var res = Enumerable.Repeat(-1, fresh.Count).ToArray();
        for (var pass = 0; pass < 4; pass++)
            for (var i = 0; i < fresh.Count; i++)
            {
                if (res[i] >= 0) continue;
                for (var j = 0; j < old.Count; j++)
                {
                    if (used[j]) continue;
                    var ok = pass switch
                    {
                        0 => old[j] == fresh[i],
                        1 => mine(old[j]) && old[j].NoiDung == fresh[i].NoiDung,
                        2 => mine(old[j]),
                        _ => true,
                    };
                    if (ok) { used[j] = true; res[i] = j; break; }
                }
            }
        return res;
    }

    /// <summary>Thay các dòng đã lưu bằng dòng vừa kéo cùng khoá (giữ vị trí, ghép như <see cref="Pair"/>), dòng mới thêm cuối. Dòng không có trong lần kéo giữ nguyên.</summary>
    private static List<Req> Upsert(List<Req> stored, List<Req> fresh, Func<Req, bool> mine)
    {
        var result = new List<Req>(stored);
        var posByKey = new Dictionary<string, List<int>>();
        for (var i = 0; i < stored.Count; i++)
        {
            var k = Key(stored[i]);
            if (!posByKey.TryGetValue(k, out var l)) posByKey[k] = l = new List<int>();
            l.Add(i);
        }
        foreach (var g in fresh.GroupBy(Key))
        {
            var fl = g.ToList();
            var pos = posByKey.TryGetValue(g.Key, out var p0) ? p0 : new List<int>();
            var map = Pair(pos.Select(i => stored[i]).ToList(), fl, mine);
            for (var i = 0; i < fl.Count; i++)
                if (map[i] >= 0) result[pos[map[i]]] = fl[i]; else result.Add(fl[i]);
        }
        return result;
    }
}
