using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BcodeViewer.App.Settings;
using Microsoft.Data.SqlClient;

namespace BcodeViewer.App.Host;

/// <summary>
/// Runs the T-SQL inside a controller's &lt;command&gt;/&lt;action&gt; against the same
/// workspace Bcode.App is pointed at, so checking what a query returns doesn't mean copying
/// it into SSMS, hand-expanding the &amp;Entity; references and re-declaring the parameters.
///
/// Three things make this safe enough to put a keystroke away, in order of how much they
/// matter:
///
/// 1. <b>Writes are off by default.</b> A script that writes is refused unless the user has
///    turned <see cref="ViewerSettings.EnableSqlWrites"/> on. This is enforced here rather
///    than only in the page, because the page is where an accidental Ctrl+Enter comes from.
/// 2. <b>Rollback mode.</b> When <c>rollbackOnly</c> is set, everything runs inside a
///    transaction that is ALWAYS rolled back — the row counts are real, the changes are not.
///    That is the mode the UI offers first for a write script, and it needs no setting.
/// 3. <b>Parameters are parameters.</b> An FCode command is full of <c>@ma_dvcs</c>-style
///    placeholders; their values are bound through SqlParameter, never pasted into the text.
///
/// Statement classification (see <see cref="Inspect"/>) strips comments and string literals
/// first. Without that, a SELECT whose WHERE clause compares against the literal 'DELETE'
/// reads as a delete, and a guard that cries wolf is a guard people switch off.
/// </summary>
public class SqlRunnerService
{
    /// <summary>Rows kept per result set. A controller's list query against a real customer
    /// database returns everything; the page only needs enough to see the shape and spot the
    /// wrong join, and a million rows marshaled through IDispatch would hang the window.</summary>
    private const int MaxRows = 500;

    /// <summary>Seconds a batch may run. Long enough for a slow report query on a UNC-hosted
    /// server, short enough that a runaway one doesn't lock up the panel indefinitely.</summary>
    private const int CommandTimeoutSeconds = 60;

