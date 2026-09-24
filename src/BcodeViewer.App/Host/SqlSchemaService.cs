using System.Text.Json;
using BcodeViewer.App.Settings;
using Microsoft.Data.SqlClient;

namespace BcodeViewer.App.Host;

/// <summary>
/// Table/column names for SQL IntelliSense, read from the workspace Bcode.App is already
/// configured with (%AppData%\Bcode\settings.json). Deliberately reads that file rather
/// than taking a project reference on Bcode.App: BcodeViewer ships and launches as its own
/// exe (one process per file — see MainForm's per-process WebView2 profile), and pulling in
/// Bcode.App would drag its whole WinForms/ClosedXML surface along for two SELECTs. The
/// only shape depended on is the two fields actually read below, so a change elsewhere in
/// AppSettings can't break this.
///
/// Three rules this follows, all for the same reason — a completion provider runs on the
/// typing path and must never make the editor wait:
///   1. Nothing connects until a .sql file actually asks (no connection at startup).
///   2. Every result is cached in memory for the process's lifetime; the table list is
///      fetched once, a table's columns once per table.
///   3. A failure (share down, VPN off, wrong credentials) is remembered for
///      <see cref="RetryAfterFailure"/> instead of being retried — otherwise every
///      keystroke would eat the 8-second connect timeout.
/// </summary>
public class SqlSchemaService
{
    /// <summary>How long to stop trying after a failed connection. Long enough that typing
    /// stays responsive on a machine off the VPN, short enough that reconnecting doesn't
    /// mean restarting BcodeViewer.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);

    private readonly ViewerSettings _settings;

    /// <summary>Serialises the DB work itself, so two keystrokes arriving together don't
    /// open two connections for the same answer. Held across a query, therefore across
    /// seconds — which is why the cache below is NOT guarded by it.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Guards the cached state only. Separate from <see cref="_gate"/> because
    /// <see cref="Invalidate"/> is called from the Settings dialog on the UI thread: sharing
    /// the gate meant closing that dialog could block the window behind an in-flight query
    /// with a 15-second command timeout. Every section under this lock is field assignment
    /// and nothing else.</summary>
    private readonly object _cacheLock = new();

    private readonly Dictionary<string, string> _columnCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _tablesJson;
    private DateTime _unavailableUntil = DateTime.MinValue;
    private string _lastError = "";

    public SqlSchemaService(ViewerSettings settings) => _settings = settings;

    /// <summary>JSON array of {"name","schema","kind"} for every table/view on the active
    /// workspace's App database. Returns "[]" — never throws — when SQL completion is off,
    /// no workspace is configured, or the server can't be reached; the page treats all
    /// three the same way (just no table suggestions).</summary>
    public async Task<string> GetTablesJsonAsync()
    {
        if (!_settings.EnableSqlCompletion) return "[]";

        // Fast path outside the gate: an already-cached answer must not queue behind
        // somebody else's query.
        lock (_cacheLock)
        {
            if (_tablesJson is not null) return _tablesJson;
            if (DateTime.UtcNow < _unavailableUntil) return "[]";
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-check: whoever held the gate may have been fetching exactly this.
            lock (_cacheLock)
            {
                if (_tablesJson is not null) return _tablesJson;
                if (DateTime.UtcNow < _unavailableUntil) return "[]";
            }

            var connectionString = BuildConnectionString();
            if (connectionString is null) return "[]";

            const string sql = @"
SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
FROM INFORMATION_SCHEMA.TABLES
ORDER BY TABLE_NAME;";

            var rows = new List<object>();
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new
                {
                    schema = reader.GetString(0),
                    name = reader.GetString(1),
                    kind = reader.GetString(2) == "VIEW" ? "view" : "table",
                });
            }

