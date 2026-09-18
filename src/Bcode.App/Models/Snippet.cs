namespace Bcode.App.Models;

public class Snippet
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";
    public string Content { get; set; } = "";
    public override string ToString() => $"{Category} / {Name}";
}
