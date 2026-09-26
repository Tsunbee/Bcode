using BcodeViewer.App.UI;

namespace BcodeViewer.App.Settings;

/// <summary>Everything BcodeViewer needs configured: the Anthropic API key and model for
/// the AI features, the team's shared template folder, and the two IntelliSense sources
/// that reach outside the open file (the database, and Claude).</summary>
public class SettingsForm : Form
{
    private readonly ViewerSettings _settings;
    private readonly Func<string> _describeSqlStatus;

    private readonly TextBox _apiKeyBox;
    private readonly TextBox _modelBox;
    private readonly TextBox _sharedPathBox;
    private readonly TextBox _completionModelBox;
    private readonly TextBox _geminiApiKeyBox;
    private readonly ComboBox _completionEngineCombo;
    private readonly CheckBox _aiCompletionCheck;
    private readonly CheckBox _sqlCompletionCheck;
    private readonly CheckBox _sqlWritesCheck;
    private readonly TextBox _sqlRegionTagsBox;
    // Initialised here rather than in the constructor body: the Test button's handler
    // captures it before the constructor reaches its own row, which the compiler correctly
    // flags as a possible null dereference.
    private readonly Label _sqlStatusLabel = new() { Dock = DockStyle.Fill, AutoSize = false, Height = 34 };

    /// <param name="describeSqlStatus">Asks the live SqlSchemaService which workspace it
    /// resolved and whether it could reach it — the one place a connection problem becomes
    /// visible, since while typing it can only ever show up as an absence of suggestions.</param>
    public SettingsForm(ViewerSettings settings, Func<string> describeSqlStatus)
    {
        _settings = settings;
        _describeSqlStatus = describeSqlStatus;

        Text = "BcodeViewer — Settings";
        Width = 640;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12), AutoScroll = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        var row = 0;

        AddSectionHeader(layout, "AI (chat + gợi ý)", ref row);

