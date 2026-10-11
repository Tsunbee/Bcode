using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services.Api;

namespace Bcode.App.Forms;

/// <summary>
/// "Khai báo &amp; quản lý API" theo chuẩn phòng LT3 (trang Web/Shell/apideclaration.html, nền <see cref="WebDialogForm"/> — WebView2 dùng chung, ăn Template, tự co giãn).
/// Dự án = địa chỉ gốc + tài khoản (mật khẩu mã hoá DPAPI) + danh sách form; tạo từ mẫu chuẩn LT3 (Templates/Api/lt3-standard.json — 31 form của tài liệu
/// http://172.168.5.14/developers/docs/), nhập / xuất Postman, lấy token (nhớ tới khi hết hạn), gửi thử từng form, kiểm tra body theo bảng trường chuẩn,
/// xem response dạng bảng. Hồ sơ lưu riêng trên máy từng người (<see cref="ApiProjectStore"/>). Mọi thông báo hiện trong trang.
/// </summary>
public sealed class ApiDeclarationForm : WebDialogForm
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    public ApiDeclarationForm() : base("Khai báo & quản lý API (chuẩn LT3)", "apideclaration.html", 1200, 780, 760, 480) { }

    protected override void OnReady() => PushInit();

    private void PushInit() => Js($"window.api && api.init({J(new
    {
        projects = ApiProjectStore.List(),
        folder = ApiProjectStore.Folder,
        oldProfiles = ApiProjectStore.OldProfiles().Count,
        catalog = Lt3Templates.Catalog().Select(f => new { f.Form, f.Kind, f.Title }),
        errors = Lt3Templates.ErrorCodes(),
        docUrl = "http://172.168.5.14/developers/docs/intro/",
    })})");

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        try
        {
            switch (action)
            {
                case "open": PushProject(ApiProjectStore.Load(Str(msg, "name")) ?? throw new InvalidOperationException("Không đọc được dự án.")); break;
                case "save":
                {
                    var p = ReadProject(msg);
                    var old = Str(msg, "originalName");
                    ApiProjectStore.Save(p);
                    if (old.Length > 0 && !string.Equals(old, p.Name, StringComparison.CurrentCultureIgnoreCase)) ApiProjectStore.Delete(old);
                    PushInit(); PushProject(p);
                    Status("Đã lưu dự án \"" + p.Name + "\" (" + ApiProjectStore.Folder + ").", "ok");
                    break;
                }
                case "delete": ApiProjectStore.Delete(Str(msg, "name")); PushInit(); Js("api.cleared()"); Status("Đã xoá dự án.", "ok"); break;
                case "new-template":
                {
                    var forms = msg.TryGetProperty("forms", out var fa) && fa.ValueKind == JsonValueKind.Array ? fa.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null;
                    PushProject(Lt3Templates.Create(Str(msg, "name"), Str(msg, "projectId"), Str(msg, "baseUrl"), forms), dirty: true);
                    Status("Đã tạo dự án từ mẫu chuẩn LT3 — nhập tài khoản rồi Lưu.", "ok");
                    break;
                }
                case "add-forms":
                {
                    var forms = msg.GetProperty("forms").EnumerateArray().Select(x => Lt3Templates.Find(x.GetString() ?? "")).OfType<Lt3Templates.FormDoc>().Select(Lt3Templates.ToForm);
                    Js($"api.addForms({P(forms)})");
                    break;
                }
                case "import-postman": ImportPostman(); break;
                case "import-old":
                {
                    var n = 0;
                    foreach (var f in ApiProjectStore.OldProfiles()) { try { ApiProjectStore.Save(ApiProjectStore.ConvertOld(f)); n++; } catch { /* hồ sơ hỏng */ } }
                    PushInit(); Status($"Đã chuyển {n} hồ sơ cũ (mật khẩu chuyển sang mã hoá). Hồ sơ cũ vẫn giữ ở thư mục ApiProfiles.", "ok");
                    break;
                }
                case "export-postman": ExportPostman(ReadProject(msg)); break;
                case "token":
                {
                    var p = ReadProject(msg);
                    Status("Đang lấy token…", "");
                    var t = await ApiClient.GetTokenAsync(p, force: true);
                    Js($"api.token({J(new { masked = Mask(t.Token), full = t.Token, expires = t.ExpiresUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") })})");
                    Status("Lấy token thành công — hết hạn " + t.ExpiresUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm") + ".", "ok");
                    break;
                }
                case "send":
                {
                    var p = ReadProject(msg);
                    var f = p.Forms.ElementAtOrDefault(msg.TryGetProperty("index", out var ix) ? ix.GetInt32() : -1) ?? throw new InvalidOperationException("Chưa chọn form.");
                    if (f.Kind is "SyncData" or "SyncVoucher" && !(msg.TryGetProperty("confirmWrite", out var cw) && cw.ValueKind == JsonValueKind.True))
                    { Js($"api.confirmWrite({J(f.Name)})"); break; }   // form GHI dữ liệu vào Fast: trang hỏi xác nhận trước
                    Status("Đang gửi " + f.Name + "…", "");
                    var r = await ApiClient.SendAsync(p, f, f.Body);
                    Js($"api.result({J(r)})");
                    Status($"{f.Name}: HTTP {r.Status} · {r.Ms} ms" + (r.Message is null ? "" : " · " + r.Message), r.Ok ? "ok" : "err");
                    break;
                }
                case "validate":
                {
                    var f = new ApiForm { Name = Str(msg, "name"), Body = Str(msg, "body") };
                    Js($"api.issues({J(ApiClient.Validate(f, f.Body))})");
                    break;
                }
                case "form-doc": Js($"api.formDoc({J(Lt3Templates.Find(Str(msg, "form")))})"); break;
                case "browse-doc":
                {
                    using var ofd = new OpenFileDialog { Filter = "Tài liệu (*.docx;*.pdf;*.json;*.xml;*.xlsx)|*.docx;*.pdf;*.json;*.xml;*.xlsx|Tất cả|*.*" };
                    if (ofd.ShowDialog(this) == DialogResult.OK) Js($"api.docPath({J(ofd.FileName)})");
                    break;
                }
                case "open-path":
                {
                    var path = Str(msg, "path");
                    if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || File.Exists(path) || Directory.Exists(path))
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    else Status("Không thấy: " + path, "err");
                    break;
                }
                case "copy": Clipboard.SetText(Str(msg, "text")); Status("Đã chép.", "ok"); break;
            }
        }
        catch (Exception ex) { Status(ex.Message, "err"); Js("api.busy(false)"); }
    }

    /// <summary>Dự án / form gửi xuống trang giữ tên thuộc tính PascalCase (trang đọc S.p.Name, S.p.Forms…) — không qua J() (camelCase).</summary>
    private static string P(object value) => JsonSerializer.Serialize(value);

    private void PushProject(ApiProject p, bool dirty = false) =>
        Js($"api.project({{\"project\":{P(NoSecret(p))},\"hasPassword\":{(p.PasswordProtected.Length > 0 && ApiProjectStore.Unprotect(p.PasswordProtected).Length > 0 ? "true" : "false")},\"dirty\":{(dirty ? "true" : "false")}}})");

    /// <summary>Gửi xuống trang KHÔNG kèm mật khẩu (kể cả bản mã hoá).</summary>
    private static object NoSecret(ApiProject p) => new
    {
        p.Name, p.ProjectId, p.Department, p.BaseUrl, p.TokenPath, p.Username, p.TokenBody, p.TokenField, p.ExpiresField, p.AuthScheme, p.DocPath, p.Note, p.Forms,
    };

    /// <summary>Dự án từ trang: giữ mật khẩu mã hoá đã lưu, nhập mới thì mã hoá lại.</summary>
    private static ApiProject ReadProject(JsonElement msg)
    {
        var p = msg.GetProperty("project").Deserialize<ApiProject>(ReadOpts) ?? new ApiProject();
        var saved = ApiProjectStore.Load(Str(msg, "originalName").Length > 0 ? Str(msg, "originalName") : p.Name);
        var typed = Str(msg, "password");
        p.PasswordProtected = typed.Length > 0 ? ApiProjectStore.Protect(typed) : saved?.PasswordProtected ?? p.PasswordProtected;
        return p;
    }

    private void ImportPostman()
    {
        using var ofd = new OpenFileDialog { Filter = "Postman collection (*.json)|*.json|Tất cả|*.*", Multiselect = false };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        var r = PostmanConverter.Import(File.ReadAllText(ofd.FileName), Path.GetFileNameWithoutExtension(ofd.FileName));
        PushProject(r.Project, dirty: true);
        Js($"api.notes({J(r.Notes)})");
        Status($"Đã nhập {r.Project.Forms.Count} form từ Postman — kiểm tra rồi Lưu.", "ok");
    }

    private void ExportPostman(ApiProject p)
    {
        using var sfd = new SaveFileDialog { Filter = "Postman collection (*.postman_collection.json)|*.postman_collection.json", FileName = (string.IsNullOrWhiteSpace(p.ProjectId) ? p.Name : p.ProjectId) + "_API.postman_collection.json" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(sfd.FileName, PostmanConverter.Export(p), new System.Text.UTF8Encoding(false));
        Status("Đã xuất Postman: " + sfd.FileName + " (không kèm mật khẩu — nhập ở biến password của collection).", "ok");
    }

    private void Status(string text, string kind) => Js($"api.status({J(text)}, {J(kind)})");
    private static string Mask(string t) => t.Length <= 16 ? t : t[..10] + "…" + t[^6..];
    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
