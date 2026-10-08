using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Duplicate Menu" — result of "Check WCommand". Two tabs, matching FCode's own dialog:
/// rows with no matching command row (which also folds in an actual duplicate-wmenu_id
/// check — see WCommandService.FindDuplicatesAsync), and rows where wcommand/command
/// disagree on sysid for the same menu_id. Giao diện là trang WebView2 (Web/Shell/wcommandduplicate.html).
/// </summary>
public class WCommandDuplicateForm : WebDialogForm
{
    private readonly WCommandDuplicateResult _result;

    public WCommandDuplicateForm(WCommandDuplicateResult result) : base("Duplicate Menu", "wcommandduplicate.html", 860, 560, 520, 320)
    {
        _result = result;
    }

    private static object[] Rows(IEnumerable<WCommandItem> items) =>
        items.Select(i => (object)new { wmenuId = i.WMenuId, bar = i.Bar, menuId = i.MenuId, link = i.Link, sysId = i.SysId }).ToArray();

    protected override void OnReady() =>
        Js($"dupMenu.init({J(new { notExists = Rows(_result.NotExistsInCommand), diffSysid = Rows(_result.DifferenceSysid) })})");

    protected override Task OnActionAsync(string action, JsonElement msg) => Task.CompletedTask;
}