    /// <summary>Statement keywords that change data or schema. EXEC is here because a
    /// procedure's body is opaque from the outside — "it only reads" is not knowable.</summary>
    private static readonly string[] WriteKeywords =
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE",
        "EXEC", "EXECUTE", "GRANT", "REVOKE", "BACKUP", "RESTORE",
    };

    private static readonly Regex GoSeparator =
        new(@"^[ \t]*GO[ \t]*;?[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>`@name`, but not `@@ROWCOUNT` and friends — those are server globals, not
    /// something anyone can supply a value for.</summary>
    private static readonly Regex ParameterRef = new(@"(?<!@)@([A-Za-z_][A-Za-z0-9_$#]*)", RegexOptions.CultureInvariant);

    /// <summary>A parameter the script declares itself needs no value from the user.</summary>
    private static readonly Regex DeclaredParameter =
        new(@"\bDECLARE\s+@([A-Za-z_][A-Za-z0-9_$#]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ViewerSettings _settings;

    public SqlRunnerService(ViewerSettings settings) => _settings = settings;

    // ---- Inspection -------------------------------------------------------------------

    /// <summary>
    /// What the page needs to know before running: which parameters to ask for, which write
    /// statements are in there, and whether the current settings allow them. Returns
    /// <c>{workspace, parameters:[], writes:[], writesAllowed, readOnly}</c>.
    /// </summary>
    public string Inspect(string sql)
    {
        var stripped = StripCommentsAndStrings(sql ?? "");

        var declared = new HashSet<string>(
            DeclaredParameter.Matches(stripped).Select(m => m.Groups[1].Value),
            StringComparer.OrdinalIgnoreCase);

        var parameters = ParameterRef.Matches(stripped)
            .Select(m => m.Groups[1].Value)
            .Where(name => !declared.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var writes = FindWrites(stripped);

        return JsonSerializer.Serialize(new
        {
            workspace = WorkspaceConnection.Describe(),
            parameters,
            writes,
            writesAllowed = _settings.EnableSqlWrites,
        });
    }

    private static string[] FindWrites(string strippedSql) =>
        WriteKeywords
            .Where(k => Regex.IsMatch(strippedSql, $@"(?<![\w@#$]){Regex.Escape(k)}(?![\w@#$])", RegexOptions.IgnoreCase))
            .ToArray();

    /// <summary>
    /// Blanks out `--` comments, `/* */` comments and quoted literals, keeping the text's
    /// length and line structure so anything computed from the result still lines up with
    /// the original. Only ever used for classification — never for what gets executed.
    /// </summary>
    private static string StripCommentsAndStrings(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') { result.Append(' '); i++; }
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                // T-SQL block comments nest, and a naive search for the first "*/" would
                // stop inside an inner one and treat the rest of the comment as code.
                var depth = 0;
                while (i < sql.Length)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; result.Append("  "); i += 2; continue; }
                    if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                    {
                        depth--; result.Append("  "); i += 2;
                        if (depth == 0) break;
                        continue;
                    }
                    result.Append(sql[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                continue;
            }

            if (c is '\'' or '"' or '[')
            {
                var close = c switch { '\'' => '\'', '"' => '"', _ => ']' };
                result.Append(' ');
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        // '' inside a '…' literal is an escaped quote, not the end of it.
                        if (close != ']' && i + 1 < sql.Length && sql[i + 1] == close)
                        {
                            result.Append("  "); i += 2; continue;
                        }
                        result.Append(' '); i++; break;
                    }
                    result.Append(sql[i] == '\n' ? '\n' : ' ');
                    i++;
                }
                continue;
            }

            result.Append(c);
            i++;
        }
        return result.ToString();
    }

    // ---- Execution ---------------------------------------------------------------------

    /// <summary>
    /// A script that has been handed to the server. Kept because <see cref="Start"/> returns
    /// as soon as the work is queued rather than when it finishes — see the note there.
    /// </summary>
    private sealed class Run
    {
        public string Id = "";
        public Task<string> Work = Task.FromResult("");
        public CancellationTokenSource Cts = new();
        public bool Abandoned;
    }

    private Run? _current;
    private readonly object _runLock = new();

    /// <summary>
    /// Queues the script and returns <c>{runId}</c> immediately, or <c>{error}</c> if it was
    /// refused before reaching the server (no workspace, or a write with writes disabled).
    ///
    /// The split into Start/Poll exists because a host object call runs on the thread that
    /// drives the page: while one is in progress the whole WebView is frozen — no rendering,
    /// no script, and no way for a second call to get through. A query that takes thirty
    /// seconds would take the editor with it, and the panel's own "Dừng" button could never
    /// be delivered, because delivering it needs the very thread the query is holding.
    /// (This applies to every long host call here, not just this one.)
    /// </summary>
    public string Start(string sql, string parametersJson, bool rollbackOnly)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return JsonSerializer.Serialize(new { error = "Không có câu SQL nào để chạy." });

        var writes = FindWrites(StripCommentsAndStrings(sql));
        if (writes.Length > 0 && !rollbackOnly && !_settings.EnableSqlWrites)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Câu lệnh có {string.Join(", ", writes)} — đang bị chặn. " +
                        "Dùng \"Chạy thử (rollback)\", hoặc bật \"Cho phép chạy câu ghi\" trong Settings.",
            });
        }

        var connectionString = WorkspaceConnection.BuildConnectionString();
        if (connectionString is null)
        {
            return JsonSerializer.Serialize(new
            {
                error = @"Chưa có workspace nào trong %AppData%\Bcode\settings.json " +
                        "(mở Bcode > Choose Server để chọn).",
            });
        }

        Dictionary<string, string> parameters;
        try
        {
            parameters = string.IsNullOrWhiteSpace(parametersJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(parametersJson) ?? new();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = "Tham số không hợp lệ: " + ex.Message });
        }

        var run = new Run { Id = Guid.NewGuid().ToString("N") };
        // Every failure is turned into a JSON result inside the task: nothing here is in a
        // position to observe an exception later, and an unobserved faulted Task is a
        // process-level crash on finalisation.
        run.Work = Task.Run(async () =>
        {
            try
            {
                return await RunAsync(sql, parameters, rollbackOnly, connectionString, run.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                return JsonSerializer.Serialize(new { error = "Đã dừng theo yêu cầu." });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        });

        lock (_runLock)
        {
            // One run at a time. A previous one still winding down is abandoned rather than
            // waited for — its result has nowhere to go now.
            if (_current is not null) _current.Abandoned = true;
            _current = run;
        }

        return JsonSerializer.Serialize(new { runId = run.Id });
    }

    /// <summary>
    /// <c>{status:"running"}</c> while the script is still going, otherwise the finished
    /// result (see <see cref="RunAsync"/>). Returns fast in every case, which is the point.
    /// </summary>
    public string Poll(string runId)
    {
        Run? run;
        lock (_runLock) run = _current;

        if (run is null || run.Id != runId)
            return JsonSerializer.Serialize(new { error = "Lần chạy này đã bị thay bằng lần chạy khác." });
        if (!run.Work.IsCompleted)
            return JsonSerializer.Serialize(new { status = "running" });

        lock (_runLock)
        {
            if (ReferenceEquals(_current, run))
            {
                run.Cts.Dispose();
                _current = null;
            }
        }
        return run.Work.Result; // the task never faults — see Start
    }

    /// <summary>
    /// The panel's "Dừng" button. Cancellation is a request: SqlClient honours the token
    /// promptly once a query is running, but an attempt to reach an unreachable server can
    /// sit in instance-name resolution well past any timeout. So the run is also marked
    /// abandoned, which lets the panel stop waiting whether or not the server ever answers.
    /// </summary>
    public void Cancel()
    {
        Run? run;
        lock (_runLock)
        {
            run = _current;
            if (run is not null) { run.Abandoned = true; _current = null; }
        }
        if (run is null) return;
        try { run.Cts.Cancel(); }
        catch (ObjectDisposedException) { /* finished on its own between the lock and here */ }
    }

    private async Task<string> RunAsync(
        string sql, Dictionary<string, string> parameters, bool rollbackOnly,
        string connectionString, CancellationToken token)
    {
        // Split on GO the way SSMS and sqlcmd do — it is a client-side batch separator that
        // SqlCommand knows nothing about, and an FCode command pasted from a script file
        // often still carries them.
        var batches = GoSeparator.Split(sql)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0)
            .ToList();
        if (batches.Count == 0)
            return JsonSerializer.Serialize(new { error = "Không có câu SQL nào để chạy." });

        var started = DateTime.UtcNow;
        var results = new List<object>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(token);

        // One transaction across every batch, so a script whose later statement depends on
        // an earlier one still behaves as a unit — and so the rollback undoes all of it.
        SqlTransaction? transaction = rollbackOnly
            ? (SqlTransaction)await conn.BeginTransactionAsync(token)
            : null;

        try
        {
            for (var i = 0; i < batches.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                results.Add(await RunOneBatchAsync(conn, transaction, batches[i], parameters, i, token));
            }
        }
        finally
        {
            if (transaction is not null)
            {
                // Always. There is no path through this method that commits — that is the
                // entire promise "Chạy thử" makes, and it must not depend on the script
                // having succeeded.
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch { /* connection already dead; the changes went with it */ }
                await transaction.DisposeAsync();
            }
        }

        return JsonSerializer.Serialize(new
        {
            elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            rollback = rollbackOnly,
            batches = results,
        });
    }

    private static async Task<object> RunOneBatchAsync(
        SqlConnection conn, SqlTransaction? transaction, string batch,
        Dictionary<string, string> parameters, int index, CancellationToken token)
    {
        var tables = new List<object>();
        var rowsAffected = 0;

        try
        {
            await using var cmd = new SqlCommand(batch, conn, transaction) { CommandTimeout = CommandTimeoutSeconds };
            foreach (var (name, value) in parameters)
            {
                // Empty means "not supplied", which for an FCode parameter is NULL rather
                // than an empty string — a WHERE on '' matches nothing and looks like a bug
                // in the query rather than a missing value.
                cmd.Parameters.AddWithValue(
                    "@" + name.TrimStart('@'),
                    string.IsNullOrEmpty(value) ? DBNull.Value : value);
            }

            await using var reader = await cmd.ExecuteReaderAsync(token);
            do
            {
                // A batch can produce several result sets — one EXEC of a procedure with
                // three SELECTs in it is three tables, and showing only the first is how you
                // end up debugging the wrong query.
                if (reader.FieldCount > 0) tables.Add(await ReadTableAsync(reader, token));
            }
            while (await reader.NextResultAsync(token));

            rowsAffected = reader.RecordsAffected;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Reported per batch rather than thrown: batch 3 failing should not hide what
            // batches 1 and 2 already returned.
            return new { index, error = ex.Message, tables, rowsAffected };
        }

        return new { index, error = (string?)null, tables, rowsAffected };
    }

    private static async Task<object> ReadTableAsync(SqlDataReader reader, CancellationToken token)
    {
        var columns = new List<object>();
        for (var c = 0; c < reader.FieldCount; c++)
        {
            columns.Add(new
            {
                name = string.IsNullOrEmpty(reader.GetName(c)) ? $"(col {c + 1})" : reader.GetName(c),
                type = reader.GetDataTypeName(c),
            });
        }

        var rows = new List<string?[]>();
        var truncated = false;
        while (await reader.ReadAsync(token))
        {
            if (rows.Count >= MaxRows) { truncated = true; break; }
            var row = new string?[reader.FieldCount];
            for (var c = 0; c < reader.FieldCount; c++) row[c] = FormatValue(reader, c);
            rows.Add(row);
        }

        return new { columns, rows, truncated };
    }

    /// <summary>
    /// One cell, as the string the grid shows. Everything is stringified here rather than
    /// passed through as JSON numbers/dates: the grid displays text either way, and going
    /// through JS numbers would quietly round a DECIMAL(19,4) amount — the one column type
    /// in this codebase where that is never acceptable.
    /// </summary>
    private static string? FormatValue(SqlDataReader reader, int c)
    {
        if (reader.IsDBNull(c)) return null; // rendered as a dimmed NULL, distinct from ''
        var value = reader.GetValue(c);
        return value switch
        {
            DateTime dt => dt.ToString("dd/MM/yyyy HH:mm:ss"),
            byte[] bytes => $"0x… ({bytes.Length} bytes)",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
}
