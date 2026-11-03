using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlWatch;

public sealed record CapturedEvent(
    DateTime Time,
    string EventName,
    string DatabaseName,
    int Spid,
    string LoginName,
    string HostName,
    string ApplicationName,
    string ObjectName,
    string Text,
    string ModuleText = "");   // phần đầu định nghĩa procedure (CREATE PROCEDURE … tham số) — dựng khung lời gọi khi không có giá trị

/// <summary>
/// Bắt SQL:BatchStarting / RPC:Starting / SP:Starting bằng DMV.
/// Login BMT_FBO có VIEW SERVER STATE, không có ALTER ANY EVENT SESSION,
/// nên không tạo được session Profiler hay Extended Events.
/// </summary>
public sealed class TraceWatcher : IAsyncDisposable
{
    public const string ApplicationName = "SqlWatch";
    private const int MaxTextLength = 16000;

    private readonly SqlConnection _connection;
    private string[] _databases;
    private readonly Dictionary<int, DateTime> _batchSeen = new();
    private readonly HashSet<string> _spSeen = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _started;
    private bool _hasInputBuffer;
    private bool _dbccInputBuffer = true;

    public TraceWatcher(string connectionString, IReadOnlyList<string> databases)
    {
        if (databases.Count is 0 or > 8)
            throw new ArgumentException("Cần từ 1 đến 8 database.");
        _databases = databases.ToArray();
        _connection = new SqlConnection(connectionString);
    }

    public static SqlConnectionStringBuilder CreateBuilder(
        string server, string user, string password, string initialCatalog, string appName) =>
        new()
        {
            DataSource = server.Trim(),
            UserID = user.Trim(),
            Password = password,
            InitialCatalog = initialCatalog,
            ApplicationName = appName,
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            ConnectTimeout = 8,
            Pooling = true
        };

