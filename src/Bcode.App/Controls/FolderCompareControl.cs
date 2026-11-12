using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>Tab "So sánh thư mục" (menu Tool mở rộng): chọn thư mục A / B → so (Services/FolderCompareService.cs, chạy nền, Dừng được) →
/// danh sách Khác nhau / Chỉ có ở A / Chỉ có ở B / Giống nhau (Web/Shell/foldercompare.html). Tick file + "Chép A → B" = chép sang B sau khi
/// xác nhận (bản B bị ghi đè được sao lưu trước); không bao giờ xoá file.
/// Nháy đúp file chữ khác nhau → <see cref="CompareFilesRequested"/> (MainForm mở Compare Text với 2 bản). Cấu hình (cặp thư mục gần đây,
/// chế độ so, mẫu bỏ qua) ở %AppData%\Bcode\foldercompare.json. Trang là WebView2 ở cả chế độ App (chưa có bản native).</summary>
public sealed class FolderCompareControl : UserControl
{
    private readonly WebBarHost _web = new("foldercompare.html") { Dock = DockStyle.Fill };
    private CancellationTokenSource? _cts;
    private Config _cfg = LoadConfig();
    private string _lastA = "", _lastB = "";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string ConfigPath => Path.Combine(BcodePaths.AppData, "Bcode", "foldercompare.json");

    /// <summary>Nháy đúp / "So sánh nội dung" một file chữ khác nhau: (đường dẫn bên A, đường dẫn bên B).</summary>
    public event Action<string, string>? CompareFilesRequested;

    public FolderCompareControl()
    {
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Ready += () => _web.Call($"window.fc && window.fc.init({JsonSerializer.Serialize(_cfg, JsonOpts)})");
        _web.Message += OnMessage;
        Disposed += (_, _) => _cts?.Cancel();
    }

