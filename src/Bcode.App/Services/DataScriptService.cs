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
    public string GenerateDeleteAndReloadScript(DataTable table, string targetTableName)
    {
        var columns = table.Columns.Cast<DataColumn>().ToList();
        var columnList = string.Join(", ", columns.Select(c => $"[{c.ColumnName}]"));
        var target = $"[{targetTableName.Trim().Trim('[', ']')}]";

        var sb = new StringBuilder();
        sb.AppendLine($"--//// FCode /////// Created By: {Environment.MachineName}; At: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"DELETE {target} WHERE 1=1");
        sb.AppendLine($"SELECT {columnList} INTO #data FROM {target} WHERE 1=0");

        foreach (DataRow row in table.Rows)
        {
            var values = columns.Select(c => FormatValue(row[c]));
            sb.AppendLine($"INSERT INTO #data VALUES({string.Join(", ", values)})");
        }

        sb.AppendLine($"INSERT INTO {target} SELECT * FROM #data");
        sb.AppendLine("DROP TABLE #data");
        return sb.ToString();
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
