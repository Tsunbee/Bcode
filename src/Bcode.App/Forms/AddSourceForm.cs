using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>Chuẩn bị chung cho mọi nơi mở <see cref="AddSourceForm"/>: kho source phải khai báo và truy cập được.</summary>
public static class AddSourceLauncher
{
    /// <returns>false nếu người dùng không khai/không có kho hợp lệ.</returns>
    public static bool EnsureCollectionPath(IWin32Window owner, Bcode.App.Models.AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SourceCollectionPath) && Directory.Exists(settings.SourceCollectionPath)) return true;

        var path = SimplePromptForm.Show(owner, "Kho source", "Đường dẫn kho chứa các phiên bản source (vd \\\\172.168.5.14\\SourceCollection\\FBO-FBI):",
            settings.SourceCollectionPath);
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!Directory.Exists(path.Trim()))
        {
            MessageBox.Show(owner, "Không truy cập được thư mục:\n" + path, "Cấp source", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        settings.SourceCollectionPath = path.Trim();
        try { settings.Save(); } catch { /* không lưu được thì chỉ phải nhập lại lần sau */ }
        return true;
    }
}

/// <summary>
/// "Cấp source" — lấy file source chuẩn của 1 PHIÊN BẢN trong kho (SourceCollection, vd
/// \\server\SourceCollection\FBO-FBI\FBOR2SP22.6 ...) bỏ vào thư mục dự án, giống màn "Add Source" của FCode:
/// Name (mẫu tên file, nhiều mẫu cách nhau ';', hỗ trợ * và ?, vd "arcthd9.aspx; JRTran*") → chọn Version → liệt kê
/// các file khớp trong App_Data\Controllers và Main của version đó → tick, sửa New Name nếu cần → OK copy vào
/// Destination giữ nguyên đường dẫn tương đối (App_Data\Controllers\Grid\JRTran.xml ...).
///
/// Toàn bộ giao diện là 1 trang WebView2 (Web/Shell/addsource.html) nên tự co giãn theo kích thước cửa sổ; C# giữ mọi
/// thao tác đụng tới đĩa. Trang gửi <c>{action, data}</c> (ready / find / existing / browse / copy / close), C# trả lời
/// bằng các hàm window.* (init, setRows, setExisting, setDest, setStatus).
///
/// Chỉ ĐỌC kho và GHI vào thư mục đích. File đích đã có thì hỏi trước khi ghi đè.
/// </summary>
public class AddSourceForm : ThemedForm
{
    private readonly string _collectionRoot;
    private readonly string _initialPattern;
    private readonly string _initialDest;
    private readonly Action? _onCopied;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private CancellationTokenSource? _findCts;

