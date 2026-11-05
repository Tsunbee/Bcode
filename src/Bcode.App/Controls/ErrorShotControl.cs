using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Tra ảnh lỗi": dán (Ctrl+V) / kéo thả / chọn ảnh chụp màn hình khách gửi → đọc chữ bằng OCR của Windows → đoán màn hình (file controller, menu) và câu
/// chữ trên ảnh nằm ở file / dòng nào (<see cref="ErrorShotService"/>). Miễn phí, chạy trong máy, không gửi ảnh đi đâu. Giao diện: Web/Shell/errshot.html.
/// </summary>
public class ErrorShotControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebBarHost _web = new("errshot.html") { Dock = DockStyle.Fill };
    private readonly Func<Workspace?> _workspace;
    private readonly Func<IReadOnlyList<(WCommandItem Item, string Path)>> _menus;
    private CancellationTokenSource? _cts;
    private string? _lastImage;

    /// <summary>Mở file controller (như File Lookup).</summary>
    public event Action<string>? OpenFileRequested;
    /// <summary>Mở sơ đồ phụ thuộc cho file.</summary>
    public event Action<string>? DependencyRequested;
    /// <summary>Chọn menu (wmenu_id) trên cây WCommand.</summary>
    public event Action<string>? RevealMenuRequested;

    public ErrorShotControl(Func<Workspace?> workspace, Func<IReadOnlyList<(WCommandItem Item, string Path)>> menus)
    {
        _workspace = workspace;
        _menus = menus;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => Handle(root.GetRawText());
        _web.Ready += () => Js($"errshot.init({J(new { ocr = File.Exists(ErrorShotService.OcrExe), workspace = _workspace()?.Name ?? "", source = _workspace()?.SourcePath ?? "" })})");
        Disposed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); };
    }

    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private void Handle(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            switch (root.GetProperty("action").GetString())
            {
                case "image": _ = RunFromDataUrlAsync(Str("data")); break;
                case "pick": PickFile(); break;
                case "rerun": if (_lastImage is not null) _ = RunAsync(_lastImage, forceIndex: root.TryGetProperty("reindex", out var ri) && ri.ValueKind == JsonValueKind.True); break;
                case "openFile": OpenFileRequested?.Invoke(Str("path")); break;
                case "deps": DependencyRequested?.Invoke(Str("path")); break;
                case "menu": RevealMenuRequested?.Invoke(Str("id")); break;
                case "copy": try { Clipboard.SetText(Str("text")); } catch { /* clipboard bận */ } break;
            }
        }
        catch { /* tin nhắn lỗi — bỏ qua */ }
    }

    private static string TempDir
    {
        get { var d = Path.Combine(Path.GetTempPath(), "Bcode", "errshot"); Directory.CreateDirectory(d); return d; }
    }

    private async Task RunFromDataUrlAsync(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            var bytes = Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl);
            var ext = dataUrl.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : dataUrl.StartsWith("data:image/bmp", StringComparison.OrdinalIgnoreCase) ? ".bmp" : ".png";
            var path = Path.Combine(TempDir, "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ext);
            await File.WriteAllBytesAsync(path, bytes);
            await RunAsync(path, forceIndex: false);
        }
        catch (Exception ex) { Js($"errshot.onError({J("Không đọc được ảnh: " + ex.Message)})"); }
    }

    private void PickFile()
    {
        using var ofd = new OpenFileDialog { Filter = "Ảnh (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Tất cả|*.*", Title = "Chọn ảnh chụp màn hình lỗi" };
        if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            var ext = Path.GetExtension(ofd.FileName).TrimStart('.').ToLowerInvariant();
            var mime = ext is "jpg" or "jpeg" ? "image/jpeg" : ext is "bmp" ? "image/bmp" : ext is "gif" ? "image/gif" : "image/png";
            if (new FileInfo(ofd.FileName).Length <= 12_000_000)
                Js($"errshot.showImage({J("data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(ofd.FileName)))})");
        }
        catch { /* chỉ là ảnh xem trước */ }
        _ = RunAsync(ofd.FileName, forceIndex: false);
    }

    private async Task RunAsync(string imagePath, bool forceIndex)
    {
        _lastImage = imagePath;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        var ws = _workspace();
        Js("errshot.onStart()");
        try
        {
            var ocr = await ErrorShotService.RunOcrAsync(imagePath, ct);
            if (ct.IsCancellationRequested) return;
            Js($"errshot.onOcr({J(new { lang = ocr.Lang, ms = ocr.Ms, width = ocr.Width, height = ocr.Height, lines = ocr.Lines })})");
            if (ws is null || string.IsNullOrWhiteSpace(ws.SourcePath))
            {
                Js($"errshot.onError({J(ws is null ? "Chưa chọn project — chọn project (có Source Path) để đối chiếu với source." : "Project " + ws.Name + " chưa khai Source Path (Edit Project) — chưa đối chiếu được với source.")})");
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var menus = _menus();
            var res = await ErrorShotService.MatchAsync(ws.SourcePath, ocr.Lines, menus, forceIndex, msg => Js($"errshot.onProgress({J(msg)})"), ct);
            if (ct.IsCancellationRequested) return;
            Js($"errshot.onResult({J(new { screens = res.Screens, texts = res.Texts, note = res.Note, ms = sw.ElapsedMilliseconds, menus = menus.Count, @fixed = res.Fixed })})");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Js($"errshot.onError({J(ex.Message)})"); }
    }
}
