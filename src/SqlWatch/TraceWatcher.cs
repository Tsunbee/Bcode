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
    string ModuleText = "",    // phần đầu định nghĩa procedure (CREATE PROCEDURE … tham số) — dựng khung lời gọi khi không có giá trị
    bool FullPending = false); // Text mới là 4000 ký tự đầu + TruncatedNote, chưa tìm hết các nguồn — TraceWatcher.ResolveFullAsync khi xem / copy

/// <summary>
/// Bắt SQL:BatchStarting / RPC:Starting / SP:Starting bằng DMV.
/// Login BMT_FBO có VIEW SERVER STATE, không có ALTER ANY EVENT SESSION,
/// nên không tạo được session Profiler hay Extended Events.
/// </summary>
public sealed class TraceWatcher : IAsyncDisposable
{
    // Câu của chính SqlWatch và của công cụ Bcode / FCode (program_name "Bcode…", "FCode…") không đưa lên lưới — chỉ xem câu của ứng dụng.
    public const string ApplicationName = "SqlWatch";

    private readonly SqlConnection _connection;
    private string[] _databases;
    private readonly Dictionary<int, DateTime> _batchSeen = new();
    private readonly HashSet<string> _spSeen = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _started;
    // Mốc last_request_start_time lớn nhất đã đọc: mỗi nhịp chỉ hỏi phiên có lệnh mới từ mốc này (lùi 1 giây cho chắc, _batchSeen lọc trùng)
    // thay vì mọi phiên đã chạy lệnh từ lúc Bắt đầu — server đông người thì mỗi nhịp trả về ít dòng hơn hẳn.
    private DateTime _watermark;
    // Procedure đang chạy đã lấy nội dung (spid|start|objectid): nhịp sau chỉ còn dò, không kéo lại 8000 ký tự nội dung mỗi nhịp.
    private readonly HashSet<string> _moduleSeen = new(StringComparer.Ordinal);
    // Một kết nối dùng chung cho nhịp đọc và việc tìm cả câu khi người dùng xem / copy (SqlConnection không chạy song song 2 lệnh).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _statsLookups;
    private bool _hasInputBuffer;
    private bool _dbccInputBuffer = true;

    /// <summary>Số lần tìm bản đầy đủ của câu dài trong dm_exec_query_stats tối đa mỗi nhịp đọc; câu dài hơn nữa trong nhịp đó để tới khi xem / copy.</summary>
    private const int StatsLookupsPerPoll = 2;

