namespace Bcode.App.Models;

public enum SqlObjectKind { Table, View, StoredProcedure, Function }

public class SqlObjectInfo
{
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = "";
    public SqlObjectKind Kind { get; set; }

    /// <summary>Which of the workspace's two databases this came from (Sys Data vs App Data).</summary>
    public bool FromSysDatabase { get; set; }

    public string QualifiedName => $"{Schema}.{Name}";
    public override string ToString() => QualifiedName;
}
