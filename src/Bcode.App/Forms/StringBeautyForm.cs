using Bcode.App.Controls;
using Bcode.App.UI;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "String Beauty" tool: reformats pasted text — SQL (with a choice of Expanded/Compact
/// style and keyword case, like FCode's own String Beauty), or JSON/XML/JavaScript pretty-
/// printing. The SQL output is also syntax-highlighted (reusing SqlSyntaxHighlighter, the
/// same one RawSqlControl/ScriptEditorControl use) instead of the old plain-text view.
/// </summary>
public class StringBeautyForm : Bcode.App.UI.ThemedForm
{
    private readonly TextBox _input;
    private readonly RichTextBox _output;
    private readonly SqlFormatterService _formatter = new();

    private readonly ComboBox _typeCombo;
    private readonly ComboBox _sqlStyleCombo;
    private readonly ComboBox _keywordCaseCombo;

    public StringBeautyForm()
    {
        Text = "String Beauty";
        Width = 980;
        Height = 680;
        MinimumSize = new Size(760, 440);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true;
        MinimizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;

        // ---- Toolbar: loại nội dung + tuỳ chọn kiểu (chỉ áp dụng khi Loại = SQL) ----
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(8, 6, 8, 6)
        };

        toolbar.Controls.Add(new Label { Text = "Loại:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _typeCombo = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 2, 16, 2) };
        _typeCombo.Items.AddRange(new object[] { "SQL", "JSON", "XML", "JavaScript" });
        _typeCombo.SelectedIndex = 0;
        _typeCombo.SelectedIndexChanged += (_, _) => UpdateSqlOptionsEnabled();
        toolbar.Controls.Add(_typeCombo);

        toolbar.Controls.Add(new Label { Text = "Kiểu SQL:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _sqlStyleCombo = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 2, 16, 2) };
        _sqlStyleCombo.Items.AddRange(new object[] { "Expanded", "Compact" });
        _sqlStyleCombo.SelectedIndex = 0;
        toolbar.Controls.Add(_sqlStyleCombo);

        toolbar.Controls.Add(new Label { Text = "Từ khoá:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _keywordCaseCombo = new ComboBox { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 2, 0, 2) };
        _keywordCaseCombo.Items.AddRange(new object[] { "Giữ nguyên", "VIẾT HOA", "viết thường" });
        _keywordCaseCombo.SelectedIndex = 0;
        toolbar.Controls.Add(_keywordCaseCombo);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        _input = MakeInputBox();
        _output = MakeOutputBox();
        split.Panel1.Controls.Add(_input);
        split.Panel2.Controls.Add(_output);

        var actions = new WebActionBar { DefaultActionId = "run", CancelActionId = "close" };
        actions.Add("copy", "Copy kết quả", WebActionKind.Normal, left: true)
               .Add("close", "Đóng", WebActionKind.Quiet)
               .Add("run", "Beautify →", WebActionKind.Primary);
        actions.Invoked += async id =>
        {
            switch (id)
            {
                case "run": await RunBeautifyAsync(actions); break;
                case "copy":
                    if (_output.TextLength > 0) Clipboard.SetText(_output.Text);
                    actions.SetStatus(_output.TextLength > 0 ? "Đã copy kết quả." : "Chưa có kết quả để copy.", ok: _output.TextLength > 0);
                    break;
                case "close": Close(); break;
            }
        };

        Controls.Add(split);
        Controls.Add(actions);
        Controls.Add(toolbar);
    }

    private void UpdateSqlOptionsEnabled()
    {
        var isSql = _typeCombo.SelectedItem as string == "SQL";
        _sqlStyleCombo.Enabled = isSql;
        _keywordCaseCombo.Enabled = isSql;
    }

    private async Task RunBeautifyAsync(WebActionBar actions)
    {
        var text = _input.Text;
        if (text.Length == 0)
        {
            actions.SetStatus("Chưa có nội dung để beautify.", ok: false);
            return;
        }

        try
        {
            string result;
            var type = _typeCombo.SelectedItem as string;
            switch (type)
            {
                case "SQL":
                    var compact = _sqlStyleCombo.SelectedItem as string == "Compact";
                    result = _formatter.Format(text, compact);
                    result = (_keywordCaseCombo.SelectedItem as string) switch
                    {
                        "VIẾT HOA" => SqlSyntaxHighlighter.TransformKeywordCase(result, toUpper: true),
                        "viết thường" => SqlSyntaxHighlighter.TransformKeywordCase(result, toUpper: false),
                        _ => result
                    };
                    _output.Text = result;
                    await SqlSyntaxHighlighter.ApplyAsync(_output); // tô màu — "view đẹp hơn"
                    break;

                case "JSON":
                    result = _formatter.FormatJson(text);
                    _output.Text = result;
                    break;

                case "XML":
                    result = _formatter.FormatXml(text);
                    _output.Text = result;
                    break;

                default: // JavaScript
                    result = _formatter.FormatJavaScript(text);
                    _output.Text = result;
                    break;
            }
            actions.SetStatus("Đã beautify.", ok: true);
        }
        catch (Exception ex)
        {
            // Nội dung không hợp lệ với loại đã chọn (vd chọn JSON nhưng paste SQL) — báo lỗi
            // ngay trên thanh action thay vì crash hoặc im lặng để trống khung kết quả.
            actions.SetStatus($"Không beautify được: {ex.Message}", ok: false);
        }
    }

    private static TextBox MakeInputBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        Font = ThemeManager.MonoFont,
        WordWrap = false
    };

    private static RichTextBox MakeOutputBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = RichTextBoxScrollBars.Both,
        Font = ThemeManager.MonoFont,
        WordWrap = false,
        ReadOnly = true,
        BorderStyle = BorderStyle.FixedSingle
    };
}