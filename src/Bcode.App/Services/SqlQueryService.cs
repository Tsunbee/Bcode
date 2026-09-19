using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Backs the "SQL Query" tool (SELECT / FROM / WHERE / ORDER BY boxes + Run,
/// as seen in FCode's WCommand &gt; Table screen). Transparently expands a
/// "$000000" period placeholder in the FROM box into a UNION ALL over every
/// real period table, so the user never has to pick a period manually.
/// </summary>
public class SqlQueryService
{
    private readonly DbConnectionService _connections;
    private readonly PeriodTableQueryService _periods;

    public SqlQueryService(DbConnectionService connections, PeriodTableQueryService periods)
    {
        _connections = connections;
        _periods = periods;
    }

    /// <summary>
    /// Matches a single FROM-clause table reference and captures an optional trailing alias,
    /// e.g. "m21$000000", "dbo.m21$000000 a", "[dbo].[m21$000000] AS a".
    ///
    /// BUG FIX: the table-name character class here was "[\w]+" — \w does NOT include "$",
    /// so this never matched any "...$000000" reference at all (the whole ^...$-anchored
    /// regex simply failed on the literal "$"), meaning ResolveFromClauseAsync's
    /// IsPeriodPlaceholder check always fell through to "pass through as-is" and the query
    /// just ran against the literal (empty template) "$000000" table instead of expanding to
    /// the real period tables. Table/schema segments now allow "$" too so the full
    /// "base$000000" text is actually captured.
    /// </summary>
    private static readonly Regex FromRefPattern = new(
        @"^\s*(?<table>\[?[\w$]+\]?(?:\.\[?[\w$]+\]?)?)\s*(?:(?:AS\s+)?(?<alias>[\w]+))?\s*$",
        RegexOptions.IgnoreCase);

    public async Task<string> ResolveFromClauseAsync(SqlConnection conn, string fromBox)
    {
        var trimmed = fromBox.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("FROM không được để trống.");

        var m = FromRefPattern.Match(trimmed);
        if (!m.Success || !_periods.IsPeriodPlaceholder(m.Groups["table"].Value))
        {
            // Not a "$000000" pattern (or a more complex FROM with joins) — pass through as-is.
            return trimmed;
        }

        var (schema, baseName) = _periods.ParsePlaceholder(m.Groups["table"].Value);
        var alias = m.Groups["alias"].Success && m.Groups["alias"].Value.Length > 0
            ? m.Groups["alias"].Value
            : $"{baseName}$000000";

        var periodTables = await _periods.DiscoverPeriodTablesAsync(conn, schema, baseName);
        return _periods.BuildUnionSubquery(periodTables, alias);
    }

    public async Task<DataTable> RunAsync(string selectBox, string fromBox, string whereBox, string orderByBox)
    {
        await using var conn = _connections.CreateConnection();
        await conn.OpenAsync();

        var resolvedFrom = await ResolveFromClauseAsync(conn, fromBox);

        var select = string.IsNullOrWhiteSpace(selectBox) ? "*" : selectBox.Trim();
        var sql = $"SELECT {select}\nFROM {resolvedFrom}";
        if (!string.IsNullOrWhiteSpace(whereBox)) sql += $"\nWHERE {whereBox.Trim()}";
        if (!string.IsNullOrWhiteSpace(orderByBox)) sql += $"\nORDER BY {orderByBox.Trim()}";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync();

        var table = new DataTable();
        table.Load(reader);
        LastSql = sql;
        return table;
    }

    /// <summary>The final SQL actually executed (with the $000000 placeholder expanded), for display/debugging.</summary>
    public string LastSql { get; private set; } = "";
}
