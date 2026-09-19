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

    public WCommandScriptForm(string script, string title = "Script")
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
            Text = script,
        };

        Controls.Add(_box);
        Controls.Add(toolbar);

        Load += (_, _) => SqlSyntaxHighlighter.Apply(_box);
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
    }
}
