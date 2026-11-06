using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Xem chứng từ" của Tạo chứng từ: dựng màn hình Dir của chứng từ mới bằng chính dirpreview.js của BcodeViewer (Web/Shell/voucherview.html),
/// file đọc từ <see cref="VoucherOverlay"/> nên thấy đúng kết quả sau khi tạo mà chưa ghi gì xuống đĩa.
/// </summary>
public class VoucherPreviewForm : WebDialogForm
{
    private readonly VoucherOverlay _overlay;
    private readonly IReadOnlyList<string> _dirs;

    /// <param name="dirs">File Dir đích (tương đối so với gốc source), file chính đứng đầu.</param>
    public VoucherPreviewForm(VoucherOverlay overlay, IReadOnlyList<string> dirs, string title)
        : base(title, "voucherview.html", 1200, 800, 700, 480)
    {
        _overlay = overlay;
        _dirs = dirs;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimizeBox = true;
        ShowInTaskbar = true;
        if (Bcode.App.UI.AppIcons.AppIcon is { } icon) { Icon = icon; ShowIcon = true; }
    }

    protected override void OnReady() =>
        Js($"vv.init({J(_dirs.Select(d => new { name = Path.GetFileName(d), path = _overlay.PathOf(d) }))})");

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        if (action != "host") return;
        var id = msg.GetProperty("id").GetInt32();
        var method = msg.GetProperty("method").GetString() ?? "";
        var args = msg.GetProperty("args").EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
        try
        {
            var result = await Task.Run(() => Call(method, args));
            Js($"vv.onHost({id}, true, {J(result)})");
        }
        catch (Exception ex) { Js($"vv.onHost({id}, false, {J(ex.Message)})"); }
    }

    /// <summary>Các lệnh đọc file mà entity.js / dirpreview.js gọi. Lệnh khác (đọc cả cây include, tìm toàn source) báo lỗi để 2 file đó tự đọc lần lượt.</summary>
    private string Call(string method, string[] args)
    {
        switch (method)
        {
            case "BeginReadFile":
                return _overlay.Read(args[0]) ?? throw new FileNotFoundException(args[0]);
            case "BeginReadFiles":
            {
                var paths = JsonSerializer.Deserialize<string[]>(args[0]) ?? Array.Empty<string>();
                _overlay.Preload(paths);
                return JsonSerializer.Serialize(paths.Distinct().ToDictionary(p => p, _overlay.Read));
            }
            case "BeginPathsExist":
                return JsonSerializer.Serialize((JsonSerializer.Deserialize<string[]>(args[0]) ?? Array.Empty<string>()).Select(_overlay.Exists));
            default:
                throw new NotSupportedException(method);
        }
    }
}