    private void OnMessage(JsonElement root)
    {
        string S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        switch (S("action"))
        {
            case "browse":
            {
                var side = S("side");
                using var dlg = new FolderBrowserDialog { Description = $"Chọn thư mục {side.ToUpperInvariant()}", UseDescriptionForTitle = true, SelectedPath = S("current") };
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                    _web.Call($"window.fc.setFolder({WebBarHost.Json(side)}, {WebBarHost.Json(dlg.SelectedPath)})");
                break;
            }
            case "compare":
                _ = RunAsync(S("a"), S("b"), root.TryGetProperty("exact", out var ex) && ex.ValueKind == JsonValueKind.True, S("ignore"));
                break;
            case "stop":
                _cts?.Cancel();
                break;
            case "diff":
                if (Paths(S("rel")) is { } pair && File.Exists(pair.A) && File.Exists(pair.B)) CompareFilesRequested?.Invoke(pair.A, pair.B);
                break;
            case "reveal":   // mở Explorer chọn sẵn file (bên A / B)
            {
                if (Paths(S("rel")) is not { } p) break;
                var path = S("side") == "b" ? p.B : p.A;
                try
                {
                    if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                    else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
                }
                catch { /* không mở được Explorer */ }
                break;
            }
            case "copyAB":
            {
                var rels = root.TryGetProperty("rels", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(FolderCompareService.SafeRel).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();
                _ = CopyAsync(rels);
                break;
            }
            case "copy":
                var text = S("text");
                if (text.Length > 0) try { Clipboard.SetText(text); } catch { /* clipboard bận */ }
                break;
        }
    }

    private (string A, string B)? Paths(string rel)
    {
        if (rel.Length == 0 || _lastA.Length == 0 || rel.Contains("..")) return null;
        return (Path.Combine(_lastA, rel), Path.Combine(_lastB, rel));
    }

    private async Task RunAsync(string a, string b, bool exact, string ignore)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        RememberConfig(a, b, exact, ignore);
        var progress = new Progress<FolderCompareService.Progress>(p =>
        {
            if (!IsDisposed && ReferenceEquals(cts, _cts)) _web.Call($"window.fc.progress({JsonSerializer.Serialize(p, JsonOpts)})");
        });
        try
        {
            var result = await FolderCompareService.CompareAsync(a, b, exact, ignore, progress, cts.Token);
            if (IsDisposed || !ReferenceEquals(cts, _cts)) return;
            _lastA = Path.GetFullPath(a.Trim().Trim('"')).TrimEnd('\\');
            _lastB = Path.GetFullPath(b.Trim().Trim('"')).TrimEnd('\\');
            // kết quả có thể hàng chục nghìn dòng: gửi qua PostWebMessage thay vì nhét vào script
            _web.PostJson(JsonSerializer.Serialize(new { type = "result", a = _lastA, b = _lastB, result }, JsonOpts));
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) _web.Call("window.fc.stopped()");
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _web.Call($"window.fc.error({WebBarHost.Json(ex.Message)})");
        }
    }

    /// <summary>Chép các file đã tick từ A sang B (theo cặp thư mục của lần so vừa xong, không theo ô đang gõ): hỏi xác nhận (số file thêm / ghi đè,
    /// dung lượng, nơi sao lưu) → chép nền → báo kết quả; trang tự so lại.</summary>
    private async Task CopyAsync(List<string> rels)
    {
        if (rels.Count == 0 || _lastA.Length == 0 || _lastB.Length == 0) return;
        int over = 0, add = 0; long bytes = 0;
        foreach (var rel in rels)
        {
            try
            {
                var src = new FileInfo(Path.Combine(_lastA, rel));
                if (src.Exists) bytes += src.Length;
                if (File.Exists(Path.Combine(_lastB, rel))) over++; else add++;
            }
            catch { /* đếm sai 1 file không sao — lúc chép sẽ báo lỗi */ }
        }
        var backup = FolderCompareService.NewBackupDir();
        var list = string.Join("\n", rels.Take(12).Select(r => "  • " + r)) + (rels.Count > 12 ? $"\n  … và {rels.Count - 12} file nữa" : "");
        var msg = $"Chép {rels.Count} file từ A sang B ({FormatBytes(bytes)})?\n\n"
                  + $"A: {_lastA}\nB: {_lastB}\n\n{list}\n\n"
                  + $"• Thêm mới: {add} file\n• Ghi đè: {over} file"
                  + (over > 0 ? $" — bản cũ ở B được sao lưu vào:\n  {backup}" : "")
                  + "\n\nKhông xoá file nào ở B.";
        if (MessageBox.Show(FindForm(), msg, "So sánh thư mục — Chép A → B", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            _web.Call("window.fc.copied(null)");
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var progress = new Progress<FolderCompareService.Progress>(p =>
        {
            if (!IsDisposed && ReferenceEquals(cts, _cts)) _web.Call($"window.fc.progress({JsonSerializer.Serialize(p, JsonOpts)})");
        });
        _web.Call("window.fc.copying()");
        try
        {
            var r = await FolderCompareService.CopyAsync(_lastA, _lastB, rels, backup, progress, cts.Token);
            if (IsDisposed) return;
            _web.Call($"window.fc.copied({JsonSerializer.Serialize(r, JsonOpts)})");
            if (r.Errors.Count > 0)
                MessageBox.Show(FindForm(), $"Đã chép {r.Copied}/{rels.Count} file. {r.Errors.Count} file lỗi (đang bị chương trình khác mở, chỉ đọc, thiếu quyền...):\n\n"
                    + string.Join("\n", r.Errors.Take(15)) + (r.Errors.Count > 15 ? $"\n… và {r.Errors.Count - 15} lỗi nữa" : ""),
                    "So sánh thư mục — Chép A → B", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) { if (!IsDisposed) _web.Call("window.fc.copied({stopped:true})"); }
        catch (Exception ex) { if (!IsDisposed) _web.Call($"window.fc.error({WebBarHost.Json(ex.Message)})"); }
    }

    private static string FormatBytes(long n) =>
        n < 1024 ? n + " B" : n < 1 << 20 ? (n / 1024.0).ToString("0.0") + " KB" : (n / 1048576.0).ToString("0.00") + " MB";

    // ---------------------------------------------------------------- cấu hình

    public sealed class Pair { public string A { get; set; } = ""; public string B { get; set; } = ""; }

    public sealed class Config
    {
        public List<Pair> Recent { get; set; } = new();
        public bool Exact { get; set; } = true;
        public string Ignore { get; set; } = FolderCompareService.DefaultIgnore;
        public string DefaultIgnore { get; set; } = FolderCompareService.DefaultIgnore;
    }

    private static Config LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath) && JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath), JsonOpts) is { } c)
            {
                c.DefaultIgnore = FolderCompareService.DefaultIgnore;   // luôn theo bản đang chạy (nút "Mặc định" của trang)
                c.Recent ??= new();
                return c;
            }
        }
        catch { /* hỏng: dùng mặc định */ }
        return new Config();
    }

    /// <summary>Lưu lần so vừa chạy: cặp thư mục lên đầu danh sách gần đây (tối đa 10), chế độ so, mẫu bỏ qua.</summary>
    private void RememberConfig(string a, string b, bool exact, string ignore)
    {
        a = a.Trim().Trim('"'); b = b.Trim().Trim('"');
        _cfg.Recent.RemoveAll(p => string.Equals(p.A, a, StringComparison.OrdinalIgnoreCase) && string.Equals(p.B, b, StringComparison.OrdinalIgnoreCase));
        if (a.Length > 0 && b.Length > 0) _cfg.Recent.Insert(0, new Pair { A = a, B = b });
        if (_cfg.Recent.Count > 10) _cfg.Recent.RemoveRange(10, _cfg.Recent.Count - 10);
        _cfg.Exact = exact;
        _cfg.Ignore = ignore;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions(JsonOpts) { WriteIndented = true }));
        }
        catch { /* lưu lỗi: chỉ mất danh sách gần đây */ }
    }
}
