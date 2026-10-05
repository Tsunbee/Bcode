using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Services;

/// <summary>Một phiên bản đã lưu của một object (procedure / function / view / trigger).</summary>
public sealed record SqlHistoryEntry(
    string Id, DateTime SavedAt, string Project, string Server, string Database, string Kind, string Schema, string Name,
    string Action, string Machine, string User, string Ip, int Length);

/// <summary>Một object tìm thấy trong script SQL (câu CREATE/ALTER PROCEDURE|FUNCTION|VIEW|TRIGGER) cùng nội dung batch của nó.</summary>
public sealed record SqlTrackedObject(string Kind, string Schema, string Name, string Batch)
{
    public string Key => $"{Schema}.{Name}";
    public string Qualified => $"[{Schema}].[{Name}]";
}

/// <summary>Một object có lịch sử — dùng cho danh sách chọn ở màn hình lịch sử.</summary>
public sealed record SqlHistoryObject(string Project, string Database, string Key, string Kind, int Count, DateTime Last);

/// <summary>
/// Lịch sử sửa procedure / function / view / trigger làm trong SQL Query. Mỗi lần chạy script có câu CREATE/ALTER (không lỗi) thì
/// lưu lại bản vừa áp dụng; lần đầu gặp một object thì lưu trước "bản gốc" lấy từ database (OBJECT_DEFINITION) để luôn có cái so sánh.
/// Mỗi phiên bản kèm dự án (workspace), database, tên máy, tài khoản Windows và IP máy sửa — để biết ai sửa ở đâu khi thư mục lịch sử
/// đặt trên ổ mạng dùng chung.
///
/// Cấu trúc: <c>{gốc}\{dự án}\{database}\{schema.tên}\{yyyyMMdd-HHmmss-fff}.sql</c> + <c>.json</c> (siêu dữ liệu). Thư mục gốc lấy từ
/// UiTemplate.HistoryPath (khai báo ở màn hình "Giao diện (Template)"), trống thì dùng <see cref="DefaultRoot"/>. Mọi thao tác ghi đều
/// "best effort": lịch sử không bao giờ được làm hỏng việc chạy script.
/// </summary>
public static class SqlHistoryService
{
    /// <summary>Số phiên bản giữ lại cho mỗi object.</summary>
    private const int MaxPerObject = 300;

    /// <summary>Thư mục dự phòng khi dự án chưa khai báo Source/Program Path: %AppData%\Bcode\sql-history.</summary>
    public static string DefaultRoot => Path.Combine(BcodePaths.AppData, "Bcode", "sql-history");

    private static string? CustomRoot => string.IsNullOrWhiteSpace(UiTemplate.Current.HistoryPath) ? null : UiTemplate.Current.HistoryPath.Trim();

    /// <summary>Thư mục của dự án dùng để đặt "History": Source Path, không có thì Program Path; null nếu dự án chưa khai báo cái nào.</summary>
    public static string? ProjectFolder(Workspace? ws)
    {
        if (ws is null) return null;
        if (!string.IsNullOrWhiteSpace(ws.SourcePath)) return ws.SourcePath.Trim();
        if (!string.IsNullOrWhiteSpace(ws.ProgramPath)) return ws.ProgramPath.Trim();
        return null;
    }

    /// <summary>
    /// Thư mục gốc lưu lịch sử của <paramref name="ws"/>. Mặc định = <c>{Source Path của dự án}\History</c> (vd
    /// \\172.168.5.14\CustomerPro\FBI\KOG\FBISP24\History) — tự tạo khi có bản đầu tiên, nên mọi máy cùng dự án thấy chung lịch sử.
    /// Người dùng khai báo thư mục riêng ở Template thì dùng thư mục đó (thêm tầng tên dự án vì dùng chung nhiều dự án).
    /// Dự án chưa có Source/Program Path thì rơi về <see cref="DefaultRoot"/>.
    /// </summary>
    public static string RootFor(Workspace? ws)
    {
        if (CustomRoot is { } custom) return custom;
        if (ProjectFolder(ws) is { } folder) return Path.Combine(folder, "History");
        return DefaultRoot;
    }

    /// <summary>Thư mục chứa các database của một dự án (đã gồm tầng tên dự án khi dùng thư mục chung).</summary>
    private static string BaseFor(Workspace? ws, string project)
    {
        if (CustomRoot is { } custom) return Path.Combine(custom, Safe(project));
        if (ws is not null && ProjectOf(ws).Equals(project, StringComparison.OrdinalIgnoreCase) && ProjectFolder(ws) is { } folder)
            return Path.Combine(folder, "History");
        return Path.Combine(DefaultRoot, Safe(project));
    }

