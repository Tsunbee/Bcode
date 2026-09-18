namespace Bcode.App.Models;

/// <summary>
/// Describes one physical period ("kỳ") table discovered for a base pattern like
/// "m21$000000" -&gt; m21$202601, m21$202602, ... m21$202612.
/// </summary>
public class PeriodTableInfo
{
    public string SchemaName { get; set; } = "dbo";
    public string TableName { get; set; } = "";   // e.g. m21$202601
    public string Period { get; set; } = "";      // e.g. 202601 ("000000" for the base/template table)
    public string QualifiedName => $"[{SchemaName}].[{TableName}]";
}
