namespace Bcode.App.Models;

/// <summary>
/// 1 dự án API theo chuẩn phòng LT3 (xem Services/Api/Lt3Templates.cs): địa chỉ gốc <c>&lt;host&gt;/&lt;DỰ_ÁN&gt;_API</c>, lấy token ở <c>api/getToken</c>
/// (body {username, password}, token ở <c>token.accesstoken</c>, hạn ở <c>token.expires</c>), gửi token TRẦN ở header Authorization (không "Bearer"),
/// mỗi chức năng là 1 <see cref="ApiForm"/> (thường cùng endpoint <c>api/getData</c> / <c>api/SyncVoucher</c>, phân biệt bằng "form" trong body).
/// Lưu riêng trên máy từng người: %AppData%\Bcode\ApiProjects\&lt;Name&gt;.json; mật khẩu mã hoá DPAPI (chỉ tài khoản Windows này giải được).
/// </summary>
public sealed class ApiProject
{
    public string Name { get; set; } = "";
    /// <summary>Mã dự án (vd NISSHIN, TADA).</summary>
    public string ProjectId { get; set; } = "";
    /// <summary>Bộ phận xử lý (mặc định LT3).</summary>
    public string Department { get; set; } = "LT3";
    /// <summary>Địa chỉ gốc, vd https://dev.fast.com.vn/NISSHIN_FBO_API (không có "/api/...").</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Đường dẫn lấy token (tương đối so với BaseUrl, hoặc đầy đủ http...).</summary>
    public string TokenPath { get; set; } = "api/getToken";
    public string Username { get; set; } = "";
    /// <summary>Mật khẩu đã mã hoá DPAPI (base64). Không bao giờ lưu mật khẩu dạng chữ.</summary>
    public string PasswordProtected { get; set; } = "";
    /// <summary>Body lấy token; {{username}} / {{password}} được thay lúc gửi.</summary>
    public string TokenBody { get; set; } = "{\n    \"username\": \"{{username}}\",\n    \"password\": \"{{password}}\"\n}";
    /// <summary>Đường dẫn tới token trong response (vd token.accesstoken). Rỗng/không thấy = tự dò.</summary>
    public string TokenField { get; set; } = "token.accesstoken";
    public string ExpiresField { get; set; } = "token.expires";
    /// <summary>"raw" = Authorization: &lt;token&gt; (chuẩn LT3); "bearer" = Authorization: Bearer &lt;token&gt;.</summary>
    public string AuthScheme { get; set; } = "raw";
    /// <summary>File tài liệu đặc tả đính kèm (docx / json / xml).</summary>
    public string DocPath { get; set; } = "";
    public string Note { get; set; } = "";
    public List<ApiForm> Forms { get; set; } = new();
}

/// <summary>1 chức năng API: endpoint + body mẫu. Body hỗ trợ biến thay lúc gửi: {{today}} (yyyy-MM-dd), {{now}}, {{key}} (khoá duy nhất), {{year}}, {{month}}.</summary>
public sealed class ApiForm
{
    public string Name { get; set; } = "";
    /// <summary>"getData" (đọc dữ liệu FBO), "SyncVoucher" (đẩy chứng từ vào FBO), "custom".</summary>
    public string Kind { get; set; } = "getData";
    public string Endpoint { get; set; } = "api/getData";
    public string Method { get; set; } = "POST";
    public string Body { get; set; } = "{\n    \"form\": \"\"\n}";
    public string Description { get; set; } = "";
}