    // ------------------------------------------------------------------ nhận diện câu CREATE/ALTER

    private static readonly Regex GoLine = new(@"^[ \t]*GO[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex Header = new(
        @"^(?<act>CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(?<kind>PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\s+(?<name>(?:\[[^\]]+\]|[\w$#@]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w$#@]+))?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Tìm các object được tạo/sửa trong <paramref name="script"/> (tách theo dòng GO, bỏ comment đầu batch).</summary>
    public static List<SqlTrackedObject> Detect(string script)
    {
        var found = new List<SqlTrackedObject>();
        if (string.IsNullOrWhiteSpace(script)) return found;

        foreach (var raw in GoLine.Split(script.Replace("\r\n", "\n").Replace("\r", "\n")))
        {
            var batch = raw.Trim('\n', ' ', '\t');
            var body = StripLeadingComments(batch);
            var m = Header.Match(body);
            if (!m.Success) continue;

            var kind = m.Groups["kind"].Value.ToUpperInvariant() switch
            {
                "PROC" or "PROCEDURE" => "Procedure",
                "FUNCTION" => "Function",
                "VIEW" => "View",
                _ => "Trigger",
            };
            var parts = m.Groups["name"].Value.Split('.').Select(p => p.Trim().Trim('[', ']')).Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0) continue;
            var schema = parts.Length > 1 ? parts[^2] : "dbo";
            found.Add(new SqlTrackedObject(kind, schema, parts[^1], batch));
        }
        return found;
    }

    private static string StripLeadingComments(string s)
    {
        while (true)
        {
            s = s.TrimStart();
            if (s.StartsWith("--", StringComparison.Ordinal))
            {
                var nl = s.IndexOf('\n');
                if (nl < 0) return "";
                s = s[(nl + 1)..];
            }
            else if (s.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = s.IndexOf("*/", 2, StringComparison.Ordinal);
                if (end < 0) return "";
                s = s[(end + 2)..];
            }
            else return s;
        }
    }

    // ------------------------------------------------------------------ máy / người sửa

    private static string? _ip;

