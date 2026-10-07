using System.Text.Json;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Cấp source" tự động từ cây File Lookup (Web/Shell/grantsource.html): các file đang tick → tìm file source (.xml) trong kho SourceCollection
/// theo PHIÊN BẢN (tự nhận phiên bản của dự án, đổi được) → không có ở phiên bản đó thì lấy bản sửa mới nhất ở phiên bản khác → tick chọn → cấp
/// vào đúng đường dẫn tương đối trong thư mục dự án. Logic ở <see cref="SourceGrantService"/>; form này chỉ nối trang với dịch vụ.
/// </summary>
public sealed class GrantSourceForm : ThemedForm
{
    private readonly string _collectionRoot;
    private readonly string _destRoot;
    private readonly List<string> _files;
    private readonly Action? _onCopied;
    private readonly string? _versionCode;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private List<string> _versions = new();
    private List<GrantRow> _rows = new();
    private int _resolveVersion;

    public GrantSourceForm(string collectionRoot, string destRoot, List<string> files, Action? onCopied = null, string? versionCode = null)
    {
        _versionCode = versionCode;
        _collectionRoot = collectionRoot;
        _destRoot = destRoot;
        _files = files;
        _onCopied = onCopied;

        Text = $"Cấp source — {files.Count} file";
        FormBorderStyle = FormBorderStyle.Sizable;
        Width = 1100;
        Height = 640;
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_web);
        ThemeManager.ThemeChanged += PushTheme;
        FormClosed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
        Load += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate(UiOverrides.UrlFor("grantsource.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Cấp source", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Đọc hết giá trị trước khi await (JsonElement không dùng được sau khi tài liệu bị giải phóng).
        string? action, version = null; bool overwrite = false; List<int> indexes = new();
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            action = doc.RootElement.GetProperty("action").GetString();
            if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                if (d.TryGetProperty("version", out var v)) version = v.GetString();
                if (d.TryGetProperty("overwrite", out var o)) overwrite = o.ValueKind == JsonValueKind.True;
                if (d.TryGetProperty("indexes", out var ix) && ix.ValueKind == JsonValueKind.Array)
                    indexes = ix.EnumerateArray().Select(x => x.GetInt32()).ToList();
            }
        }
        catch { return; }

        try
        {
            switch (action)
            {
                case "ready": await OnReadyAsync(); break;
                case "resolve": if (version is not null) await ResolveAsync(version); break;
                case "grant": await GrantAsync(overwrite, indexes); break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            await Js($"grant.onError({Json(ex.Message)})");
        }
    }

    private async Task OnReadyAsync()
    {
        PushTheme();
        string? error = null, note = "", selected = null, current = null;
        try
        {
            _versions = await Task.Run(() => SourceGrantService.ListVersions(_collectionRoot));
            selected = SourceGrantService.DetectVersion(_destRoot, _versions, out note, _versionCode) ?? _versions.FirstOrDefault();
            // Dự án có khai mã phiên bản và tìm được thư mục khớp → đưa thư mục phiên bản hiện tại lên ĐẦU danh sách (và đánh dấu).
            // Không khai mã thì giữ nguyên như cũ: mới nhất trước, chọn sẵn phiên bản đoán theo thư mục source.
            if (!string.IsNullOrWhiteSpace(_versionCode) && selected is not null && note.StartsWith("theo ", StringComparison.Ordinal))
            {
                current = selected;
                _versions = new[] { selected }.Concat(_versions.Where(v => !v.Equals(selected, StringComparison.OrdinalIgnoreCase))).ToList();
            }
        }
        catch (Exception ex) { error = "Không đọc được kho source: " + ex.Message; }

        await Js($"grant.init({JsonSerializer.Serialize(new { versions = _versions, selected, current, detectNote = note, error })})");
        if (selected is not null) await ResolveAsync(selected);
    }

    private async Task ResolveAsync(string version)
    {
        var token = ++_resolveVersion;
        var rows = await Task.Run(() => SourceGrantService.Resolve(_collectionRoot, version, _versions, _files, _destRoot));
        if (token != _resolveVersion || IsDisposed) return; // đã đổi phiên bản khác trong lúc tìm
        _rows = rows;
        var view = rows.Select(r => new
        {
            projectFile = r.ProjectFile, rel = r.RelativeSource, found = r.SourcePath is not null, fallback = r.FromFallback,
            version = r.FromVersion, sourcePath = r.SourcePath, modified = r.Modified?.ToString("dd/MM/yyyy HH:mm"), destExists = r.DestExists,
        });
        await Js($"grant.onRows({JsonSerializer.Serialize(view)})");
    }

    private async Task GrantAsync(bool overwrite, List<int> indexes)
    {
        var picked = indexes.Where(i => i >= 0 && i < _rows.Count).Select(i => _rows[i]).Where(r => r.SourcePath is not null).ToList();
        if (picked.Count == 0) { await Js("grant.onError('Chưa chọn file nào có source.')"); return; }
        if (!Directory.Exists(_destRoot)) { await Js($"grant.onError({Json("Thư mục dự án không tồn tại: " + _destRoot)})"); return; }

        var (copied, skipped, errors) = await Task.Run(() => SourceGrantService.CopyRows(picked, overwrite));
        if (copied > 0) _onCopied?.Invoke();
        var msg = $"Đã cấp {copied} file" + (skipped > 0 ? $", bỏ qua {skipped} file đã có ở đích (tick “Ghi đè” nếu muốn thay)" : "") +
                  (errors.Count > 0 ? $". Lỗi: {string.Join("; ", errors.Take(3))}" : ".");
        await Js($"grant.onDone({Json(msg)}, {(errors.Count == 0 ? "true" : "false")})");
        // Làm mới nhãn "đã có" ở cột Đích.
        foreach (var r in _rows) r.DestExists = r.DestPath is not null && File.Exists(r.DestPath);
        var view = _rows.Select(r => new
        {
            projectFile = r.ProjectFile, rel = r.RelativeSource, found = r.SourcePath is not null, fallback = r.FromFallback,
            version = r.FromVersion, sourcePath = r.SourcePath, modified = r.Modified?.ToString("dd/MM/yyyy HH:mm"), destExists = r.DestExists,
        });
        await Js($"(function(){{ var s = document.getElementById('status').textContent; grant.onRows({JsonSerializer.Serialize(view)}); grant.onDone({Json(msg)}, {(errors.Count == 0 ? "true" : "false")}); }})()");
    }

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
}
