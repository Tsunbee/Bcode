using System.Diagnostics;
using System.Text.Json;

namespace Bcode.App.Controls;

/// <summary>Tab "Trợ giúp": cây tính năng (Nội dung / Chỉ mục / Tìm kiếm) + hướng dẫn sử dụng của tính năng đang chọn — kiểu file CHM.
/// Trang WebView2 Web/Shell/help.html (cả chế độ App, chưa có bản native). Nội dung là file Markdown <see cref="HelpFilePath"/>
/// (bản gốc docs/HuongDanSuDung.md, chép kèm exe lúc build): sửa file rồi bấm "Tải lại" là đổi nội dung, không cần build.</summary>
public class HelpControl : UserControl
{
    private readonly WebBarHost _web = new("help.html") { Dock = DockStyle.Fill };
    private string? _topic;

    /// <summary>File hướng dẫn cạnh Bcode.exe.</summary>
    public static string HelpFilePath => Path.Combine(AppContext.BaseDirectory, "Help", "HuongDanSuDung.md");

    /// <param name="topic">id mục mở sẵn (dòng &lt;a id="..."&gt; trong file .md), null = mục đầu.</param>
    public HelpControl(string? topic = null)
    {
        Dock = DockStyle.Fill;
        _topic = topic;
        Controls.Add(_web);
        _web.Ready += () => Push(_topic);
        _web.Message += OnMessage;
    }

    /// <summary>Chuyển sang mục <paramref name="topic"/> (tab Trợ giúp đã mở sẵn).</summary>
    public void ShowTopic(string? topic)
    {
        _topic = topic;
        if (_web.IsReady) _web.Call($"help.show({WebBarHost.Json(topic)})");
    }

    private void Push(string? topic)
    {
        object data;
        try { data = new { md = File.ReadAllText(HelpFilePath), topic }; }
        catch (Exception ex) { data = new { error = $"Không đọc được file hướng dẫn {HelpFilePath}: {ex.Message}" }; }
        _web.Call($"help.init({JsonSerializer.Serialize(data)})");
    }

    /// <summary>Ảnh chụp màn hình trong bài (![chú thích](images/x.png)): đọc từ thư mục Help (bản gốc docs/images, chép kèm exe) rồi gửi cho trang
    /// dạng data URL — trang không tự đọc được file ngoài thư mục Web/Shell. Chỉ nhận đường dẫn nằm trong thư mục Help.</summary>
    private void SendImage(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return;
        string data = "";
        try
        {
            var dir = Path.GetFullPath(Path.GetDirectoryName(HelpFilePath)!);
            var file = Path.GetFullPath(Path.Combine(dir, src.Replace('/', Path.DirectorySeparatorChar)));
            var mime = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => null,
            };
            if (mime is not null && file.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                data = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}";
        }
        catch { /* ảnh lỗi → trang hiện chữ "thiếu ảnh" */ }
        _web.Call($"help.img({WebBarHost.Json(src)}, {WebBarHost.Json(data)})");
    }

    private void OnMessage(JsonElement root)
    {
        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        switch (action)
        {
            case "reload":
                Push(null);
                break;
            case "open-file":
                // .md thường chưa gắn chương trình mở → không mở được thì dùng Notepad.
                try { Process.Start(new ProcessStartInfo(HelpFilePath) { UseShellExecute = true }); }
                catch
                {
                    try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + HelpFilePath + "\"") { UseShellExecute = true }); }
                    catch (Exception ex) { MessageBox.Show(this, "Không mở được file hướng dẫn:\n" + ex.Message, "Bcode — Trợ giúp"); }
                }
                break;
            case "img":
                SendImage(root.TryGetProperty("src", out var s) ? s.GetString() : null);
                break;
            case "open-url":
                var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (url is not null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* không có trình duyệt mặc định */ }
                break;
        }
    }
}
