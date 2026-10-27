using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services.Api;

/// <summary>
/// Postman collection v2.1 ⇄ <see cref="ApiProject"/>.
/// Nhập: request có đường dẫn …/getToken → địa chỉ gốc (phần trước "/api/"), tài khoản (mật khẩu mã hoá DPAPI) — luồng lấy token chuẩn LT3, token cũ dán sẵn
/// trong header Authorization bị BỎ. Không có getToken (và Authorization không dùng {{token}}) → kiểu "Header tĩnh": request tự mang header xác thực
/// (Authentication / TaxCode / Authorization: Basic…), không gọi getToken. Mỗi request → form: tên, method, endpoint, header (bỏ mục disabled; auth Postman
/// basic / bearer / apikey đổi thành header), body raw / urlencoded / formdata / none. Header giống hệt nhau ở mọi form → header chung của dự án.
/// Header bí mật (<see cref="ApiProjectStore.IsSecretHeader"/>) mã hoá DPAPI. Biến collection có giá trị được thay vào URL / header.
/// Body "{{payload}}" sinh bằng script trước request thì không đọc được — báo lại để dùng request cùng loại.
/// Xuất: biến base_url (+ username / password để trống / token nếu lấy token); request getToken tự lưu token.accesstoken vào {{token}};
/// script cấp collection sinh {{key}} {{today}}…; header chung + header form (+ "Authorization: {{token}}" / "Bearer {{token}}");
/// header bí mật: kèm giá trị hoặc thay bằng biến collection để trống; form gom thư mục theo SyncData / SyncVoucher / GetData.
/// </summary>
public static class PostmanConverter
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed record ImportResult(ApiProject Project, List<string> Notes);
    private sealed record Req(string Name, string Method, string Url, string Mode, string Body, List<ApiHeader> Headers, List<ApiHeader> Params, string RawType);

    public static ImportResult Import(string json, string fallbackName)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("File rỗng.");
        var notes = new List<string>();
        var name = Str(root["info"]?["name"]) ?? fallbackName;
        var p = new ApiProject { Name = name, ProjectId = Regex.Replace(name, @"[_\- ]?(FBO|FBI|API)$", "", RegexOptions.IgnoreCase), Department = "LT3", Forms = new() };
        var vars = Vars(root["variable"] as JsonArray);
        var requests = new List<Req>();
        Collect(root["item"] as JsonArray, requests, vars, AuthHeaders(root["auth"], vars, notes), notes);

        var tokenReq = requests.FirstOrDefault(r => Regex.IsMatch(r.Url, @"/gettoken\b", RegexOptions.IgnoreCase));
        // Có getToken, hoặc Authorization dùng biến {{token}} → luồng lấy token LT3. Còn lại request tự mang header xác thực → header tĩnh.
        var tokenFlow = tokenReq is not null || requests.Any(r => r.Headers.Any(h => IsAuthorization(h.Key) && h.Value.Contains("{{token", StringComparison.OrdinalIgnoreCase)));
        string baseUrl = "";
        if (tokenReq is not null)
        {
            var i = tokenReq.Url.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            baseUrl = i > 0 ? tokenReq.Url[..i] : tokenReq.Url;
            p.TokenPath = i > 0 ? tokenReq.Url[(i + 1)..] : "api/getToken";
            try
            {
                var tb = JsonNode.Parse(Sub(tokenReq.Body, vars));
                string Plain(string k) => Str(tb?[k]) is { } s && !s.Contains("{{") ? s : "";   // biến Postman chưa có giá trị → coi như trống
                p.Username = Plain("username");
                var pw = Plain("password");
                if (pw.Length > 0) { p.PasswordProtected = ApiProjectStore.Protect(pw); notes.Add("Đã lấy tài khoản từ request getToken — mật khẩu lưu mã hoá trên máy này."); }
            }
            catch { notes.Add("Body getToken không phải JSON — nhập tài khoản tay."); }
        }
        else
        {
            var first = requests.FirstOrDefault(r => r.Url.Contains("/api/", StringComparison.OrdinalIgnoreCase));
            if (first is not null) baseUrl = first.Url[..first.Url.IndexOf("/api/", StringComparison.OrdinalIgnoreCase)];
            if (tokenFlow) notes.Add("Không có request getToken — dùng mặc định api/getToken.");
            else
            {
                p.AuthScheme = "none";
                var keys = requests.SelectMany(r => r.Headers).Select(h => h.Key).Where(k => ApiProjectStore.IsSecretHeader(k) || k.Equals("TaxCode", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                notes.Add(keys.Count > 0
                    ? $"Không có request getToken — các request tự mang header xác thực ({string.Join(", ", keys)}) → kiểu \"Header tĩnh\", không lấy token."
                    : "Không có request getToken, cũng không có header xác thực → kiểu \"Header tĩnh\" (không lấy token). Cần token thì đổi ô Xác thực.");
            }
        }
        if (tokenFlow && requests.Any(r => r != tokenReq && r.Headers.Any(h => IsAuthorization(h.Key) && h.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)))) p.AuthScheme = "bearer";
        p.BaseUrl = baseUrl;

        var tokens = 0;
        foreach (var r in requests)
        {
            if (ReferenceEquals(r, tokenReq)) continue;
            if (tokenFlow)
            {
                tokens += r.Headers.Count(h => IsAuthorization(h.Key) && !h.Value.Contains("{{"));
                r.Headers.RemoveAll(h => IsAuthorization(h.Key));   // Bcode tự lấy token khi gọi
            }
            if (r.Body.Trim() == "{{payload}}") { notes.Add($"\"{r.Name}\": body sinh bằng script Postman — bỏ qua (dùng request cùng form có body sẵn)."); continue; }
            var endpoint = r.Url.StartsWith(baseUrl + "/", StringComparison.OrdinalIgnoreCase) && baseUrl.Length > 0 ? r.Url[(baseUrl.Length + 1)..] : r.Url;
            string body = r.Body, formName = r.Name;
            if (r.Mode == "raw")
                try
                {
                    var node = JsonNode.Parse(r.Body);
                    body = node?.ToJsonString(Pretty) ?? r.Body;
                    formName = Str(node?["form"]) ?? r.Name;
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
                Method = r.Method,
                Body = body,
                BodyMode = r.Mode,
                RawType = r.RawType,
                Headers = r.Headers,
                Params = r.Params,
                Description = std is null ? "Form riêng của dự án (không có trong tài liệu chuẩn LT3)" : std.Title,
            });
        }
        // Header giống hệt nhau ở mọi form (vd Content-Type) → header chung của dự án.
        if (p.Forms.Count > 1)
            foreach (var h in p.Forms[0].Headers.ToList())
                if (p.Forms.All(f => f.Headers.Any(x => Same(x, h))))
                {
                    p.Headers.Add(h);
                    foreach (var f in p.Forms) f.Headers.RemoveAll(x => Same(x, h));
                }
        foreach (var h in p.Headers.Concat(p.Forms.SelectMany(f => f.Headers.Concat(f.Params)))) h.Secret = ApiProjectStore.IsSecretHeader(h.Key);
        var secrets = p.Headers.Concat(p.Forms.SelectMany(f => f.Headers)).Where(h => h.Secret).Select(h => h.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ApiProjectStore.Seal(p);   // header bí mật → mã hoá DPAPI ngay khi nhập

        if (p.Forms.Any(f => f.Headers.Count > 0)) notes.Add("Đã giữ header riêng của từng form" + (p.Headers.Count > 0 ? $"; header chung mọi form ({string.Join(", ", p.Headers.Select(h => h.Key))}) để ở Kết nối" : "") + ".");
        if (secrets.Count > 0) notes.Add($"Header bí mật ({string.Join(", ", secrets)}) lưu mã hoá trên máy này — chép file dự án sang máy khác phải nhập lại.");
        if (tokens > 0) notes.Add($"Đã bỏ {tokens} token cũ dán sẵn trong header Authorization — Bcode tự lấy token khi gọi.");
        if (p.Forms.Count == 0) notes.Add("Không có request nào ngoài getToken.");
        return new ImportResult(p, notes);
    }

    private static void Collect(JsonArray? items, List<Req> into, Dictionary<string, string> vars, List<ApiHeader> inheritedAuth, List<string> notes)
    {
        if (items is null) return;
        foreach (var it in items)
        {
            if (it?["item"] is JsonArray sub) { Collect(sub, into, vars, it["auth"] is null ? inheritedAuth : AuthHeaders(it["auth"], vars, notes), notes); continue; }
            var req = it?["request"]; if (req is null) continue;
            var name = Str(it?["name"]) ?? "Request";
            var url = Sub(req["url"] is JsonValue v ? Str(v) ?? "" : Str(req["url"]?["raw"]) ?? "", vars).Trim();
            // Tham số query → bảng Params (giữ dòng tắt); URL chỉ còn phần trước "?".
            var prms = new List<ApiHeader>();
            if (req["url"]?["query"] is JsonArray qa)
                foreach (var q in qa.OfType<JsonObject>())
                {
                    var key = Str(q["key"])?.Trim() ?? "";
                    if (key.Length > 0) prms.Add(new ApiHeader { Key = key, Value = Sub(Str(q["value"]) ?? "", vars), Disabled = IsTrue(q["disabled"]) });
                }
            var qi = url.IndexOf('?');
            if (qi >= 0)
            {
                if (prms.Count == 0)
                    foreach (var part in url[(qi + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = part.IndexOf('=');
                        var key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' ')).Trim();
                        if (key.Length > 0) prms.Add(new ApiHeader { Key = key, Value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')) });
                    }
                url = url[..qi];
            }
            var headers = new List<ApiHeader>();
            if (req["header"] is JsonArray ha)
                foreach (var h in ha.OfType<JsonObject>())
                {
                    var key = Str(h["key"])?.Trim() ?? "";
                    if (key.Length > 0) headers.Add(new ApiHeader { Key = key, Value = Sub(Str(h["value"]) ?? "", vars), Disabled = IsTrue(h["disabled"]) });   // header tắt: giữ, không gửi
                }
            else if (Str(req["header"]) is { } hs)   // v2.0: header dạng chuỗi "Key: Value\n…"
                foreach (var kv in ApiClient.Pairs(hs)) headers.Add(new ApiHeader { Key = kv.Key, Value = Sub(kv.Value, vars) });
            foreach (var a in req["auth"] is null ? inheritedAuth : AuthHeaders(req["auth"], vars, notes))
                if (!headers.Any(x => !x.Disabled && x.Key.Equals(a.Key, StringComparison.OrdinalIgnoreCase))) headers.Add(new ApiHeader { Key = a.Key, Value = a.Value });
            var (mode, body) = BodyOf(req["body"], name, notes);
            var lang = Str(req["body"]?["options"]?["raw"]?["language"])?.ToLowerInvariant();
            into.Add(new Req(name, (Str(req["method"]) ?? "POST").Trim().ToUpperInvariant(), url, mode, body, headers, prms, lang is "text" or "xml" or "html" or "javascript" ? lang : "json"));
        }
    }

    /// <summary>Auth của Postman (request / thư mục / collection) → header: basic, bearer, apikey (đặt ở header). noauth / không có → không thêm.</summary>
    private static List<ApiHeader> AuthHeaders(JsonNode? auth, Dictionary<string, string> vars, List<string> notes)
    {
        var list = new List<ApiHeader>();
        var type = Str(auth?["type"]);
        if (type is null or "noauth") return list;
        string P(string k) => Sub((auth?[type] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => Str(x["key"]) == k) is { } o ? Str(o["value"]) ?? "" : "", vars);
        switch (type)
        {
            case "basic": list.Add(new ApiHeader { Key = "Authorization", Value = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(P("username") + ":" + P("password"))) }); break;
            case "bearer": list.Add(new ApiHeader { Key = "Authorization", Value = "Bearer " + P("token") }); break;
            case "apikey":
                if (P("in") != "query" && P("key").Length > 0) list.Add(new ApiHeader { Key = P("key"), Value = P("value") });
                else notes.Add("API key Postman đặt trên query string — thêm tay vào Endpoint.");
                break;
            default: notes.Add($"Kiểu xác thực Postman \"{type}\" chưa hỗ trợ — thêm header tay."); break;
        }
        return list;
    }

    /// <summary>Body Postman → (kiểu, nội dung): raw giữ nguyên; urlencoded / formdata thành các dòng "key: value" (mục disabled → "// key: value", bỏ trường file); còn lại none.</summary>
    private static (string Mode, string Body) BodyOf(JsonNode? b, string name, List<string> notes)
    {
        var mode = Str(b?["mode"]);
        switch (mode)
        {
            case null: return ("none", "");
            case "raw": return ("raw", Str(b!["raw"]) ?? "");
            case "urlencoded" or "formdata":
            {
                var lines = new List<string>();
                foreach (var x in (b![mode] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (Str(x["type"]) == "file") { notes.Add($"\"{name}\": bỏ trường file \"{Str(x["key"])}\" (chưa hỗ trợ gửi file)."); continue; }
                    lines.Add((IsTrue(x["disabled"]) ? "// " : "") + Str(x["key"]) + ": " + Str(x["value"]));
                }
                return (mode, string.Join("\n", lines));
            }
            default: notes.Add($"\"{name}\": body kiểu {mode} chưa hỗ trợ — gửi không body."); return ("none", "");
        }
    }

    private static Dictionary<string, string> Vars(JsonArray? variables)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in (variables ?? new JsonArray()).OfType<JsonObject>())
            if (Str(v["key"]) is { Length: > 0 } k && Str(v["value"]) is { Length: > 0 } val && !IsTrue(v["disabled"])) d[k] = val;
        return d;
    }

    /// <summary>Thay {{biến}} của collection (chỉ biến có giá trị — {{token}} để trống giữ nguyên).</summary>
    private static string Sub(string s, Dictionary<string, string> vars) =>
        vars.Count == 0 ? s : Regex.Replace(s, @"\{\{([^{}]+)\}\}", m => vars.TryGetValue(m.Groups[1].Value.Trim(), out var v) ? v : m.Value);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool IsTrue(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static bool IsAuthorization(string key) => key.Trim().Equals("Authorization", StringComparison.OrdinalIgnoreCase);
    private static bool Same(ApiHeader a, ApiHeader b) => a.Key.Equals(b.Key, StringComparison.OrdinalIgnoreCase) && a.Value == b.Value && a.Disabled == b.Disabled;

    public static string KindOf(string endpoint) =>
        Regex.IsMatch(endpoint, @"syncvoucher", RegexOptions.IgnoreCase) ? "SyncVoucher"
        : Regex.IsMatch(endpoint, @"syncdata", RegexOptions.IgnoreCase) ? "SyncData"
        : Regex.IsMatch(endpoint, @"getdata", RegexOptions.IgnoreCase) ? "GetData" : "custom";

    /// <param name="withSecrets">true: ghi thẳng giá trị header bí mật vào file; false: thay bằng biến collection để trống (nhập trong Postman).</param>
    public static string Export(ApiProject p, bool withSecrets = false)
    {
        var tokenFlow = ApiClient.UsesToken(p);
        var auth = p.AuthScheme == "bearer" ? "Bearer {{token}}" : "{{token}}";
        var variables = new List<object> { new { key = "base_url", value = p.BaseUrl } };
        if (tokenFlow)
            variables.AddRange(new object[]
            {
                new { key = "username", value = p.Username },
                new { key = "password", value = "" },   // không xuất mật khẩu — người dùng tự nhập trong Postman
                new { key = "token", value = "" },
            });
        var secretVars = new Dictionary<string, string>();   // giá trị thật → tên biến
        string ValueOf(ApiHeader h)
        {
            if (!h.Secret) return h.Value;
            var plain = ApiProjectStore.PlainValue(h);
            if (withSecrets) return plain;
            if (!secretVars.TryGetValue(plain, out var v))
            {
                var stem = Regex.Replace(h.Key, @"\W", "_");
                v = stem;
                for (var n = 2; secretVars.ContainsValue(v) || v is "base_url" or "username" or "password" or "token"; n++) v = stem + "_" + n;
                secretVars[plain] = v;
                variables.Add(new { key = v, value = "" });
            }
            return "{{" + v + "}}";
        }
        // {{key}} {{today}}… trong body là biến — script cấp collection đặt giá trị trước mỗi request.
        object RequestOf(string method, string endpoint, string mode, string body, List<ApiHeader> headers, bool withAuth, ApiForm? f = null)
        {
            var hs = headers.Select(h => new { key = h.Key, value = ValueOf(h), type = "text", disabled = (bool?)null }).ToList();
            if (mode == "raw" && !headers.Any(h => h.Key.Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase))) hs.Insert(0, new { key = "Content-Type", value = ApiClient.RawContentType(f?.RawType), type = "text", disabled = (bool?)null });
            if (withAuth && !headers.Any(h => IsAuthorization(h.Key))) hs.Add(new { key = "Authorization", value = auth, type = "text", disabled = (bool?)null });
            if (f is not null) hs.AddRange(f.Headers.Where(h => h.Disabled && !string.IsNullOrWhiteSpace(h.Key)).Select(h => new { key = h.Key, value = ValueOf(h), type = "text", disabled = (bool?)true }));
            var baseRaw = endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? endpoint : "{{base_url}}/" + endpoint.TrimStart('/');
            var query = (f?.Params ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.Key)).Select(x => new { key = x.Key.Trim(), value = ValueOf(x), disabled = x.Disabled ? (bool?)true : null }).ToList();
            var on = query.Where(x => x.disabled is null).ToList();
            return new
            {
                method = string.IsNullOrWhiteSpace(method) ? "POST" : method,
                header = hs,
                body = BodyJson(mode, body, f?.RawType),
                url = query.Count == 0 ? (object)new { raw = baseRaw }
                    : new { raw = baseRaw + (on.Count == 0 ? "" : (baseRaw.Contains('?') ? "&" : "?") + string.Join("&", on.Select(x => x.key + "=" + x.value))), query },
                auth = f is null ? null : AuthJson(f, ValueOf),
            };
        }
        var tokenItem = new
        {
            name = "getToken",
            @event = new object[] { new { listen = "test", script = new { type = "text/javascript", exec = new[] {
                "// Chuẩn LT3: token nằm ở token.accesstoken — lưu vào biến {{token}} cho các request khác",
                "const j = pm.response.json();",
                "const t = (j.token && (j.token.accesstoken || j.token)) || j.access_token || (j.data && j.data.token);",
                "if (t) pm.collectionVariables.set('token', t);",
                "pm.test('Lấy token thành công', () => pm.expect(t).to.be.a('string'));" } } } },
            request = RequestOf("POST", p.TokenPath, "raw", p.TokenBody, new(), false),
        };
        var groups = p.Forms.GroupBy(f => f.Kind).Select(g => new
        {
            name = g.Key switch { "SyncData" => "Đồng bộ danh mục (SyncData)", "SyncVoucher" => "Đồng bộ chứng từ (SyncVoucher)", "GetData" => "Lấy dữ liệu (GetData)", _ => "Khác" },
            item = g.Select(f => new { name = f.Name, request = RequestOf(f.Method, f.Endpoint, f.BodyMode ?? "raw", f.Body, ApiClient.MergeHeaders(p, f), tokenFlow && ApiClient.AuthType(f) == "inherit", f) }).ToArray(),
        }).ToArray();
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
            variable = variables,
            item = tokenFlow ? new object[] { tokenItem }.Concat(groups).ToArray() : groups.Cast<object>().ToArray(),
        };
        return JsonSerializer.Serialize(collection, Pretty);   // body getToken dùng biến {{username}} / {{password}} của Postman
    }

    /// <summary>Body xuất Postman: raw (language json), urlencoded / formdata (mỗi dòng "key: value"), none → không có body.</summary>
    private static object? BodyJson(string mode, string body, string? rawType) => mode switch
    {
        "none" => null,
        "urlencoded" or "formdata" => new Dictionary<string, object> { ["mode"] = mode, [mode] = ApiClient.Rows(body).Select(r => new { key = r.Key, value = r.Value, type = "text", disabled = r.Disabled ? (bool?)true : null }).ToArray() },
        _ => new { mode = "raw", raw = body, options = new { raw = new { language = string.IsNullOrWhiteSpace(rawType) ? "json" : rawType } } },
    };

    /// <summary>Tab Authorization của form → auth của request Postman (giá trị bí mật qua <paramref name="valueOf"/>: kèm chữ hoặc biến collection). inherit → null (theo collection / header).</summary>
    private static object? AuthJson(ApiForm f, Func<ApiHeader, string> valueOf)
    {
        var a = f.Auth; var type = ApiClient.AuthType(f);
        if (a is null || type == "inherit") return null;
        var secret = valueOf(new ApiHeader { Key = type == "apikey" && a.KeyName.Length > 0 ? a.KeyName : type == "basic" ? "password" : "token", Value = a.Value, ValueProtected = a.ValueProtected, Secret = true });
        object Kv(string k, string v) => new { key = k, value = v, type = "string" };
        return type switch
        {
            "bearer" => new Dictionary<string, object> { ["type"] = "bearer", ["bearer"] = new[] { Kv("token", secret) } },
            "basic" => new Dictionary<string, object> { ["type"] = "basic", ["basic"] = new[] { Kv("username", a.Username), Kv("password", secret) } },
            "apikey" => new Dictionary<string, object> { ["type"] = "apikey", ["apikey"] = new[] { Kv("key", a.KeyName), Kv("value", secret), Kv("in", a.In) } },
            _ => new { type = "noauth" },
        };
    }
}
