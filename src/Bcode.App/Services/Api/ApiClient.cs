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
/// Header: "Authorization: &lt;token&gt;" (raw, chuẩn LT3) hoặc "Bearer &lt;token&gt;". Body thay biến {{today}} {{now}} {{key}} {{year}} {{month}} {{monthStart}}.
/// </summary>
public sealed class ApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresUtc)> Tokens = new();

    public sealed record CallResult(int Status, long Ms, string Url, string RequestBody, string ResponseText, bool Ok, string? Message, List<ResultTable> Tables);
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
        var url = Url(p, f.Endpoint);
        var expanded = Expand(body);
        var sw = Stopwatch.StartNew();
        var (status, text) = await PostAsync(p, url, expanded, retryOn401: true, ct);
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
        return new CallResult(status, sw.ElapsedMilliseconds, url, expanded, text, ok, message, Tables(text));
    }

    private static async Task<(int, string)> PostAsync(ApiProject p, string url, string body, bool retryOn401, CancellationToken ct)
    {
        var tk = await GetTokenAsync(p, false, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", p.AuthScheme == "bearer" ? "Bearer " + tk.Token : tk.Token);
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (retryOn401 && (int)res.StatusCode == 401) { ForgetToken(p); return await PostAsync(p, url, body, false, ct); }   // token hết hạn phía server → lấy lại 1 lần
        return ((int)res.StatusCode, text);
    }

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
