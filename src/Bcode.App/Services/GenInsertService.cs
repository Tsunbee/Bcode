using System.Data;
using System.Globalization;
using System.Text;

namespace Bcode.App.Services;

/// <summary>
/// "Gen Insert" tool: turns the current result grid (or a selection of rows)
/// into ready-to-run INSERT INTO statements, e.g. for moving reference rows
/// between environments/periods.
/// </summary>
public class GenInsertService
{
    public string GenerateInsertStatements(DataTable table, string targetTableName, IEnumerable<DataRow>? rowsOverride = null)
    {
        var rows = (rowsOverride ?? table.Rows.Cast<DataRow>()).ToList();
        if (rows.Count == 0) return "-- Không có dòng nào để sinh INSERT.";

        var columns = table.Columns.Cast<DataColumn>().ToList();
        var columnList = string.Join(", ", columns.Select(c => $"[{c.ColumnName}]"));

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var values = columns.Select(c => FormatValue(row[c]));
            sb.Append($"INSERT INTO {targetTableName} ({columnList}) VALUES (");
            sb.Append(string.Join(", ", values));
            sb.AppendLine(");");
        }

        return sb.ToString();
    }

    private static string FormatValue(object value)
    {
        if (value is null || value is DBNull) return "NULL";

        return value switch
        {
            string s => "'" + s.Replace("'", "''") + "'",
            DateTime dt => "'" + dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'",
            bool b => b ? "1" : "0",
            byte[] => "0x" + Convert.ToHexString((byte[])value),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL"
        };
    }
}
