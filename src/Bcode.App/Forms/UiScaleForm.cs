using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>Chọn tỉ lệ giao diện: Tự động (theo cỡ màn hình) hoặc một mức cố định. Giao diện là trang WebView2 (Web/Shell/uiscale.html).</summary>
public class UiScaleForm : WebDialogForm
{
    private string _mode;

    public string SelectedMode => _mode;

    public UiScaleForm(string currentMode) : base("Tỉ lệ giao diện", "uiscale.html", 460, 300, 380, 240)
    {
        _mode = UiScale.Options.Any(o => o.Equals(currentMode, StringComparison.OrdinalIgnoreCase)) ? currentMode : UiScale.Auto;
    }

    protected override void OnReady() =>
        Js($"uiScale.init({J(new
        {
            current = _mode,
            options = UiScale.Options.Select(o => new { value = o, label = o == UiScale.Auto ? $"Tự động (đang dùng {UiScale.Factor:P0})" : o + "%" }),
        })})");

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action == "ok")
        {
            var v = msg.TryGetProperty("value", out var e) ? e.GetString() : null;
            _mode = UiScale.Options.FirstOrDefault(o => o.Equals(v, StringComparison.OrdinalIgnoreCase)) ?? UiScale.Auto;
            CloseWith(DialogResult.OK);
        }
        return Task.CompletedTask;
    }
}
