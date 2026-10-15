using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>File Reference (filereference.html): tìm theo NỘI DUNG mọi file dưới App_Data; kết quả gom theo file, nhấp đúp mở file tại dòng đó.</summary>
internal sealed class FileReferencePage : NativePage
{
    private readonly RoundedInput _root = NK.Input(@"\\server\...\App_Data"), _term = NK.Input("Tên link/từ khoá...");
    private readonly Label _status = NK.Muted("Nhập Root và từ khoá cần tìm.", true);
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, FullRowSelect = true, ShowLines = false };
    private readonly Label _busy = new() { Text = "Đang tìm...", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, Visible = false, BackColor = Color.Transparent };
    private string _usedTerm = "";

    public FileReferencePage()
    {
        _root.Width = NativeSkin.S(420); _term.Width = NativeSkin.S(320);
        var bar = new BarFlow { Dock = DockStyle.Top, Padding = new Padding(10, 8, 10, 4), AutoSize = true, WrapContents = true };
        bar.Controls.AddRange(new Control[]
        {
            NK.Text("Root (thư mục App_Data)"), _root, NK.Button("…", () => Post(new { action = "browse", data = new { root = _root.Text } }), tip: "Chọn thư mục"),
            NK.Text("Tìm"), _term, NK.Button("🔍 Tìm tham chiếu", Search, primary: true),
        });
        _status.Dock = DockStyle.Top; _status.Padding = new Padding(12, 2, 12, 4);
        _term.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Search(); } };
        _root.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Search(); } };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            switch (e.Node.Tag)
            {
                case (string path, int line): Post(new { action = "open", data = new { path, line } }); break;
                case string p: Post(new { action = "open", data = new { path = p, line = (e.Node.FirstNode?.Tag is (string, int l0)) ? l0 : 1 } }); break;
            }
        };
        Controls.Add(_tree); Controls.Add(_status); Controls.Add(bar);
    }

    private void Search() { _usedTerm = _term.Text.Trim(); Post(new { action = "search", data = new { root = _root.Text.Trim(), term = _usedTerm } }); }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.setRoot": _root.Text = Str(a, 0); break;
            case "window.setTerm": _term.Text = Str(a, 0); break;
            case "window.setBusy": _tree.Enabled = !Bool(a, 0); _status.Text = Bool(a, 0) ? "Đang tìm..." : _status.Text; break;
            case "window.setStatus": SetStatus(Str(a, 0), Str(a, 1)); break;
            case "window.setResults":
            {
                if (a.Count > 3 && a[3].ValueKind == JsonValueKind.String) _usedTerm = a[3].GetString() ?? _usedTerm;
                SetStatus(Str(a, 1), Str(a, 2));
                _tree.BeginUpdate(); _tree.Nodes.Clear();
                var items = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>()).ToList();
                if (items.Count == 0) _tree.Nodes.Add("Không có kết quả.");
                foreach (var g in items.GroupBy(i => P(i, "path")))
                {
                    var name = System.IO.Path.GetFileName(g.Key);
                    var n = new TreeNode($"{name}      {g.Key}      {g.Count()} dòng") { Tag = g.Key, ToolTipText = g.Key, NodeFont = new Font(ThemeManager.BaseFont, FontStyle.Bold) };
                    foreach (var h in g) n.Nodes.Add(new TreeNode($"{P(h, "line"),6}   {P(h, "text").Replace("\t", "    ").Trim()}") { Tag = (g.Key, PI(h, "line")), ToolTipText = "Nhấp đúp để mở file tại dòng này" });
                    _tree.Nodes.Add(n); n.Expand();
                }
                _tree.EndUpdate();
                _tree.Enabled = true;
                break;
            }
        }
    }

    private void SetStatus(string text, string kind)
    {
        _status.Text = text;
        _status.ForeColor = kind switch { "ok" => AppColors.Success, "err" => AppColors.Danger, _ => AppColors.TextMuted };
    }

    public override void FocusPrimary() => _term.Inner.Focus();
}
