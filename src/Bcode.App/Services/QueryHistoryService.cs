using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>Một lần chạy script trong tab SQL Query.</summary>
public sealed record QueryRun(string Id, DateTime At, string Workspace, bool Sys, string Sql, string Skeleton, bool Ok, int Ms, int Rows)
{
    /// <summary>Nhãn và thẻ gắn tay để lọc nhanh (lần lưu cũ chưa có → rỗng).</summary>
    public string Label { get; init; } = "";
    public List<string> Tags { get; init; } = new();
}

/// <summary>Câu SQL được ghim: nhãn + thẻ (dự án / báo cáo / việc...) để tìm lại. <see cref="Skeleton"/> = script với giá trị trong DECLARE/SET đã bỏ — dùng nối ghim với các lần chạy.</summary>
public sealed record QueryPin(string Id, string Label, List<string> Tags, string? Workspace, string Sql, string Skeleton, DateTime Created);

public sealed record QueryParam(string Name, string Value);

/// <summary>
/// Lịch sử chạy SQL và câu SQL ghim. Lưu ở %AppData%\Bcode\query-history\ (nội dung có thể chứa dữ liệu khách nên không đặt lên ổ mạng):
/// <c>runs.jsonl</c> (mỗi dòng một lần chạy, chỉ nối thêm vào cuối) và <c>pins.json</c>. KHÔNG nạp gì lúc khởi động — file chỉ được đọc (ở luồng nền)
/// lần đầu mở tab lịch sử; ghi một lần chạy là một dòng nối thêm, làm ở luồng nền nên không chặn việc chạy script.
/// "Tham số lần trước": script thường có <c>DECLARE @x ... = giá trị</c> / <c>SET @x = giá trị</c>; hai lần chạy cùng "bộ khung" (script bỏ giá trị đó)
/// là cùng một câu, nên <see cref="LastRunOf"/> trả lần chạy gần nhất của câu — script của nó chính là câu với tham số lần trước.
/// </summary>
public sealed class QueryHistoryService
{
    private const int MaxSqlChars = 100_000;
    private const int KeepRuns = 20_000;            // quá số này (kiểm tra khi nạp) thì cắt bớt đoạn cũ nhất
    private static readonly JsonSerializerOptions Opts = new();

    public static QueryHistoryService Instance { get; } = new();

    private readonly object _lock = new();
    private readonly object _fileLock = new();
    private List<QueryRun>? _runs;                  // mới nhất ở cuối; null = chưa nạp
    private List<QueryPin>? _pins;
    private QueryRun? _lastRecorded;
    private readonly Queue<QueryRun> _pendingWrites = new();
    private bool _writerRunning;

    private static string Dir => Path.Combine(BcodePaths.AppData, "Bcode", "query-history");
    private static string RunsFile => Path.Combine(Dir, "runs.jsonl");
    private static string PinsFile => Path.Combine(Dir, "pins.json");

    // ---- bộ khung & tham số ------------------------------------------------------------------------------

