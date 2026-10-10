using System.Text.Json;

namespace Bcode.App.Controls;

/// <summary>
/// Một trang Web/Shell/*.html mà code C# nói chuyện cùng — dù trang chiếm nguyên 1 WebView2 (<see cref="WebBarHost"/>) hay chỉ là 1 khung
/// iframe trong WebView2 dùng chung (<see cref="WebFrameHost"/>, để bớt RAM). Code dùng trang chỉ phụ thuộc vào giao diện này.
/// </summary>
public interface IWebPage
{
    /// <summary>Trang đã nạp xong — trước đó <see cref="Call"/>/<see cref="PostJson"/> không có tác dụng.</summary>
    bool IsReady { get; }

    /// <summary>Mỗi tin nhắn trang gửi (JSON đã parse; chỉ hợp lệ trong lúc xử lý).</summary>
    event Action<JsonElement>? Message;

    /// <summary>Trang vừa nạp xong (đã đẩy theme).</summary>
    event Action? Ready;

    /// <summary>Chạy 1 đoạn script trong trang.</summary>
    void Call(string script);

    /// <summary>Gửi JSON lớn cho trang (nhận bằng <c>chrome.webview.addEventListener('message')</c>). false = trang chưa sẵn sàng.</summary>
    bool PostJson(string json);
}
