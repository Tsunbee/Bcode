namespace Bcode.App.UI;

/// <summary>Base class for every Bcode form — applies the flat/dark (or flat/light) skin automatically on load.</summary>
public class ThemedForm : Form
{
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ThemeManager.Apply(this);
    }
}
