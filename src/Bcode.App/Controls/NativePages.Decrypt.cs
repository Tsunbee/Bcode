using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Decrypt SQL Object (decryptsql.html): chọn server / database, liệt kê object WITH ENCRYPTION, giải mã 1 hoặc tất cả, xem / copy / lưu .sql.</summary>
internal sealed class DecryptSqlPage : NativePage
{
    private sealed class Obj { public string Name = "", Type = ""; }

    private readonly RoundedInput _server = NK.Input(""), _db = NK.Input("");
    private readonly CheckBox _fmt = NK.Check("Format");
    private readonly RoundedInput _filter = NK.Input("Lọc object (gõ để lọc theo tên)...");
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly Label _count = NK.Muted(""), _warn = new() { AutoSize = false, Height = 0, Dock = DockStyle.Top, ForeColor = AppColors.Warning, Padding = new Padding(8, 4, 8, 4), BackColor = Color.Transparent };
    private readonly RoundedInput _out = NK.Memo(20, "", mono: true);
    private readonly Label _status = NK.Muted("");
    private readonly PillButton _bDbs, _bObjs, _bOne, _bAll, _bCopy, _bSave;
    private readonly AutoCompleteStringCollection _dbSrc = new();
    private List<Obj> _objects = new(), _view = new();
    private string _selected = "";
    private bool _busy, _loading;

    public DecryptSqlPage()
    {
        _fmt.Checked = true; _out.ReadOnly = true;
        _db.Inner.AutoCompleteMode = AutoCompleteMode.SuggestAppend; _db.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource; _db.Inner.AutoCompleteCustomSource = _dbSrc;
        _server.Width = NativeSkin.S(360); _db.Width = NativeSkin.S(260);
        _bDbs = NK.Button("Tải DB", () => Post(new { action = "loadDbs", conn = Conn() }));
        _bObjs = NK.Button("Tải object mã hóa", () => Post(WithDb("loadObjs")));
        var conn = new BarFlow { Dock = DockStyle.Top, Padding = new Padding(10, 8, 10, 4), AutoSize = true, WrapContents = true };
        conn.Controls.AddRange(new Control[] { NK.Text("Server\\Instance"), _server, NK.Text("Database"), _db, _bDbs, _bObjs, _fmt });

        _list.ItemHeight = (int)(ThemeManager.BaseFont.Height * 1.6);
        _list.DrawItem += Draw;
        _list.SelectedIndexChanged += (_, _) => { if (_loading || _list.SelectedIndex < 0 || _list.SelectedIndex >= _view.Count) return; _selected = _view[_list.SelectedIndex].Name; UpdateButtons(); };
        _list.DoubleClick += (_, _) => { if (!_busy && _selected.Length > 0) Post(WithDb("decrypt", new { name = _selected })); };
        _filter.TextChanged += (_, _) => RenderList();
        _db.TextChanged += (_, _) => UpdateButtons();
        _fmt.CheckedChanged += (_, _) => Post(new { action = "render", format = _fmt.Checked });

        var left = new Panel { Dock = DockStyle.Fill };
        _filter.Dock = DockStyle.Top; _count.Dock = DockStyle.Bottom; _count.Padding = new Padding(8, 2, 8, 2);
        left.Controls.Add(_list); left.Controls.Add(_count); left.Controls.Add(_filter);
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        _out.Dock = DockStyle.Fill; right.Controls.Add(_out); right.Controls.Add(_warn);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        HandleCreated += (_, _) => BeginInvoke(() => { try { split.SplitterDistance = Math.Max(240, (int)(Width * 0.3)); } catch { } });

        _bOne = NK.Button("Giải mã", () => Post(WithDb("decrypt", new { name = _selected })), primary: true);
        _bAll = NK.Button("Giải mã tất cả", () => Post(WithDb("decryptAll")));
        _bCopy = NK.Button("Copy", () => Post(new { action = "copy", text = _out.Text }));
        _bSave = NK.Button("Lưu .sql", () => Post(new { action = "saveSql", text = _out.Text, name = _selected }));
        var bar = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
        bar.Controls.AddRange(new Control[] { _bOne, _bAll, _bSave, _bCopy, _status });

        Controls.Add(split); Controls.Add(conn); Controls.Add(bar);
        UpdateButtons();
    }

