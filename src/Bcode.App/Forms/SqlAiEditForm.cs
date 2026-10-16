using Bcode.App.Controls;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>Hộp thoại Ctrl+I của tab SQL Query chế độ App: gõ yêu cầu → Generate → xem đề xuất → Insert (thay đoạn đang chọn, hoặc chèn tại con trỏ).
/// Giống hộp "AI: Sửa/Sinh SQL" của bản Web (sqleditor.html showAiEdit). Gọi AI qua <see cref="Func{T, TResult}"/> do tab truyền vào.</summary>
public sealed class SqlAiEditForm : ThemedForm
{
    private readonly Func<string, Task<(string Text, string Error)>> _generate;
    private readonly RoundedInput _instruction = NK.Memo(3, "", mono: true);
    private readonly RoundedInput _output = NK.Memo(10, "", mono: true);
    private readonly Label _status = NK.Muted("");
    private readonly PillButton _btnGenerate, _btnInsert, _btnClose;
    private bool _busy;

    /// <summary>Đoạn SQL AI đề xuất (đã bấm Insert).</summary>
    public string ResultText { get; private set; } = "";

    public SqlAiEditForm(Func<string, Task<(string Text, string Error)>> generate, bool hasSelection, string engine)
    {
        _generate = generate;
        Text = hasSelection ? "AI: Sửa SQL (đang chọn đoạn)" : "AI: Sinh SQL tại con trỏ";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Width = 780;
        Height = 620;
        KeyPreview = true;

        _instruction.PlaceholderText = hasSelection
            ? "Ví dụ: thêm điều kiện ma_dvcs, đổi sang LEFT JOIN dmkh..."
            : "Ví dụ: truy vấn tổng tiền theo khách hàng trong tháng...";
        _output.ReadOnly = true;
        _instruction.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; _ = GenerateAsync(); }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };

        _btnGenerate = Button("Generate", () => _ = GenerateAsync(), primary: true);
        _btnInsert = Button("Insert", () => { ResultText = _output.Text; DialogResult = DialogResult.OK; Close(); });
        _btnClose = Button("Close", () => { DialogResult = DialogResult.Cancel; Close(); });
        _btnInsert.Enabled = false;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = NativeSkin.RowHeight(this, 44), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 6, 10, 6), BackColor = Color.Transparent };
        buttons.Controls.AddRange(new Control[] { _btnClose, _btnInsert, _btnGenerate });

        _status.Dock = DockStyle.Bottom;
        _status.AutoSize = false;
        _status.Height = NativeSkin.RowHeight(this, 24);
        _status.Padding = new Padding(12, 0, 12, 0);

        var hint = NK.Muted($"Yêu cầu (Ctrl+Enter để gửi) — {engine} biết cấu trúc các bảng đang dùng trong script");
        hint.Dock = DockStyle.Top; hint.AutoSize = false; hint.Height = NativeSkin.RowHeight(this, 26); hint.Padding = new Padding(12, 8, 12, 0);
        var hintOut = NK.Muted("Đề xuất từ AI");
        hintOut.Dock = DockStyle.Top; hintOut.AutoSize = false; hintOut.Height = NativeSkin.RowHeight(this, 26); hintOut.Padding = new Padding(12, 8, 12, 0);
        _instruction.Dock = DockStyle.Top;
        _output.Dock = DockStyle.Fill;

        // Dock: control thêm SAU được xếp trước (trên cùng) — Fill thêm đầu tiên.
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0), BackColor = Color.Transparent };
        body.Controls.Add(_output);
        body.Controls.Add(hintOut);
        body.Controls.Add(_instruction);
        body.Controls.Add(hint);
        Controls.Add(body);
        Controls.Add(_status);
        Controls.Add(buttons);

        Shown += (_, _) => _instruction.Focus();
    }

    private PillButton Button(string text, Action onClick, bool primary = false)
    {
        var b = PillButton.Flat(text, primary);
        b.Margin = new Padding(6, 0, 0, 0);
        b.Height = NativeSkin.RowHeight(this);
        b.Click += (_, _) => onClick();
        return b;
    }

    private async Task GenerateAsync()
    {
        var instruction = _instruction.Text.Trim();
        if (instruction.Length == 0 || _busy) return;
        _busy = true;
        _btnGenerate.Enabled = false; _btnInsert.Enabled = false;
        _output.Text = "";
        _status.Text = "Đang hỏi AI...";
        try
        {
            var (text, error) = await _generate(instruction);
            if (IsDisposed) return;
            if (error.Length > 0) { _status.Text = error; return; }
            _output.Text = text;
            _status.Text = "Xong — xem lại rồi bấm Insert.";
            _btnInsert.Enabled = text.Length > 0;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) _btnGenerate.Enabled = true;
        }
    }
}
