namespace Bcode.App.Models;

/// <summary>
/// One row of the wcommand table (menu tree source in FastBusiness-based systems).
/// Column names follow what is visible in the FCode "Table" view of wcommand:
/// wmenu_id, wmenu_id0 (parent), menu_id, bar, bar2, link, parameter, icon_url,
/// status, icon, sysid, type, syscode, msys, target, xtype, edition, expl_icon.
/// </summary>
public class WCommandItem
{
    public string WMenuId { get; set; } = "";
    public string WMenuId0 { get; set; } = "";   // parent id, "" / null for root
    public string MenuId { get; set; } = "";
    public string Bar { get; set; } = "";        // display text (Vietnamese menu label)
    public string Bar2 { get; set; } = "";        // secondary/English label
    public string Link { get; set; } = "";        // relative path to the .aspx / controller
    public string Parameter { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string Status { get; set; } = "";
    public string Icon { get; set; } = "";
    public string SysId { get; set; } = "";
    public string Type { get; set; } = "";

    public List<WCommandItem> Children { get; } = new();

    public override string ToString() => Bar;
}
