namespace Bcode.App.Models;

/// <summary>
/// 1 nhóm công cụ trên thanh công cụ (Quick Access): hiện thành 1 nút "Tên ▾", bấm (hoặc Alt+số thứ tự nhóm — xem ShortcutRegistry "bar.groupN")
/// thả ra menu các công cụ trong nhóm. Công cụ đã vào nhóm thì không hiện riêng trên thanh; công cụ không thuộc nhóm nào và không bị ẩn = "ghim".
/// Danh sách nhóm rỗng = thanh như cũ (mỗi công cụ 1 nút).
/// </summary>
public sealed class ToolBarGroup
{
    public string Name { get; set; } = "";

    /// <summary>Khoá công cụ (vd "sql_query") theo thứ tự trong menu của nhóm.</summary>
    public List<string> Keys { get; set; } = new();
}
