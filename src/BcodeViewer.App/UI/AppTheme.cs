using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BcodeViewer.App.UI;

/// <summary>
/// The active theme's palette, as seen by every WinForms control in the app. These were
/// static readonly fields when there was exactly one (dark) theme; they are now properties
/// reading <see cref="ThemeManager.Current"/>, which is what lets the ~90 existing
/// AppColors.X call sites across MainForm/HintCodeForm/the dialogs re-theme without a single
/// one of them changing. Deliberately still a standalone copy rather than referencing
/// Bcode.App's own ThemeManager — the two projects have no project reference in either
/// direction by design (BcodeViewer runs as its own process; see MainForm.cs's doc comment).
/// </summary>
public static class AppColors
{
    public static Color Background => ThemeManager.Current.Background;
    public static Color Panel => ThemeManager.Current.Panel;
    public static Color PanelAlt => ThemeManager.Current.PanelAlt;
    public static Color Border => ThemeManager.Current.Border;
    public static Color Text => ThemeManager.Current.Text;
    public static Color TextMuted => ThemeManager.Current.TextMuted;
    public static Color Accent => ThemeManager.Current.Accent;
    public static Color AccentHover => ThemeManager.Current.AccentHover;
    public static Color AccentText => ThemeManager.Current.AccentText;
    public static Color Selection => ThemeManager.Current.Selection;
    public static Color Input => ThemeManager.Current.Input;
    public static Color ButtonBack => ThemeManager.Current.ButtonBack;

    /// <summary>Amber "unsaved changes" marker on a file node in the left tree.</summary>
    public static Color DirtyMarker => ThemeManager.Current.DirtyMarker;
}

public class FlatColorTable : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin => AppColors.PanelAlt;
    public override Color ToolStripGradientMiddle => AppColors.PanelAlt;
    public override Color ToolStripGradientEnd => AppColors.PanelAlt;
    public override Color MenuStripGradientBegin => AppColors.PanelAlt;
    public override Color MenuStripGradientEnd => AppColors.PanelAlt;
    public override Color ImageMarginGradientBegin => AppColors.PanelAlt;
    public override Color ImageMarginGradientMiddle => AppColors.PanelAlt;
    public override Color ImageMarginGradientEnd => AppColors.PanelAlt;
    public override Color MenuItemSelected => AppColors.Selection;
    public override Color MenuItemSelectedGradientBegin => AppColors.Selection;
    public override Color MenuItemSelectedGradientEnd => AppColors.Selection;
    public override Color MenuItemPressedGradientBegin => AppColors.Selection;
    public override Color MenuItemPressedGradientEnd => AppColors.Selection;
    public override Color MenuItemBorder => AppColors.Accent;
    public override Color MenuBorder => AppColors.Border;
    public override Color ButtonSelectedHighlight => AppColors.Selection;
    public override Color ButtonSelectedHighlightBorder => AppColors.Accent;
    public override Color ButtonPressedHighlight => AppColors.Selection;
    public override Color ButtonPressedHighlightBorder => AppColors.Accent;
    public override Color SeparatorDark => AppColors.Border;
    public override Color SeparatorLight => AppColors.Border;
    public override Color ToolStripBorder => AppColors.Border;
    public override Color OverflowButtonGradientBegin => AppColors.PanelAlt;
    public override Color OverflowButtonGradientMiddle => AppColors.PanelAlt;
    public override Color OverflowButtonGradientEnd => AppColors.PanelAlt;
}

public class FlatToolStripRenderer : ToolStripProfessionalRenderer
{
    public FlatToolStripRenderer() : base(new FlatColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = AppColors.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // no extra border line — keeps the flat look
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripButton { Enabled: true } button) { base.OnRenderButtonBackground(e); return; }
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        if (button.Pressed || button.Checked)
            using (var b = new SolidBrush(AppColors.Selection)) e.Graphics.FillRectangle(b, rect);
        else if (button.Selected)
            using (var b = new SolidBrush(AppColors.PanelAlt)) e.Graphics.FillRectangle(b, rect);
    }
}

public static class ThemeManager
{
    /// <summary>Font chung cho phần giao diện WinForms: Roboto nếu máy đã cài (khớp với phần
    /// editor/web vốn luôn dùng Roboto đóng gói sẵn trong Web/fonts), không thì Segoe UI.
    /// Cỡ 10 thay vì 9 cũ cho dễ đọc hơn.</summary>
    public static readonly Font BaseFont = CreateBaseFont();