    private static readonly Regex DeclareValue = new(@"^(?<pre>[ \t]*declare[ \t]+@\w+[ \t]+[\w\(\), ]+?[ \t]*=[ \t]*)(?<v>[^\r\n;]+?)(?<post>[ \t]*;?[ \t]*)(?=\r?$)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex SetValue = new(@"^(?<pre>[ \t]*set[ \t]+@\w+[ \t]*=[ \t]*)(?<v>[^\r\n;]+?)(?<post>[ \t]*;?[ \t]*)(?=\r?$)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex NameOf = new(@"@\w+", RegexOptions.Compiled);

    /// <summary>Script với giá trị của DECLARE/SET thay bằng "?", gộp khoảng trắng, chữ thường — hai lần chạy chỉ khác tham số thì cho cùng kết quả.</summary>
    public static string Skeleton(string sql)
    {
        var s = DeclareValue.Replace(sql, "${pre}?${post}");
        s = SetValue.Replace(s, "${pre}?${post}");
        return Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
    }

    /// <summary>Các tham số (DECLARE @x ... = v / SET @x = v) có trong script, theo thứ tự xuất hiện.</summary>
    public static List<QueryParam> Params(string sql)
    {
        var found = new List<(int Pos, QueryParam P)>();
        foreach (Match m in DeclareValue.Matches(sql)) found.Add((m.Index, new QueryParam(NameOf.Match(m.Groups["pre"].Value).Value, m.Groups["v"].Value.Trim())));
        foreach (Match m in SetValue.Matches(sql)) found.Add((m.Index, new QueryParam(NameOf.Match(m.Groups["pre"].Value).Value, m.Groups["v"].Value.Trim())));
        return found.OrderBy(f => f.Pos).Select(f => f.P).ToList();
    }

    // ---- ghi ---------------------------------------------------------------------------------------------

    /// <summary>Ghi một lần chạy (gọi từ luồng giao diện, trả về ngay). Bỏ qua script rỗng và lần bấm Execute đúp trong 2 giây.</summary>
    public void Record(string workspace, bool sys, string sql, bool ok, int ms, int rows)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        if (sql.Length > MaxSqlChars) sql = sql[..MaxSqlChars];
        var run = new QueryRun(Guid.NewGuid().ToString("N")[..12], DateTime.Now, workspace ?? "", sys, sql, Skeleton(sql), ok, ms, rows);
        lock (_lock)
        {
            if (_lastRecorded is { } last && last.Sql == sql && last.Workspace == run.Workspace && last.Sys == sys && (run.At - last.At).TotalSeconds < 2) return;
            _lastRecorded = run;
            _runs?.Add(run);
            _pendingWrites.Enqueue(run);
            if (_writerRunning) return;
            _writerRunning = true;
        }
        _ = Task.Run(DrainWrites);
    }

