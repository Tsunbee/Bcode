using System.Data;
using System.Globalization;
using System.Text;

namespace Bcode.App.Services;

/// <summary>
/// "Gen Update" tool (sibling of Gen Insert): turns selected result-grid rows
/// into ready-to-run UPDATE statements, keyed off one or more chosen "key"
/// columns (e.g. the primary key) — every OTHER column becomes a SET clause.
/// </summary>
public class GenUpdateService
{
    public string GenerateUpdateStatements(DataTable table, string targetTableName, IEnumerable<string> keyColumns, IEnumerable<DataRow>? rowsOverride = null)
    {
        var keys = keyColumns.ToList();
        if (keys.Count == 0) return "-- Chưa chọn cột khoá (key) để làm điều kiện WHERE.";

        var rows = (rowsOverride ?? table.Rows.Cast<DataRow>()).ToList();
        if (rows.Count == 0) return "-- Không có dòng nào để sinh UPDATE.";

        var setColumns = table.Columns.Cast<DataColumn>()
            .Select(c => c.ColumnName)
            .Where(c => !keys.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (setColumns.Count == 0) return "-- Không còn cột nào khác ngoài cột khoá để SET.";

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var setClause = string.Join(", ", setColumns.Select(c => $"[{c}] = {FormatValue(row[c])}"));
            var whereClause = string.Join(" AND ", keys.Select(k => $"[{k}] = {FormatValue(row[k])}"));
            sb.AppendLine($"UPDATE {targetTableName} SET {setClause} WHERE {whereClause};");
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
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL"
        };
    }
}
