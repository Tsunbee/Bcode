using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Khung trái "Structure / Fields" của tab Table ở chế độ App/WinForms — thay trang WebView2 tablestruct.html: hai tab (Structure: cấu trúc cột + chuột phải Gen script;
/// Fields: tick nhanh cột đưa lên ô Fields), ô lọc cột, ☑ / ☐ (tick / bỏ tick các cột đang hiện), danh sách cột có ô tick, tên cột khoá chính nổi bật, kiểu dữ liệu bên phải.
/// Gửi về chủ các thông điệp cùng tên với trang web: structChecked / fieldsChecked {names}, select {name}, menu {name}, reload.
/// </summary>
public sealed class NativeStructPanel : Panel
{
    public sealed record Col(string Name, string Type, bool Key);

    private sealed class CheckList : SmoothListBox
    {
        public List<Col> Cols = new();
        public HashSet<string> Checked = new();
        public string Selected = "";
        public bool ShowType = true;
        public event Action<string, bool, bool>? Toggled;          // name, newState, shift
        public event Action<string>? RowClicked;
        public event Action<string>? RowRightClicked;

        public CheckList()
        {
            FontChanged += (_, _) => ItemHeight = Math.Max(18, Font.Height + UiTemplate.Dens(8));
            ItemHeight = Math.Max(18, Font.Height + UiTemplate.Dens(8));
        }

        /// <summary>Danh sách không đổi (vd chỉ tick / bỏ tick) thì chỉ vẽ lại, không nạp lại — trước đây mỗi lần tick là xoá trắng cả danh sách (chớp + nhảy cuộn).</summary>
        public void Fill(IReadOnlyList<Col> list) => SetItemsSmooth(list);

        /// <summary>Đổi dòng đang chọn: chỉ vẽ lại dòng cũ và dòng mới.</summary>
        public void Select(string name)
        {
            if (Selected == name) return;
            var old = IndexOfName(Selected); Selected = name;
            InvalidateItem(old); InvalidateItem(IndexOfName(name));
        }

        private int IndexOfName(string name) { for (var i = 0; i < Items.Count; i++) if (((Col)Items[i]).Name == name) return i; return -1; }

        private int BoxSize => (int)(Font.Height * 0.9);

        protected override void DrawRow(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) { e.Graphics.FillRectangle(new SolidBrush(AppColors.Input), e.Bounds); return; }
            var c = (Col)Items[e.Index];
            var sel = ShowType && c.Name == Selected;
            var bg = sel ? AppColors.Selection : e.Index == Hot ? AppColors.PanelAlt : AppColors.Input;
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var s = BoxSize;
            var box = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + (e.Bounds.Height - s) / 2, s, s);
            var on = Checked.Contains(c.Name);
            using (var path = FlatToolStripRenderer.RoundedRect(box, Math.Max(2, s / 4)))
            {
                using (var fb = new SolidBrush(on ? AppColors.Accent : AppColors.Input)) e.Graphics.FillPath(fb, path);
                using var pen = new Pen(on ? AppColors.Accent : AppColors.TextMuted);
                e.Graphics.DrawPath(pen, path);
            }
            if (on)
            {
                using var tick = new Pen(AppColors.OnAccent, Math.Max(1.6f, s / 8f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                e.Graphics.DrawLines(tick, new[] { new Point(box.X + s / 5, box.Y + s / 2), new Point(box.X + s * 2 / 5, box.Y + s * 7 / 10), new Point(box.X + s * 4 /5, box.Y + s / 4) });
            }
            var textX = box.Right + 8;
            var typeW = ShowType ? Math.Min(TextRenderer.MeasureText(c.Type, Font).Width + 4, Math.Max(60, e.Bounds.Width / 3)) : (c.Key ? 24 : 0);
            var nameRect = new Rectangle(textX, e.Bounds.Y, e.Bounds.Width - textX - typeW - 8, e.Bounds.Height);
            using var nameFont = c.Key ? new Font(Font, FontStyle.Bold) : null;
            TextRenderer.DrawText(e.Graphics, c.Name, nameFont ?? Font, nameRect, c.Key ? AppColors.Accent : AppColors.Text, bg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var rt = ShowType ? c.Type : (c.Key ? "PK" : "");
            if (rt.Length > 0)
                TextRenderer.DrawText(e.Graphics, rt, Font, new Rectangle(e.Bounds.Right - typeW - 8, e.Bounds.Y, typeW, e.Bounds.Height), c.Key ? AppColors.Text : AppColors.TextMuted, bg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var i = IndexFromPoint(e.Location);
            if (i < 0 || i >= Items.Count) { base.OnMouseDown(e); return; }
            var c = (Col)Items[i];
            if (e.Button == MouseButtons.Right) { RowRightClicked?.Invoke(c.Name); return; }
            if (e.Button != MouseButtons.Left) return;
            Focus();
            var onBox = e.X < 8 + BoxSize + 6;
            if (onBox || !ShowType) Toggled?.Invoke(c.Name, !Checked.Contains(c.Name), (ModifierKeys & Keys.Shift) != 0);
            else RowClicked?.Invoke(c.Name);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            var i = IndexFromPoint(e.Location);
            if (i >= 0 && i < Items.Count && ShowType && e.X >= 8 + BoxSize + 6) { var c = (Col)Items[i]; Toggled?.Invoke(c.Name, !Checked.Contains(c.Name), false); }
        }
    }

    private readonly PillButton _tS, _tF;
    private readonly RoundedInput _filter = new() { PlaceholderText = "Lọc cột..." };
    private readonly CheckList _list = new() { Dock = DockStyle.Fill };
    private readonly Label _foot = new MutedLabel { Dock = DockStyle.Bottom, Height = NativeSkin.S(22), AutoEllipsis = true, Padding = new Padding(8, 3, 4, 0) };
    private List<Col> _cols = new();
    private HashSet<string> _structChecked = new(), _fieldsChecked = new();
    private bool _tabStruct = true;
    private int _anchor = -1;

    /// <summary>Thông điệp gửi về chủ: {action = "structChecked"|"fieldsChecked", names}, {action = "select"|"menu", name}, {action = "reload"}.</summary>
    public event Action<object>? Action;

    public NativeStructPanel()
    {
        Dock = DockStyle.Fill;
        _tS = PillButton.Flat("Structure"); _tF = PillButton.Flat("Fields");
        foreach (var b in new[] { _tS, _tF }) { b.Margin = new Padding(0, 0, 4, 0); b.Height = NativeSkin.RowHeight(this, 24); }
        _tS.Click += (_, _) => { _tabStruct = true; Render(); };
        _tF.Click += (_, _) => { _tabStruct = false; Render(); };
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(6, 6, 6, 2) };
        tabs.Controls.Add(_tS); tabs.Controls.Add(_tF);

        var all = PillButton.Flat("☑"); var none = PillButton.Flat("☐");
        foreach (var b in new[] { all, none }) { b.Dock = DockStyle.Right; b.AutoWidth = false; b.Width = NativeSkin.RowHeight(this, 30); b.Margin = new Padding(4, 0, 0, 0); }
        new ToolTip().SetToolTip(all, "Tick hết các cột đang hiện (Ctrl+A)"); new ToolTip().SetToolTip(none, "Bỏ tick hết các cột đang hiện");
        all.Click += (_, _) => SetChecked(Visible().Select(c => c.Name), true);
        none.Click += (_, _) => SetChecked(Visible().Select(c => c.Name), false);
        _filter.Dock = DockStyle.Fill;
        var tools = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 34), Padding = new Padding(6, 3, 6, 3) };
        tools.Controls.Add(_filter); tools.Controls.Add(all); tools.Controls.Add(none);

        Controls.Add(_list); Controls.Add(_foot); Controls.Add(tools); Controls.Add(tabs);

        _filter.TextChanged += (_, _) => Render();
        _list.Toggled += (name, on, shift) =>
        {
            var vis = Visible().Select(c => c.Name).ToList();
            var i = vis.IndexOf(name);
            if (shift && _anchor >= 0 && i >= 0) { var (a, b) = (Math.Min(_anchor, i), Math.Max(_anchor, i)); SetChecked(vis.Skip(a).Take(b - a + 1), on); }
            else SetChecked(new[] { name }, on);
            _anchor = i;
        };
        _list.RowClicked += name => { _list.Select(name); _anchor = Visible().Select(c => c.Name).ToList().IndexOf(name); Action?.Invoke(new { action = "select", name }); };
        _list.RowRightClicked += name => { if (!_tabStruct) return; _list.Select(name); Action?.Invoke(new { action = "menu", name }); };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; Action?.Invoke(new { action = "reload" }); return; }
            if (e.Control && e.KeyCode == Keys.A) { e.Handled = true; SetChecked(Visible().Select(c => c.Name), true); return; }
            var vis = Visible().Select(c => c.Name).ToList();
            if (_tabStruct && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) && vis.Count > 0)
            {
                e.Handled = true;
                var i = Math.Clamp(vis.IndexOf(_list.Selected) + (e.KeyCode == Keys.Down ? 1 : -1), 0, vis.Count - 1);
                _list.Select(vis[i]); _anchor = i;
                var row = _list.Items.IndexOf(_cols.First(c => c.Name == vis[i]));   // cuộn theo để dòng đang chọn luôn nhìn thấy
                var per = Math.Max(1, _list.ClientSize.Height / _list.ItemHeight);
                if (row >= 0 && row < _list.TopIndex) _list.TopIndex = row; else if (row >= _list.TopIndex + per) _list.TopIndex = row - per + 1;
                Action?.Invoke(new { action = "select", name = _list.Selected });
            }
            else if (_tabStruct && e.KeyCode == Keys.Space && _list.Selected.Length > 0) { e.Handled = true; SetChecked(new[] { _list.Selected }, !_structChecked.Contains(_list.Selected)); }
        };
        Render();
    }

    private IEnumerable<Col> Visible()
    {
        var q = _filter.Text.Trim().ToLowerInvariant();
        return _cols.Where(c => q.Length == 0 || c.Name.ToLowerInvariant().Contains(q) || (c.Type ?? "").ToLowerInvariant().Contains(q));
    }

    private HashSet<string> Set => _tabStruct ? _structChecked : _fieldsChecked;

    private void SetChecked(IEnumerable<string> names, bool on)
    {
        foreach (var n in names.ToList()) { if (on) Set.Add(n); else Set.Remove(n); }
        // Báo theo thứ tự cột của bảng để chủ cập nhật ô Fields.
        var ordered = _cols.Select(c => c.Name).Where(n => Set.Contains(n)).ToList();
        Action?.Invoke(new { action = _tabStruct ? "structChecked" : "fieldsChecked", names = ordered });
        Render();
    }

    private void Render()
    {
        _tS.IsPrimary = _tabStruct; _tF.IsPrimary = !_tabStruct;
        _tS.Text = "Structure" + (_structChecked.Count > 0 ? $" ({_structChecked.Count})" : "");
        _tF.Text = "Fields" + (_cols.Count > 0 ? $" ({_fieldsChecked.Count}/{_cols.Count})" : "");
        _tS.Invalidate(); _tF.Invalidate();
        _list.ShowType = _tabStruct;
        _list.Checked = Set;
        var vis = Visible().ToList();
        _list.Fill(vis);
        _foot.Text = _cols.Count == 0 ? "Chưa tải bảng. Nhập tên bảng ở ô Table rồi Enter."
            : _tabStruct ? (_structChecked.Count > 0 ? $"{_structChecked.Count} cột đã tick → ô Fields · chuột phải: Gen script" : "Tick cột → đưa lên ô Fields · chuột phải: Gen script")
            : $"{_fieldsChecked.Count}/{_cols.Count} cột đưa lên ô Fields";
    }

    /// <summary>Dựng lại danh sách (tương đương window.setColumns): cột theo thứ tự bảng, các cột tick ở Structure, các cột tick ở Fields (rỗng = tất cả).</summary>
    public void SetColumns(IEnumerable<Col> cols, IEnumerable<string> structChecked, IEnumerable<string>? fields)
    {
        _cols = cols.ToList();
        _structChecked = new HashSet<string>(structChecked ?? Array.Empty<string>());
        _fieldsChecked = fields is null ? _cols.Select(c => c.Name).ToHashSet() : new HashSet<string>(fields);
        if (!_cols.Any(c => c.Name == _list.Selected)) _list.Selected = "";
        _anchor = -1;
        Render();
    }
}
