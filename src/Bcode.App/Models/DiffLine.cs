namespace Bcode.App.Models;

public enum DiffKind { Equal, Added, Removed }

public class DiffLine
{
    public DiffKind Kind { get; set; }
    public string Text { get; set; } = "";
    public int? LeftLineNo { get; set; }
    public int? RightLineNo { get; set; }
}

public class SchemaColumn
{
    public string ColumnName { get; set; } = "";
    public string DataType { get; set; } = "";
    public int? MaxLength { get; set; }
    public bool IsNullable { get; set; }
    public int OrdinalPosition { get; set; }

    public string Signature => $"{ColumnName} {DataType}({MaxLength}) {(IsNullable ? "NULL" : "NOT NULL")}";
}