    /// <summary>USER ID (program_name = u_id mà Fast gắn) — lọc ngay khi đọc trên server: server đông người thì bớt tải, bớt đọc
    /// DBCC INPUTBUFFER cho phiên của người khác. "" = mọi user. Đổi lúc đang bắt thì áp từ nhịp đọc kế tiếp (đọc lại từ lúc Bắt đầu như trước).</summary>
    public string UserIdFilter
    {
        get => _userIdFilter;
        set
        {
            value ??= "";
            if (value.Trim().Equals(_userIdFilter.Trim(), StringComparison.Ordinal))
                return;
            _userIdFilter = value;
            _watermark = _started;
        }
    }
    private string _userIdFilter = "";

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
        _watermark = _started;

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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _statsLookups = 0;
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            await ReadBatchesAsync(found, cancellationToken).ConfigureAwait(false);
            await ReadModulesAsync(found, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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

    /// <summary>Kết nối đang mở được (false sau lỗi nặng: mất mạng, server khởi động lại… — nhịp sau tự mở lại).</summary>
    public bool IsConnected => _connection.State == ConnectionState.Open;

    /// <summary>Kết nối bị rớt (lỗi class ≥ 20 thì SqlClient đóng kết nối) → mở lại, giữ nguyên mốc Bắt đầu / watermark / các dòng đã thấy.</summary>
    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection.State == ConnectionState.Open)
            return;
        if (_connection.State != ConnectionState.Closed)
            _connection.Close();
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lỗi tạm thời khi đọc — thử lại nhịp sau là được, không phải lỗi cấu hình / quyền: database không ONLINE (offline, đang
    /// restore / recovery, single user đang bận…), mất mạng / kết nối rớt, hết giờ, deadlock / chờ khoá, server đang tắt / thiếu bộ nhớ.</summary>
    public static bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SqlException sql:
                    if (TransientNumbers.Contains(sql.Number))
                        return true;
                    foreach (SqlError error in sql.Errors)
                    {
                        if (error.Class >= 20 || TransientNumbers.Contains(error.Number))
                            return true;
                    }
                    return false;
                case IOException or System.Net.Sockets.SocketException or TimeoutException:
                    return true;
            }
        }
        return false;
    }

    private static readonly HashSet<int> TransientNumbers =
    [
        // database không mở được lúc này
        942, 943, 945, 922, 924, 927, 952, 913, 4060,
        // mạng / kết nối / hết giờ
        -2, -1, 0, 2, 26, 40, 53, 64, 121, 233, 258, 1231, 10053, 10054, 10060, 10061, 11001,
        // khoá, tài nguyên, server tắt / tạm dừng
        1205, 1222, 701, 8645, 6005, 6006, 17142,
    ];

    /// <summary>Biểu thức sql_handle "an toàn" cho sys.dm_exec_sql_text: handle của procedure / function / trigger (byte đầu 0x03, byte 3-4 = dbid)
    /// thuộc database không ONLINE thì thay bằng NULL (APPLY bỏ dòng đó). dm_exec_sql_text đọc nội dung module từ chính database của nó —
    /// database đã OFFLINE / đang restore… thì cả câu lỗi 942 ("Database '…' cannot be opened because it is offline"), dù database đó không
    /// nằm trong danh sách theo dõi (vd most_recent_sql_handle cũ của 1 kết nối từng gọi procedure ở database đó; filter dbid chạy sau APPLY).
    /// Bỏ qua dbid 32767 (resource db — DATABASEPROPERTYEX trả NULL nhưng luôn đọc được). Đã thử trên SQL 2008 R2 / 2014 / 2016.</summary>
    private static string SafeHandle(string handle) =>
        $"""
        CASE WHEN SUBSTRING({handle}, 1, 1) = 0x03
                  AND CONVERT(int, SUBSTRING({handle}, 4, 1) + SUBSTRING({handle}, 3, 1)) <> 32767
                  AND ISNULL(CONVERT(nvarchar(60), DATABASEPROPERTYEX(DB_NAME(CONVERT(int, SUBSTRING({handle}, 4, 1) + SUBSTRING({handle}, 3, 1))), 'Status')), N'') <> N'ONLINE'
             THEN NULL ELSE {handle} END
        """;

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
              AND ISNULL(s.program_name, N'') NOT LIKE N'Bcode%'
              AND ISNULL(s.program_name, N'') NOT LIKE N'FCode%'
              AND (@uid = N'' OR ISNULL(s.program_name, N'') = @uid)
              AND s.last_request_start_time >= @since
              AND p.dbid IN ({DatabaseIdList()})
            """;

        var since = _watermark.AddSeconds(-1) > _started ? _watermark.AddSeconds(-1) : _started;
        var changed = new List<SessionHead>();
        await using (var command = new SqlCommand(sql, _connection) { CommandTimeout = 20 })
        {
            command.Parameters.Add("@appName", SqlDbType.NVarChar, 128).Value = ApplicationName;
            command.Parameters.Add("@uid", SqlDbType.NVarChar, 128).Value = UserIdFilter.Trim();
            command.Parameters.Add("@since", SqlDbType.DateTime2).Value = since;
            AddDatabaseParameters(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var spid = GetInt(reader, 0);
                var start = reader.GetDateTime(5);
                if (start > _watermark)
                    _watermark = start;
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
            var (eventType, text, module, pending, current) = await ReadClientTextAsync(head.Spid, head.Start, cancellationToken).ConfigureAwait(false);

            // Câu đọc ở bước này có thể đã là của lệnh MỚI HƠN (client gửi tiếp sau lúc dò danh sách phiên ở trên — nhất là khi phải đọc
            // nhiều phiên lần lượt, hoặc server ở xa). Trước đây vẫn gắn giờ của lệnh cũ và chỉ nhớ giờ cũ → nhịp sau thấy giờ mới, đọc lại
            // đúng câu đó lần nữa: lưới có 2 dòng trùng, giờ dòng đầu sai. Giờ lấy giờ bắt đầu đọc cùng lúc với câu (current).
            var start = current > head.Start ? current : head.Start;
            if (start > _watermark)
                _watermark = start;
            _batchSeen[head.Spid] = start;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            text = TrimText(text);
            var calls = ProcCalls.Find(text);
            var eventName = MapBatchEvent(eventType);
            found.Add(new CapturedEvent(
                start,
                eventName,
                head.DatabaseName,
                head.Spid,
                head.LoginName,
                head.HostName,
                head.ApplicationName,
                calls.Count == 0 ? "" : calls[0].Name,
                text,
                module,
                pending));

            foreach (var call in calls)
            {
                if (!MarkSp(head.Spid, start, call.Name))
                    continue;
                found.Add(new CapturedEvent(
                    start,
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

    /// <summary>Nội dung lệnh gần nhất của phiên + Start = last_request_start_time đọc CÙNG LÚC với nội dung (cùng câu / cùng batch trên server)
    /// — lớn hơn giờ lúc dò danh sách phiên nghĩa là nội dung này đã là của lệnh mới hơn (DateTime.MinValue = không đọc được giờ).</summary>
    private async Task<(string EventType, string Text, string Module, bool Pending, DateTime Start)> ReadClientTextAsync(int spid, DateTime start, CancellationToken cancellationToken)
    {
        // SQL 2008 (không có dm_exec_input_buffer): DBCC INPUTBUFFER — chỉ đọc, cần VIEW SERVER STATE — trả đúng batch client gửi
        // ("declare @0…; select @0=…; exec zrpt_67 @0…"). most_recent_sql_handle bị SQL động bên trong procedure ghi đè (đọc ra
        // "insert into #tct70tmpx…" thay vì câu gọi báo cáo). Lời gọi RPC thì DBCC chỉ có "tên;1" → đọc tiếp như cũ để lấy tên + định nghĩa.
        if (!_hasInputBuffer && _dbccInputBuffer)
        {
            var (bufferType, bufferText, truncated, bufferStart) = await ReadInputBufferAsync(spid, cancellationToken).ConfigureAwait(false);
            if (bufferType.Equals("Language Event", StringComparison.OrdinalIgnoreCase) && bufferText.Length > 0)
            {
                if (bufferStart > start)
                    start = bufferStart;
                if (!truncated)
                    return (bufferType, bufferText, "", false, start);
                // Lúc bắt chỉ tìm ở nguồn rẻ (câu đang chạy của phiên) + query_stats tối đa StatsLookupsPerPoll lần / nhịp; quét plan cache
                // (đọc text mọi câu ad-hoc — server Fast có hàng chục nghìn câu, nặng) để tới khi người dùng xem / copy dòng đó.
                var useStats = _statsLookups < StatsLookupsPerPoll;
                if (useStats)
                    _statsLookups++;
                var full = await FindFullBatchAsync(_connection, spid, start, bufferText, useStats, useCache: false, cancellationToken).ConfigureAwait(false);
                return full is not null
                    ? (bufferType, full, "", false, start)
                    : (bufferType, bufferText + Environment.NewLine + TruncatedNote, "", true, start);
            }
        }

        // last_request_start_time đọc trong cùng câu với nội dung (xem ReadBatchesAsync).
        var sql = _hasInputBuffer
            ? """
              SELECT i.event_type, i.event_info, s.last_request_start_time
              FROM sys.dm_exec_sessions AS s
              CROSS APPLY sys.dm_exec_input_buffer(s.session_id, NULL) AS i
              WHERE s.session_id = @spid;
              """
            : """
              SELECT
                  CASE
                      WHEN t.objectid IS NULL OR t.dbid IS NULL OR t.objectid = 0 THEN N'Language Event'
                      ELSE N'RPC Event'
                  END,
                  t.text,
                  t.objectid,
                  OBJECT_SCHEMA_NAME(t.objectid, t.dbid),
                  OBJECT_NAME(t.objectid, t.dbid),
                  s.last_request_start_time
              FROM sys.dm_exec_connections AS c
              INNER JOIN sys.dm_exec_sessions AS s ON s.session_id = c.session_id
              CROSS APPLY sys.dm_exec_sql_text(
              """ + SafeHandle("c.most_recent_sql_handle") + """
              ) AS t
              WHERE c.session_id = @spid;
              """;

        await using var command = new SqlCommand(sql, _connection) { CommandTimeout = 20 };
        command.Parameters.Add("@spid", SqlDbType.Int).Value = spid;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return ("", "", "", false, DateTime.MinValue);
        var eventType = GetString(reader, 0);
        if (_hasInputBuffer)
            return (eventType, GetString(reader, 1), "", false, GetTime(reader, 2));
        var current = GetTime(reader, 5);
        if (!eventType.Equals("RPC Event", StringComparison.OrdinalIgnoreCase))
            return (eventType, GetString(reader, 1), "", false, current);
        // SQL 2008: lời gọi RPC chỉ còn tên procedure (không có giá trị tham số) — giữ phần đầu định nghĩa để dựng khung lời gọi.
        var module = GetString(reader, 1);
        return (eventType, "EXEC " + ModuleName(GetInt(reader, 2), GetString(reader, 3), GetString(reader, 4), module),
            module.Length > 8000 ? module[..8000] : module, false, current);
    }

    /// <summary>DBCC INPUTBUFFER(spid): (EventType, EventInfo, bị cắt, giờ bắt đầu lệnh). EventInfo chỉ là nvarchar(4000) — câu dài hơn bị cắt
    /// ở ký tự 4000 (Truncated = true). Giờ bắt đầu (last_request_start_time) đọc ngay trước DBCC trong cùng batch — nội dung đọc sau đó
    /// vài micro giây nên thuộc lệnh đó. Không chạy được (thiếu quyền...) thì tắt hẳn cách này cho phiên bắt.</summary>
    private async Task<(string EventType, string Text, bool Truncated, DateTime Start)> ReadInputBufferAsync(int spid, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new SqlCommand(
                $"SELECT last_request_start_time FROM sys.dm_exec_sessions WHERE session_id = {spid}; DBCC INPUTBUFFER({spid}) WITH NO_INFOMSGS;",
                _connection) { CommandTimeout = 20 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var start = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? GetTime(reader, 0) : DateTime.MinValue;
            if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)
                || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return ("", "", false, start);
            var info = GetString(reader, 2);
            return (GetString(reader, 0), info.Trim(), info.Length >= InputBufferLimit, start);
        }
        catch (SqlException ex) when (!IsTransient(ex))
        {
            // Lỗi tạm thời (mất mạng…) thì ném lên cho nhịp đọc thử lại — không tắt hẳn DBCC cho cả phiên bắt vì 1 lần rớt mạng.
            _dbccInputBuffer = false;
            return ("", "", false, DateTime.MinValue);
        }
    }

    private const int InputBufferLimit = 4000;

    /// <summary>Dòng chú thích nối sau câu bị cắt (không tìm được bản đầy đủ) — MainForm dò chuỗi này để cảnh báo khi Copy SQL Command.</summary>
    public const string TruncatedNote = "-- ... cắt bớt: máy chủ (SQL 2008 / 2012) chỉ cho đọc 4000 ký tự đầu của câu này";

    /// <summary>Batch dài hơn 4000 ký tự mà DBCC INPUTBUFFER cắt mất đuôi: tìm bản đầy đủ (dm_exec_sql_text, nvarchar(max)) — chỉ nhận bản
    /// có cùng 4000 ký tự đầu với buffer. Thứ tự: câu đang chạy / câu gần nhất trên kết nối (rẻ nhất, nhưng batch gọi procedure thì
    /// sql_handle / most_recent_sql_handle trỏ vào procedure hoặc SQL động bên trong) → dm_exec_query_stats chạy từ lúc batch bắt đầu
    /// (câu đầu batch chạy xong là có, sắp mới nhất trước) → plan cache (Adhoc / Prepared, kể cả stub khi bật optimize for ad hoc workloads).
    /// Cả ba chỉ cần VIEW SERVER STATE. Không thấy thì null (nơi gọi giữ 4000 ký tự đầu + dòng <see cref="TruncatedNote"/>).
    /// useStats / useCache = có hỏi 2 nguồn sau không (lúc bắt bỏ plan cache — xem ReadClientTextAsync).</summary>
    private static async Task<string?> FindFullBatchAsync(SqlConnection connection, int spid, DateTime start, string buffer,
        bool useStats, bool useCache, CancellationToken cancellationToken)
    {
        var sessionSql = $"""
            SELECT t.text FROM sys.dm_exec_requests AS r CROSS APPLY sys.dm_exec_sql_text({SafeHandle("r.sql_handle")}) AS t WHERE r.session_id = @spid
            UNION ALL
            SELECT t.text FROM sys.dm_exec_connections AS c CROSS APPLY sys.dm_exec_sql_text({SafeHandle("c.most_recent_sql_handle")}) AS t WHERE c.session_id = @spid;
            """;
        // Lọc thô trên server (dài ≥ 4000 ký tự + dòng đầu khớp) để không kéo text của mọi câu về; so khớp đủ 4000 ký tự làm ở dưới.
        var statsSql = $"""
            SELECT TOP (20) t.text
            FROM (SELECT sql_handle, MAX(last_execution_time) AS last_time
                  FROM sys.dm_exec_query_stats WHERE last_execution_time >= @since GROUP BY sql_handle) AS q
            CROSS APPLY sys.dm_exec_sql_text({SafeHandle("q.sql_handle")}) AS t
            WHERE DATALENGTH(t.text) >= 8000 AND CHARINDEX(@needle, LEFT(t.text, 1000)) > 0
            ORDER BY q.last_time DESC;
            """;
        const string cacheSql = """
            SELECT TOP (20) t.text
            FROM sys.dm_exec_cached_plans AS p
            CROSS APPLY sys.dm_exec_sql_text(p.plan_handle) AS t
            WHERE p.objtype IN (N'Adhoc', N'Prepared') AND DATALENGTH(t.text) >= 8000 AND CHARINDEX(@needle, LEFT(t.text, 1000)) > 0;
            """;
        var head = Squash(buffer);
        var needle = buffer.TrimStart();
        var lineEnd = needle.IndexOfAny(['\r', '\n']);
        needle = (lineEnd > 0 ? needle[..lineEnd] : needle).Trim();
        needle = needle[..Math.Min(needle.Length, 200)];

        var sources = new List<(string Sql, bool DistinctOnly)> { (sessionSql, false) };
        if (useStats)
            sources.Add((statsSql, false));
        if (useCache)
            sources.Add((cacheSql, true));
        foreach (var (sql, distinctOnly) in sources)
        {
            try
            {
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 };
                command.Parameters.Add("@spid", SqlDbType.Int).Value = spid;
                command.Parameters.Add("@since", SqlDbType.DateTime).Value = start.AddSeconds(-2);
                command.Parameters.Add("@needle", SqlDbType.NVarChar, 200).Value = needle;
                // Plan cache không có thời điểm chạy: nhiều bản khác nhau cùng 4000 ký tự đầu thì không biết bản nào là của phiên này → bỏ.
                string? match = null;
                var ambiguous = false;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var text = GetString(reader, 0).Trim();
                    if (text.Length < buffer.Length || !Squash(text).StartsWith(head, StringComparison.Ordinal))
                        continue;
                    if (!distinctOnly)
                        return text;
                    if (match is null)
                        match = text;
                    else if (!match.Equals(text, StringComparison.Ordinal))
                        ambiguous = true;
                }
                if (match is not null && !ambiguous)
                    return match;
            }
            catch (SqlException)
            {
                // nguồn này không đọc được (quyền / hết giờ) — thử nguồn kế tiếp
            }
        }
        return null;
    }

    /// <summary>Dòng FullPending (câu dài, lúc bắt chưa tìm hết nguồn): tìm bản đầy đủ ở mọi nguồn, dùng kết nối đang bắt (chờ nhịp đọc xong).
    /// Trả dòng mới (FullPending = false; không tìm được thì giữ 4000 ký tự đầu + TruncatedNote, không tìm lại nữa).</summary>
    public async Task<CapturedEvent> ResolveFullAsync(CapturedEvent row, CancellationToken cancellationToken)
    {
        if (!row.FullPending)
            return row;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            return await ResolveFullAsync(_connection, row, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Như trên nhưng đã Dừng bắt: mở kết nối riêng theo chuỗi kết nối lúc bắt rồi đóng.</summary>
    public static async Task<CapturedEvent> ResolveFullAsync(string connectionString, CapturedEvent row, CancellationToken cancellationToken)
    {
        if (!row.FullPending)
            return row;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ResolveFullAsync(connection, row, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CapturedEvent> ResolveFullAsync(SqlConnection connection, CapturedEvent row, CancellationToken cancellationToken)
    {
        var note = Environment.NewLine + TruncatedNote;
        var buffer = row.Text.EndsWith(note, StringComparison.Ordinal) ? row.Text[..^note.Length] : row.Text;
        var full = await FindFullBatchAsync(connection, row.Spid, row.Time, buffer, useStats: true, useCache: true, cancellationToken).ConfigureAwait(false);
        if (full is null)
            return row with { FullPending = false };
        full = TrimText(full);
        var objectName = row.ObjectName.Length > 0 ? row.ObjectName : ProcCalls.Find(full) is { Count: > 0 } calls ? calls[0].Name : "";
        return row with { Text = full, ObjectName = objectName, FullPending = false };
    }

    private static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();

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
                r.sql_handle
            FROM sys.dm_exec_requests AS r
            INNER JOIN sys.dm_exec_sessions AS s ON s.session_id = r.session_id
            INNER JOIN sys.sysprocesses AS p ON p.spid = r.session_id AND p.ecid = 0
            CROSS APPLY sys.dm_exec_sql_text({SafeHandle("r.sql_handle")}) AS t
            WHERE r.session_id <> @@SPID
              AND ISNULL(s.program_name, N'') <> @appName
              AND ISNULL(s.program_name, N'') NOT LIKE N'Bcode%'
              AND ISNULL(s.program_name, N'') NOT LIKE N'FCode%'
              AND (@uid = N'' OR ISNULL(s.program_name, N'') = @uid)
              AND r.start_time >= @started
              AND p.dbid IN ({DatabaseIdList()})
              AND t.objectid IS NOT NULL
              AND t.objectid <> 0
            """;

        await using var command = new SqlCommand(sql, _connection) { CommandTimeout = 20 };
        command.Parameters.Add("@appName", SqlDbType.NVarChar, 128).Value = ApplicationName;
        command.Parameters.Add("@uid", SqlDbType.NVarChar, 128).Value = UserIdFilter.Trim();
        command.Parameters.Add("@started", SqlDbType.DateTime2).Value = _started;
        AddDatabaseParameters(command);
        // Procedure đang chạy lâu (báo cáo) hiện ở mọi nhịp: trước đây mỗi nhịp kéo lại 8000 ký tự nội dung. Giờ nhịp chỉ dò (session,
        // start, objectid, sql_handle); procedure mới thấy lần đầu mới đọc nội dung bằng sql_handle (1 câu riêng).
        var fresh = new List<(int Spid, DateTime Start, string Database, string Login, string Host, string App, int ObjectId, string Schema, string Name, byte[]? Handle)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var spid = GetInt(reader, 0);
                var start = reader.GetDateTime(1);
                var objectId = GetInt(reader, 6);
                if (!_moduleSeen.Add(spid + "|" + start.Ticks + "|" + objectId))
                    continue;
                fresh.Add((spid, start, GetString(reader, 2), GetString(reader, 3), GetString(reader, 4), GetString(reader, 5),
                    objectId, GetString(reader, 7), GetString(reader, 8), reader.IsDBNull(9) ? null : (byte[])reader.GetValue(9)));
            }
        }
        if (_moduleSeen.Count > 50000)
            _moduleSeen.Clear();

        foreach (var m in fresh)
        {
            var moduleText = m.Handle is null ? "" : await ReadModuleTextAsync(m.Handle, cancellationToken).ConfigureAwait(false);
            var objectName = ModuleName(m.ObjectId, m.Schema, m.Name, moduleText);
            if (!MarkSp(m.Spid, m.Start, objectName))
                continue;

            found.Add(new CapturedEvent(
                m.Start,
                "SP:Starting",
                m.Database,
                m.Spid,
                m.Login,
                m.Host,
                m.App,
                objectName,
                "EXEC " + objectName,
                moduleText));
        }
    }

    private async Task<string> ReadModuleTextAsync(byte[] handle, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT LEFT(text, 8000) FROM sys.dm_exec_sql_text({SafeHandle("@h")});", _connection) { CommandTimeout = 20 };
        command.Parameters.Add("@h", SqlDbType.VarBinary, 64).Value = handle;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string text ? text : "";
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

    private static DateTime GetTime(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? DateTime.MinValue : reader.GetDateTime(ordinal);

    private static string GetString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";

    // Không giới hạn độ dài: batch của Fast có thể rất dài (khai báo hàng trăm biến / cột) mà Copy SQL Command / Debug store cần nguyên câu.
    // RAM vẫn chặn được vì lưới chỉ giữ MaxRows dòng gần nhất (MainForm).
    private static string TrimText(string text) => text.Replace("\0", "");

    private sealed record SessionHead(
        int Spid,
        DateTime Start,
        string LoginName,
        string HostName,
        string ApplicationName,
        string DatabaseName);
}