            var tablesJson = JsonSerializer.Serialize(rows);
            lock (_cacheLock) _tablesJson = tablesJson;
            return tablesJson;
        }
        catch (Exception ex)
        {
            MarkUnavailable(ex);
            return "[]";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>JSON array of {"name","type","pk"} for one table — fetched on the first
    /// `alias.` in a query and cached from then on. <paramref name="table"/> may be
    /// "schema.table" or bare (dbo assumed).</summary>
    public async Task<string> GetColumnsJsonAsync(string table)
    {
        if (!_settings.EnableSqlCompletion || string.IsNullOrWhiteSpace(table)) return "[]";

        // Parameterised below, so this is not an injection guard — it's a sanity filter that
        // keeps junk the caller scraped off a half-typed FROM clause from becoming a
        // pointless round trip and a cache entry.
        var (schema, name) = SplitTableName(table);
        if (!IsPlainIdentifier(schema) || !IsPlainIdentifier(name)) return "[]";

        var cacheKey = $"{schema}.{name}";

        lock (_cacheLock)
        {
            if (_columnCache.TryGetValue(cacheKey, out var cached)) return cached;
            if (DateTime.UtcNow < _unavailableUntil) return "[]";
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_cacheLock)
            {
                if (_columnCache.TryGetValue(cacheKey, out var cached)) return cached;
                if (DateTime.UtcNow < _unavailableUntil) return "[]";
            }

            var connectionString = BuildConnectionString();
            if (connectionString is null) return "[]";

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            // Same primary-key lookup Bcode.App's SqlObjectBrowserService.GetColumnsAsync
            // uses, so the "(PK)" marker means the same thing in both apps.
            var pkColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string pkSql = @"
SELECT c.name
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@qualified);";
            await using (var pkCmd = new SqlCommand(pkSql, conn) { CommandTimeout = 15 })
            {
                pkCmd.Parameters.AddWithValue("@qualified", $"[{schema}].[{name}]");
                await using var pkReader = await pkCmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await pkReader.ReadAsync().ConfigureAwait(false)) pkColumns.Add(pkReader.GetString(0));
            }

            const string colSql = @"
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION;";
            var columns = new List<object>();
            await using (var colCmd = new SqlCommand(colSql, conn) { CommandTimeout = 15 })
            {
                colCmd.Parameters.AddWithValue("@schema", schema);
                colCmd.Parameters.AddWithValue("@table", name);
                await using var reader = await colCmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var dataType = reader.GetString(1);
                    var maxLength = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                    columns.Add(new
                    {
                        name = reader.GetString(0),
                        type = maxLength.HasValue ? $"{dataType}({(maxLength == -1 ? "max" : maxLength.ToString())})" : dataType,
                        nullable = reader.GetString(3) == "YES",
                        pk = pkColumns.Contains(reader.GetString(0)),
                    });
                }
            }

            var json = JsonSerializer.Serialize(columns);
            // Cache the empty result too — a mistyped table name would otherwise re-query
            // the server on every dot the user types after it.
            lock (_cacheLock) _columnCache[cacheKey] = json;
            return json;
        }
        catch (Exception ex)
        {
            MarkUnavailable(ex);
            return "[]";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the cached schema so the next request re-queries — for the Hint Code /
    /// settings round trip where someone just pointed Bcode.App at a different workspace.</summary>
    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _tablesJson = null;
            _columnCache.Clear();
            _unavailableUntil = DateTime.MinValue;
        }
    }

    /// <summary>Short human-readable status for the Settings dialog's "Test" button — the
    /// one place a connection problem should be visible, rather than as a silent absence of
    /// suggestions while typing.</summary>
    public string DescribeStatus()
    {
        if (!_settings.EnableSqlCompletion) return "SQL completion đang tắt.";
        var ws = WorkspaceConnection.Describe();
        if (ws is null) return "Chưa tìm thấy workspace nào trong %AppData%\\Bcode\\settings.json (mở Bcode > Choose Server để tạo).";
        lock (_cacheLock)
        {
            if (_lastError.Length > 0 && DateTime.UtcNow < _unavailableUntil)
                return $"Không kết nối được: {_lastError}";
        }
        return $"Workspace: {ws}";
    }

    private void MarkUnavailable(Exception ex)
    {
        lock (_cacheLock)
        {
            _lastError = ex.Message;
            _unavailableUntil = DateTime.UtcNow + RetryAfterFailure;
        }
    }

    private static string? BuildConnectionString() => WorkspaceConnection.BuildConnectionString();

    private static (string Schema, string Name) SplitTableName(string table)
    {
        var cleaned = table.Replace("[", "").Replace("]", "").Trim();
        var dot = cleaned.LastIndexOf('.');
        return dot > 0
            ? (cleaned[..dot], cleaned[(dot + 1)..])
            : ("dbo", cleaned);
    }

    private static bool IsPlainIdentifier(string s) =>
        s.Length is > 0 and <= 128 && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '@');
}