    private static Font CreateBaseFont()
    {
        using var installed = new System.Drawing.Text.InstalledFontCollection();
        var hasRoboto = installed.Families.Any(f => f.Name.Equals("Roboto", StringComparison.OrdinalIgnoreCase));
        return new Font(hasRoboto ? "Roboto" : "Segoe UI", 10f);
    }

    private static ThemeDefinition _current = ThemeCatalog.Default;
    private static bool _followSystem;

    /// <summary>The palette everything reads. Never null — <see cref="ThemeCatalog.ById"/>
    /// falls back to the default for an unknown id.</summary>
    public static ThemeDefinition Current => _current;

    /// <summary>True while the theme is being driven by the Windows app-colour setting.</summary>
    public static bool FollowSystem => _followSystem;

    /// <summary>Raised after <see cref="Current"/> changes. Long-lived windows (MainForm)
    /// subscribe and re-skin in place; short-lived dialogs don't need to, since they read
    /// the palette when they're constructed and a modal can't be open while the theme menu
    /// is being used anyway.</summary>
    public static event Action? ThemeChanged;

    /// <summary>
    /// Applies a theme by id, or follows Windows when <paramref name="followSystem"/> is
    /// set. Idempotent: re-selecting the active theme does nothing rather than firing a
    /// full re-skin of every window, which is visible as a flicker.
    /// </summary>
    public static void SetTheme(string? themeId, bool followSystem = false)
    {
        var resolved = followSystem ? ThemeCatalog.ForSystem(SystemPrefersDark()) : ThemeCatalog.ById(themeId);
        var changed = !ReferenceEquals(resolved, _current) || followSystem != _followSystem;

        _followSystem = followSystem;
        HookSystemThemeWatcher(followSystem);
        if (!changed) return;

        _current = resolved;
        ThemeChanged?.Invoke();
    }

