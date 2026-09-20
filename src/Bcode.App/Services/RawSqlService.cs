using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Backs the "Command" tool: a free-form SQL script runner (SSMS-style — any
/// number of statements, not just one SELECT), as opposed to "SQL Query"
/// (the structured SELECT/FROM/WHERE/ORDER BY builder that already existed
/// as SqlQueryService before this batch of features).
///
/// Splits on "GO" batch separators (ADO.NET/SqlCommand has no native concept
/// of GO — that's a client-side batch separator SSMS/sqlcmd handle, so we do
/// the same: split the script on lines that are just "GO" and run each batch
/// as its own SqlCommand). The LAST batch that produces a result set is what
/// gets returned as a DataTable; other batches just report rows-affected.
/// </summary>
public class RawSqlService
{
    private readonly DbConnectionService _connections;
    private readonly PeriodTableQueryService _periods;

    public RawSqlService(DbConnectionService connections, PeriodTableQueryService periods)
    {
        _connections = connections;
        _periods = periods;
    }

    private static readonly Regex GoSeparator = new(@"^[ \t]*GO[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>Matches a FROM/JOIN table reference so a "...$000000" placeholder can be
    /// expanded the same way SQL Query already does — fixes "Command" running the raw
    /// (near-empty) "$000000" template table literally instead of UNION-ing every real
    /// period table, when the user types e.g. "SELECT * FROM r00$000000" by hand.</summary>
    private static readonly Regex FromJoinRegex = new(
        @"\b(?:FROM|JOIN)\s+(\[?[\w$]+\]?(?:\.\[?[\w$]+\]?)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public record BatchResult(string Batch, DataTable? Table, int RowsAffected, string? Error);

    /// <summary>
    /// Runs the script on a brand-new connection that's closed again right after — the
    /// "Reset Connection" behavior: any #temp tables the script created die with the
    /// connection, so re-running the same "CREATE TABLE #tmp ..." script never collides
    /// with a leftover #temp table from the previous run (no explicit DROP needed).
    /// </summary>
    public Task<List<BatchResult>> ExecuteScriptAsync(string script, bool useSysDatabase = false) =>
        ExecuteScriptOnConnectionAsync(script, () => _connections.CreateConnection(useSysDatabase), ownsConnection: true);

    /// <summary>
    /// Runs the script on an existing, already-open connection instead ("Reset Connection"
    /// unchecked) — #temp tables and any SET/session state persist across calls, matching
    /// what a real SSMS query window does when you keep the same connection open.
    /// </summary>
    public Task<List<BatchResult>> ExecuteScriptOnAsync(string script, SqlConnection conn) =>
        ExecuteScriptOnConnectionAsync(script, () => conn, ownsConnection: false);

    /// <summary>Opens a new, caller-owned connection (used to hold a persistent connection across
    /// several ExecuteScriptOnAsync calls when "Reset Connection" is unchecked in RawSqlControl —
    /// the caller is responsible for opening/disposing it).</summary>
    public SqlConnection CreateConnection(bool useSysDatabase = false) => _connections.CreateConnection(useSysDatabase);

    private async Task<List<BatchResult>> ExecuteScriptOnConnectionAsync(string script, Func<SqlConnection> connFactory, bool ownsConnection)
    {
        var batches = GoSeparator.Split(script)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0)
            .ToList();

        var results = new List<BatchResult>();
        if (batches.Count == 0) return results;

        var conn = connFactory();
        try
        {
            if (conn.State != ConnectionState.Open) await conn.OpenAsync();
            var expanded = await ExpandPeriodPlaceholdersAsync(conn, batches);
            return await RunBatchesAsync(expanded, conn, results);
        }
        finally
        {
            if (ownsConnection) await conn.DisposeAsync();
        }
    }

    /// <summary>Rewrites every "...$000000" FROM/JOIN reference in each batch into the
    /// UNION ALL subquery over its real period tables (see PeriodTableQueryService) — the
    /// same "$000000 = mọi kỳ" behavior SQL Query has, applied to free-form scripts here.</summary>
    private async Task<List<string>> ExpandPeriodPlaceholdersAsync(SqlConnection conn, List<string> batches)
    {
        var result = new List<string>(batches.Count);
        foreach (var batch in batches)
            result.Add(await ExpandPeriodPlaceholdersInBatchAsync(conn, batch));
        return result;
    }

    private async Task<string> ExpandPeriodPlaceholdersInBatchAsync(SqlConnection conn, string batch)
    {
        var matches = FromJoinRegex.Matches(batch);
        if (matches.Count == 0) return batch;

        var sb = new System.Text.StringBuilder(batch);
        // Back-to-front so an earlier replacement's length change doesn't shift the
        // character indices of matches still to be processed.
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var tableGroup = matches[i].Groups[1];
            var tableRef = tableGroup.Value;
            if (!_periods.IsPeriodPlaceholder(tableRef)) continue;

            var (schema, baseName) = _periods.ParsePlaceholder(tableRef);
            var periodTables = await _periods.DiscoverPeriodTablesAsync(conn, schema, baseName);
            var union = _periods.BuildUnionSubquery(periodTables, $"{baseName}$000000");

            sb.Remove(tableGroup.Index, tableGroup.Length);
            sb.Insert(tableGroup.Index, union);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Compile-only validation (SET NOEXEC ON) — SQL Server parses and binds
    /// (resolves table/column names) each batch without running it, so a typo'd
    /// column or table surfaces the same error it would on Execute, without
    /// actually running INSERT/UPDATE/DELETE statements. Backs "Check Fields".
    /// </summary>
    public async Task<string?> CheckFieldsAsync(string script, bool useSysDatabase = false)
    {
        var batches = GoSeparator.Split(script).Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        if (batches.Count == 0) return null;

        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();
        var expandedBatches = await ExpandPeriodPlaceholdersAsync(conn, batches);

        await using (var on = new SqlCommand("SET NOEXEC ON;", conn)) await on.ExecuteNonQueryAsync();
        try
        {
            foreach (var batch in expandedBatches)
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 30 };
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            // NOEXEC must always be turned back off on this connection/session, even on error.
            await using var off = new SqlCommand("SET NOEXEC OFF;", conn);
            await off.ExecuteNonQueryAsync();
        }
        return null; // no error => every batch compiled/bound cleanly
    }

    private static async Task<List<BatchResult>> RunBatchesAsync(List<string> batches, SqlConnection conn, List<BatchResult> results)
    {
        foreach (var batch in batches)
        {
            try
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
                await using var reader = await cmd.ExecuteReaderAsync();

                if (reader.FieldCount > 0)
                {
                    var table = new DataTable();
                    // DataTable.Load is a plain synchronous, CPU-bound read of the whole
                    // result set — left on the UI thread (the default continuation after the
                    // awaits above), a big/unbounded result (this runner has no row cap,
                    // unlike SQL Query/Table) froze the whole window until it finished
                    // loading. Task.Run moves that work off the UI thread; the reader is
                    // still only ever touched from one thread at a time.
                    await Task.Run(() => table.Load(reader)); // also advances/consumes the reader
                    results.Add(new BatchResult(batch, table, table.Rows.Count, null));
                }
                else
                {
                    var rowsAffected = reader.RecordsAffected;
                    await reader.CloseAsync();
                    results.Add(new BatchResult(batch, null, rowsAffected, null));
                }
            }
            catch (Exception ex)
            {
                results.Add(new BatchResult(batch, null, 0, ex.Message));
                break; // stop at the first failing batch, same as SSMS default behavior
            }
        }

        return results;
    }
}