    private void DrainWrites()
    {
        while (true)
        {
            List<QueryRun> batch;
            lock (_lock)
            {
                if (_pendingWrites.Count == 0) { _writerRunning = false; return; }
                batch = _pendingWrites.ToList();
                _pendingWrites.Clear();
            }
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                foreach (var r in batch) sb.Append(JsonSerializer.Serialize(r, Opts)).Append('\n');
                lock (_fileLock) File.AppendAllText(RunsFile, sb.ToString(), Encoding.UTF8);
            }
            catch { /* ghi lịch sử không được làm hỏng việc chạy script */ }
        }
    }

    // ---- đọc ---------------------------------------------------------------------------------------------

    /// <summary>Nạp lịch sử + ghim (nếu chưa). Chạy ở luồng nền khi mở tab lần đầu; file hỏng từng dòng thì bỏ dòng đó.</summary>
    public Task EnsureLoadedAsync() => _runs is not null ? Task.CompletedTask : Task.Run(() =>
    {
        var runs = new List<QueryRun>();
        try
        {
            if (File.Exists(RunsFile))
                foreach (var line in File.ReadLines(RunsFile, Encoding.UTF8))
                {
                    if (line.Length < 10) continue;
                    try { if (JsonSerializer.Deserialize<QueryRun>(line, Opts) is { } r) { r = r with { Tags = r.Tags ?? new(), Label = r.Label ?? "" }; runs.Add(r); } } catch { /* dòng hỏng */ }
                }
            runs = runs.GroupBy(r => r.Id).Select(g => g.Last()).ToList();      // bỏ dòng trùng (do ghi chồng lúc sửa/xoá)
            if (runs.Count > KeepRuns * 1.25)
            {
                runs = runs.Skip(runs.Count - KeepRuns).ToList();
                Compact(runs);
            }
        }
        catch { /* không đọc được — coi như chưa có lịch sử */ }

        var pins = new List<QueryPin>();
        try { if (File.Exists(PinsFile)) pins = JsonSerializer.Deserialize<List<QueryPin>>(File.ReadAllText(PinsFile, Encoding.UTF8), Opts) ?? new(); } catch { /* ghim hỏng */ }

        lock (_lock)
        {
            if (_runs is null)
            {
                // Ghép các lần chạy được ghi trong lúc đang nạp (đã nằm trong hàng đợi ghi, chưa có trong runs).
                _runs = runs;
                foreach (var pending in _pendingWrites) if (runs.All(r => r.Id != pending.Id)) _runs.Add(pending);
                _pins = pins;
            }
        }
    });

    private void Compact(List<QueryRun> keep)
    {
        try
        {
            var tmp = RunsFile + ".tmp";
            lock (_fileLock)
            {
                File.WriteAllLines(tmp, keep.Select(r => JsonSerializer.Serialize(r, Opts)), Encoding.UTF8);
                File.Move(tmp, RunsFile, overwrite: true);
            }
        }
        catch { /* để lần sau */ }
    }

    private static string Fold(string s) => new string(s.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
        .Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant();

    /// <summary>Tìm trong lịch sử: mọi từ khoá (cách nhau dấu cách) đều phải có trong script; mới nhất trước.</summary>
    public List<QueryRun> SearchRuns(string text, string? workspace, string? tag, int limit)
    {
        List<QueryRun> snapshot;
        lock (_lock) snapshot = _runs?.ToList() ?? new();
        var tokens = Fold(text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<QueryRun>();
        for (var i = snapshot.Count - 1; i >= 0 && result.Count < limit; i--)
        {
            var r = snapshot[i];
            if (!string.IsNullOrEmpty(workspace) && !string.Equals(r.Workspace, workspace, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(tag) && !r.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            if (tokens.Length > 0)
            {
                var hay = Fold(r.Label + " " + string.Join(" ", r.Tags) + " " + r.Sql);
                if (!tokens.All(t => hay.Contains(t, StringComparison.Ordinal))) continue;
            }
            result.Add(r);
        }
        return result;
    }

    public List<string> Workspaces()
    {
        lock (_lock) return (_runs ?? new()).Select(r => r.Workspace).Concat((_pins ?? new()).Select(p => p.Workspace ?? "")).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(w => w).ToList();
    }

    public QueryRun? FindRun(string id) { lock (_lock) return _runs?.LastOrDefault(r => r.Id == id); }

    /// <summary>Lần chạy gần nhất của cùng một câu (cùng bộ khung) — script của nó mang tham số lần trước.</summary>
    public QueryRun? LastRunOf(string skeleton, string? workspace = null)
    {
        lock (_lock) return _runs?.LastOrDefault(r => r.Skeleton == skeleton && (string.IsNullOrEmpty(workspace) || string.Equals(r.Workspace, workspace, StringComparison.OrdinalIgnoreCase)))
                            ?? _runs?.LastOrDefault(r => r.Skeleton == skeleton);
    }

    // ---- lưu tay / sửa / xoá ------------------------------------------------------------------------------

    /// <summary>Lưu một script vào lịch sử theo yêu cầu của người dùng (nút "Lưu lịch sử" ở tab SQL Query).</summary>
    public QueryRun Save(string workspace, bool sys, string sql, string label = "", IEnumerable<string>? tags = null)
    {
        if (sql.Length > MaxSqlChars) sql = sql[..MaxSqlChars];
        var run = new QueryRun(Guid.NewGuid().ToString("N")[..12], DateTime.Now, workspace ?? "", sys, sql, Skeleton(sql), true, 0, 0)
        { Label = label.Trim(), Tags = CleanTags(tags) };
        lock (_lock)
        {
            _runs?.Add(run);
            _pendingWrites.Enqueue(run);
            if (_writerRunning) return run;
            _writerRunning = true;
        }
        _ = Task.Run(DrainWrites);
        return run;
    }

    private static List<string> CleanTags(IEnumerable<string>? tags) =>
        (tags ?? Array.Empty<string>()).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Gắn / đổi nhãn và thẻ của một mục lịch sử (cần đã nạp — <see cref="EnsureLoadedAsync"/>).</summary>
    public void UpdateRun(string id, string label, IEnumerable<string> tags)
    {
        lock (_lock)
        {
            if (_runs is null) return;
            var i = _runs.FindIndex(r => r.Id == id);
            if (i < 0) return;
            _runs[i] = _runs[i] with { Label = label.Trim(), Tags = CleanTags(tags) };
        }
        RewriteRuns();
    }

    /// <summary>Xoá các mục lịch sử đã chọn.</summary>
    public int DeleteRuns(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        int removed;
        lock (_lock) removed = _runs?.RemoveAll(r => set.Contains(r.Id)) ?? 0;
        if (removed > 0) RewriteRuns();
        return removed;
    }

    private void RewriteRuns()
    {
        List<QueryRun> copy;
        lock (_lock) { copy = _runs?.ToList() ?? new(); _pendingWrites.Clear(); }
        Compact(copy);
    }

    /// <summary>Mọi thẻ đang dùng (lịch sử + ghim), để lọc nhanh.</summary>
    public List<string> AllTags()
    {
        lock (_lock)
            return (_runs ?? new()).SelectMany(r => r.Tags).Concat((_pins ?? new()).SelectMany(p => p.Tags))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- ghim --------------------------------------------------------------------------------------------

    public List<QueryPin> SearchPins(string text, string? workspace)
    {
        List<QueryPin> snapshot;
        lock (_lock) snapshot = _pins?.ToList() ?? new();
        var tokens = Fold(text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return snapshot.Where(p => string.IsNullOrEmpty(workspace) || p.Workspace is null || string.Equals(p.Workspace, workspace, StringComparison.OrdinalIgnoreCase))
            .Where(p => tokens.Length == 0 || tokens.All(t => Fold(p.Label + " " + string.Join(" ", p.Tags) + " " + p.Sql).Contains(t, StringComparison.Ordinal)))
            .OrderByDescending(p => p.Created).ToList();
    }

    public QueryPin? FindPin(string id) { lock (_lock) return _pins?.FirstOrDefault(p => p.Id == id); }

    /// <summary>Thêm / sửa ghim. <paramref name="id"/> null = ghim mới.</summary>
    public QueryPin SavePin(string? id, string label, IEnumerable<string> tags, string? workspace, string sql)
    {
        QueryPin pin;
        lock (_lock)
        {
            _pins ??= new();
            var cleanTags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var old = id is null ? null : _pins.FirstOrDefault(p => p.Id == id);
            pin = new QueryPin(old?.Id ?? Guid.NewGuid().ToString("N")[..12], label.Trim(), cleanTags, string.IsNullOrWhiteSpace(workspace) ? null : workspace, sql, Skeleton(sql), old?.Created ?? DateTime.Now);
            if (old is not null) _pins[_pins.IndexOf(old)] = pin; else _pins.Add(pin);
        }
        WritePins();
        return pin;
    }

    public void DeletePin(string id)
    {
        lock (_lock) _pins?.RemoveAll(p => p.Id == id);
        WritePins();
    }

    private void WritePins()
    {
        List<QueryPin> copy;
        lock (_lock) copy = _pins?.ToList() ?? new();
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = PinsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy, Opts), Encoding.UTF8);
            File.Move(tmp, PinsFile, overwrite: true);
        }
        catch { /* thử lại ở lần lưu sau */ }
    }

    /// <summary>Xoá lịch sử chạy (giữ ghim).</summary>
    public void ClearRuns()
    {
        lock (_lock) { _runs = new(); _pendingWrites.Clear(); }
        try { if (File.Exists(RunsFile)) File.Delete(RunsFile); } catch { /* đang bị khoá */ }
    }

    public static string ShortHash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..8];
}
