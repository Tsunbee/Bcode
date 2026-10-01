using System.Diagnostics;
using System.Text.Json;
using System.Web;
using Bcode.App.Services.ExcelToRpt;
using Bcode.App.UI;
using Microsoft.Web.WebView2.Core;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Excel → RPT": giao diện kéo thả của tool Convert (Web/ExcelToRpt/index.html, dùng lại
/// gần như nguyên) chạy trong WebView2, mọi request /api/* do <see cref="RptApiBridge"/> trả
/// lời bằng C# — không còn Python, không mở cổng HTTP. Chỉ RptGenerator.exe (net48 x86, gọi
/// Crystal) còn chạy như tiến trình phụ.
///
/// Trang được phục vụ dưới host ảo https://rpt.bcode/ qua WebResourceRequested (không map
/// thư mục) để cùng một chỗ trả cả file tĩnh lẫn /api/file (PDF xem trước trong iframe).
/// </summary>
public sealed class ExcelToRptControl : UserControl
{
    private const string Origin = "https://rpt.bcode";

    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly RptApiBridge _bridge;
    private readonly Func<string> _defaultOutDir;
    private string? _pendingXlsx;
    private bool _pageReady;

    private static string WebFolder => Path.Combine(AppContext.BaseDirectory, "Web", "ExcelToRpt");

    internal ExcelToRptControl(RptGeneratorRunner generator, TemplateLocator templates,
        Func<string> defaultOutDir, Action<string> rememberOutDir)
    {
        _defaultOutDir = defaultOutDir;
        _bridge = new RptApiBridge(generator, templates, defaultOutDir, rememberOutDir);
        Controls.Add(_web);
        Disposed += (_, _) => _bridge.Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = InitAsync();
    }

    /// <summary>Nạp sẵn một file Excel mẫu in (vd từ File Lookup) — như người dùng tự chọn file.</summary>
    public void LoadExcel(string path)
    {
        _pendingXlsx = path;
        if (_pageReady) PushPendingExcel();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await WebViewEnvironment.GetAsync();
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            ShowFatal("Không khởi tạo được WebView2: " + ex.Message);
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += OnNewWindowRequested;
        core.Navigate(Origin + "/index.html");
    }

    private void ShowFatal(string message)
    {
        Controls.Clear();
        Controls.Add(new Label { Dock = DockStyle.Fill, Text = message, TextAlign = ContentAlignment.MiddleCenter });
    }

    // -----------------------------------------------------------------------
    // File tĩnh + GET /api/file
    // -----------------------------------------------------------------------

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = _web.CoreWebView2;
        var uri = new Uri(e.Request.Uri);
        if (!uri.AbsolutePath.StartsWith("/api/"))
        {
            var rel = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (rel.Length == 0) rel = "index.html";
            var full = Path.GetFullPath(Path.Combine(WebFolder, rel));
            if (!full.StartsWith(WebFolder, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            var ctype = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                _ => "application/octet-stream",
            };
            e.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(File.ReadAllBytes(full)), 200, "OK", $"Content-Type: {ctype}\r\nCache-Control: no-store");
            return;
        }

        using var deferral = e.GetDeferral();
        var resp = await _bridge.HandleAsync(e.Request.Method.ToUpperInvariant(), uri.PathAndQuery, Array.Empty<byte>());
        var headers = $"Content-Type: {resp.ContentType}\r\nCache-Control: no-store";
        if (resp.DownloadName is not null)
            headers += $"\r\nContent-Disposition: attachment; filename*=UTF-8''{Uri.EscapeDataString(resp.DownloadName)}";
        e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(resp.Body), resp.Status,
            resp.Status == 200 ? "OK" : "Error", headers);
    }

    // -----------------------------------------------------------------------
    // Tin nhắn từ bcode-bridge.js
    // -----------------------------------------------------------------------

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement m;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            m = doc.RootElement.Clone();
        }
        catch (JsonException) { return; }

        switch (m.TryGetProperty("kind", out var k) ? k.GetString() : null)
        {
            case "api":
                var id = m.GetProperty("id").GetInt32();
                ApiResponse resp;
                try
                {
                    var body = Convert.FromBase64String(m.GetProperty("body").GetString() ?? "");
                    resp = await _bridge.HandleAsync(m.GetProperty("method").GetString() ?? "POST",
                        m.GetProperty("url").GetString() ?? "", body);
                }
                catch (Exception ex)
                {
                    resp = ApiResponse.Error(500, ex.Message);
                }
                Post(new { kind = "api", id, status = resp.Status, body = resp.BodyText });
                break;

            case "save":
                SaveCopy(m.GetProperty("path").GetString() ?? "", m.GetProperty("name").GetString() ?? "");
                break;

            case "ready":
                _pageReady = true;
                Post(new { kind = "outDir", value = _defaultOutDir() });
                PushPendingExcel();
                break;
        }
    }

    private void Post(object message)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, LayoutJson.Compact));
    }

    private void PushPendingExcel()
    {
        if (_pendingXlsx is null) return;
        var path = _pendingXlsx;
        _pendingXlsx = null;
        try
        {
            Post(new { kind = "loadXlsx", name = Path.GetFileName(path), b64 = Convert.ToBase64String(File.ReadAllBytes(path)) });
        }
        catch (Exception ex)
        {
            Post(new { kind = "log", text = "LỖI: không đọc được " + path + " — " + ex.Message });
        }
    }

    /// <summary>"Tải .rpt về máy" / "Tải PDF": hộp thoại Save As thay cho tải của trình duyệt.</summary>
    private void SaveCopy(string path, string name)
    {
        if (!_bridge.IsReadable(path))
        {
            Post(new { kind = "log", text = "Không tìm thấy file để lưu: " + path });
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(path);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        using var sfd = new SaveFileDialog
        {
            FileName = name,
            Filter = ext == ".pdf" ? "PDF (*.pdf)|*.pdf|Tất cả (*.*)|*.*" : "Crystal Reports (*.rpt)|*.rpt|Tất cả (*.*)|*.*",
        };
        sfd.OverwritePrompt = false;       // tự chặn bên dưới: không cho đè, kể cả khi bấm Yes
        if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (File.Exists(sfd.FileName))
        {
            MessageBox.Show(FindForm(), $"Đã có file \"{Path.GetFileName(sfd.FileName)}\" trong thư mục này — không ghi đè.\n" +
                "Hãy đặt tên khác, hoặc tự xoá/đổi tên file cũ trước.", "Bcode — Excel → RPT",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Post(new { kind = "log", text = "CẢNH BÁO: đã có " + sfd.FileName + " — không ghi đè." });
            return;
        }
        try
        {
            File.Copy(path, sfd.FileName, overwrite: false);
            Post(new { kind = "log", text = "Đã lưu: " + sfd.FileName });
        }
        catch (Exception ex)
        {
            Post(new { kind = "log", text = "LỖI lưu file: " + ex.Message });
        }
    }

    /// <summary>"Mở tab mới" của bản xem trước PDF: mở bằng trình xem PDF mặc định của Windows.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        try
        {
            var uri = new Uri(e.Uri);
            if (uri.AbsolutePath != "/api/file") return;
            var p = HttpUtility.ParseQueryString(uri.Query)["p"];
            if (p is null || !_bridge.IsReadable(p)) return;
            Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Post(new { kind = "log", text = "Không mở được file: " + ex.Message });
        }
    }
}
