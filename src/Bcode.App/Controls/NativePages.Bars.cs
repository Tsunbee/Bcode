using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Thanh công cụ của cửa sổ Script / WCommand Script (scriptviewbar.html): Save · Clear · Close + ô Find.</summary>
internal sealed class ScriptViewBarPage : NativePage
{
    private readonly RoundedInput _find = NK.Input("Find... (Enter)");
    private readonly Label _status = NK.Muted("");

    public ScriptViewBarPage()
    {
        var bar = new BarFlow { Dock = DockStyle.Fill, Padding = new Padding(8, 5, 8, 5) };
        _find.Width = NativeSkin.S(220);
        _find.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Post(new { action = "find", value = _find.Text }); } };
        bar.Controls.AddRange(new Control[]
        {
            NK.Button("Save", () => Post(new { action = "save" }), primary: true), NK.Button("Clear", () => Post(new { action = "clear" })), NK.Button("Close", () => Post(new { action = "close" })),
            _find, NK.Button("Find Next", () => Post(new { action = "find", value = _find.Text })), _status,
        });
        Controls.Add(bar);
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path is "window.setFindStatus" or "setFindStatus") _status.Text = Str(a, 0);
    }

    public override void FocusPrimary() => _find.Inner.Focus();
}