        _apiKeyBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = settings.AnthropicApiKey };
        AddRow(layout, "Anthropic API key:", _apiKeyBox, ref row);
        _geminiApiKeyBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = settings.GeminiApiKey };
        AddRow(layout, "Gemini API key:", _geminiApiKeyBox, ref row);

        _modelBox = new TextBox { Dock = DockStyle.Fill, Text = settings.Model };
        AddRow(layout, "Model (chat):", _modelBox, ref row);

        _aiCompletionCheck = new CheckBox
        {
            Dock = DockStyle.Fill, AutoSize = true, Checked = settings.EnableAiCompletion,
            Text = "Bật gợi ý AI khi gõ (ghost text)",
        };
        AddRow(layout, "", _aiCompletionCheck, ref row);

        AddNote(layout,
            "Mỗi lần ngừng gõ ~0,4 giây sẽ gọi Claude 1 lần và bị tính phí. Ctrl+I (gọi tay) " +
            "vẫn chạy kể cả khi tắt mục này.", ref row);

        _completionModelBox = new TextBox { Dock = DockStyle.Fill, Text = settings.CompletionModel };
        AddRow(layout, "Model (ghost text):", _completionModelBox, ref row);
        _completionEngineCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _completionEngineCombo.Items.AddRange(new object[] { "Claude", "Gemini" });
        _completionEngineCombo.SelectedIndex = string.Equals(settings.CompletionEngine, "gemini", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AddRow(layout, "Engine gợi ý (ghost text):", _completionEngineCombo, ref row);

        AddNote(layout,
            "Chọn Gemini vẫn dùng nguyên prompt như Claude, chỉ đổi nơi gửi request — cần điền " +
            "Gemini API key ở trên. Chat panel và Ctrl+I không đổi theo mục này, luôn dùng Claude.", ref row);
        AddSectionHeader(layout, "Template dùng chung", ref row);

        _sharedPathBox = new TextBox { Dock = DockStyle.Fill, Text = settings.SharedTemplatePath };
        var browseButton = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        browseButton.Click += (_, _) => BrowseSharedFolder();
        AddRow(layout, "Thư mục chung:", _sharedPathBox, ref row, browseButton);

        AddNote(layout,
            "Thư mục UNC hoặc 1 repo git cả nhóm đọc được. Trong đó: *.code-snippets (định dạng " +
            "VSCode) và *.json là snippet gõ theo prefix; thư mục con files\\ là template cả file " +
            "cho Ctrl+N. BcodeViewer chỉ ĐỌC thư mục này.", ref row);

        AddSectionHeader(layout, "Gợi ý SQL", ref row);

        _sqlCompletionCheck = new CheckBox
        {
            Dock = DockStyle.Fill, AutoSize = true, Checked = settings.EnableSqlCompletion,
            Text = "Gợi ý tên bảng / cột trong vùng SQL",
        };
        var testButton = new Button { Text = "Test", Dock = DockStyle.Fill };
        testButton.Click += (_, _) => _sqlStatusLabel.Text = _describeSqlStatus();
        AddRow(layout, "", _sqlCompletionCheck, ref row, testButton);

        AddRow(layout, "", _sqlStatusLabel, ref row);

        _sqlWritesCheck = new CheckBox
        {
            Dock = DockStyle.Fill, AutoSize = true, Checked = settings.EnableSqlWrites,
            Text = "Cho phép \"Chạy SQL\" thực thi câu ghi (INSERT/UPDATE/DELETE/EXEC...)",
        };
        AddRow(layout, "", _sqlWritesCheck, ref row);

        AddNote(layout,
            "Mặc định TẮT. Khi tắt, câu có lệnh ghi vẫn chạy được ở chế độ \"Chạy thử (rollback)\" — " +
            "chạy trong transaction rồi luôn rollback, nên vẫn biết được số dòng bị ảnh hưởng mà " +
            "không đổi dữ liệu. Chỉ bật mục này khi bạn thực sự muốn ghi thật: BcodeViewer chạy trên " +
            "đúng WS mà Bcode đang chọn, thường là CSDL thật của khách.", ref row);

        _sqlRegionTagsBox = new TextBox { Dock = DockStyle.Fill, Text = settings.SqlRegionTags };
        AddRow(layout, "Thẻ chứa SQL:", _sqlRegionTagsBox, ref row);

        AddNote(layout,
            "Thường để trống. BcodeViewer đã tự nhận cấu trúc controller: command và action là " +
            "SQL, script và clientScript là JavaScript, css là CSS, CDATA có /* <flatten " +
            "type=\"Javascript\"> */ là JavaScript, và giá trị <!ENTITY> được phân loại theo nội " +
            "dung. Chỉ khai thêm ở đây nếu dự án bạn còn thẻ riêng nào khác chứa SQL " +
            "(cách nhau bằng dấu phẩy).", ref row);

        AddNote(layout,
            "Dùng lại đúng workspace (WS) mà Bcode đang chọn — không cần khai báo kết nối riêng. " +
            "Chỉ kết nối khi mở file .sql, và schema lấy về được nhớ sẵn cho tới khi đóng app.", ref row);

        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        var okButton = new Button { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        okButton.Click += (_, _) => Save();
        buttonRow.Controls.Add(cancelButton);
        buttonRow.Controls.Add(okButton);

        Controls.Add(layout);
        Controls.Add(buttonRow);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        ThemeManager.Apply(this);
        okButton.BackColor = AppColors.Accent;
        _sqlStatusLabel.ForeColor = AppColors.TextMuted;
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control, ref int row, Control? trailing = null)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0)
        {
            layout.Controls.Add(new Label
            {
                Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 0),
            }, 0, row);
        }
        layout.Controls.Add(control, 1, row);
        if (trailing is not null) layout.Controls.Add(trailing, 2, row);
        else layout.SetColumnSpan(control, 2);
        row++;
    }

    private static void AddSectionHeader(TableLayoutPanel layout, string text, ref int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = text, AutoSize = true, Margin = new Padding(0, row == 0 ? 0 : 14, 0, 4),
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        };
        layout.Controls.Add(label, 0, row);
        layout.SetColumnSpan(label, 3);
        row++;
    }

    private static void AddNote(TableLayoutPanel layout, string text, ref int row)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = text, Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(600, 0),
            ForeColor = AppColors.TextMuted, Margin = new Padding(0, 2, 0, 6),
        };
        layout.Controls.Add(label, 1, row);
        layout.SetColumnSpan(label, 2);
        row++;
    }

    private void BrowseSharedFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Chọn thư mục template dùng chung" };
        if (Directory.Exists(_sharedPathBox.Text)) dialog.SelectedPath = _sharedPathBox.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _sharedPathBox.Text = dialog.SelectedPath;
    }

    private void Save()
    {
        _settings.AnthropicApiKey = _apiKeyBox.Text.Trim();
        _settings.GeminiApiKey = _geminiApiKeyBox.Text.Trim();
        _settings.CompletionEngine = _completionEngineCombo.SelectedIndex == 1 ? "gemini" : "claude";
        _settings.Model = string.IsNullOrWhiteSpace(_modelBox.Text) ? "claude-sonnet-5" : _modelBox.Text.Trim();
        _settings.CompletionModel = string.IsNullOrWhiteSpace(_completionModelBox.Text)
            ? "claude-haiku-4-5-20251001"
            : _completionModelBox.Text.Trim();
        _settings.SharedTemplatePath = _sharedPathBox.Text.Trim();
        _settings.EnableAiCompletion = _aiCompletionCheck.Checked;
        _settings.EnableSqlCompletion = _sqlCompletionCheck.Checked;
        _settings.EnableSqlWrites = _sqlWritesCheck.Checked;
        _settings.SqlRegionTags = _sqlRegionTagsBox.Text.Trim();
        _settings.Save();
    }
}
