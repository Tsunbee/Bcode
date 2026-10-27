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
    /// <summary>"raw" = Authorization: &lt;token&gt; (chuẩn LT3); "bearer" = Authorization: Bearer &lt;token&gt;;
    /// "none" = header tĩnh — không lấy token, chỉ gửi <see cref="Headers"/> + header của form (API tự mang Authentication / TaxCode…).</summary>
    public string AuthScheme { get; set; } = "raw";
    /// <summary>Header chung mọi form (form có header cùng tên thì form thắng).</summary>
    public List<ApiHeader> Headers { get; set; } = new();
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
    /// <summary>"raw" (JSON / chữ), "urlencoded", "formdata" (Body = mỗi dòng "key: value", dòng "//…" = tắt), "none" (không body).</summary>
    public string BodyMode { get; set; } = "raw";
    /// <summary>Body raw: "json" (mặc định), "text", "xml", "html", "javascript" — chọn Content-Type mặc định (header Content-Type của form vẫn thắng).</summary>
    public string RawType { get; set; } = "json";
    /// <summary>Header riêng của form (nhập từ Postman) — gộp với <see cref="ApiProject.Headers"/>, form ưu tiên.</summary>
    public List<ApiHeader> Headers { get; set; } = new();
    /// <summary>Tham số query (?key=value) nối vào Endpoint lúc gửi; dòng <see cref="ApiHeader.Disabled"/> bỏ qua. Giá trị thay biến {{today}}… như body.</summary>
    public List<ApiHeader> Params { get; set; } = new();
    /// <summary>Xác thực riêng của form (tab Authorization). null / "inherit" = theo "Kết nối" của dự án (dữ liệu cũ).</summary>
    public ApiAuth? Auth { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>1 dòng key–value: header hoặc tham số query. Header bí mật (Authorization / Authentication / token / key…): giá trị mã hoá DPAPI ở
/// <see cref="ValueProtected"/>, <see cref="Value"/> để trống khi lưu. <see cref="Disabled"/> = giữ dòng nhưng không gửi (như bỏ tick trong Postman).</summary>
public sealed class ApiHeader
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Secret { get; set; }
    public string ValueProtected { get; set; } = "";
    public bool Disabled { get; set; }
}

/// <summary>Xác thực riêng của 1 form (như tab Authorization của Postman). Giá trị bí mật (token / mật khẩu / API key) mã hoá DPAPI ở <see cref="ValueProtected"/>.</summary>
public sealed class ApiAuth
{
    /// <summary>"inherit" (theo Kết nối), "none" (không xác thực — không lấy token), "bearer", "basic", "apikey".</summary>
    public string Type { get; set; } = "inherit";
    /// <summary>basic: tài khoản.</summary>
    public string Username { get; set; } = "";
    /// <summary>apikey: tên header / tham số (vd X-Api-Key).</summary>
    public string KeyName { get; set; } = "";
    /// <summary>apikey: "header" hoặc "query".</summary>
    public string In { get; set; } = "header";
    /// <summary>Token (bearer) / mật khẩu (basic) / giá trị key (apikey) — chỉ dạng chữ khi vừa nhập, lưu thì chuyển sang <see cref="ValueProtected"/>.</summary>
    public string Value { get; set; } = "";
    public string ValueProtected { get; set; } = "";
}
