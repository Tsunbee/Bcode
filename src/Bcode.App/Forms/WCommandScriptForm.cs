using Bcode.App.Controls;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// "Script" popup — shows the DELETE/INSERT script "Gen Script Menu" builds for a
/// wcommand (+ command) row, SQL-highlighted the same way ScriptEditorControl/
/// RawSqlControl do (SqlSyntaxHighlighter.Apply). Deliberately just Save/Clear/
/// Close + a Find box — no line-number gutter, unlike FCode's own script viewer;
/// this is a one-off generated script to copy or save, not something you'd want
/// to navigate by line number.
/// </summary>
public class WCommandScriptForm : ThemedForm
{
    private readonly RichTextBox _box;
    private readonly TextBox _findBox;

    /// <summary>Fired after Save actually writes the script to a file, with the path it was
    /// saved to — Add Script (TableEditControl/SqlQueryControl) subscribes to this to also add
    /// the file to the Script Cart, so the toolbar's View/Save/Copy Script (which all work off
    /// that cart) pick up a script generated this way too, the same as one added by hand via
    /// the toolbar's own "Add Script".</summary>
    public event Action<string>? ScriptSaved;

    public WCommandScriptForm(string script, string title = "Script") : this(script, title, chunkedHighlight: false)
    {
    }

    /// <summary><paramref name="chunkedHighlight"/>=true is Add Script's path for a big
    /// generated result (an 18k+-row DELETE+INSERT script). An earlier version of this
    /// constructor took a precomputed whole-document RTF string built off the UI thread and
    /// assigned it via box.Rtf — that moved the *building* off the UI thread, but the final
    /// box.Rtf = rtf assignment is still one synchronous native RichEdit parse of the whole
    /// document, and for a many-MB result THAT single call was the actual freeze ("Fcode khi
    /// add script 18k dòng KH chỉ mất vài giây, còn Bcode làm not responding" — it doesn't
    /// matter that the string was built in the background if handing it to the control is one
    /// long blocking call). So this path instead leaves the box empty here and defers to
    /// SqlSyntaxHighlighter.ApplyChunkedAsync once the window has actually appeared (Shown):
    /// that sets the plain text immediately (fast, no RTF parsing) and colors it in small
    /// chunks, yielding to the message loop between them, so the window is visible and usable
    /// right away and never goes long enough without pumping messages for Windows to flag it
    /// unresponsive — matching FCode. Every other caller (Gen Script Menu, Structure list
    /// Preview, small Add Script results) keeps the instant single-Rtf-on-Load path below,
    /// which is already fast enough for those sizes and doesn't need chunking.</summary>
    public WCommandScriptForm(string script, string title, bool chunkedHighlight)
    {
        Text = title;
        Width = 760;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        var saveButton = new Button { Text = "Save", AutoSize = true };
        saveButton.Click += (_, _) => SaveToFile();
        var clearButton = new Button { Text = "Clear", AutoSize = true };
        clearButton.Click += (_, _) => { _box.Clear(); };
        var closeButton = new Button { Text = "Close", AutoSize = true };
        closeButton.Click += (_, _) => Close();

        _findBox = new TextBox { Width = 200, PlaceholderText = "Find..." };
        var findNextButton = new Button { Text = "Find Next", AutoSize = true };
        findNextButton.Click += (_, _) => FindNext();
        _findBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; FindNext(); } };

        toolbar.Controls.Add(saveButton);
        toolbar.Controls.Add(clearButton);
        toolbar.Controls.Add(closeButton);
        toolbar.Controls.Add(new Label { Text = "  ", AutoSize = true });
        toolbar.Controls.Add(_findBox);
        toolbar.Controls.Add(findNextButton);

        _box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = ThemeManager.MonoFont,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };

        if (chunkedHighlight)
        {
            // Left empty here on purpose — ApplyChunkedAsync (on Shown) sets the text itself,
            // right before it starts coloring, so there's no separate plain-text assignment to
            // do twice over.
            Shown += async (_, _) => await SqlSyntaxHighlighter.ApplyChunkedAsync(_box, script, _box.Font, AppColors.Text);
        }
        else
        {
            _box.Text = script;
        }

        Controls.Add(_box);
        Controls.Add(toolbar);

        if (!chunkedHighlight) Load += (_, _) => SqlSyntaxHighlighter.Apply(_box);
    }

    private void FindNext()
    {
        var term = _findBox.Text;
        if (string.IsNullOrEmpty(term)) return;

        var start = _box.SelectionStart + _box.SelectionLength;
        var index = _box.Text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = _box.Text.IndexOf(term, 0, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return;

        _box.Select(index, term.Length);
        _box.ScrollToCaret();
        _box.Focus();
    }

    private void SaveToFile()
    {
        using var sfd = new SaveFileDialog { Filter = "SQL script (*.sql)|*.sql|Tất cả (*.*)|*.*", FileName = "wcommand_script.sql" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(sfd.FileName, _box.Text);
        ScriptSaved?.Invoke(sfd.FileName);
    }
}
