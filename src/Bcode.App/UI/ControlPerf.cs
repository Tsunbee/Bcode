namespace Bcode.App.UI;

/// <summary>
/// Tiny WinForms perf helper usable on any Control — currently just double buffering, which
/// several controls need turned on from outside their own class since Control.DoubleBuffered
/// is a protected property (no public setter). GridDisplayHelper already does this for a
/// DataGridView specifically; this is the same one-liner for anything else that gets
/// populated/resized/scrolled enough to flicker without it — a TreeView with a few hundred+
/// nodes (WCommand's menu tree, File Lookup's file tree) being the case that prompted adding
/// it here ("Fcode's lookup bars are smooth — check what makes them not lag").
/// </summary>
public static class ControlPerf
{
    public static void EnableDoubleBuffering(Control control) =>
        typeof(Control)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(control, true);
}
