using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bcode.App.Models;

namespace Bcode.App.Services.Api;

/// <summary>
/// Gọi API theo chuẩn LT3: lấy token (đọc <see cref="ApiProject.TokenField"/>, không thấy thì dò token.accesstoken / token / access_token / data.token),
/// nhớ token tới khi hết hạn (<see cref="ApiProject.ExpiresField"/>, không có thì đọc exp trong JWT) và tự lấy lại 1 lần khi gặp 401.
/// Header: "Authorization: &lt;token&gt;" (raw, chuẩn LT3) hoặc "Bearer &lt;token&gt;"; kiểu "none" (header tĩnh) không lấy token. Kèm header chung của dự án + header của form
/// (form thắng; form tự mang Authorization thì không thêm token). Tab Authorization của form (<see cref="ApiForm.Auth"/>): kế thừa / không / bearer / basic / API key —
/// khác "kế thừa" thì không lấy token. Tham số query (<see cref="ApiForm.Params"/>) nối vào URL; dòng tắt (Disabled) bỏ qua.
/// Body raw (JSON / Text / XML…) / urlencoded / formdata / none. Body + endpoint + tham số thay biến {{today}} {{now}} {{key}} {{year}} {{month}} {{monthStart}}.
/// </summary>
public sealed class ApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresUtc)> Tokens = new();

    /// <summary><paramref name="RequestHead"/>: dòng "METHOD url" + header đã gửi (header bí mật che giá trị). <paramref name="ResponseHeaders"/>: "Key: Value" mỗi dòng.
    /// <paramref name="Size"/>: số byte body phản hồi.</summary>
    public sealed record CallResult(int Status, string Reason, long Ms, long Size, string Url, string RequestHead, string RequestBody, string ResponseText, string ResponseHeaders,
        string ContentType, bool Ok, string? Message, List<ResultTable> Tables);
    public sealed record ResultTable(string Name, List<string> Columns, List<List<string?>> Rows);
    public sealed record TokenInfo(string Token, DateTime ExpiresUtc, bool FromCache);

    public static string Url(ApiProject p, string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : p.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static string CacheKey(ApiProject p) => Url(p, p.TokenPath) + "|" + p.Username;

    public static void ForgetToken(ApiProject p) => Tokens.TryRemove(CacheKey(p), out _);

    public static async Task<TokenInfo> GetTokenAsync(ApiProject p, bool force = false, CancellationToken ct = default)
    {
        var key = CacheKey(p);
        if (!force && Tokens.TryGetValue(key, out var c) && c.ExpiresUtc > DateTime.UtcNow.AddMinutes(1)) return new TokenInfo(c.Token, c.ExpiresUtc, true);
        var password = ApiProjectStore.Unprotect(p.PasswordProtected);
        var body = p.TokenBody.Replace("{{username}}", JsonEsc(p.Username)).Replace("{{password}}", JsonEsc(password));
        using var res = await Http.PostAsync(Url(p, p.TokenPath), new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Lấy token lỗi HTTP {(int)res.StatusCode}: {Short(text)}");
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException("Lấy token bị từ chối: " + (doc.RootElement.TryGetProperty("messages", out var m) ? m.ToString() : Short(text)));
        var token = Pick(doc.RootElement, p.TokenField) ?? Pick(doc.RootElement, "token.accesstoken") ?? Pick(doc.RootElement, "token") ?? Pick(doc.RootElement, "access_token") ?? Pick(doc.RootElement, "data.token")
            ?? throw new InvalidOperationException("Không tìm thấy token trong response (khai báo đúng \"Trường token\"): " + Short(text));
        var exp = ParseDate(Pick(doc.RootElement, p.ExpiresField) ?? Pick(doc.RootElement, "token.expires")) ?? JwtExpiry(token) ?? DateTime.UtcNow.AddHours(1);
        Tokens[key] = (token, exp);
        return new TokenInfo(token, exp, false);
    }

    public static async Task<CallResult> SendAsync(ApiProject p, ApiForm f, string body, CancellationToken ct = default)
    {
        var url = FullUrl(p, f);
        var expanded = Expand(body);
        var sw = Stopwatch.StartNew();
        var (status, reason, text, size, head, resHead, ctype) = await PostAsync(p, f, url, expanded, retryOn401: true, ct);
        sw.Stop();
        bool ok = status is >= 200 and < 300;
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("success", out var s) && s.ValueKind is JsonValueKind.False) ok = false;
                var code = r.TryGetProperty("code", out var c) ? c.ToString() : null;
                var msg = r.TryGetProperty("messages", out var mm) ? mm.ToString() : r.TryGetProperty("message", out var m2) ? m2.ToString() : null;
                if (code is not null && code != "200") ok = false;
                if (code is not null && Lt3Templates.ErrorCodes().TryGetValue(code, out var meaning)) message = $"{code} — {meaning}" + (string.IsNullOrWhiteSpace(msg) ? "" : ": " + msg);
                else message = msg;
                if (r.TryGetProperty("records", out var rec)) message = (message ?? "") + $" · {rec} bản ghi";
            }
        }
        catch { /* không phải JSON */ }
        return new CallResult(status, reason, sw.ElapsedMilliseconds, size, url, head, f.BodyMode == "none" ? "" : expanded, text, resHead, ctype, ok, message, Tables(text));
    }

    /// <summary>Kiểu xác thực lấy token (raw / bearer); "none" = header tĩnh, không gọi getToken.</summary>
    public static bool UsesToken(ApiProject p) => !string.Equals(p.AuthScheme, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>Kiểu xác thực riêng của form ("inherit" khi chưa đặt — dữ liệu cũ).</summary>
    public static string AuthType(ApiForm f) => f.Auth?.Type is { Length: > 0 } t ? t.ToLowerInvariant() : "inherit";

    /// <summary>Header chung của dự án rồi header của form — trùng tên (không phân biệt hoa thường) thì form thắng. Dòng tắt (Disabled) bỏ qua.</summary>
    public static List<ApiHeader> MergeHeaders(ApiProject p, ApiForm f)
    {
        var list = new List<ApiHeader>();
        foreach (var h in (p.Headers ?? new()).Concat(f.Headers ?? new()))
        {
            if (h is null || h.Disabled || string.IsNullOrWhiteSpace(h.Key)) continue;
            var i = list.FindIndex(x => IsKey(x.Key, h.Key));
            if (i >= 0) list[i] = h; else list.Add(h);
        }
        return list;
    }

    /// <summary>Header do tab Authorization của form sinh ra (bearer / basic / apikey ở header). inherit / none → không có.</summary>
    public static List<ApiHeader> AuthHeaders(ApiForm f)
    {
        var a = f.Auth; var list = new List<ApiHeader>();
        if (a is null) return list;
        var v = ApiProjectStore.PlainValue(a);
        switch (AuthType(f))
        {
            case "bearer": list.Add(new ApiHeader { Key = "Authorization", Value = "Bearer " + v, Secret = true }); break;
            case "basic": list.Add(new ApiHeader { Key = "Authorization", Value = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(a.Username + ":" + v)), Secret = true }); break;
            case "apikey": if (a.In != "query" && a.KeyName.Trim().Length > 0) list.Add(new ApiHeader { Key = a.KeyName.Trim(), Value = v, Secret = true }); break;
        }
        return list;
    }

    /// <summary>Địa chỉ gửi: địa chỉ gốc + Endpoint + tham số query đang bật (thay biến {{today}}…, mã hoá URL) + API key đặt ở query.</summary>
    public static string FullUrl(ApiProject p, ApiForm f)
    {
        var url = Url(p, Expand(f.Endpoint ?? ""));
        var q = (f.Params ?? new()).Where(x => x is not null && !x.Disabled && !string.IsNullOrWhiteSpace(x.Key))
            .Select(x => (Key: x.Key.Trim(), Value: Expand(ApiProjectStore.PlainValue(x)))).ToList();
        if (AuthType(f) == "apikey" && f.Auth is { In: "query" } a && a.KeyName.Trim().Length > 0) q.Add((a.KeyName.Trim(), ApiProjectStore.PlainValue(a)));
        if (q.Count == 0) return url;
        return url + (url.Contains('?') ? (url.EndsWith('?') || url.EndsWith('&') ? "" : "&") : "?") + string.Join("&", q.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    }

    private static async Task<(int Status, string Reason, string Text, long Size, string Head, string ResHead, string ContentType)> PostAsync(ApiProject p, ApiForm f, string url, string body, bool retryOn401, CancellationToken ct)
    {
        // Thứ tự: header chung dự án → header từ tab Authorization của form → header của form (trùng tên thì sau thắng).
        var merged = new List<ApiHeader>();
        foreach (var h in (p.Headers ?? new()).Concat(AuthHeaders(f)).Concat(f.Headers ?? new()))
        {
            if (h is null || h.Disabled || string.IsNullOrWhiteSpace(h.Key)) continue;
            var i = merged.FindIndex(x => IsKey(x.Key, h.Key));
            if (i >= 0) merged[i] = h; else merged.Add(h);
        }
        var headers = merged.Select(h => (Key: h.Key.Trim(), Value: ApiProjectStore.PlainValue(h), h.Secret)).ToList();
        // Lấy token chỉ khi form "kế thừa" Kết nối kiểu token và không tự mang Authorization.
        var withToken = AuthType(f) == "inherit" && UsesToken(p) && !headers.Any(h => IsKey(h.Key, "Authorization"));
        if (withToken)
        {
            var tk = await GetTokenAsync(p, false, ct);
            headers.Add(("Authorization", p.AuthScheme == "bearer" ? "Bearer " + tk.Token : tk.Token, true));
        }
        var method = new HttpMethod(string.IsNullOrWhiteSpace(f.Method) ? "POST" : f.Method.Trim().ToUpperInvariant());
        var contentType = headers.LastOrDefault(h => IsKey(h.Key, "Content-Type")).Value;
        using var req = new HttpRequestMessage(method, url) { Content = Content(f.BodyMode, f.RawType, method, body, contentType) };
        if (!headers.Any(h => IsKey(h.Key, "Accept"))) headers.Insert(0, ("Accept", "*/*", false));   // như Postman tự thêm
        foreach (var h in headers.Where(h => !IsKey(h.Key, "Content-Type")))
            if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value)) req.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value);
        var head = new List<string> { method + " " + url };
        if (req.Content?.Headers.ContentType is { } cth) head.Add("Content-Type: " + cth);
        head.AddRange(headers.Where(h => !IsKey(h.Key, "Content-Type")).Select(h => h.Key + ": " + (h.Secret ? Hide(h.Value) : h.Value)));
        using var res = await Http.SendAsync(req, ct);
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        if (retryOn401 && withToken && (int)res.StatusCode == 401) { ForgetToken(p); return await PostAsync(p, f, url, body, false, ct); }   // token hết hạn phía server → lấy lại 1 lần
        Encoding enc;
        try { enc = res.Content.Headers.ContentType?.CharSet is { Length: > 0 } cs ? Encoding.GetEncoding(cs.Trim('"')) : Encoding.UTF8; } catch { enc = Encoding.UTF8; }
        var text = enc.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var resHead = string.Join("\n", res.Headers.Concat(res.Content.Headers).SelectMany(h => h.Value.Select(v => h.Key + ": " + v)));
        return ((int)res.StatusCode, res.ReasonPhrase ?? "", text, bytes.LongLength, string.Join("\n", head), resHead, res.Content.Headers.ContentType?.MediaType ?? "");
    }

    /// <summary>Content-Type mặc định của body raw theo kiểu (như ô chọn JSON / Text / XML của Postman).</summary>
    public static string RawContentType(string? rawType) => (rawType ?? "").ToLowerInvariant() switch
    {
        "text" => "text/plain", "xml" => "application/xml", "html" => "text/html", "javascript" => "application/javascript", _ => "application/json",
    };

    /// <summary>Body theo kiểu Postman: raw (Content-Type theo <see cref="ApiForm.RawType"/>), urlencoded / formdata (mỗi dòng key: value), none. Header Content-Type của form ghi đè (trừ formdata — cần boundary).</summary>
    private static HttpContent? Content(string? mode, string? rawType, HttpMethod method, string body, string? contentType)
    {
        HttpContent c;
        switch (mode)
        {
            case "none":
                if (string.IsNullOrWhiteSpace(contentType) || method == HttpMethod.Get || method == HttpMethod.Head) return null;
                c = new ByteArrayContent(Array.Empty<byte>());
                break;
            case "urlencoded": c = new FormUrlEncodedContent(Pairs(body)); break;
            case "formdata":
            {
                var mp = new MultipartFormDataContent();
                foreach (var kv in Pairs(body)) mp.Add(new StringContent(kv.Value, Encoding.UTF8), kv.Key);
                return mp;
            }
            default: c = new StringContent(body, Encoding.UTF8, RawContentType(rawType)); break;
        }
        if (!string.IsNullOrWhiteSpace(contentType)) { c.Headers.Remove("Content-Type"); c.Headers.TryAddWithoutValidation("Content-Type", contentType.Trim()); }
        return c;
    }

    /// <summary>Body urlencoded / formdata: mỗi dòng "key: value" (giống Bulk Edit của Postman); dòng trống hoặc bắt đầu "//" bỏ qua.</summary>
    public static List<KeyValuePair<string, string>> Pairs(string body) =>
        Rows(body).Where(r => !r.Disabled).Select(r => new KeyValuePair<string, string>(r.Key, r.Value)).ToList();

    /// <summary>Như <see cref="Pairs"/> nhưng giữ cả dòng "// key: value" (dòng tắt trong bảng key–value) với Disabled = true.</summary>
    public static List<(string Key, string Value, bool Disabled)> Rows(string body)
    {
        var list = new List<(string, string, bool)>();
        foreach (var raw in (body ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var off = line.TrimStart().StartsWith("//");
            if (off) line = line.TrimStart()[2..];
            if (line.Trim().Length == 0) continue;
            var i = line.IndexOf(':');
            list.Add(i < 0 ? (line.Trim(), "", off) : (line[..i].Trim(), line[(i + 1)..].TrimStart(), off));
        }
        return list;
    }

    private static bool IsKey(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string Hide(string v) => v.Length == 0 ? "" : $"•••••• ({v.Length} ký tự)";

    /// <summary>Thay biến trong body: {{today}} yyyy-MM-dd, {{now}} yyyy-MM-ddTHH:mm:ss, {{key}} khoá duy nhất (mỗi lần gọi), {{year}}, {{month}}, {{monthStart}}.</summary>
    public static string Expand(string body)
    {
        var now = DateTime.Now;
        var key = now.ToString("yyyyMMddHHmmssfff") + "-" + Random.Shared.Next(0x1000, 0xFFFF).ToString("x");
        return body.Replace("{{today}}", now.ToString("yyyy-MM-dd")).Replace("{{now}}", now.ToString("yyyy-MM-ddTHH:mm:ss"))
            .Replace("{{key}}", key).Replace("{{year}}", now.Year.ToString()).Replace("{{month}}", now.Month.ToString())
            .Replace("{{monthStart}}", new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd"));
    }

    /// <summary>Kiểm tra body theo bảng trường chuẩn LT3 của form (nếu có trong mẫu): JSON hợp lệ, thiếu trường bắt buộc, trường lạ.</summary>
    public static List<string> Validate(ApiForm f, string body)
    {
        var issues = new List<string>();
        JsonNode? node;
        try { node = JsonNode.Parse(Expand(body)); }
        catch (Exception ex) { issues.Add("Body không phải JSON hợp lệ: " + ex.Message); return issues; }
        var formName = node?["form"]?.GetValue<string>() ?? f.Name;
        var doc = Lt3Templates.Find(formName);
        if (doc is null) return issues;   // form riêng của dự án — không có bảng chuẩn để so
        void Check(JsonObject obj, Lt3Templates.SectionDoc sec, string where)
        {
            foreach (var fd in sec.Fields.Where(x => x.Required && x.Name is not ("detail" or "tax")))
                if (!obj.Any(kv => string.Equals(kv.Key, fd.Name, StringComparison.OrdinalIgnoreCase))) issues.Add($"{where}: thiếu trường bắt buộc \"{fd.Name}\" ({fd.Description})");
            var known = new HashSet<string>(sec.Fields.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in obj) if (!known.Contains(kv.Key) && kv.Key is not ("detail" or "tax")) issues.Add($"{where}: trường \"{kv.Key}\" không có trong tài liệu chuẩn");
        }
        if (doc.Kind == "GetData") return issues;
        var head = doc.Sections.FirstOrDefault(s => s.Name is "header" or "data");
        var data = node?["data"] as JsonArray;
        if (data is null || data.Count == 0) { issues.Add("Thiếu mảng \"data\" (202 — Dữ liệu trống)."); return issues; }
        for (var i = 0; i < data.Count; i++)
        {
            if (data[i] is not JsonObject row) continue;
            if (head is not null) Check(row, head, $"data[{i}]");
            foreach (var sub in doc.Sections.Where(s => s.Name is "detail" or "tax"))
            {
                var arr = row.FirstOrDefault(kv => string.Equals(kv.Key, sub.Name, StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
                if (arr is null) { if (head?.Fields.Any(x => x.Name == sub.Name && x.Required) == true) issues.Add($"data[{i}]: thiếu mảng \"{sub.Name}\""); continue; }
                for (var j = 0; j < arr.Count; j++) if (arr[j] is JsonObject d) Check(d, sub, $"data[{i}].{sub.Name}[{j}]");
            }
        }
        return issues;
    }

    /// <summary>Bung response thành bảng: mảng object (data / data.data…) và dạng nén {structure:{master,detail}, invoices:[{master:[…], detail:[[…]]}]}.</summary>
    public static List<ResultTable> Tables(string text)
    {
        var list = new List<ResultTable>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            Walk(doc.RootElement, "data", list, 0);
        }
        catch { /* không phải JSON */ }
        return list;
    }

    private static void Walk(JsonElement e, string name, List<ResultTable> list, int depth)
    {
        if (depth > 4 || list.Count >= 6) return;
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("structure", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                foreach (var part in st.EnumerateObject())
                {
                    if (part.Value.ValueKind != JsonValueKind.Array) continue;
                    var cols = part.Value.EnumerateArray().Select(x => x.ToString()).ToList();
                    var rows = new List<List<string?>>();
                    foreach (var arrProp in e.EnumerateObject().Where(x => x.Name != "structure" && x.Value.ValueKind == JsonValueKind.Array))
                        foreach (var item in arrProp.Value.EnumerateArray())
                        {
                            if (!item.TryGetProperty(part.Name, out var vals) || vals.ValueKind != JsonValueKind.Array) continue;
                            var arr = vals.EnumerateArray().ToList();
                            if (arr.Count > 0 && arr[0].ValueKind == JsonValueKind.Array) rows.AddRange(arr.Select(r => r.EnumerateArray().Select(Cell).ToList()));
                            else rows.Add(arr.Select(Cell).ToList());
                        }
                    list.Add(new ResultTable(part.Name, cols, rows));
                }
                return;
            }
            foreach (var p in e.EnumerateObject()) if (p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) Walk(p.Value, p.Name, list, depth + 1);
            return;
        }
        if (e.ValueKind != JsonValueKind.Array) return;
        var objs = e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList();
        if (objs.Count == 0) return;
        var columns = new List<string>();
        foreach (var o in objs.Take(200)) foreach (var p in o.EnumerateObject()) if (p.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object) && !columns.Contains(p.Name)) columns.Add(p.Name);
        list.Add(new ResultTable(name, columns, objs.Take(2000).Select(o => columns.Select(c => o.TryGetProperty(c, out var v) ? Cell(v) : null).ToList()).ToList()));
        // mảng con (detail / tax) của các dòng
        var subNames = objs.SelectMany(o => o.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).Select(p => p.Name)).Distinct().ToList();
        foreach (var sn in subNames)
        {
            var subRows = objs.SelectMany(o => o.TryGetProperty(sn, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>()).ToList();
            var tmp = JsonSerializer.SerializeToElement(subRows);
            Walk(tmp, name + "." + sn, list, depth + 1);
        }
    }

    private static string? Cell(JsonElement v) => v.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => v.GetString(), _ => v.ToString() };

    private static string? Pick(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var cur = root;
        foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (cur.ValueKind != JsonValueKind.Object) return null;
            var found = cur.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, seg, StringComparison.OrdinalIgnoreCase));
            if (found.Value.ValueKind == JsonValueKind.Undefined) return null;
            cur = found.Value;
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
    }

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>exp (giây Unix) trong phần payload của JWT.</summary>
    public static DateTime? JwtExpiry(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.TryGetProperty("exp", out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime : null;
        }
        catch { return null; }
    }

    private static string JsonEsc(string s) => JsonSerializer.Serialize(s)[1..^1];
    private static string Short(string s) => s.Length > 300 ? s[..297] + "…" : s;
}
