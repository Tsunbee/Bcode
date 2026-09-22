using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Bcode.App.UI;

namespace Bcode.App.Forms;

public class CompareTextControl : UserControl
{
    private readonly TextBox _file1Box;
    private readonly TextBox _file2Box;
    private readonly Button _btnBrowse1;
    private readonly Button _btnBrowse2;

    // Khung soạn thảo/so sánh
    private readonly RichTextBox _content1Box;
    private readonly RichTextBox _content2Box;

    public event Action? FloatWindowRequested;

    public CompareTextControl()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(20, 20, 20);

        // ---- 1. Toolbar trên cùng (Float window, Compare) ----
        var toolStrip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Color.FromArgb(24, 24, 24)
        };
        var btnFloat = new ToolStripButton("⧉ Float window") { ForeColor = Color.White };
        btnFloat.Click += (_, _) => FloatWindowRequested?.Invoke();

        var btnCompare = new ToolStripButton("📄 Compare") { ForeColor = Color.White };
        btnCompare.Click += (_, _) => RunCompare();

        toolStrip.Items.Add(btnFloat);
        toolStrip.Items.Add(btnCompare);

        // ---- 2. Vùng Declare (File 1, File 2) ----
        var declarePanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 100,
            Padding = new Padding(12, 6, 12, 6),
            BackColor = Color.FromArgb(16, 16, 16)
        };

        var lblDeclare = new Label
        {
            Text = "Declare",
            ForeColor = Color.FromArgb(0, 150, 255),
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 22
        };

        // Bảng chứa 2 dòng chọn file
        var declareTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            ColumnCount = 3,
            RowCount = 2
        };
        declareTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        declareTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        declareTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));

        _file1Box = new TextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
        _file2Box = new TextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };

        _btnBrowse1 = new Button { Text = "...", Dock = DockStyle.Fill, BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _btnBrowse2 = new Button { Text = "...", Dock = DockStyle.Fill, BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };

        _btnBrowse1.Click += (_, _) => ChooseFile(_file1Box, _content1Box);
        _btnBrowse2.Click += (_, _) => ChooseFile(_file2Box, _content2Box);

        declareTable.Controls.Add(new Label { Text = "File 1", ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        declareTable.Controls.Add(_file1Box, 1, 0);
        declareTable.Controls.Add(_btnBrowse1, 2, 0);

        declareTable.Controls.Add(new Label { Text = "File 2", ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        declareTable.Controls.Add(_file2Box, 1, 1);
        declareTable.Controls.Add(_btnBrowse2, 2, 1);

        var lblHint = new Label
        {
            Text = "* If you have a text, you can skip entering the path",
            ForeColor = Color.FromArgb(200, 140, 20),
            Font = new Font(Font.FontFamily, 8f, FontStyle.Italic),
            Dock = DockStyle.Bottom,
            Height = 18
        };

        declarePanel.Controls.Add(lblHint);
        declarePanel.Controls.Add(declareTable);
        declarePanel.Controls.Add(lblDeclare);

        // ---- 3. Vùng Editor so sánh (Content 1 & Content 2) ----
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4
        };

        // Cột bên trái: Content 1
        var pnlLeft = new Panel { Dock = DockStyle.Fill };
        var lblC1 = new Label { Text = "Content 1", Dock = DockStyle.Top, Height = 22, BackColor = Color.FromArgb(220, 220, 220), ForeColor = Color.Black, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
        _content1Box = new RichTextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.White, Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.None };
        pnlLeft.Controls.Add(_content1Box);
        pnlLeft.Controls.Add(lblC1);

        // Cột bên phải: Content 2
        var pnlRight = new Panel { Dock = DockStyle.Fill };
        var lblC2 = new Label { Text = "Content 2", Dock = DockStyle.Top, Height = 22, BackColor = Color.FromArgb(220, 220, 220), ForeColor = Color.Black, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
        _content2Box = new RichTextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.White, Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.None };
        pnlRight.Controls.Add(_content2Box);
        pnlRight.Controls.Add(lblC2);

        split.Panel1.Controls.Add(pnlLeft);
        split.Panel2.Controls.Add(pnlRight);
        
        split.HandleCreated += (_, _) =>
        {
            try
            {
                if (split.Width > 100)
                    split.SplitterDistance = split.Width / 2;
            }
            catch { }
        };

        Controls.Add(split);
        Controls.Add(declarePanel);
        Controls.Add(toolStrip);
    }

    private void ChooseFile(TextBox targetBox, RichTextBox contentBox)
    {
        using var ofd = new OpenFileDialog { Filter = "All files (*.*)|*.*|SQL files (*.sql)|*.sql" };
        if (ofd.ShowDialog() == DialogResult.OK)
        {
            targetBox.Text = ofd.FileName;
            try
            {
                contentBox.Text = File.ReadAllText(ofd.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Không đọc được file: " + ex.Message);
            }
        }
    }

    private void RunCompare()
    {
        var text1 = _content1Box.Text;
        var text2 = _content2Box.Text;

        if (string.Equals(text1, text2))
        {
            MessageBox.Show(this, "Nội dung hai bên hoàn toàn giống nhau.", "Compare Text", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this, "Phát hiện nội dung có sự khác biệt giữa hai bên.", "Compare Text", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}

/// <summary>
/// Cửa sổ Form Compare Text độc lập để tương thích với các lệnh mở Form truyền thống
/// </summary>
public class CompareTextForm : Form
{
    public CompareTextForm()
    {
        Text = "Compare Text";
        Width = 1000;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 20, 20);

        var ctrl = new CompareTextControl { Dock = DockStyle.Fill };
        ctrl.FloatWindowRequested += () => { };
        Controls.Add(ctrl);

        ThemeManager.Apply(this);
    }
}