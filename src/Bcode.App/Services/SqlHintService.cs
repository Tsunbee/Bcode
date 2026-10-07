using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Dữ liệu cho gợi ý code SQL lấy từ chính database đang chọn: chữ ký (tên + tham số) của mọi procedure / function / table-function và danh sách tên
/// trong bảng <c>options</c> (kèm giá trị hiện tại). Chỉ ĐỌC. Nạp một lần mỗi database rồi lưu cache (bộ nhớ + <c>%AppData%\Bcode\hint-cache\</c>);
/// những lần sau chỉ hỏi một "chữ ký" rất nhẹ (số lượng + ngày sửa mới nhất) và chỉ nạp lại khi có object mới / đổi — giống cách cây SQL Object làm.
/// </summary>
public sealed class SqlHintService
{
    private readonly DbConnectionService _connections;
    private static readonly ConcurrentDictionary<string, (string Signature, string Json)> Memory = new();

    public SqlHintService(DbConnectionService connections) => _connections = connections;

    /// <summary>Ghi lỗi (nếu có) ra %AppData%\Bcode\hint-cache\hints.log để biết vì sao gợi ý trống — gợi ý là phần phụ nên lỗi không hiện lên giao diện.</summary>
    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(BcodePaths.AppData, "Bcode", "hint-cache");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "hints.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* bỏ qua */ }
    }

    private string CachePath(string stamp)
    {
        var safe = Regex.Replace(stamp, @"[^\w.\-]+", "_");
        if (safe.Length > 120) safe = safe[^120..];
        return Path.Combine(BcodePaths.AppData, "Bcode", "hint-cache", safe + ".json");
    }

    /// <summary>JSON <c>{"r":[[tên,loại,[[tham số,kiểu,cờ]]]],"o":[[tên option,giá trị]]}</c>; cờ: 1 = OUTPUT, 2 = có giá trị mặc định. Loại: P / FN / IF / TF.</summary>
    public async Task<string> GetPayloadJsonAsync(bool sys)
    {
        var stamp = _connections.CurrentStamp(sys);
        if (stamp.Length == 0) return "{\"r\":[],\"o\":[]}";

        string? signature = null;
        try { signature = await GetSignatureAsync(sys); }
        catch (Exception ex) { Log("signature: " + ex.Message); /* offline hoặc server cũ — dùng bản lưu nếu có, không thì vẫn nạp (bên dưới) */ }

        if (Memory.TryGetValue(stamp, out var mem) && (signature is null || mem.Signature == signature)) return mem.Json;
        var disk = ReadDisk(stamp);
        if (disk is { } d && (signature is null || d.Signature == signature)) { Memory[stamp] = d; return d.Json; }
        if (signature is null && disk is { } stale) return stale.Json;          // không hỏi được chữ ký: dùng bản lưu cũ
        signature ??= "";                                                       // chưa có bản lưu: vẫn nạp (rồi lưu trong bộ nhớ), không để gợi ý trống

        string json;
        try { json = await BuildAsync(sys); }
        catch (Exception ex) { Log("build: " + ex.Message); throw; }
        Memory[stamp] = (signature, json);
        WriteDisk(stamp, signature, json);
        return json;
    }

    private async Task<string> GetSignatureAsync(bool sys)
    {
        await using var conn = _connections.CreateConnection(sys);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT CAST(COUNT(*) AS varchar(20)) + '|' + ISNULL(CONVERT(varchar(30), MAX(modify_date), 126), '') FROM sys.objects WHERE type IN ('P','FN','IF','TF') AND is_ms_shipped = 0;", conn);
        return (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
    }

    private async Task<string> BuildAsync(bool sys)
    {
        const string objSql = "SELECT o.object_id, s.name, o.name, o.type FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE o.type IN ('P','FN','IF','TF') AND o.is_ms_shipped = 0;";
        const string parSql = @"
SELECT p.object_id, p.name,
  CASE WHEN t.name IN ('varchar','char','varbinary','binary') THEN t.name + '(' + CASE WHEN p.max_length = -1 THEN 'MAX' ELSE CAST(p.max_length AS varchar(10)) END + ')'
       WHEN t.name IN ('nvarchar','nchar') THEN t.name + '(' + CASE WHEN p.max_length = -1 THEN 'MAX' ELSE CAST(p.max_length / 2 AS varchar(10)) END + ')'
       WHEN t.name IN ('numeric','decimal') THEN t.name + '(' + CAST(p.precision AS varchar(5)) + ',' + CAST(p.scale AS varchar(5)) + ')'
       ELSE t.name END,
  CAST(p.is_output AS int) + CASE WHEN p.has_default_value = 1 THEN 2 ELSE 0 END
FROM sys.parameters p JOIN sys.objects o ON o.object_id = p.object_id JOIN sys.types t ON t.user_type_id = p.user_type_id
WHERE p.parameter_id > 0 AND o.type IN ('P','FN','IF','TF') AND o.is_ms_shipped = 0
ORDER BY p.object_id, p.parameter_id;";

        await using var conn = _connections.CreateConnection(sys);
        await conn.OpenAsync();

        var ps = new Dictionary<int, List<object[]>>();
        await using (var cmd = new SqlCommand(parSql, conn) { CommandTimeout = 120 })
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var id = r.GetInt32(0);
                if (!ps.TryGetValue(id, out var list)) ps[id] = list = new();
                list.Add(new object[] { r.GetString(1), r.GetString(2), r.GetInt32(3) });
            }

        // sys.parameters.has_default_value luôn = 0 với procedure/function T-SQL → đọc phần đầu định nghĩa để biết tham số nào có "= giá trị mặc định" (tham số tuỳ chọn).
        const string defSql = @"
SELECT m.object_id, LEFT(m.definition, 4000)
FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
WHERE o.type IN ('P','FN','IF','TF') AND o.is_ms_shipped = 0 AND m.definition IS NOT NULL
  AND EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = o.object_id AND p.parameter_id > 0);";
        try
        {
            await using var cmd = new SqlCommand(defSql, conn) { CommandTimeout = 120 };
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var id = r.GetInt32(0);
                if (!ps.TryGetValue(id, out var plist) || r.IsDBNull(1)) continue;
                var withDefault = ParametersWithDefault(r.GetString(1));
                foreach (var p in plist)
                    if (withDefault.Contains(((string)p[0]).ToLowerInvariant())) p[2] = (int)p[2] | 2;
            }
        }
        catch (Exception ex) { Log("defaults: " + ex.Message); /* không đọc được thì coi mọi tham số là bắt buộc */ }

        var routines = new List<object>();
        await using (var cmd = new SqlCommand(objSql, conn) { CommandTimeout = 120 })
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var schema = r.GetString(1);
                var name = schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? r.GetString(2) : schema + "." + r.GetString(2);
                routines.Add(new object[] { name, r.GetString(3).Trim(), ps.GetValueOrDefault(r.GetInt32(0)) ?? new List<object[]>() });
            }

        // Tên + giá trị của bảng options (nếu database có): để gợi ý khi gõ  FROM options WHERE name = '…'
        var options = new List<object>();
        try
        {
            await using var cmd = new SqlCommand("SELECT name, LEFT(CAST(val AS nvarchar(200)), 80) FROM options ORDER BY name;", conn) { CommandTimeout = 60 };
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) options.Add(new object[] { r.GetValue(0)?.ToString()?.Trim() ?? "", r.IsDBNull(1) ? "" : r.GetString(1).Trim() });
        }
        catch (SqlException) { /* không có bảng options (vd Sys Data) */ }

        return System.Text.Json.JsonSerializer.Serialize(new { r = routines, o = options });
    }

    /// <summary>Tên (chữ thường) các tham số có giá trị mặc định trong phần đầu định nghĩa (đến AS). Tách theo dấu phẩy ở cấp ngoài cùng (bỏ qua ngoặc và chuỗi), đoạn nào có dấu "=" ngoài ngoặc/chuỗi là có mặc định.</summary>
    public static HashSet<string> ParametersWithDefault(string definition)
    {
        var result = new HashSet<string>();
        var text = Regex.Replace(Regex.Replace(definition, @"--[^\n]*", " "), @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var m = Regex.Match(text, @"\b(?:create|alter)\s+(?:or\s+alter\s+)?(?:proc|procedure|function)\s+[^\s(@]+", RegexOptions.IgnoreCase);
        if (!m.Success) return result;
        var from = m.Index + m.Length;
        // AS bắt đầu thân: AS không đứng ngay sau "@tham_số" (kiểu "@d AS smalldatetime" là kiểu dữ liệu)
        var end = text.Length;
        foreach (Match a in Regex.Matches(text[from..], @"\bas\b", RegexOptions.IgnoreCase))
            if (!Regex.IsMatch(text.Substring(from, a.Index), @"@\w+\s*$")) { end = from + a.Index; break; }
        var header = text[from..end].Trim();
        if (header.StartsWith('('))
        {
            var depth = 0; var close = -1;
            for (var i = 0; i < header.Length && close < 0; i++) { if (header[i] == '(') depth++; else if (header[i] == ')' && --depth == 0) close = i; }
            header = close > 0 ? header[1..close] : header[1..];
        }
        // tách theo dấu phẩy cấp ngoài cùng
        var segs = new List<string>(); var sb = new StringBuilder(); var d = 0; var q = false;
        foreach (var c in header)
        {
            if (c == '\'') q = !q;
            else if (!q) { if (c == '(') d++; else if (c == ')') d--; else if (c == ',' && d == 0) { segs.Add(sb.ToString()); sb.Clear(); continue; } }
            sb.Append(c);
        }
        segs.Add(sb.ToString());
        foreach (var seg in segs)
        {
            var name = Regex.Match(seg, @"@\w+");
            if (!name.Success) continue;
            var depth = 0; var inQ = false; var hasEq = false;
            foreach (var c in seg[(name.Index + name.Length)..])
            {
                if (c == '\'') inQ = !inQ;
                else if (!inQ) { if (c == '(') depth++; else if (c == ')') depth--; else if (c == '=' && depth == 0) { hasEq = true; break; } }
            }
            if (hasEq) result.Add(name.Value.ToLowerInvariant());
        }
        return result;
    }

    private (string Signature, string Json)? ReadDisk(string stamp)
    {
        try
        {
            var path = CachePath(stamp);
            if (!File.Exists(path)) return null;
            var all = File.ReadAllText(path, Encoding.UTF8);
            var nl = all.IndexOf('\n');
            return nl < 0 ? null : (all[..nl], all[(nl + 1)..]);
        }
        catch { return null; }
    }

    private void WriteDisk(string stamp, string signature, string json)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var path = CachePath(stamp);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, signature + "\n" + json, Encoding.UTF8);
                File.Move(tmp, path, overwrite: true);
            }
            catch { /* không lưu được thì lần sau nạp lại */ }
        });
    }
}
