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
/// <para>Form theo dự án: Project (Workspace) có "API Path" → đọc &lt;API Path&gt;\Config\ColumnMappings.xml (<see cref="ApiColumnMappings"/>, chỉ đọc), form của
/// mapping thay / ghi đè form chuẩn cùng tên (chọn form, Tài liệu form, Kiểm tra, Sinh JSON). Project lấy theo Mã dự án của dự án API (khớp ProjectId / tên WS),
/// Mã dự án trống = Project đang mở trong Bcode. Không có API Path / file → chuẩn LT3 như cũ.</para>
/// <para>Nút "Word ▾" (<see cref="ApiMappingWord"/>): word-export = xuất tài liệu Word mô tả API từ mapping; word-import = đọc Word API mẫu → so với mapping
/// (api.wordImport hiện bảng xem trước); word-apply = ghép form đã chọn → ghi ColumnMappings.xml (sao lưu .bak, chặn nếu file đổi sau lúc nhập) / lưu file khác / chép XML.</para>
/// </summary>
public sealed class ApiDeclarationForm : WebDialogForm
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly List<Workspace> _workspaces;
    private readonly Workspace? _current;
    /// <summary>Mapping đang dùng (theo Mã dự án của dự án API đang mở) và danh mục form đã gộp mapping + chuẩn LT3.</summary>
    private ApiColumnMappings.Info? _map;
    private List<Lt3Templates.FormDoc> _catalog = Lt3Templates.Catalog();
    private string? _mapKey;
    private string _mapProjectId = "";

    /// <summary>Lần "Nhập từ Word" đang xem trước: các form đọc từ Word + nội dung mapping lúc so (để ghép đúng bản đã so, và chặn ghi nếu file vừa bị người khác sửa).</summary>
    private sealed class WordImport
    {
        public string DocPath = "", Target = "", BaseXml = "";
        public bool Exists, Bom = true;
        public DateTime Stamp;
        public long Size;
        public List<ApiMappingWord.MapForm> Forms = new();
    }
    private WordImport? _wi;

    /// <summary>Form dựng sẵn từ menu / file controller ("Tạo cấu trúc API…" ở chuột phải — <see cref="ApiMenuSchema"/>): mở form là tự thêm vào dự án API
    /// của Project đang mở (chưa có thì dự án mới), đứng trước danh mục để Kiểm tra / Tài liệu form / Sinh JSON dùng được.</summary>
    private readonly Lt3Templates.FormDoc? _incoming;
    private readonly string _incomingNote;

    public ApiDeclarationForm(IEnumerable<Workspace>? workspaces = null, Workspace? current = null, Lt3Templates.FormDoc? incoming = null, string incomingNote = "")
        : base("Khai báo & quản lý API (chuẩn LT3)", "apideclaration.html", 1200, 780, 760, 480)
    {
        _workspaces = workspaces?.ToList() ?? new();
        _current = current;
        _incoming = incoming;
        _incomingNote = incomingNote ?? "";
    }

    protected override void OnReady() => _ = ReadyAsync();

    private async Task ReadyAsync()
    {
        try { await UseMappingAsync("", push: false); } catch { /* mạng lỗi: dùng chuẩn LT3 */ }
        PushInit();
        if (_incoming is not null)
        {
            try { await ApplyIncomingAsync(_incoming); }
            catch (Exception ex) { Status("Không thêm được form " + _incoming.Form + ": " + ex.Message, "err"); }
        }
    }

    /// <summary>Danh mục form: form dựng từ menu (nếu có) đứng trước, thắng form cùng tên của mapping / chuẩn LT3.</summary>
    private List<Lt3Templates.FormDoc> WithIncoming(List<Lt3Templates.FormDoc> list) =>
        _incoming is null ? list : list.Where(f => !string.Equals(f.Form, _incoming.Form, StringComparison.OrdinalIgnoreCase)).Prepend(_incoming).ToList();

    /// <summary>Mở dự án API của Project đang mở (khớp Mã dự án / tên) rồi thêm form dựng từ menu; form cùng tên đã có thì thay (chưa lưu — đóng không
    /// Lưu là giữ bản cũ). Không có dự án nào khớp → dự án mới (chưa lưu) chỉ có form này.</summary>
    private async Task ApplyIncomingAsync(Lt3Templates.FormDoc doc)
    {
        var wsId = _current is null ? "" : string.IsNullOrWhiteSpace(_current.ProjectId) ? _current.Name : _current.ProjectId;
        bool Eq(string? a) => wsId.Length > 0 && string.Equals(a?.Trim(), wsId.Trim(), StringComparison.OrdinalIgnoreCase);
        ApiProject? p = null;
        foreach (var name in ApiProjectStore.List())
        {
            var cand = ApiProjectStore.Load(name);
            if (cand is not null && (Eq(cand.ProjectId) || Eq(cand.Name))) { p = cand; break; }
        }
        var isNew = p is null;
        p ??= Lt3Templates.Create(wsId.Length > 0 ? wsId + " API" : "", wsId, "", Array.Empty<string>(), _catalog);
        try { await UseMappingAsync(p.ProjectId); }
        catch { _catalog = WithIncoming(_catalog); }   // không đọc được mapping (mạng): vẫn có form vừa dựng trong danh mục
        var replaced = p.Forms.RemoveAll(f => string.Equals(f.Name, doc.Form, StringComparison.OrdinalIgnoreCase)) > 0;
        PushProject(p, dirty: true);
        Js($"api.addForms({P(new[] { Lt3Templates.ToForm(doc) })})");
        Status((isNew ? $"Chưa có dự án API của {(wsId.Length > 0 ? wsId : "Project này")} — đã tạo dự án mới (nhập địa chỉ, tài khoản rồi Lưu). " : $"Dự án \"{p.Name}\": ")
               + (replaced ? $"đã THAY form {doc.Form} bằng cấu trúc vừa dựng (chưa lưu — đóng không Lưu là giữ bản cũ). " : $"đã thêm form {doc.Form} ({doc.Kind}) — chưa lưu. ")
               + _incomingNote, _incomingNote.Contains('⚠') ? "warn" : "ok");
    }

    private void PushInit() => Js($"window.api && api.init({J(new
    {
        projects = ApiProjectStore.List(),
        folder = ApiProjectStore.Folder,
        oldProfiles = ApiProjectStore.OldProfiles().Count,
        catalog = CatalogView(),
        mapping = MappingView(),
        currentProjectId = _current is null ? "" : string.IsNullOrWhiteSpace(_current.ProjectId) ? _current.Name : _current.ProjectId,
        errors = Lt3Templates.ErrorCodes(),
        docUrl = "http://172.168.5.14/developers/docs/intro/",
    })})");

    private object CatalogView() => _catalog.Select(f => new { f.Form, f.Kind, f.Title, src = f.Source });
    private object MappingView() => new { project = _map?.Project ?? "", file = _map?.File ?? "", count = _map is { Ok: true } m ? m.Forms.Count : 0, error = _map?.Error ?? "" };

    /// <summary>Project của dự án API: khớp ProjectId rồi tên WS; Mã dự án trống = Project đang mở. Không khớp = không có mapping.</summary>
    private Workspace? WorkspaceFor(string projectId)
    {
        projectId = projectId.Trim();
        if (projectId.Length == 0) return _current;
        bool Eq(string? a) => string.Equals(a?.Trim(), projectId, StringComparison.OrdinalIgnoreCase);
        if (_current is not null && (Eq(_current.ProjectId) || Eq(_current.Name))) return _current;
        return _workspaces.FirstOrDefault(w => Eq(w.ProjectId)) ?? _workspaces.FirstOrDefault(w => Eq(w.Name));
    }

    /// <summary>Đọc (nền) ColumnMappings.xml của Project ứng với Mã dự án; đổi so với lần trước thì gửi danh mục mới xuống trang (api.catalog).</summary>
    private async Task UseMappingAsync(string projectId, bool push = true)
    {
        _mapProjectId = projectId;
        var ws = WorkspaceFor(projectId);
        var map = ws is null
            ? new ApiColumnMappings.Info(projectId.Trim(), "", "", new(), $"Mã dự án {projectId.Trim()} không khớp Project nào trong Bcode")
            : await ApiColumnMappings.LoadAsync(ws);
        var key = map.File + "|" + map.Error + "|" + map.Forms.Count + "|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(map.Forms);
        _map = map;
        _catalog = WithIncoming(ApiColumnMappings.Merge(map));
        if (push && key != _mapKey) Js($"api.catalog({J(new { catalog = CatalogView(), mapping = MappingView() })})");
        _mapKey = key;
    }

    private Lt3Templates.FormDoc? Find(string form) => _catalog.FirstOrDefault(f => string.Equals(f.Form, form, StringComparison.OrdinalIgnoreCase));

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        try
        {
            switch (action)
            {
                case "open":
                {
                    var p = ApiProjectStore.Load(Str(msg, "name")) ?? throw new InvalidOperationException("Không đọc được dự án.");
                    await UseMappingAsync(p.ProjectId);
                    PushProject(p);
                    break;
                }
                case "project-id": await UseMappingAsync(Str(msg, "projectId")); break;   // sửa Mã dự án → đổi Project lấy mapping
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
                case "delete": ApiProjectStore.Delete(Str(msg, "name")); _shown = null; PushInit(); Js("api.cleared()"); Status("Đã xoá dự án.", "ok"); break;
                case "new-template":
                {
                    var forms = msg.TryGetProperty("forms", out var fa) && fa.ValueKind == JsonValueKind.Array ? fa.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null;
                    await UseMappingAsync(Str(msg, "projectId"));
                    PushProject(Lt3Templates.Create(Str(msg, "name"), Str(msg, "projectId"), Str(msg, "baseUrl"), forms, _catalog), dirty: true);
                    Status(_map is { Ok: true } ? $"Đã tạo dự án — form theo ColumnMappings.xml của {_map.Project} + chuẩn LT3. Nhập tài khoản rồi Lưu." : "Đã tạo dự án từ mẫu chuẩn LT3 — nhập tài khoản rồi Lưu.", "ok");
                    break;
                }
                case "add-forms":
                {
                    var forms = msg.GetProperty("forms").EnumerateArray().Select(x => Find(x.GetString() ?? "")).OfType<Lt3Templates.FormDoc>().Select(Lt3Templates.ToForm);
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
                case "export-postman": ExportPostman(ReadProject(msg), msg.TryGetProperty("withSecrets", out var ws) && ws.ValueKind == JsonValueKind.True); break;
                case "word-export": await WordExportAsync(msg); break;
                case "word-import": await WordImportAsync(); break;
                case "word-template":
                {
                    var project = Str(msg, "projectId") is { Length: > 0 } pid ? pid : _map?.Project ?? "";
                    using var sfd = new SaveFileDialog { Title = "Lưu file Word mẫu để điền tài liệu API", Filter = "Word (*.docx)|*.docx", FileName = (project.Length > 0 ? project + "_" : "") + "MauTaiLieuAPI.docx" };
                    if (sfd.ShowDialog(DialogOwner()) != DialogResult.OK) break;
                    ApiMappingWord.WriteTemplate(sfd.FileName, new ApiMappingWord.DocInfo(project, "", "", ""));
                    Status("Đã tạo file Word mẫu: " + sfd.FileName + " — điền xong dùng Word ▾ → Nhập từ Word.", "ok");
                    Js($"api.wordExported({J(sfd.FileName)}, 0, {J("Đã tạo file Word mẫu để điền (hướng dẫn + 2 form ví dụ: danh mục, chứng từ có chi tiết)")})");
                    break;
                }
                case "word-apply": await WordApplyAsync(msg); break;
                case "token":
                {
                    var p = ReadProject(msg);
                    if (!ApiClient.UsesToken(p)) { Status("Kiểu xác thực \"Header tĩnh\" — không lấy token, Gửi dùng thẳng header của kết nối / form.", "warn"); Js("api.busy(false)"); break; }
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
                    var issues = ApiClient.Validate(f, f.Body, Find, out var used);
                    Js($"api.issues({J(issues)}, {J(used is null ? "" : ReferenceEquals(used, _incoming) ? "cấu trúc dựng từ menu " + used.Form : used.Source == ApiColumnMappings.Source ? "ColumnMappings.xml của dự án" : "bảng trường chuẩn LT3")})");
                    break;
                }
                case "form-doc": Js($"api.formDoc({J(Find(Str(msg, "form")))})"); break;
                case "gen-body":
                {
                    // Body mẫu sinh theo cấu hình form (mapping của dự án nếu có, không thì request mẫu của tài liệu LT3).
                    var d = Find(Str(msg, "form"));
                    if (d is null) { Status("Form \"" + Str(msg, "form") + "\" không có trong " + (_map is { Ok: true } ? "ColumnMappings.xml của dự án / " : "") + "tài liệu chuẩn LT3 — không sinh được JSON.", "warn"); break; }
                    Js($"api.genBody({J(Lt3Templates.ToForm(d).Body)}, {J(d.Source == ApiColumnMappings.Source ? "ColumnMappings.xml" : "mẫu LT3")})");
                    break;
                }
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
                case "reveal":
                {
                    // Nút 👁 của giá trị bí mật: giải mã bản DPAPI trang đang giữ (chỉ tài khoản Windows này giải được) để xem / sửa.
                    var plain = msg.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array ? vs.EnumerateArray().Select(x => ApiProjectStore.Unprotect(x.GetString() ?? "")).ToList() : new List<string>();
                    Js($"api.revealed({J(Str(msg, "key"))}, {J(plain)})");
                    if (plain.Any(x => x.Length == 0)) Status("Có giá trị bí mật không giải mã được (mã hoá trên máy / tài khoản khác) — nhập lại.", "warn");
                    break;
                }
            }
        }
        catch (Exception ex) { Status(ex.Message, "err"); Js("api.busy(false)"); }
    }

    /// <summary>Dự án / form gửi xuống trang giữ tên thuộc tính PascalCase (trang đọc S.p.Name, S.p.Forms…) — không qua J() (camelCase).</summary>
    private static string P(object value) => JsonSerializer.Serialize(value);

    /// <summary>Dự án đang hiện trên trang (kể cả vừa nhập Postman, chưa lưu) — nguồn mật khẩu mã hoá khi trang gửi lên.</summary>
    private ApiProject? _shown;

    private void PushProject(ApiProject p, bool dirty = false)
    {
        _shown = p;
        Js($"api.project({{\"project\":{P(NoSecret(p))},\"hasPassword\":{(p.PasswordProtected.Length > 0 && ApiProjectStore.Unprotect(p.PasswordProtected).Length > 0 ? "true" : "false")},\"dirty\":{(dirty ? "true" : "false")}}})");
    }

    /// <summary>Gửi xuống trang KHÔNG kèm mật khẩu (kể cả bản mã hoá). Header bí mật chỉ có bản mã hoá DPAPI (ValueProtected), Value để trống.</summary>
    private static object NoSecret(ApiProject p) => new
    {
        p.Name, p.ProjectId, p.Department, p.BaseUrl, p.TokenPath, p.Username, p.TokenBody, p.TokenField, p.ExpiresField, p.AuthScheme, p.Headers, p.DocPath, p.Note, p.Forms,
    };

    /// <summary>Dự án từ trang: giữ mật khẩu mã hoá đã lưu (hoặc của dự án vừa nhập Postman chưa lưu), nhập mới thì mã hoá lại; header bí mật vừa gõ → mã hoá.</summary>
    private ApiProject ReadProject(JsonElement msg)
    {
        var p = msg.GetProperty("project").Deserialize<ApiProject>(ReadOpts) ?? new ApiProject();
        var saved = ApiProjectStore.Load(Str(msg, "originalName").Length > 0 ? Str(msg, "originalName") : p.Name);
        var typed = Str(msg, "password");
        p.PasswordProtected = typed.Length > 0 ? ApiProjectStore.Protect(typed) : _shown?.PasswordProtected is { Length: > 0 } shown ? shown : saved?.PasswordProtected ?? p.PasswordProtected;
        ApiProjectStore.Seal(p);
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

    private void ExportPostman(ApiProject p, bool withSecrets)
    {
        using var sfd = new SaveFileDialog { Filter = "Postman collection (*.postman_collection.json)|*.postman_collection.json", FileName = (string.IsNullOrWhiteSpace(p.ProjectId) ? p.Name : p.ProjectId) + "_API.postman_collection.json" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(sfd.FileName, PostmanConverter.Export(p, withSecrets), new System.Text.UTF8Encoding(false));
        var hasSecret = HasSecret(p);
        Status("Đã xuất Postman: " + sfd.FileName + (ApiClient.UsesToken(p) ? " (không kèm mật khẩu — nhập ở biến password của collection)" : "")
            + (!hasSecret ? "." : withSecrets ? " — CÓ kèm giá trị header bí mật dạng chữ, đừng chia sẻ / commit file này." : " — header bí mật thay bằng biến collection để trống, nhập trong Postman."), hasSecret && withSecrets ? "warn" : "ok");
    }

    // ---------------------------------------------------------------- Word ↔ ColumnMappings.xml (ApiMappingWord)

    /// <summary>Chủ cho hộp chọn file: hộp đang nổi trên cùng (chế độ App: hộp xem trước Nhập Word) hoặc form này.</summary>
    private IWin32Window DialogOwner() => Form.ActiveForm is { IsDisposed: false } f ? f : this;

    /// <summary>Đọc file văn bản không khoá file (service có thể đang đọc), nhớ BOM để ghi lại đúng kiểu.</summary>
    private static (string Text, bool Bom) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        using var sr = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return (sr.ReadToEnd(), bom);
    }

    private static void WriteText(string path, string text, bool bom) => File.WriteAllText(path, text, new System.Text.UTF8Encoding(bom));

    /// <summary>Xuất tài liệu Word mô tả API từ ColumnMappings.xml của dự án (source = "file": chọn file XML khác).</summary>
    private async Task WordExportAsync(JsonElement msg)
    {
        string file, src;
        if (Str(msg, "source") != "file" && _map is { Ok: true } m) { file = m.File; src = "ColumnMappings.xml của " + m.Project; }
        else
        {
            if (Str(msg, "source") != "file") Status((_map?.Error is { Length: > 0 } e ? e + " — " : "") + "chọn file mapping XML để xuất Word.", "warn");
            using var ofd = new OpenFileDialog { Title = "Chọn file mapping (ColumnMappings.xml)", Filter = "Mapping XML (*.xml)|*.xml|Tất cả|*.*" };
            if (ofd.ShowDialog(DialogOwner()) != DialogResult.OK) return;
            file = ofd.FileName; src = Path.GetFileName(file);
        }
        Status("Đang đọc " + file + "…", "");
        var forms = await Task.Run(() => ApiMappingWord.ReadXml(ReadText(file).Text));
        if (forms.Count == 0) { Status("Không có <Form> nào trong " + file + ".", "warn"); return; }
        var project = Str(msg, "projectId") is { Length: > 0 } pid ? pid : _map?.Project ?? "";
        using var sfd = new SaveFileDialog { Title = "Lưu tài liệu API (Word)", Filter = "Word (*.docx)|*.docx", FileName = (project.Length > 0 ? project + "_" : "") + $"TaiLieuAPI_{DateTime.Now:yyyyMMdd}.docx" };
        if (sfd.ShowDialog(DialogOwner()) != DialogResult.OK) { Status("Đã huỷ xuất Word.", ""); return; }
        ApiMappingWord.WriteDocx(sfd.FileName, forms, new ApiMappingWord.DocInfo(project, src, Str(msg, "baseUrl"), Str(msg, "tokenPath")));
        Status($"Đã xuất tài liệu Word ({forms.Count} form): {sfd.FileName}", "ok");
        Js($"api.wordExported({J(sfd.FileName)}, {forms.Count})");
    }

    /// <summary>Đọc tài liệu Word API → so với ColumnMappings.xml của dự án → trang hiện bảng xem trước (form mới / sửa / giống, từng thay đổi) trước khi ghi.</summary>
    private async Task WordImportAsync()
    {
        using var ofd = new OpenFileDialog { Title = "Chọn tài liệu API (Word) để nhập vào ColumnMappings.xml", Filter = "Word (*.docx)|*.docx|Tất cả|*.*" };
        if (ofd.ShowDialog(DialogOwner()) != DialogResult.OK) return;
        Status("Đang đọc " + ofd.FileName + "…", "");
        var target = _map?.File ?? "";
        var wi = await Task.Run(() =>
        {
            var notes = new List<string>();
            var w = new WordImport { DocPath = ofd.FileName, Target = target, Forms = ApiMappingWord.ReadDocx(ofd.FileName, notes) };
            if (target.Length > 0 && File.Exists(target))
            {
                var fi = new FileInfo(target);
                w.Stamp = fi.LastWriteTimeUtc; w.Size = fi.Length; w.Exists = true;
                (w.BaseXml, w.Bom) = ReadText(target);
            }
            return (w, notes, diff: ApiMappingWord.Diff(w.Exists ? w.BaseXml : null, w.Forms));
        });
        if (wi.w.Forms.Count == 0)
        {
            Js($"api.notes({J(wi.notes)})");
            Status("Không thấy form nào trong file Word — cần tiêu đề tên form (vd setItems) và bảng có cột Parameter + Field / CRM Value (+ Data Type, Length, Description, Require…).", "warn");
            return;
        }
        _wi = wi.w;
        var dir = target.Length > 0 ? Path.GetDirectoryName(target) : null;
        Js($"api.wordImport({J(new { doc = ofd.FileName, target, exists = wi.w.Exists, canWrite = dir is { Length: > 0 } && Directory.Exists(dir), project = _map?.Project ?? "", forms = wi.diff, notes = wi.notes })})");
        Status($"Đọc được {wi.w.Forms.Count} form từ Word — xem khác biệt rồi chọn form để ghi.", "ok");
    }

    /// <summary>Ghép các form đã chọn (kèm table / store sửa ở bảng xem trước) — mode: write = ghi thẳng ColumnMappings.xml của dự án (sao lưu .bak trước),
    /// save = lưu thành file XML khác, copy = chép XML các form.</summary>
    private async Task WordApplyAsync(JsonElement msg)
    {
        var wi = _wi;
        if (wi is null) { Status("Chưa nhập Word.", "warn"); return; }
        var mode = Str(msg, "mode");
        var sel = new List<ApiMappingWord.MapForm>();
        if (msg.TryGetProperty("forms", out var fa) && fa.ValueKind == JsonValueKind.Array)
            foreach (var x in fa.EnumerateArray())
            {
                var f = wi.Forms.FirstOrDefault(f => string.Equals(f.Name, Str(x, "name"), StringComparison.OrdinalIgnoreCase));
                if (f is null) continue;
                f.Table = Str(x, "table").Trim(); f.Store = Str(x, "store").Trim();
                sel.Add(f);
            }
        if (sel.Count == 0) { Status("Chưa chọn form nào.", "warn"); return; }
        var baseXml = wi.Exists ? wi.BaseXml : null;
        var bad = ApiMappingWord.Diff(baseXml, sel).Where(d => d.Errors.Count > 0).ToList();
        if (bad.Count > 0) { Status("Chưa ghi — " + string.Join("; ", bad.Select(d => d.Name + ": " + string.Join(", ", d.Errors))) + ".", "err"); return; }
        var merged = ApiMappingWord.Merge(baseXml, sel, out var changes);
        switch (mode)
        {
            case "copy":
                Clipboard.SetText(ApiMappingWord.Fragment(merged, sel.Select(f => f.Name)));
                Status($"Đã chép XML {sel.Count} form (đã ghép với mapping hiện có) — dán vào <Mappings> của ColumnMappings.xml.", "ok");
                break;
            case "save":
            {
                using var sfd = new SaveFileDialog { Title = "Lưu ColumnMappings.xml (đã ghép form từ Word)", Filter = "Mapping XML (*.xml)|*.xml", FileName = "ColumnMappings.xml" };
                if (sfd.ShowDialog(DialogOwner()) != DialogResult.OK) return;
                if (wi.Exists && string.Equals(Path.GetFullPath(sfd.FileName), Path.GetFullPath(wi.Target), StringComparison.OrdinalIgnoreCase))
                { Status("Muốn ghi đè ColumnMappings.xml của dự án thì dùng nút \"Ghi vào ColumnMappings.xml\" (có sao lưu + kiểm tra file vừa đổi).", "warn"); return; }
                WriteText(sfd.FileName, merged, wi.Bom);
                Status($"Đã lưu {sfd.FileName} — {(wi.Exists ? "mapping hiện có + " : "")}{sel.Count} form từ Word ({changes.Count} thay đổi).", "ok");
                break;
            }
            case "write":
            {
                if (wi.Target.Length == 0) { Status("Project chưa khai báo API Path — không biết ghi ColumnMappings.xml ở đâu (dùng \"Lưu file XML…\").", "err"); return; }
                if (changes.Count == 0) { Status("Các form đã chọn giống hệt ColumnMappings.xml — không có gì để ghi.", "warn"); return; }
                var target = wi.Target;
                var outcome = await Task.Run(() =>
                {
                    var fi = new FileInfo(target);
                    if (wi.Exists ? !fi.Exists || fi.LastWriteTimeUtc != wi.Stamp || fi.Length != wi.Size : fi.Exists)
                        return (ok: false, backup: "");
                    var backup = "";
                    if (fi.Exists) { backup = target + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"); File.Copy(target, backup); }
                    WriteText(target, merged, wi.Bom);
                    return (ok: true, backup);
                });
                if (!outcome.ok) { Status("ColumnMappings.xml vừa bị sửa sau lúc nhập Word — chưa ghi. Bấm \"Nhập từ Word…\" lại để so với bản mới.", "err"); return; }
                var nfi = new FileInfo(target);
                wi.BaseXml = merged; wi.Exists = true; wi.Stamp = nfi.LastWriteTimeUtc; wi.Size = nfi.Length;
                await UseMappingAsync(_mapProjectId);
                Status($"Đã ghi {sel.Count} form ({changes.Count} thay đổi) vào {target}" + (outcome.backup.Length > 0 ? " — bản cũ: " + Path.GetFileName(outcome.backup) : "") + ". Service đọc lại file khi cache hết hạn / restart app pool.", "ok");
                Js("api.wordDone()");
                break;
            }
        }
    }

    /// <summary>Dự án có giá trị bí mật (header / tham số đánh dấu bí mật, hoặc xác thực riêng của form có token / mật khẩu / key).</summary>
    private static bool HasSecret(ApiProject p) =>
        p.Headers.Concat(p.Forms.SelectMany(f => f.Headers.Concat(f.Params))).Any(h => h.Secret)
        || p.Forms.Any(f => ApiClient.AuthType(f) is "bearer" or "basic" or "apikey" && f.Auth!.ValueProtected.Length > 0);

    private void Status(string text, string kind) => Js($"api.status({J(text)}, {J(kind)})");
    private static string Mask(string t) => t.Length <= 16 ? t : t[..10] + "…" + t[^6..];
    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
