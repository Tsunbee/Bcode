namespace Bcode.App.Models;

public class Snippet
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";
    public string Content { get; set; } = "";

    /// <summary>Tên dự án (workspace) mà snippet thuộc về; rỗng = dùng cho mọi dự án.</summary>
    public string Project { get; set; } = "";

    /// <summary>Snippet hiện ở dự án <paramref name="project"/>: chung (rỗng) hoặc đúng dự án đó.</summary>
    public bool AppliesTo(string? project) => string.IsNullOrWhiteSpace(Project) || string.Equals(Project.Trim(), (project ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    public override string ToString() => $"{Category} / {Name}";
}
