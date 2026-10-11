using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services.Api;

/// <summary>
/// Postman collection v2.1 ⇄ <see cref="ApiProject"/> theo chuẩn LT3.
/// Nhập: request có đường dẫn …/getToken → địa chỉ gốc (phần trước "/api/"), tài khoản (mật khẩu mã hoá DPAPI); các request khác → form (tên request, endpoint, body).
/// Token cũ dán sẵn trong header Authorization bị BỎ. Body "{{payload}}" sinh bằng script trước request thì không đọc được — báo lại để dùng request cùng loại.
/// Xuất: biến base_url / username / password (để trống) / token; request getToken tự lưu token.accesstoken vào {{token}}; script cấp collection sinh {{key}} {{today}}…;
/// header "Authorization: {{token}}" (hoặc "Bearer {{token}}"); form gom thư mục theo SyncData / SyncVoucher / GetData.
/// </summary>
public static class PostmanConverter
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public sealed record ImportResult(ApiProject Project, List<string> Notes);

    public static ImportResult Import(string json, string fallbackName)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("File rỗng.");
        var notes = new List<string>();
        var name = root["info"]?["name"]?.GetValue<string>() ?? fallbackName;
        var p = new ApiProject { Name = name, ProjectId = Regex.Replace(name, @"[_\- ]?(FBO|FBI|API)$", "", RegexOptions.IgnoreCase), Department = "LT3", Forms = new() };
        var requests = new List<(string Name, string Url, string Body, string Auth)>();
        Collect(root["item"] as JsonArray, requests);

        var tokenReq = requests.FirstOrDefault(r => Regex.IsMatch(r.Url, @"/gettoken\b", RegexOptions.IgnoreCase));
        string baseUrl = "";
        if (tokenReq.Url is not null)
        {
            var i = tokenReq.Url.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            baseUrl = i > 0 ? tokenReq.Url[..i] : tokenReq.Url;
            p.TokenPath = i > 0 ? tokenReq.Url[(i + 1)..] : "api/getToken";
            try
            {
                var tb = JsonNode.Parse(tokenReq.Body);
                p.Username = tb?["username"]?.GetValue<string>() ?? "";
                var pw = tb?["password"]?.GetValue<string>() ?? "";
                if (pw.Length > 0) { p.PasswordProtected = ApiProjectStore.Protect(pw); notes.Add("Đã lấy tài khoản từ request getToken — mật khẩu lưu mã hoá trên máy này."); }
            }
            catch { notes.Add("Body getToken không phải JSON — nhập tài khoản tay."); }
        }
        else
        {
            var first = requests.FirstOrDefault(r => r.Url.Contains("/api/", StringComparison.OrdinalIgnoreCase));
            if (first.Url is not null) baseUrl = first.Url[..first.Url.IndexOf("/api/", StringComparison.OrdinalIgnoreCase)];
            notes.Add("Không có request getToken — dùng mặc định api/getToken.");
        }
        p.BaseUrl = baseUrl;

        var tokens = 0;
        foreach (var r in requests)
        {
            if (ReferenceEquals(r.Url, tokenReq.Url) && r.Name == tokenReq.Name) continue;
            if (r.Auth.Length > 0) tokens++;
            if (r.Body.Trim() == "{{payload}}") { notes.Add($"\"{r.Name}\": body sinh bằng script Postman — bỏ qua (dùng request cùng form có body sẵn)."); continue; }
            var endpoint = r.Url.StartsWith(baseUrl + "/", StringComparison.OrdinalIgnoreCase) && baseUrl.Length > 0 ? r.Url[(baseUrl.Length + 1)..] : r.Url;
            string body = r.Body, formName = r.Name;
            try
            {
                var node = JsonNode.Parse(r.Body);
                body = node?.ToJsonString(Pretty) ?? r.Body;
                formName = node?["form"]?.GetValue<string>() ?? r.Name;
            }
            catch { /* body không phải JSON — giữ nguyên */ }
            if (baseUrl.Length > 0 && endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(r.Url, UriKind.Absolute, out var other))
                notes.Add($"\"{r.Name}\" gọi máy chủ khác ({other.Authority}) — giữ nguyên địa chỉ đầy đủ.");
            var std = Lt3Templates.Find(formName);
            p.Forms.Add(new ApiForm
            {
                Name = p.Forms.Any(f => f.Name == r.Name) ? r.Name + " (" + (p.Forms.Count + 1) + ")" : r.Name,
                Kind = KindOf(endpoint),
                Endpoint = endpoint,
                Body = body,
                Description = std is null ? "Form riêng của dự án (không có trong tài liệu chuẩn LT3)" : std.Title,
            });
        }
        if (tokens > 0) notes.Add($"Đã bỏ {tokens} token cũ dán sẵn trong header Authorization — Bcode tự lấy token khi gọi.");
        if (p.Forms.Count == 0) notes.Add("Không có request nào ngoài getToken.");
        return new ImportResult(p, notes);
    }

    private static void Collect(JsonArray? items, List<(string, string, string, string)> into)
    {
        if (items is null) return;
        foreach (var it in items)
        {
            if (it?["item"] is JsonArray sub) { Collect(sub, into); continue; }
            var req = it?["request"]; if (req is null) continue;
            var url = req["url"] is JsonValue v ? v.GetValue<string>() : req["url"]?["raw"]?.GetValue<string>() ?? "";
            var body = req["body"]?["raw"]?.GetValue<string>() ?? "";
            var auth = (req["header"] as JsonArray)?.FirstOrDefault(h => string.Equals(h?["key"]?.GetValue<string>(), "Authorization", StringComparison.OrdinalIgnoreCase))?["value"]?.GetValue<string>() ?? "";
            into.Add((it?["name"]?.GetValue<string>() ?? "Request", url.Trim(), body, auth));
        }
    }

    public static string KindOf(string endpoint) =>
        Regex.IsMatch(endpoint, @"syncvoucher", RegexOptions.IgnoreCase) ? "SyncVoucher"
        : Regex.IsMatch(endpoint, @"syncdata", RegexOptions.IgnoreCase) ? "SyncData"
        : Regex.IsMatch(endpoint, @"getdata", RegexOptions.IgnoreCase) ? "GetData" : "custom";

    public static string Export(ApiProject p)
    {
        var auth = p.AuthScheme == "bearer" ? "Bearer {{token}}" : "{{token}}";
        // {{key}} {{today}}… trong body là biến — script cấp collection đặt giá trị trước mỗi request.
        object RequestOf(string endpoint, string body, bool withAuth) => new
        {
            method = "POST",
            header = withAuth ? new object[] { new { key = "Content-Type", value = "application/json" }, new { key = "Authorization", value = auth, type = "text" } }
                              : new object[] { new { key = "Content-Type", value = "application/json" } },
            body = new { mode = "raw", raw = body, options = new { raw = new { language = "json" } } },
            url = new { raw = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : "{{base_url}}/" + endpoint.TrimStart('/') },
        };
        object Request(string name, string endpoint, string body, bool withAuth) => new { name, request = RequestOf(endpoint, body, withAuth) };
        var tokenItem = new
        {
            name = "getToken",
            @event = new object[] { new { listen = "test", script = new { type = "text/javascript", exec = new[] {
                "// Chuẩn LT3: token nằm ở token.accesstoken — lưu vào biến {{token}} cho các request khác",
                "const j = pm.response.json();",
                "const t = (j.token && (j.token.accesstoken || j.token)) || j.access_token || (j.data && j.data.token);",
                "if (t) pm.collectionVariables.set('token', t);",
                "pm.test('Lấy token thành công', () => pm.expect(t).to.be.a('string'));" } } } },
            request = RequestOf(p.TokenPath, p.TokenBody, false),
        };
        var groups = p.Forms.GroupBy(f => f.Kind).Select(g => new
        {
            name = g.Key switch { "SyncData" => "Đồng bộ danh mục (SyncData)", "SyncVoucher" => "Đồng bộ chứng từ (SyncVoucher)", "GetData" => "Lấy dữ liệu (GetData)", _ => "Khác" },
            item = g.Select(f => Request(f.Name, f.Endpoint, f.Body, true)).ToArray(),
        });
        var collection = new
        {
            info = new { name = string.IsNullOrWhiteSpace(p.ProjectId) ? p.Name : p.ProjectId + "_API", description = p.Note, schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
            @event = new object[] { new { listen = "prerequest", script = new { type = "text/javascript", exec = new[] {
                "// Biến dùng trong body (giống Bcode): {{today}} {{now}} {{key}} {{year}} {{month}} {{monthStart}}",
                "const d = new Date(), p2 = (n) => String(n).padStart(2, '0');",
                "const today = `${d.getFullYear()}-${p2(d.getMonth() + 1)}-${p2(d.getDate())}`;",
                "pm.variables.set('today', today);",
                "pm.variables.set('now', `${today}T${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`);",
                "pm.variables.set('key', Date.now().toString(16) + '-' + Math.floor(Math.random() * 65536).toString(16));",
                "pm.variables.set('year', String(d.getFullYear()));",
                "pm.variables.set('month', String(d.getMonth() + 1));",
                "pm.variables.set('monthStart', `${d.getFullYear()}-${p2(d.getMonth() + 1)}-01`);" } } } },
            variable = new object[]
            {
                new { key = "base_url", value = p.BaseUrl },
                new { key = "username", value = p.Username },
                new { key = "password", value = "" },   // không xuất mật khẩu — người dùng tự nhập trong Postman
                new { key = "token", value = "" },
            },
            item = new object[] { tokenItem }.Concat(groups).ToArray(),
        };
        return JsonSerializer.Serialize(collection, Pretty);   // body getToken dùng biến {{username}} / {{password}} của Postman
    }
}
