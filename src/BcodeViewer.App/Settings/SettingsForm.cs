using BcodeViewer.App.UI;

namespace BcodeViewer.App.Settings;

/// <summary>Small modal for the one thing BcodeViewer needs configured: the Anthropic
/// API key (and which model to use) for the AI chat panel.</summary>
public class SettingsForm : Form
{
    private readonly ViewerSettings _settings;
    private readonly TextBox _apiKeyBox;
    private readonly TextBox _modelBox;

    public SettingsForm(ViewerSettings settings)
    {
        _settings = settings;
        Text = "BcodeViewer — Settings";
        Width = 480;
        Height = 200;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "Anthropic API key:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) }, 0, 0);
        _apiKeyBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Text = settings.AnthropicApiKey };
        layout.Controls.Add(_apiKeyBox, 1, 0);

        layout.Controls.Add(new Label { Text = "Model:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) }, 0, 1);
        _modelBox = new TextBox { Dock = DockStyle.Fill, Text = settings.Model };
        layout.Controls.Add(_modelBox, 1, 1);

        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40 };
        var okButton = new Button { Text = "OK", Width = 80, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
        okButton.Click += (_, _) => Save();
        buttonRow.Controls.Add(cancelButton);
        buttonRow.Controls.Add(okButton);

        Controls.Add(layout);
        Controls.Add(buttonRow);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        ThemeManager.Apply(this);
        okButton.BackColor = AppColors.Accent;
    }

    private void Save()
    {
        _settings.AnthropicApiKey = _apiKeyBox.Text.Trim();
        _settings.Model = string.IsNullOrWhiteSpace(_modelBox.Text) ? "claude-sonnet-5" : _modelBox.Text.Trim();
        _settings.Save();
    }
}
