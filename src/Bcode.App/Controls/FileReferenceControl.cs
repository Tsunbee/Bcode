using System.Text.Json;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Controls;

/// <summary>
/// "File Reference" tool UI — ô Root + ô tìm + danh sách mọi file/dòng dưới App_Data có chứa từ khoá (tìm theo NỘI DUNG, khác File
/// Lookup tìm theo TÊN file). Nhấp đúp một kết quả để mở file tại dòng đó.
///
/// Toàn bộ giao diện là 1 trang WebView2 (Web/Shell/filereference.html), tự co giãn theo cỡ tab; kết quả gom theo file, từ khoá được
/// tô sáng. Việc quét file (qua UNC) chạy ở luồng nền, không treo cửa sổ như bản ListView cũ. API ngoài giữ nguyên:
/// <see cref="SetRootPath"/>, <see cref="SearchFor"/>, <see cref="FileActivated"/>.
/// </summary>
public class FileReferenceControl : UserControl
{
    private const int MaxResults = 500; // chặn từ khoá quá phổ biến trên cây source rất lớn

    private readonly FileReferenceService _service;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _ready;
    private string _root = "";
    private string? _pendingTerm;
    private CancellationTokenSource? _cts;

    public event Action<string, int>? FileActivated; // path, line number

    public FileReferenceControl(FileReferenceService service)
    {
        _service = service;
        Dock = DockStyle.Fill;
        Controls.Add(_web);

        ThemeManager.ThemeChanged += PushTheme;
        Disposed += (_, _) => { ThemeManager.ThemeChanged -= PushTheme; _cts?.Cancel(); };
        HandleCreated += async (_, _) => await InitWebAsync();
    }

    private bool _initStarted;

    private async Task InitWebAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate($"https://{WebViewEnvironment.Host}/filereference.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không khởi tạo được giao diện File Reference (WebView2): " + ex.Message, "Bcode — WebView2",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void SetRootPath(string path)
    {
        _root = path ?? "";
        if (_ready) Js($"window.setRoot({Json(_root)})");
    }

    /// <summary>Used from the WCommand tree / File Lookup: pre-fill the search term and run.</summary>
    public void SearchFor(string term)
    {
        if (!_ready) { _pendingTerm = term; return; }
        Js($"window.setTerm({Json(term)})");
        _ = SearchAsync(_root, term);
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
                case "ready":
                    _ready = true;
                    PushTheme();
                    Js($"window.setRoot({Json(_root)})");
                    if (_pendingTerm is { } t) { var term = t; _pendingTerm = null; SearchFor(term); }
                    break;
                case "search":
                    await SearchAsync(Str(data, "root"), Str(data, "term"));
                    break;
                case "browse":
                    BrowseRoot(Str(data, "root"));
                    break;
                case "open":
                    if (Str(data, "path") is { Length: > 0 } path)
                        FileActivated?.Invoke(path, data.TryGetProperty("line", out var ln) && ln.TryGetInt32(out var n) ? n : 1);
                    break;
            }
        }
        catch (Exception ex)
        {
            Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
    }

    private void BrowseRoot(string current)
    {
        using var dialog = new FolderBrowserDialog { Description = "Chọn thư mục gốc để tìm tham chiếu (App_Data)" };
        if (Directory.Exists(current)) dialog.SelectedPath = current;
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) { _root = dialog.SelectedPath; Js($"window.setRoot({Json(_root)})"); }
    }

    private async Task SearchAsync(string root, string term)
    {
        root = root.Trim();
        term = term.Trim();
        _root = root;
        if (root.Length == 0 || term.Length == 0)
        {
            Js("window.setResults([], 'Nhập Root và từ khoá cần tìm.', '', null)");
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        Js("window.setBusy(true)");
        try
        {
            var found = await Task.Run(() =>
            {
                var list = new List<object>();
                foreach (var m in _service.FindReferences(root, term))
                {
                    token.ThrowIfCancellationRequested();
                    list.Add(new { path = m.FilePath, line = m.LineNumber, text = m.LineText });
                    if (list.Count >= MaxResults) break;
                }
                return list;
            }, token);
            if (token.IsCancellationRequested) return;

            var message = $"{found.Count} chỗ tham chiếu tới \"{term}\"" + (found.Count >= MaxResults ? $" (đã dừng ở {MaxResults} kết quả đầu)" : "") + ".";
            Js($"window.setResults({JsonSerializer.Serialize(found)}, {Json(message)}, {(found.Count > 0 ? "'ok'" : "''")}, {Json(term)})");
        }
        catch (OperationCanceledException) { /* đã có lần tìm mới hơn */ }
        catch (Exception ex)
        {
            Js($"window.setResults([], {Json("Lỗi khi tìm: " + ex.Message)}, 'err', null)");
        }
        finally
        {
            if (!token.IsCancellationRequested) Js("window.setBusy(false)");
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private void Js(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void PushTheme() => Js($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
}