    public AddSourceForm(string collectionRoot, string initialPattern, string destinationRoot, Action? onCopied = null)
    {
        _collectionRoot = collectionRoot;
        _initialPattern = initialPattern;
        _initialDest = destinationRoot;
        _onCopied = onCopied;

        Text = "Cấp source (Add Source)";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        Width = 1000;
        Height = 660;
        MinimumSize = new Size(420, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_web);

        ThemeManager.ThemeChanged += PushTheme;
        FormClosed += (_, _) => { ThemeManager.ThemeChanged -= PushTheme; _findCts?.Cancel(); };
        Load += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate($"https://{WebViewEnvironment.Host}/addsource.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Cấp source",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var action = doc.RootElement.GetProperty("action").GetString();
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;

            switch (action)
            {
                case "ready": await OnReadyAsync(); break;
                case "find": await FindAsync(Str(data, "name"), Str(data, "version"), Str(data, "dest")); break;
                case "existing": await PushExistingAsync(Str(data, "dest"), ReadPairs(data)); break;
                case "browse": BrowseDest(Str(data, "dest")); break;
                case "copy": await CopyAsync(data); break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
    }

    private async Task OnReadyAsync()
    {
        PushTheme();
        List<string> versions = new();
        string? error = null;
        try
        {
            var root = _collectionRoot;
            versions = await Task.Run(() => Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().ToList());
            versions.Sort((a, b) => NaturalCompare(b, a)); // mới nhất (số lớn) lên đầu
        }
        catch (Exception ex) { error = "Không đọc được kho source: " + ex.Message; }

        await Js($"window.init({JsonSerializer.Serialize(new { name = _initialPattern, dest = _initialDest, versions, error })})");
    }

    private async Task FindAsync(string name, string version, string dest)
    {
        var patterns = name.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0) { await Js("window.setStatus('Nhập Name (tên file, có thể dùng * và ?).', 'err')"); return; }

        _findCts?.Cancel();
        var cts = _findCts = new CancellationTokenSource();
        var token = cts.Token;
        var versionRoot = Path.Combine(_collectionRoot, version);

        List<(string Path, string Name, string Sub, string Modified)> found;
        try
        {
            found = await Task.Run(() =>
            {
                var list = new List<(string, string, string, string)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sub in new[] { Path.Combine("App_Data", "Controllers"), "Main" })
                {
                    var dir = Path.Combine(versionRoot, sub);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var pattern in patterns)
                    {
                        IEnumerable<string> files;
                        try { files = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories); }
                        catch (ArgumentException) { continue; } // mẫu chứa ký tự không hợp lệ
                        foreach (var f in files)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!seen.Add(f)) continue;
                            list.Add((f, Path.GetFileName(f),
                                Path.GetRelativePath(versionRoot, Path.GetDirectoryName(f) ?? versionRoot),
                                File.GetLastWriteTime(f).ToString("dd/MM/yyyy HH:mm")));
                        }
                    }
                }
                return list.OrderBy(r => r.Item3, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Item2, StringComparer.OrdinalIgnoreCase).ToList();
            }, token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { await Js($"window.setStatus({Json("Lỗi khi tìm: " + ex.Message)}, 'err')"); return; }
        if (token.IsCancellationRequested) return;

        var rows = found.Select(r => new
        {
            path = r.Path, name = r.Name, subpath = r.Sub, modified = r.Modified,
            existing = SafeExists(dest, r.Sub, r.Name),
        });
        var msg = found.Count == 0 ? $"Không có file nào khớp trong {version}." : $"{found.Count} file khớp trong {version}.";
        await Js($"window.setRows({JsonSerializer.Serialize(rows)}, {Json(msg)})");
    }

    private static bool SafeExists(string dest, string sub, string name)
    {
        try { return dest.Length > 0 && File.Exists(Path.Combine(dest.Trim(), sub, name)); }
        catch { return false; }
    }

    private async Task PushExistingAsync(string dest, List<(string Sub, string Name)> items)
    {
        var flags = await Task.Run(() => items.Select(i => SafeExists(dest, i.Sub, i.Name)).ToList());
        await Js($"window.setExisting({JsonSerializer.Serialize(flags)})");
    }

    private void BrowseDest(string current)
    {
        using var dialog = new FolderBrowserDialog { Description = "Chọn thư mục đích (gốc site dự án)" };
        if (Directory.Exists(current)) dialog.SelectedPath = current;
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = Js($"window.setDest({Json(dialog.SelectedPath)})");
    }

    private async Task CopyAsync(JsonElement data)
    {
        var dest = Str(data, "dest").Trim();
        var items = data.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(i => (Path: Str(i, "path"), Sub: Str(i, "subpath"), NewName: Str(i, "newName").Trim(), Name: Str(i, "name"))).ToList()
            : new();

        if (items.Count == 0) { MessageBox.Show(this, "Chưa tick file nào.", "Cấp source"); return; }
        if (dest.Length == 0 || !Directory.Exists(dest))
        {
            MessageBox.Show(this, "Thư mục Destination không tồn tại:\n" + dest, "Cấp source", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (items.Any(i => i.NewName.Length == 0 || i.NewName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            MessageBox.Show(this, "Có dòng New Name trống hoặc chứa ký tự không hợp lệ.", "Cấp source", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var existing = items.Where(i => File.Exists(Path.Combine(dest, i.Sub, i.NewName))).ToList();
        if (existing.Count > 0 && MessageBox.Show(this,
                $"{existing.Count} file đích đã có, ghi đè?\n\n" + string.Join("\n", existing.Take(15).Select(i => i.Sub + "\\" + i.NewName)) + (existing.Count > 15 ? "\n..." : ""),
                "Cấp source", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        await Js("window.setStatus('Đang copy...', '')");
        var (copied, errors) = await Task.Run(() =>
        {
            var ok = 0;
            var errs = new List<string>();
            foreach (var i in items)
            {
                try
                {
                    var target = Path.Combine(dest, i.Sub, i.NewName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(i.Path, target, overwrite: true);
                    ok++;
                }
                catch (Exception ex) { errs.Add($"{i.Name}: {ex.Message}"); }
            }
            return (ok, errs);
        });
        if (copied > 0) _onCopied?.Invoke();

        var msg = errors.Count == 0 ? $"Đã cấp {copied} file." : $"Đã cấp {copied}/{items.Count} file. Lỗi: {string.Join("; ", errors)}";
        await Js($"window.setStatus({Json(msg)}, {(errors.Count == 0 ? "'ok'" : "'err'")})");
        // Cập nhật lại nhãn "đã có" cho toàn bộ danh sách hiện tại.
        await Js("scheduleExisting()");
    }

    // ---------------------------------------------------------------- helpers

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static List<(string Sub, string Name)> ReadPairs(JsonElement data) =>
        data.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(i => (Str(i, "subpath"), Str(i, "newName"))).ToList()
            : new();

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private async Task Js(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        await _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }

    /// <summary>So sánh tên có số ("FBOR2SP9" &lt; "FBOR2SP22.6") theo giá trị số chứ không theo từng ký tự.</summary>
    private static int NaturalCompare(string a, string b)
    {
        var pa = Regex.Split(a, @"(\d+)");
        var pb = Regex.Split(b, @"(\d+)");
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            if (long.TryParse(pa[i], out var na) && long.TryParse(pb[i], out var nb))
            {
                if (na != nb) return na.CompareTo(nb);
            }
            else
            {
                var c = string.Compare(pa[i], pb[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
        }
        return pa.Length.CompareTo(pb.Length);
    }
}