    public static string[] ParseDatabases(string text)
    {
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new InvalidOperationException("Nhập ít nhất một database.");
        if (parts.Length > 8)
            throw new InvalidOperationException("Tối đa 8 database.");

        var unique = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length > 128 || part.IndexOfAny(['\'', '"', ';', '[', ']', '\0', ' ']) >= 0)
                throw new InvalidOperationException("Tên database không hợp lệ: " + part);
            if (!unique.Contains(part, StringComparer.OrdinalIgnoreCase))
                unique.Add(part);
        }
        return unique.ToArray();
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var started = new SqlCommand("SELECT SYSDATETIME();", _connection);
        var clock = await started.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        _started = clock is DateTime time ? time : DateTime.Now;

        await using var bufferProbe = new SqlCommand("SELECT OBJECT_ID(N'sys.dm_exec_input_buffer');", _connection);
        var bufferId = await bufferProbe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        _hasInputBuffer = bufferId is not null and not DBNull;

        await using var check = new SqlCommand(BuildDatabaseCheckSql(), _connection);
        AddDatabaseParameters(check);
        await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        // Database không có trên server này (vd {dự_án}_eInv nằm ở server khác) thì bỏ qua và báo lại, không chặn cả phiên bắt;
        // chỉ lỗi khi không còn database nào để theo dõi.
        var missing = new List<string>();
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var index = 0; index < _databases.Length; index++)
            {
                if (reader.IsDBNull(index))
                    missing.Add(_databases[index]);
            }
        }
        if (missing.Count == _databases.Length)
            throw new InvalidOperationException("Không thấy database nào trên server: " + string.Join(", ", missing) + ".");
        MissingDatabases = missing;
        _databases = _databases.Except(missing, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Database khai báo nhưng không có trên server (đã bỏ qua khi mở).</summary>
    public IReadOnlyList<string> MissingDatabases { get; private set; } = [];

    /// <summary>Database đang theo dõi thật (sau khi bỏ các database không có).</summary>
    public IReadOnlyList<string> Databases => _databases;

    public async Task<IReadOnlyList<CapturedEvent>> PollAsync(CancellationToken cancellationToken)
    {
        var found = new List<CapturedEvent>();
        await ReadBatchesAsync(found, cancellationToken).ConfigureAwait(false);
        await ReadModulesAsync(found, cancellationToken).ConfigureAwait(false);
        found.Sort(static (a, b) =>
        {
            var time = a.Time.CompareTo(b.Time);
            if (time != 0)
                return time;
            var rank = Rank(a.EventName).CompareTo(Rank(b.EventName));
            return rank != 0 ? rank : a.Spid.CompareTo(b.Spid);
        });
        return found;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    private async Task ReadBatchesAsync(List<CapturedEvent> found, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
                s.session_id,
                s.login_name,
                s.host_name,
                s.program_name,
                DB_NAME(p.dbid) AS database_name,
                s.last_request_start_time
            FROM sys.dm_exec_sessions AS s
            INNER JOIN sys.sysprocesses AS p ON p.spid = s.session_id AND p.ecid = 0
            WHERE s.is_user_process = 1
              AND s.session_id <> @@SPID
              AND ISNULL(s.program_name, N'') <> @appName
              AND s.last_request_start_time >= @started
              AND p.dbid IN ({DatabaseIdList()})
            """;

        var changed = new List<SessionHead>();
        await using (var command = new SqlCommand(sql, _connection) { CommandTimeout = 20 })
        {
            command.Parameters.Add("@appName", SqlDbType.NVarChar, 128).Value = ApplicationName;
            command.Parameters.Add("@started", SqlDbType.DateTime2).Value = _started;
            AddDatabaseParameters(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var spid = GetInt(reader, 0);
                var start = reader.GetDateTime(5);
                if (_batchSeen.TryGetValue(spid, out var seen) && seen >= start)
                    continue;

                changed.Add(new SessionHead(
                    spid,
                    start,
                    GetString(reader, 1),
                    GetString(reader, 2),
                    GetString(reader, 3),
                    GetString(reader, 4)));
            }
        }

        foreach (var head in changed)
        {
            var (eventType, text, module) = await ReadClientTextAsync(head.Spid, cancellationToken).ConfigureAwait(false);

            _batchSeen[head.Spid] = head.Start;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            text = TrimText(text);
            var calls = ProcCalls.Find(text);
            var eventName = MapBatchEvent(eventType);
            found.Add(new CapturedEvent(
                head.Start,
                eventName,
                head.DatabaseName,
                head.Spid,
                head.LoginName,
                head.HostName,
                head.ApplicationName,
                calls.Count == 0 ? "" : calls[0].Name,
                text,
                module));

            foreach (var call in calls)
            {
                if (!MarkSp(head.Spid, head.Start, call.Name))
                    continue;
                found.Add(new CapturedEvent(
                    head.Start,
                    "SP:Starting",
                    head.DatabaseName,
                    head.Spid,
                    head.LoginName,
                    head.HostName,
                    head.ApplicationName,
                    call.Name,
                    call.Statement));
            }
        }
    }

    private async Task<(string EventType, string Text, string Module)> ReadClientTextAsync(int spid, CancellationToken cancellationToken)
    {
        // SQL 2008 (không có dm_exec_input_buffer): DBCC INPUTBUFFER — chỉ đọc, cần VIEW SERVER STATE — trả đúng batch client gửi
        // ("declare @0…; select @0=…; exec zrpt_67 @0…"). most_recent_sql_handle bị SQL động bên trong procedure ghi đè (đọc ra
        // "insert into #tct70tmpx…" thay vì câu gọi báo cáo). Lời gọi RPC thì DBCC chỉ có "tên;1" → đọc tiếp như cũ để lấy tên + định nghĩa.
        if (!_hasInputBuffer && _dbccInputBuffer)
        {
            var (bufferType, bufferText) = await ReadInputBufferAsync(spid, cancellationToken).ConfigureAwait(false);
            if (bufferType.Equals("Language Event", StringComparison.OrdinalIgnoreCase) && bufferText.Length > 0)
                return (bufferType, bufferText, "");
        }

        var sql = _hasInputBuffer
            ? "SELECT event_type, event_info FROM sys.dm_exec_input_buffer(@spid, NULL);"
            : """
              SELECT
                  CASE
                      WHEN t.objectid IS NULL OR t.dbid IS NULL OR t.objectid = 0 THEN N'Language Event'
                      ELSE N'RPC Event'
                  END,
                  t.text,
                  t.objectid,
                  OBJECT_SCHEMA_NAME(t.objectid, t.dbid),
                  OBJECT_NAME(t.objectid, t.dbid)
              FROM sys.dm_exec_connections AS c
              CROSS APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) AS t
              WHERE c.session_id = @spid;
              """;

        await using var command = new SqlCommand(sql, _connection) { CommandTimeout = 20 };
        command.Parameters.Add("@spid", SqlDbType.Int).Value = spid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return ("", "", "");
        var eventType = GetString(reader, 0);
        if (_hasInputBuffer || !eventType.Equals("RPC Event", StringComparison.OrdinalIgnoreCase))
            return (eventType, GetString(reader, 1), "");
        // SQL 2008: lời gọi RPC chỉ còn tên procedure (không có giá trị tham số) — giữ phần đầu định nghĩa để dựng khung lời gọi.
        var module = GetString(reader, 1);
        return (eventType, "EXEC " + ModuleName(GetInt(reader, 2), GetString(reader, 3), GetString(reader, 4), module),
            module.Length > 8000 ? module[..8000] : module);
    }

    /// <summary>DBCC INPUTBUFFER(spid): (EventType, EventInfo). Không chạy được (thiếu quyền...) thì tắt hẳn cách này cho phiên bắt.</summary>
    private async Task<(string EventType, string Text)> ReadInputBufferAsync(int spid, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand($"DBCC INPUTBUFFER({spid}) WITH NO_INFOMSGS;", _connection) { CommandTimeout = 20 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return ("", "");
            return (GetString(reader, 0), GetString(reader, 2).Trim());
        }
        catch (SqlException)
        {
            _dbccInputBuffer = false;
            return ("", "");
        }
    }

    /// <summary>Tên procedure: OBJECT_NAME nếu đọc được; login chỉ có VIEW SERVER STATE (không vào được database của dự án, vd
    /// profile trên server khách) thì OBJECT_NAME trả NULL — lấy tên từ dòng CREATE PROCEDURE trong chính nội dung module
    /// (dm_exec_sql_text đọc được chỉ với VIEW SERVER STATE). Không ra tên thì mới dùng "#object_id".</summary>
    private static string ModuleName(int objectId, string schema, string name, string moduleText)
    {
        if (!string.IsNullOrEmpty(name))
            return string.IsNullOrEmpty(schema) ? name : schema + "." + name;
        var match = CreateModule.Match(moduleText);
        if (match.Success)
            return Regex.Replace(match.Groups[1].Value, @"\s+", "").Replace("[", "").Replace("]", "");
        return objectId < 0 ? "(temp procedure)" : "#" + objectId;
    }

    private static readonly Regex CreateModule = new(
        @"\b(?:CREATE|ALTER)\s+(?:PROC(?:EDURE)?|FUNCTION|TRIGGER)\s+((?:\[[^\]]+\]|[\w$#@]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w$#@]+))?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task ReadModulesAsync(List<CapturedEvent> found, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT
                r.session_id,
                r.start_time,
                DB_NAME(p.dbid) AS database_name,
                s.login_name,
                s.host_name,
                s.program_name,
                t.objectid,
                CASE
                    WHEN t.objectid IS NULL OR t.dbid IS NULL THEN NULL
                    ELSE OBJECT_SCHEMA_NAME(t.objectid, t.dbid)
                END AS schema_name,
                CASE
                    WHEN t.objectid IS NULL OR t.dbid IS NULL THEN NULL
                    ELSE OBJECT_NAME(t.objectid, t.dbid)
                END AS object_name,
                LEFT(t.text, 8000) AS module_text
            FROM sys.dm_exec_requests AS r
            INNER JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
            INNER JOIN sys.sysprocesses AS p ON p.spid = r.session_id AND p.ecid = 0
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
            WHERE r.session_id <> @@SPID
              AND ISNULL(s.program_name, N'') <> @appName
              AND r.start_time >= @started
              AND p.dbid IN ({DatabaseIdList()})
              AND t.objectid IS NOT NULL
              AND t.objectid <> 0
            """;

        await using var command = new SqlCommand(sql, _connection) { CommandTimeout = 20 };
        command.Parameters.Add("@appName", SqlDbType.NVarChar, 128).Value = ApplicationName;
        command.Parameters.Add("@started", SqlDbType.DateTime2).Value = _started;
        AddDatabaseParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var spid = GetInt(reader, 0);
            var start = reader.GetDateTime(1);
            var objectId = GetInt(reader, 6);
            var schema = GetString(reader, 7);
            var name = GetString(reader, 8);
            var objectName = ModuleName(objectId, schema, name, GetString(reader, 9));
            if (!MarkSp(spid, start, objectName))
                continue;

            found.Add(new CapturedEvent(
                start,
                "SP:Starting",
                GetString(reader, 2),
                spid,
                GetString(reader, 3),
                GetString(reader, 4),
                GetString(reader, 5),
                objectName,
                "EXEC " + objectName,
                GetString(reader, 9)));
        }
    }

    private string BuildDatabaseCheckSql()
    {
        var columns = new string[_databases.Length];
        for (var i = 0; i < _databases.Length; i++)
            columns[i] = $"DB_ID(@d{i})";
        return "SELECT " + string.Join(", ", columns) + ";";
    }

    private string DatabaseIdList()
    {
        var parts = new string[_databases.Length];
        for (var i = 0; i < _databases.Length; i++)
            parts[i] = $"DB_ID(@d{i})";
        return string.Join(", ", parts);
    }

    private void AddDatabaseParameters(SqlCommand command)
    {
        for (var i = 0; i < _databases.Length; i++)
            command.Parameters.Add("@d" + i, SqlDbType.NVarChar, 128).Value = _databases[i];
    }

    private bool MarkSp(int spid, DateTime start, string objectName)
    {
        var full = spid + "|" + start.Ticks + "|" + objectName.Replace("[", "").Replace("]", "").Trim();
        var shortName = objectName;
        var dot = shortName.LastIndexOf('.');
        if (dot >= 0 && dot < shortName.Length - 1)
            shortName = shortName[(dot + 1)..].Trim();
        var brief = spid + "|" + start.Ticks + "|" + shortName;
        if (!_spSeen.Add(full))
            return false;
        if (!brief.Equals(full, StringComparison.OrdinalIgnoreCase) && !_spSeen.Add(brief))
            return false;
        if (_spSeen.Count > 50000)
            _spSeen.Clear();
        return true;
    }

    private static string MapBatchEvent(string eventType) =>
        eventType.Equals("RPC Event", StringComparison.OrdinalIgnoreCase)
            ? "RPC:Starting"
            : "SQL:BatchStarting";

    private static int Rank(string eventName) => eventName == "SP:Starting" ? 1 : 0;

    private static int GetInt(SqlDataReader reader, int ordinal) =>
        Convert.ToInt32(reader.GetValue(ordinal));

    private static string GetString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";

    private static string TrimText(string text)
    {
        var normalized = text.Replace("\0", "");
        return normalized.Length <= MaxTextLength
            ? normalized
            : normalized[..MaxTextLength] + Environment.NewLine + "-- ... cắt bớt";
    }

    private sealed record SessionHead(
        int Spid,
        DateTime Start,
        string LoginName,
        string HostName,
        string ApplicationName,
        string DatabaseName);
}