    private object Conn() => new { server = _server.Text };

    private object WithDb(string action, object? extra = null)
    {
        var d = new Dictionary<string, object?> { ["action"] = action, ["conn"] = Conn(), ["db"] = _db.Text.Trim(), ["format"] = _fmt.Checked };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) d[p.Name] = p.GetValue(extra);
        return d;
    }

    private void UpdateButtons()
    {
        var hasDb = _db.Text.Trim().Length > 0; var hasOut = _out.Text.Length > 0;
        _bDbs.Enabled = !_busy; _bObjs.Enabled = !_busy && hasDb; _bOne.Enabled = !_busy && hasDb && _selected.Length > 0; _bAll.Enabled = !_busy && hasDb && _objects.Count > 0;
        _bCopy.Enabled = hasOut; _bSave.Enabled = hasOut;
    }

    private void RenderList()
    {
        var kw = _filter.Text.Trim().ToLowerInvariant();
        _view = kw.Length == 0 ? _objects : _objects.Where(o => o.Name.ToLowerInvariant().Contains(kw) || o.Type.ToLowerInvariant().Contains(kw)).ToList();
        _loading = true;
        _list.BeginUpdate(); _list.Items.Clear(); foreach (var o in _view) _list.Items.Add(o.Name); _list.EndUpdate();
        var at = _view.FindIndex(o => o.Name == _selected); if (at >= 0) _list.SelectedIndex = at;
        _loading = false;
        _count.Text = kw.Length > 0 ? $"{_view.Count}/{_objects.Count} object khớp '{_filter.Text.Trim()}'" : $"{_objects.Count} object mã hóa";
        UpdateButtons();
    }

    private void Draw(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _view.Count) return;
        var o = _view[e.Index]; var sel = (e.State & DrawItemState.Selected) != 0;
        using (var br = new SolidBrush(sel ? AppColors.Accent : AppColors.Panel)) e.Graphics.FillRectangle(br, e.Bounds);
        var fore = sel ? AppColors.OnAccent : AppColors.Text; var muted = sel ? AppColors.OnAccent : AppColors.TextMuted;
        var tw = TextRenderer.MeasureText(o.Type, _list.Font).Width + 8;
        TextRenderer.DrawText(e.Graphics, o.Type, _list.Font, new Rectangle(e.Bounds.Right - tw - 6, e.Bounds.Y, tw, e.Bounds.Height), muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        TextRenderer.DrawText(e.Graphics, o.Name, _list.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - tw - 20, e.Bounds.Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "decryptSql.init":
            {
                var d = Arg(a, 0);
                _server.Text = P(d, "server");
                SetDbs(PA(d, "databases").Select(x => x.GetString() ?? "").ToList());
                if (_db.Text.Length == 0 && _dbSrc.Count > 0) _db.Text = _dbSrc[0]!;
                UpdateButtons();
                break;
            }
            case "decryptSql.onDatabases": SetDbs((a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(x => x.GetString() ?? "").ToList()); UpdateButtons(); break;
            case "decryptSql.onObjects":
                _objects = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(o => new Obj { Name = P(o, "name"), Type = P(o, "type") }).ToList();
                _selected = ""; RenderList(); break;
            case "decryptSql.onOutput": _out.Text = Str(a, 0); UpdateButtons(); break;
            case "decryptSql.onWarning": { var t = Str(a, 0); _warn.Text = t.Length > 0 ? "⚠ " + t : ""; _warn.Height = t.Length > 0 ? NativeSkin.RowHeight(this, 30) : 0; break; }
            case "decryptSql.onStatus":
                _status.Text = Str(a, 0);
                _status.ForeColor = Str(a, 1) switch { "ok" => AppColors.Success, "err" => AppColors.Danger, "warn" => AppColors.Warning, _ => AppColors.TextMuted };
                break;
            case "decryptSql.onBusy": _busy = Bool(a, 0); Cursor = _busy ? Cursors.AppStarting : Cursors.Default; UpdateButtons(); break;
        }
    }

    private void SetDbs(List<string> dbs) { _dbSrc.Clear(); _dbSrc.AddRange(dbs.ToArray()); }

    public override void FocusPrimary() => _filter.Inner.Focus();
}