    /// <summary>IPv4 của máy này (ưu tiên card mạng có gateway); "" nếu không xác định.</summary>
    public static string LocalIp()
    {
        if (_ip is not null) return _ip;
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)));
            foreach (var n in nics)
                foreach (var u in n.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(u.Address))
                        return _ip = u.Address.ToString();
        }
        catch { /* không đọc được card mạng */ }
        return _ip = "";
    }

    // ------------------------------------------------------------------ ghi

    public static string ProjectOf(Workspace ws) => !string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.ProjectId.Trim() : ws.Name.Trim();

    private static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return t.Length == 0 ? "_" : t;
    }

    private static string ObjectFolder(Workspace? ws, string project, string database, string key) =>
        Path.Combine(BaseFor(ws, project), Safe(database), Safe(key));

    public static bool HasAny(Workspace ws, bool useSys, SqlTrackedObject obj)
    {
        try
        {
            var folder = ObjectFolder(ws, ProjectOf(ws), useSys ? ws.SysDatabase : ws.AppDatabase, obj.Key);
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.sql").Any();
        }
        catch { return false; } // ổ mạng rớt: coi như chưa có, Record sau đó cũng sẽ tự bỏ qua
    }

    /// <summary>Lưu một phiên bản; im lặng bỏ qua khi lỗi hoặc nội dung trùng bản mới nhất.</summary>
    public static void Record(Workspace ws, bool useSys, SqlTrackedObject obj, string content, string action)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            var db = useSys ? ws.SysDatabase : ws.AppDatabase;
            var folder = ObjectFolder(ws, ProjectOf(ws), db, obj.Key);
            Directory.CreateDirectory(folder); // tự tạo cả thư mục History lần đầu

            var newest = Directory.EnumerateFiles(folder, "*.sql").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (newest is not null && File.ReadAllText(newest) == content) return; // chạy lại không sửa gì — không thêm bản trùng

            var id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            File.WriteAllText(Path.Combine(folder, id + ".sql"), content, new UTF8Encoding(false));
            var meta = new
            {
                project = ProjectOf(ws), server = ws.Server, database = db, kind = obj.Kind, schema = obj.Schema, name = obj.Name,
                action, machine = Environment.MachineName, user = Environment.UserName, ip = LocalIp(),
                savedAt = DateTime.Now.ToString("o"),
            };
            File.WriteAllText(Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(meta), new UTF8Encoding(false));

            var old = Directory.EnumerateFiles(folder, "*.sql").OrderByDescending(f => f, StringComparer.Ordinal).Skip(MaxPerObject).ToList();
            foreach (var f in old)
            {
                File.Delete(f);
                var j = Path.ChangeExtension(f, ".json");
                if (File.Exists(j)) File.Delete(j);
            }
        }
        catch { /* hết ổ đĩa / không có quyền / ổ mạng rớt — lịch sử chỉ là tiện ích */ }
    }

    // ------------------------------------------------------------------ đọc

    /// <summary>Các object có lịch sử (mới sửa gần nhất trước): của dự án đang chọn, hoặc mọi dự án khi dùng thư mục chung do người dùng khai báo.</summary>
    public static List<SqlHistoryObject> ListObjects(Workspace? ws)
    {
        var result = new List<SqlHistoryObject>();
        try
        {
            // (tên dự án, thư mục chứa các database của dự án đó)
            var projects = new List<(string Name, string Dir)>();
            if (CustomRoot is { } custom)
            {
                if (Directory.Exists(custom))
                    projects.AddRange(Directory.EnumerateDirectories(custom).Select(d => (Path.GetFileName(d), d)));
            }
            else if (ws is not null && Directory.Exists(BaseFor(ws, ProjectOf(ws))))
                projects.Add((ProjectOf(ws), BaseFor(ws, ProjectOf(ws))));

            foreach (var (projectName, projectDir) in projects)
                foreach (var dbDir in Directory.EnumerateDirectories(projectDir))
                    foreach (var objDir in Directory.EnumerateDirectories(dbDir))
                    {
                        var files = Directory.EnumerateFiles(objDir, "*.sql").OrderByDescending(f => f, StringComparer.Ordinal).ToList();
                        if (files.Count == 0) continue;
                        var meta = ReadMeta(files[0]);
                        result.Add(new SqlHistoryObject(
                            meta?.Project ?? projectName, meta?.Database ?? Path.GetFileName(dbDir),
                            Path.GetFileName(objDir), meta?.Kind ?? "", files.Count, meta?.SavedAt ?? File.GetLastWriteTime(files[0])));
                    }
        }
        catch { /* thư mục đọc không được */ }
        return result.OrderByDescending(o => o.Last).ToList();
    }

    /// <summary>Các phiên bản của một object, mới nhất trước.</summary>
    public static List<SqlHistoryEntry> List(Workspace? ws, string project, string database, string key)
    {
        var list = new List<SqlHistoryEntry>();
        try
        {
            var folder = ObjectFolder(ws, project, database, key);
            if (!Directory.Exists(folder)) return list;
            foreach (var file in Directory.EnumerateFiles(folder, "*.sql").OrderByDescending(f => f, StringComparer.Ordinal))
            {
                var m = ReadMeta(file);
                var id = Path.GetFileNameWithoutExtension(file);
                var savedAt = m?.SavedAt ?? (DateTime.TryParseExact(id, "yyyyMMdd-HHmmss-fff", null, System.Globalization.DateTimeStyles.None, out var p) ? p : File.GetLastWriteTime(file));
                list.Add(new SqlHistoryEntry(id, savedAt, m?.Project ?? project, m?.Server ?? "", m?.Database ?? database, m?.Kind ?? "", m?.Schema ?? "", m?.Name ?? key,
                    m?.Action ?? "", m?.Machine ?? "", m?.User ?? "", m?.Ip ?? "", (int)new FileInfo(file).Length));
            }
        }
        catch { }
        return list;
    }

    public static string? Read(Workspace? ws, string project, string database, string key, string id)
    {
        try
        {
            if (!Regex.IsMatch(id, @"^[0-9\-]+$")) return null; // id đến từ trang web: chặn "..\" thoát khỏi thư mục lịch sử
            var file = Path.Combine(ObjectFolder(ws, project, database, key), id + ".sql");
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch { return null; }
    }

    public static string FolderOf(Workspace? ws, string project, string database, string key) => ObjectFolder(ws, project, database, key);

    private sealed record Meta(string Project, string Server, string Database, string Kind, string Schema, string Name, string Action, string Machine, string User, string Ip, DateTime SavedAt);

    private static Meta? ReadMeta(string sqlFile)
    {
        try
        {
            var json = Path.ChangeExtension(sqlFile, ".json");
            if (!File.Exists(json)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return new Meta(S("project"), S("server"), S("database"), S("kind"), S("schema"), S("name"), S("action"), S("machine"), S("user"), S("ip"),
                DateTime.TryParse(S("savedAt"), out var d) ? d : File.GetLastWriteTime(sqlFile));
        }
        catch { return null; }
    }
}
