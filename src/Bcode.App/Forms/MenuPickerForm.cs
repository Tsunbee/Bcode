using System.Text.Json;
using Bcode.App.Controls;
using Bcode.App.Models;

namespace Bcode.App.Forms;

/// <summary>
/// "Chọn menu…" của Gen nhanh Menu (tab Gen Update): cây WCommand (bản đầy đủ đã nạp ở cây bên trái — không hỏi lại database) có ô lọc theo tên
/// (Bar / Bar2, gõ không dấu cũng khớp) / WMenu ID / link / SysId — cùng luật ô lọc cây WCommand (<see cref="Services.WCommandService.Matches"/>) — và ô tick
/// nhiều menu; tick nhóm = cả nhóm. OK trả WMenu ID các menu đã tick (theo thứ tự cây) để gen script ngay.
/// Giao diện: Web/Shell/menupicker.html; chế độ App: trang native <see cref="MenuPickerPage"/> (cùng giao thức menuPicker.init / ok / cancel).
/// Chỉ đưa menu bảng <c>wcommand</c> — menu <c>command</c> của sản phẩm APP không gen bằng Gen Script Menu được.
/// </summary>
public sealed class MenuPickerForm : WebDialogForm
{
    private static string _lastFilter = "";   // nhớ ô lọc trong phiên — mở lại hộp là thấy lại nhóm vừa chọn

    private readonly List<object> _view;
    private readonly List<string> _preChecked;

    /// <summary>WMenu ID các menu đã tick, theo thứ tự trên cây.</summary>
    public List<string> Selected { get; private set; } = new();

    private MenuPickerForm(List<object> view, List<string> preChecked)
        : base("Chọn menu để Gen Script Menu", "menupicker.html", 760, 720, 480, 360)
    {
        _view = view;
        _preChecked = preChecked;
    }

    /// <summary>Mở hộp chọn; trả WMenu ID đã tick (theo thứ tự cây), null = huỷ. <paramref name="preChecked"/> = khoá đang có trong ô (WMenu ID nào có trên cây thì tick sẵn).</summary>
    public static List<string>? Pick(IWin32Window? owner, IEnumerable<WCommandItem> roots, IEnumerable<string> preChecked)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object View(WCommandItem it)
        {
            if (!string.IsNullOrWhiteSpace(it.WMenuId)) known.Add(it.WMenuId);
            return new
            {
                id = it.WMenuId, name = WCommandTreeControl.DisplayName(it), bar = it.Bar, bar2 = it.Bar2, link = it.Link, sysId = it.SysId,
                kids = it.Children.OrderBy(c => c.WMenuId, StringComparer.OrdinalIgnoreCase).Select(View).ToList(),
            };
        }
        var view = roots.Where(r => !r.IsAppCommand).OrderBy(r => r.WMenuId, StringComparer.OrdinalIgnoreCase).Select(View).ToList();
        if (view.Count == 0)
        {
            MessageBox.Show(owner, "Cây WCommand chưa có menu (wcommand) nào — mở / F5 cây WCommand bên trái rồi thử lại.", "Chọn menu", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        var pre = preChecked.Where(known.Contains).ToList();
        using var form = new MenuPickerForm(view, pre);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Selected : null;
    }

    protected override void OnReady() =>
        Js($"menuPicker.init({J(new { roots = _view, @checked = _preChecked, filter = _lastFilter })})");

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action == "ok")
        {
            _lastFilter = msg.TryGetProperty("filter", out var f) ? f.GetString() ?? "" : "";
            Selected = msg.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            if (Selected.Count > 0) CloseWith(DialogResult.OK);
        }
        return Task.CompletedTask;
    }
}
