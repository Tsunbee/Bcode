using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "File Reference" tool UI — search box + results list of every file/line
/// under App_Data that mentions a term (content grep), unlike File Lookup
/// which matches on file NAME. Double-click a result to open that file.
/// </summary>
public class FileReferenceControl : UserControl
{
    private readonly TextBox _rootBox;
    private readonly TextBox _searchBox;
    private readonly Button _searchButton;
    private readonly ListView _list;
    private readonly Label _statusLabel;
    private readonly FileReferenceService _service;

    public event Action<string, int>? FileActivated; // path, line number

    public FileReferenceControl(FileReferenceService service)
    {
        _service = service;
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false, Padding = new Padding(4, 4, 0, 0) };
        _rootBox = new TextBox { Width = 260, PlaceholderText = @"\\server\...\App_Data" };
        _searchBox = new TextBox { Width = 220, PlaceholderText = "Tên link/từ khoá cần tìm nơi tham chiếu..." };
        _searchBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; Search(); } };
        _searchButton = PillButton.Flat("🔍 Tìm tham chiếu", primary: true);
        _searchButton.Click += (_, _) => Search();

        top.Controls.Add(new Label { Text = "Root:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        top.Controls.Add(_rootBox);
        top.Controls.Add(new Label { Text = "Tìm:", AutoSize = true, Padding = new Padding(6, 6, 4, 0) });
        top.Controls.Add(_searchBox);
        top.Controls.Add(_searchButton);

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };

        _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        _list.Columns.Add("File", 420);
        _list.Columns.Add("Dòng", 60);
        _list.Columns.Add("Nội dung", 500);
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedItems.Count == 0) return;
            var item = _list.SelectedItems[0];
            if (item.Tag is (string path, int line)) FileActivated?.Invoke(path, line);
        };

        Controls.Add(_list);
        Controls.Add(_statusLabel);
        Controls.Add(top);
    }

    public void SetRootPath(string path) => _rootBox.Text = path;

    /// <summary>Used from the WCommand tree / File Lookup: pre-fill the search term and run.</summary>
    public void SearchFor(string term)
    {
        _searchBox.Text = term;
        Search();
    }

    private void Search()
    {
        _list.Items.Clear();
        if (string.IsNullOrWhiteSpace(_rootBox.Text) || string.IsNullOrWhiteSpace(_searchBox.Text))
        {
            _statusLabel.Text = "Nhập Root và từ khoá cần tìm.";
            return;
        }

        _statusLabel.Text = "Đang tìm...";
        Cursor = Cursors.WaitCursor;
        try
        {
            var count = 0;
            foreach (var m in _service.FindReferences(_rootBox.Text.Trim(), _searchBox.Text.Trim()))
            {
                var item = new ListViewItem(m.FilePath) { Tag = (m.FilePath, m.LineNumber) };
                item.SubItems.Add(m.LineNumber.ToString());
                item.SubItems.Add(m.LineText);
                _list.Items.Add(item);
                count++;
                if (count >= 500) break; // safety cap for a very common term on a huge source tree
            }
            _statusLabel.Text = $"{count} chỗ tham chiếu tới \"{_searchBox.Text.Trim()}\"" + (count >= 500 ? " (đã dừng ở 500 kết quả đầu)" : "") + ".";
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }
}
