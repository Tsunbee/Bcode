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
    private readonly bool _chain;   // chế độ "gen cả menu mẹ" (PickChain): tick từng dòng, không kéo theo menu con, không ô lọc

    /// <summary>WMenu ID các menu đã tick, theo thứ tự trên cây.</summary>
    public List<string> Selected { get; private set; } = new();

    private MenuPickerForm(List<object> view, List<string> preChecked, bool chain = false)
        : base(chain ? "Gen Script Menu — chọn menu mẹ cần gen kèm" : "Chọn menu để Gen Script Menu", "menupicker.html",
               chain ? 640 : 760, chain ? 440 : 720, 480, 360)
    {
        _view = view;
        _preChecked = preChecked;
        _chain = chain;
    }

    /// <summary>
    /// Gen Script Menu (cây WCommand / Mobile) trên menu có menu mẹ, người dùng chọn "gen cả menu mẹ": hộp tick các menu mẹ (gốc trước) + menu đang chọn,
    /// hiện dạng cây 1 nhánh, tick sẵn hết; mỗi dòng tick riêng (bỏ tick menu mẹ không bỏ menu con). Trả WMenu ID đã tick theo thứ tự cha trước con, null = huỷ.
    /// </summary>
    public static List<string>? PickChain(IWin32Window? owner, IReadOnlyList<WCommandItem> chain)
    {
        if (chain.Count == 0) return null;
        object? view = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var it = chain[i];
            view = new
            {
                id = it.WMenuId, name = WCommandTreeControl.DisplayName(it) + (i == chain.Count - 1 ? "   ← menu đang chọn" : ""),
                bar = it.Bar, bar2 = it.Bar2, link = it.Link, sysId = it.SysId,
                kids = view is null ? new List<object>() : new List<object> { view },
            };
        }
        using var form = new MenuPickerForm(new List<object> { view! }, chain.Select(c => c.WMenuId).ToList(), chain: true);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Selected : null;
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
        Js($"menuPicker.init({J(new { roots = _view, @checked = _preChecked, filter = _chain ? "" : _lastFilter, chain = _chain })})");

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action == "ok")
        {
            if (!_chain) _lastFilter = msg.TryGetProperty("filter", out var f) ? f.GetString() ?? "" : "";
            Selected = msg.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            if (Selected.Count > 0) CloseWith(DialogResult.OK);
        }
        return Task.CompletedTask;
    }
}
