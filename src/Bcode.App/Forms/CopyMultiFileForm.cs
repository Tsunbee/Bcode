using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Copy to..." nhiều file cùng lúc cho File Lookup: các file đã tick ở cây được liệt kê trong một bảng, người dùng chọn thư mục đích (hoặc
/// dự án), tuỳ chọn giữ cấu trúc thư mục con, xử lý file trùng, và đổi tên từng file (hoặc đổi hàng loạt: tiền tố / hậu tố / tìm-thay).
/// Giao diện là trang WebView2 (Web/Shell/copymulti.html) tự co giãn theo cửa sổ và UiScale; C# giữ phần đụng tới đĩa. Trang chỉ gửi mã
/// (số thứ tự) của file và tên mới, KHÔNG gửi đường dẫn nguồn — đường dẫn nguồn lấy từ danh sách C# đã có.
/// </summary>
public class CopyMultiFileForm : ThemedForm
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Item(string FullPath, string Rel);

    private readonly List<Item> _items;
    private readonly string _root;
    private readonly AppSettings _settings;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    /// <param name="files">Đường dẫn đầy đủ các file cần copy.</param>
    /// <param name="root">Thư mục gốc đang duyệt ở File Lookup — vị trí tương đối của file dưới gốc này được giữ lại ở đích khi bật "Giữ cấu trúc".</param>
    public CopyMultiFileForm(IEnumerable<string> files, string root, AppSettings settings)
    {
        _root = root;
        _settings = settings;
        _items = files.Distinct(StringComparer.OrdinalIgnoreCase).Select(f => new Item(f, RelativeTo(root, f))).ToList();

        Text = "Copy files to...";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        Width = 1000;
        Height = 700;
        MinimumSize = new Size(520, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_web);

        ThemeManager.ThemeChanged += PushTheme;
        FormClosed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
        Load += async (_, _) => await InitWebAsync();
    }

    private static string RelativeTo(string root, string file)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                var rel = Path.GetRelativePath(root.Trim().TrimEnd('\\', '/') + Path.DirectorySeparatorChar, file);
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return rel;
            }
        }
        catch { /* đường dẫn lạ — dùng tên file */ }
        return Path.GetFileName(file);
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate(UiOverrides.UrlFor("copymulti.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Copy files", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                case "ready": PushState(); break;
                case "browse": BeginInvoke(new Action(() => Browse(Str(data, "path")))); break;
                case "copy": await CopyAsync(data); break;
                case "open-folder": OpenFolder(Str(data, "path")); break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            Js($"window.setStatus({JsonSerializer.Serialize(ex.Message)}, 'err'); window.setBusy(false)");
        }
    }

    private void PushState()
    {
        PushTheme();
        long Size(string p) { try { return new FileInfo(p).Length; } catch { return 0; } }
        var state = new
        {
            root = _root,
            lastDest = _settings.LastCopyToPath ?? "",
            projects = _settings.Workspaces
                .Where(w => !string.IsNullOrWhiteSpace(w.SourcePath))
                .Select(w => new { name = string.IsNullOrWhiteSpace(w.ProjectId) ? w.Name : w.ProjectId, path = w.SourcePath.Trim() }),
            files = _items.Select((it, i) => new { id = i, rel = it.Rel, name = Path.GetFileName(it.FullPath), size = Size(it.FullPath) }),
        };
        Js($"window.init({JsonSerializer.Serialize(state, Web)}, {(AppColors.IsDark ? "true" : "false")})");
    }

    private void Browse(string current)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Chọn thư mục đích để copy các file vào",
            UseDescriptionForTitle = true,
            InitialDirectory = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current) ? current : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) Js($"window.setDest({JsonSerializer.Serialize(dlg.SelectedPath)})");
    }

    // ---------------------------------------------------------------- copy

    private sealed record Req(int Id, string NewName);

    private async Task CopyAsync(JsonElement data)
    {
        var dest = Str(data, "dest").Trim().Trim('"');
        var keep = data.TryGetProperty("keepStructure", out var k) && k.ValueKind == JsonValueKind.True;
        var mode = Str(data, "mode"); // skip | overwrite | rename
        var reqs = new List<Req>();
        if (data.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
                reqs.Add(new Req(x.GetProperty("id").GetInt32(), x.GetProperty("name").GetString() ?? ""));

        if (dest.Length == 0) { Js("window.setStatus('Chưa chọn thư mục đích.', 'err'); window.setBusy(false)"); return; }
        if (reqs.Count == 0) { Js("window.setStatus('Chưa chọn file nào để copy.', 'err'); window.setBusy(false)"); return; }

        if (reqs.Select(r => r.Id).Any(i => i < 0 || i >= _items.Count)) { Js("window.setStatus('Danh sách file không hợp lệ.', 'err'); window.setBusy(false)"); return; }

        var results = await Task.Run(() => DoCopy(dest, keep, mode, reqs));
        var ok = results.Count(r => r.Ok);
        _settings.LastCopyToPath = dest;
        try { _settings.Save(); } catch { /* không lưu được thì thôi */ }

        Js($"window.setResults({JsonSerializer.Serialize(results.Select(r => new { id = r.Id, ok = r.Ok, message = r.Message, dest = r.Dest }), Web)})");
        Js($"window.setStatus({JsonSerializer.Serialize($"Đã copy {ok}/{results.Count} file vào {dest}")}, {(ok == results.Count ? "'ok'" : "'err'")}); window.setBusy(false)");
    }

    private sealed record Result(int Id, bool Ok, string Message, string Dest);

    private List<Result> DoCopy(string dest, bool keepStructure, string mode, List<Req> reqs)
    {
        var results = new List<Result>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 2 file cùng đích trong một lượt copy
        var invalid = Path.GetInvalidFileNameChars();

        foreach (var r in reqs)
        {
            var item = _items[r.Id];
            var name = r.NewName.Trim();
            try
            {
                if (name.Length == 0 || name.IndexOfAny(invalid) >= 0 || name is "." or "..")
                    throw new InvalidOperationException("Tên file mới không hợp lệ.");
                if (!File.Exists(item.FullPath))
                    throw new FileNotFoundException("File nguồn không còn tồn tại.");

                var relDir = keepStructure ? (Path.GetDirectoryName(item.Rel) ?? "") : "";
                var targetDir = Path.GetFullPath(Path.Combine(dest, relDir));
                if (!targetDir.StartsWith(Path.GetFullPath(dest).TrimEnd('\\') , StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Đường dẫn đích không hợp lệ.");
                var target = Path.Combine(targetDir, name);

                if (!used.Add(target))
                    throw new InvalidOperationException("Hai file trong danh sách trùng cùng một đường dẫn đích — hãy đổi tên một trong hai.");

                if (File.Exists(target))
                {
                    if (mode == "skip") { results.Add(new Result(r.Id, false, "Bỏ qua — file đích đã tồn tại.", target)); continue; }
                    if (mode == "rename") target = UniqueName(target);
                    // "overwrite": ghi đè bên dưới
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(item.FullPath, target, overwrite: true);
                results.Add(new Result(r.Id, true, target == Path.Combine(targetDir, name) ? "Đã copy." : "Đã copy (đổi tên vì trùng).", target));
            }
            catch (Exception ex)
            {
                results.Add(new Result(r.Id, false, ex.Message, ""));
            }
        }
        return results;
    }

    /// <summary>a.xml đã có → a_1.xml, a_2.xml...</summary>
    private static string UniqueName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* không mở được thì thôi */ }
    }

    // ---------------------------------------------------------------- helpers

    private static string Str(JsonElement d, string name) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void Js(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        if (InvokeRequired) { BeginInvoke(new Action(() => Js(script))); return; }
        _ = _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }
}
