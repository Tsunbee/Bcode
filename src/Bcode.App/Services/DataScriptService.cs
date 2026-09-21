using System.Data;
using System.Globalization;
using System.Text;

namespace Bcode.App.Services;

/// <summary>
/// "Add Script" for a loaded Table/Command result: packages every row CURRENTLY loaded in
/// the grid into a DELETE + bulk-INSERT script that reproduces the same data on another
/// server — matching FCode's own "Add Script" output (the "--//// FCode ..." header
/// comment, a #data staging table built from the real table's own column list via
/// "SELECT ... INTO #data ... WHERE 1=0", one INSERT INTO #data per row, then a final
/// merge back into the real table) so a script generated here still works wherever these
/// scripts get run today.
///
/// This is deliberately a full-table swap (DELETE everything, reload everything) rather
/// than per-row UPDATE/INSERT (see GenUpdateService/GenInsertService for that) — it's meant
/// for "ship a whole reference table's data" master-data rows (a customer catalog, a unit
/// list, ...), where the destination's own rows for that table should end up exactly
/// matching what's on screen right now.
/// </summary>
public class DataScriptService
{
    /// <summary>Row-count threshold above which the caller (TableEditControl/SqlQueryControl's
    /// GenDataScript) should skip the interactive "Script" RichTextBox popup and stream
    /// straight to a .sql file via <see cref="WriteDeleteAndReloadScript"/> instead. A
    /// RichTextBox is fine for a quick look at a few hundred rows, but loading a multi-megabyte
    /// script into one (as an 18k+-row "Table" load with Top=0 produces) is what was hanging
    /// the whole app ("chậm hơn FCode... bị treo") — not the generation itself. 2000 rows keeps
    /// the popup path comfortably under a few hundred KB even for a wide table.</summary>
    public const int InlinePreviewRowLimit = 2000;

    /// <summary>Builds the full script as one in-memory string — fine for the interactive
    /// popup's row-count range; for anything larger, use <see cref="WriteDeleteAndReloadScript"/>
    /// to stream straight to disk instead of holding a multi-MB string in RAM.</summary>
    public string GenerateDeleteAndReloadScript(DataTable table, string targetTableName)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            WriteDeleteAndReloadScript(writer, table, targetTableName);
        return sb.ToString();
    }

    /// <summary>Same script as <see cref="GenerateDeleteAndReloadScript"/>, written straight to
    /// <paramref name="writer"/> row by row instead of accumulated into one big string first —
    /// the direct-to-file path for a large table only ever holds one row's worth of text in
    /// memory at a time, rather than the whole multi-MB script twice over (once to build it,
    /// again inside whatever ends up displaying/holding it).</summary>
    public void WriteDeleteAndReloadScript(TextWriter writer, DataTable table, string targetTableName)
    {
        var columns = table.Columns.Cast<DataColumn>().ToList();
        var columnList = string.Join(", ", columns.Select(c => $"[{c.ColumnName}]"));
        var target = $"[{targetTableName.Trim().Trim('[', ']')}]";

        writer.WriteLine($"--//// FCode /////// Created By: {Environment.MachineName}; At: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        writer.WriteLine();
        writer.WriteLine($"DELETE {target} WHERE 1=1");
        writer.WriteLine($"SELECT {columnList} INTO #data FROM {target} WHERE 1=0");

        var rowSb = new StringBuilder(256);
        foreach (DataRow row in table.Rows)
        {
            rowSb.Clear();
            rowSb.Append("INSERT INTO #data VALUES(");
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) rowSb.Append(", ");
                rowSb.Append(FormatValue(row[columns[i]]));
            }
            rowSb.Append(')');
            writer.WriteLine(rowSb.ToString());
        }

        writer.WriteLine($"INSERT INTO {target} SELECT * FROM #data");
        writer.WriteLine("DROP TABLE #data");
    }

    private static string FormatValue(object value)
    {
        if (value is null || value is DBNull) return "NULL";

        return value switch
        {
            string s => "N'" + s.Replace("'", "''") + "'",
            DateTime dt => "'" + dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'",
            bool b => b ? "1" : "0",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL"
        };
    }
}