    /// <summary>Reads the Windows "app mode" setting (Settings &gt; Personalisation &gt;
    /// Colours). Defaults to dark if the value is missing or unreadable — a locked-down
    /// registry shouldn't silently flip an editor to white.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch
        {
            return true;
        }
    }

    private static bool _watchingSystem;

    /// <summary>
    /// Windows raises UserPreferenceChanged(General) when the light/dark setting flips, so
    /// "theo Windows" actually tracks rather than only being read at startup. Hooked only
    /// while that mode is on: SystemEvents holds a static handler reference, and leaving it
    /// attached would keep this alive (and re-theming) after the user picked a fixed theme.
    /// </summary>
    private static void HookSystemThemeWatcher(bool enable)
    {
        if (enable == _watchingSystem) return;
        _watchingSystem = enable;
        if (enable) SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        else SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || !_followSystem) return;
        var resolved = ThemeCatalog.ForSystem(SystemPrefersDark());
        if (ReferenceEquals(resolved, _current)) return;
        _current = resolved;
        ThemeChanged?.Invoke();
    }

    public static void Apply(Control root)
    {
        StyleControl(root);
        foreach (Control child in root.Controls)
            Apply(child);
    }

    private static void StyleControl(Control c)
    {
        switch (c)
        {
            case Form form:
                form.BackColor = AppColors.Background;
                form.ForeColor = AppColors.Text;
                ApplyTitleBar(form);
                break;

            case MenuStrip menu:
                menu.BackColor = AppColors.PanelAlt;
                menu.ForeColor = AppColors.Text;
                menu.Renderer = new FlatToolStripRenderer();
                foreach (ToolStripItem item in menu.Items)
                    if (item is ToolStripDropDownItem ddItem) ApplyMenu(ddItem.DropDown);
                break;

            case StatusStrip status:
                status.BackColor = AppColors.PanelAlt;
                status.ForeColor = AppColors.TextMuted;
                status.Renderer = new FlatToolStripRenderer();
                status.SizingGrip = false;
                break;

            case ToolStrip toolStrip:
                toolStrip.BackColor = AppColors.PanelAlt;
                toolStrip.ForeColor = AppColors.Text;
                toolStrip.Renderer = new FlatToolStripRenderer();
                toolStrip.GripStyle = ToolStripGripStyle.Hidden;
                foreach (ToolStripItem item in toolStrip.Items)
                    if (item is ToolStripDropDownItem ddItem) ApplyMenu(ddItem.DropDown);
                break;

            case Button button:
                StyleButton(button);
                break;

            case TextBox textBox:
                textBox.BackColor = AppColors.Input;
                textBox.ForeColor = AppColors.Text;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case RichTextBox richTextBox:
                richTextBox.BackColor = AppColors.Panel;
                richTextBox.ForeColor = AppColors.Text;
                richTextBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ComboBox combo:
                combo.BackColor = AppColors.Input;
                combo.ForeColor = AppColors.Text;
                combo.FlatStyle = FlatStyle.Flat;
                break;

            case ListBox listBox:
                listBox.BackColor = AppColors.Panel;
                listBox.ForeColor = AppColors.Text;
                listBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case TreeView tree:
                tree.BackColor = AppColors.Panel;
                tree.ForeColor = AppColors.Text;
                tree.BorderStyle = BorderStyle.None;
                tree.LineColor = AppColors.Border;
                tree.FullRowSelect = true;
                tree.HotTracking = true;
                break;

            case LinkLabel link:
                link.LinkColor = AppColors.Text;
                link.ActiveLinkColor = AppColors.AccentHover;
                link.VisitedLinkColor = AppColors.Text;
                link.BackColor = Color.Transparent;
                break;

            case CheckBox or RadioButton:
                c.ForeColor = AppColors.Text;
                c.BackColor = Color.Transparent;
                break;

            case Label label:
                label.ForeColor = AppColors.Text;
                break;

            case SplitContainer split:
                split.BackColor = AppColors.Border;
                break;

            case Panel or TableLayoutPanel or FlowLayoutPanel:
                c.BackColor = AppColors.Background;
                c.ForeColor = AppColors.Text;
                break;
        }

        if (c.ContextMenuStrip is { } ownContextMenu) ApplyMenu(ownContextMenu);

        if (!c.Font.FontFamily.Name.Equals("Consolas", StringComparison.OrdinalIgnoreCase) && c is not Form)
            c.Font = c.Font.Bold ? new Font(BaseFont, FontStyle.Bold) : BaseFont;
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = AppColors.Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = AppColors.AccentHover;
        button.FlatAppearance.MouseDownBackColor = AppColors.Accent;
        var isPrimary = button.DialogResult == DialogResult.OK;
        button.BackColor = isPrimary ? AppColors.Accent : AppColors.ButtonBack;
        // Text on the accent is its own palette entry rather than the theme's body text:
        // Monokai's accent is a hot pink that white sits on and #F8F8F2-on-pink does not,
        // and Light+ needs white here while its body text is near-black.
        button.ForeColor = isPrimary ? AppColors.AccentText : AppColors.Text;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    public static void ApplyMenu(ToolStripDropDown menu)
    {
        menu.BackColor = AppColors.PanelAlt;
        menu.ForeColor = AppColors.Text;
        menu.Renderer = new FlatToolStripRenderer();
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = AppColors.Text;
            item.BackColor = AppColors.PanelAlt;
            if (item is ToolStripDropDownItem { HasDropDownItems: true } ddItem)
                ApplyMenu(ddItem.DropDown);
        }
    }

    // ---- Windows title bar -------------------------------------------------------------

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModePre20H1 = 19;

    /// <summary>
    /// Paints the non-client area (title bar, window border) to match the theme. Without
    /// this a dark theme stops dead at the top of the window — a black app under a white
    /// Windows caption bar, which is the single most obvious way a "dark mode" reads as
    /// half-finished.
    ///
    /// Two attribute numbers because the constant was renumbered in Windows 10 20H1: the
    /// older build ignores 20 and the newer one ignores 19, so both are set and whichever
    /// the OS understands wins. Failures are ignored — on a Windows version with neither,
    /// the only consequence is the caption bar staying light.
    /// </summary>
    public static void ApplyTitleBar(Form form)
    {
        // Apply() runs from a form's constructor, where the window handle does not exist
        // yet and the DWM call would be a no-op. Defer to the moment it does exist — as a
        // one-shot, since Apply() runs again on every theme change and a handler left
        // attached per switch would pile up.
        if (!form.IsHandleCreated)
        {
            void OnHandleCreated(object? s, EventArgs e)
            {
                form.HandleCreated -= OnHandleCreated;
                ApplyTitleBar(form);
            }
            form.HandleCreated += OnHandleCreated;
            return;
        }

        var value = Current.IsDark ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkModePre20H1, ref value, sizeof(int));

            // Setting the attribute on an already-visible window doesn't repaint the caption
            // on its own — switching from Light+ to a dark theme would leave the old white
            // title bar until the window was next resized or reactivated. A frame-changed
            // SetWindowPos forces the non-client area to redraw now, without moving,
            // resizing, restacking or flashing the window.
            if (form.Visible)
                SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch (DllNotFoundException)
        {
            // dwmapi.dll missing (composition disabled / Server Core) — nothing to do.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoActivate = 0x0010;
}
