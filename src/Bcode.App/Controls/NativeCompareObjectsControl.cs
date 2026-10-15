using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>Khung xem khác biệt kiểu diff: mỗi dòng có tiền tố (+ thêm / − bớt / ⋯ gap / khoảng trắng = giống), dòng thêm tô xanh, dòng bớt tô đỏ; có cột số dòng. Dùng chung cho So sánh object / Compare Text...</summary>
internal sealed class DiffView : Scintilla
{
    private const int MarkPlus = 3, MarkMinus = 4, MarkGap = 5;

    public DiffView()
    {
        BorderStyle = ScintillaNET.BorderStyle.None;
        SqlScintilla.Configure(this);
        ReadOnly = true;
        ApplyColors();
        ThemeManager.ThemeChanged += ApplyColors;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= ApplyColors;
    }

    private void ApplyColors()
    {
        if (IsDisposed) return;
        SqlScintilla.ApplyTheme(this);
        Lexer = Lexer.Null;
        Margins[0].Width = TextWidth(Style.LineNumber, "99999") + 10;
        foreach (var (m, c, a) in new[] { (MarkPlus, Color.FromArgb(63, 174, 90), 70), (MarkMinus, Color.FromArgb(214, 76, 76), 70), (MarkGap, Color.FromArgb(128, 128, 128), 40) })
        {
            Markers[m].Symbol = MarkerSymbol.Background;
            Markers[m].SetBackColor(Color.FromArgb(Blend(AppColors.Input.R, c.R, a), Blend(AppColors.Input.G, c.G, a), Blend(AppColors.Input.B, c.B, a)));
        }
    }

    private static int Blend(int bg, int fg, int alpha) => (bg * (100 - alpha) + fg * alpha) / 100;

    public void Clear() { ReadOnly = false; Text = ""; ReadOnly = true; }

    /// <summary>Hiện danh sách dòng: (kiểu "+", "-", "…" hoặc " ", số dòng, nội dung). Số dòng hiện trong chữ (cột số dòng của editor là số thứ tự dòng hiển thị).</summary>
    public void Show(IEnumerable<(string Kind, int? No, string Text)> lines, string? note = null)
    {
        ReadOnly = false;
        var sb = new System.Text.StringBuilder();
        var kinds = new List<string>();
        if (!string.IsNullOrEmpty(note)) { sb.AppendLine(note); kinds.Add(" "); }
        foreach (var (k, n, t) in lines)
        {
            sb.Append(k == "…" ? "⋯" : k).Append(' ').Append(n is { } no ? no.ToString().PadLeft(5) + "  " : "       ").Append(t).Append('\n');
            kinds.Add(k);
        }
        Text = sb.ToString();
        MarkerDeleteAll(MarkPlus); MarkerDeleteAll(MarkMinus); MarkerDeleteAll(MarkGap);
        for (var i = 0; i < kinds.Count && i < Lines.Count; i++)
        {
            if (kinds[i] == "+") Lines[i].MarkerAdd(MarkPlus);
            else if (kinds[i] == "-") Lines[i].MarkerAdd(MarkMinus);
            else if (kinds[i] == "…") Lines[i].MarkerAdd(MarkGap);
        }
        GotoPosition(0);
        ReadOnly = true;
    }
}

/// <summary>Hộp xem script sinh ra (chỉ đọc, tô màu SQL) + Chép + "Mở trong tab SQL". Bcode chỉ sinh script, không chạy.</summary>
internal sealed class ScriptPreviewDialog : ThemedForm
{
    private readonly Scintilla _sc = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };

    public bool OpenInSql { get; private set; }

    public ScriptPreviewDialog(string title, string script, string note, bool canOpen = true)
    {
        Text = title; Width = NativeSkin.S(1000); Height = NativeSkin.S(720); StartPosition = FormStartPosition.CenterParent; ShowIcon = false; KeyPreview = true;
        SqlScintilla.Configure(_sc); _sc.Text = script; _sc.ReadOnly = true;
        var bar = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 6, 8, 6) };
        bar.Controls.Add(NK.Muted(note));
        bar.Controls.Add(NK.Button("Chép", () => NK.CopyText(script)));
        if (canOpen) bar.Controls.Add(NK.Button("Mở trong tab SQL", () => { OpenInSql = true; DialogResult = DialogResult.OK; Close(); }, primary: true));
        bar.Controls.Add(NK.Button("Đóng", () => { DialogResult = DialogResult.Cancel; Close(); }));
        Controls.Add(_sc); Controls.Add(bar);
        Shown += (_, _) => { SqlScintilla.ApplyTheme(_sc); _sc.Margins[0].Width = _sc.TextWidth(Style.LineNumber, "99999") + 10; };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }
}

/// <summary>
/// "So sánh object" ở chế độ App/WinForms (thay compareobjects.html): chọn hai bên (workspace + App/Sys), So sánh → danh sách object theo trạng thái (Khác nhau / Chỉ trái / Chỉ phải / Khoảng trắng /
/// Mã hoá / Giống) có lọc + tìm tên; bấm 1 object xem diff từng dòng; tick các mục đưa sang phải → Sinh script ALTER (chỉ sinh chữ, không chạy). Logic ở <see cref="ObjectCompareService"/>.
/// </summary>
public sealed class NativeCompareObjectsControl : UserControl
{
    private static readonly Dictionary<ObjectDiffStatus, string> Label = new()
    {
        [ObjectDiffStatus.Different] = "Khác nhau", [ObjectDiffStatus.OnlyLeft] = "Chỉ trái", [ObjectDiffStatus.OnlyRight] = "Chỉ phải",
        [ObjectDiffStatus.WhitespaceOnly] = "Khoảng trắng", [ObjectDiffStatus.Encrypted] = "Mã hoá", [ObjectDiffStatus.Same] = "Giống",
    };

    private readonly AppSettings _settings;
    private readonly Func<Workspace?> _current;
    private readonly ObjectCompareService _service = new();
    private ObjectCompareService.Session? _session;
    private CancellationTokenSource? _cts;

    private readonly DropDownButton _lw = new() { Width = NativeSkin.S(170) }, _ls = new() { Width = NativeSkin.S(110) }, _rw = new() { Width = NativeSkin.S(170) }, _rs = new() { Width = NativeSkin.S(110) };
    private readonly Label _status = NK.Muted("Chọn hai bên rồi bấm So sánh. Chỉ đọc — không ghi gì vào database.");
    private readonly PillButton _go, _bScript;
    private readonly Dictionary<ObjectDiffStatus, PillButton> _chips = new();
    private readonly HashSet<ObjectDiffStatus> _filter = new() { ObjectDiffStatus.Different, ObjectDiffStatus.OnlyLeft, ObjectDiffStatus.OnlyRight };
    private readonly RoundedInput _q = new() { PlaceholderText = "Lọc theo tên…", Width = NativeSkin.S(220) };
    private readonly DataGridView _list = new() { Dock = DockStyle.Fill };
    private readonly DiffView _diff = new() { Dock = DockStyle.Fill };
    private readonly Label _dh = new() { Dock = DockStyle.Top, Height = NativeSkin.S(30), Padding = new Padding(10, 7, 0, 0), Text = "Bấm một object ở bên trái để xem khác biệt." };
    private readonly Panel _barPanel;
    private List<ObjectDiffItem> _items = new(), _shown = new();
    private readonly HashSet<string> _sel = new();
    private string? _active;
    private bool _loading;
    private List<Workspace> _ws = new();

    /// <summary>(script, tiêu đề tab) — mở script vừa sinh trong tab SQL để xem / sửa / chạy tay.</summary>
    public event Action<string, string>? OpenSqlRequested;

    public NativeCompareObjectsControl(AppSettings settings, Func<Workspace?> current)
    {
        _settings = settings; _current = current;
        Dock = DockStyle.Fill;

        _ws = _settings.Workspaces.ToList();
        var names = _ws.Select(w => w.Name).ToList();
        var cur = _current()?.Name ?? "";
        var li = Math.Max(0, names.IndexOf(cur));
        var ri = names.Count > 1 ? (names.FindIndex(n => n != names[li]) is var x && x >= 0 ? x : 0) : 0;
        _lw.SetItems(names, li); _rw.SetItems(names, ri);
        _ls.SetItems(new[] { "App Data", "Sys Data" }, 0); _rs.SetItems(new[] { "App Data", "Sys Data" }, 0);
        foreach (var d in new[] { _lw, _ls, _rw, _rs }) { d.Height = NativeSkin.RowHeight(d, 26); d.OnPanelAlt = true; d.Margin = new Padding(0, 0, 4, 0); }

        _go = NK.Button("So sánh", () => _ = CompareAsync(), primary: true);
        var header = new BarFlow { Dock = DockStyle.Top, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 6) };
        header.Controls.AddRange(new Control[]
        {
            NK.Text("Trái (nguồn — vd bản dev):"), _lw, _ls,
            NK.Button("⇄", Swap, tip: "Đổi chỗ hai bên"),
            NK.Text("Phải (đích — vd bản khách):"), _rw, _rs, _go, _status,
        });

        _barPanel = new BarFlow { Dock = DockStyle.Top, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 4), Visible = false };
        foreach (var st in new[] { ObjectDiffStatus.Different, ObjectDiffStatus.OnlyLeft, ObjectDiffStatus.OnlyRight, ObjectDiffStatus.WhitespaceOnly, ObjectDiffStatus.Encrypted, ObjectDiffStatus.Same })
        {
            var s = st;
            var chip = NK.Button(Label[st] + " 0", () => { if (!_filter.Remove(s)) _filter.Add(s); View(); });
            _chips[st] = chip; _barPanel.Controls.Add(chip);
        }
        _q.Margin = new Padding(8, 0, 8, 0); _q.TextChanged += (_, _) => View();
        _bScript = NK.Button("Sinh script ALTER (0)", () => _ = ScriptAsync(), primary: true); _bScript.Enabled = false;
        _barPanel.Controls.AddRange(new Control[] { _q, NK.Button("Chọn các mục đưa sang phải", SelectAll), NK.Button("Bỏ chọn", () => { _sel.Clear(); View(); }), _bScript });

        _list.ReadOnly = false; _list.AllowUserToAddRows = false; _list.RowHeadersVisible = false; _list.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _list.MultiSelect = false;
        _list.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; _list.BorderStyle = System.Windows.Forms.BorderStyle.None; _list.AllowUserToResizeRows = false;
        _list.Columns.Add(new DataGridViewCheckBoxColumn { Name = "ck", HeaderText = "✓", Width = NativeSkin.S(34) });
        _list.Columns.Add(new DataGridViewTextBoxColumn { Name = "st", HeaderText = "Trạng thái", ReadOnly = true, Width = NativeSkin.S(100) });
        _list.Columns.Add(new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "Loại", ReadOnly = true, Width = NativeSkin.S(80) });
        _list.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Object", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 });
        _list.CurrentCellDirtyStateChanged += (_, _) => { if (_list.IsCurrentCellDirty) _list.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _list.CellValueChanged += (_, e) =>
        {
            if (_loading || e.ColumnIndex != 0 || e.RowIndex < 0 || e.RowIndex >= _shown.Count) return;
            var key = _shown[e.RowIndex].Key;
            if (_list.Rows[e.RowIndex].Cells[0].Value is true) _sel.Add(key); else _sel.Remove(key);
            UpdSel();
        };
        _list.SelectionChanged += (_, _) => { if (!_loading && _list.CurrentRow is { } r && r.Index >= 0 && r.Index < _shown.Count) _ = DetailAsync(_shown[r.Index]); };

        var detail = new Panel { Dock = DockStyle.Fill };
        detail.Controls.Add(_diff); detail.Controls.Add(_dh);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_list); split.Panel2.Controls.Add(detail);
        Controls.Add(split); Controls.Add(_barPanel); Controls.Add(header);
        HandleCreated += (_, _) => BeginInvoke(new Action(() => { try { split.SplitterDistance = Math.Max(320, (int)(Width * 0.38)); } catch { } }));
        Disposed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); };
    }

    private void Swap()
    {
        var (a, b, c, d) = (_lw.SelectedIndex, _rw.SelectedIndex, _ls.SelectedIndex, _rs.SelectedIndex);
        var names = _ws.Select(w => w.Name).ToList();
        _lw.SetItems(names, b); _rw.SetItems(names, a);
        _ls.SetItems(new[] { "App Data", "Sys Data" }, d); _rs.SetItems(new[] { "App Data", "Sys Data" }, c);
    }

    private void SetStatus(string text, bool error = false) { _status.Text = text; _status.ForeColor = error ? AppColors.Danger : AppColors.TextMuted; }

    private async Task CompareAsync()
    {
        if (_lw.SelectedIndex < 0 || _rw.SelectedIndex < 0) { SetStatus("Chọn workspace cho cả hai bên.", true); return; }
        var left = _ws[_lw.SelectedIndex]; var right = _ws[_rw.SelectedIndex];
        var leftSys = _ls.SelectedIndex == 1; var rightSys = _rs.SelectedIndex == 1;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _session = null; _items = new(); _sel.Clear(); _active = null;
        _go.Enabled = false; SetStatus("Đang chạy…"); _barPanel.Visible = false; _diff.Clear();
        try
        {
            var ses = await _service.CompareAsync(left, leftSys, right, rightSys, msg => { try { if (!IsDisposed) BeginInvoke(new Action(() => SetStatus(msg))); } catch { } }, cts.Token);
            if (cts.IsCancellationRequested) return;
            _session = ses;
            _items = ses.Items;
            SetStatus($"{left.Name} · {ObjectCompareService.DbName(left, leftSys)}  ⇄  {right.Name} · {ObjectCompareService.DbName(right, rightSys)} — {_items.Count} object");
            _barPanel.Visible = true; _dh.Text = "Bấm một object ở bên trái để xem khác biệt.";
            View();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) SetStatus("Lỗi: " + ex.Message, true); }
        finally { _go.Enabled = true; }
    }

    private static bool Sendable(ObjectDiffStatus s) => s is ObjectDiffStatus.Different or ObjectDiffStatus.OnlyLeft;

    private void View()
    {
        var q = _q.Text.Trim().ToLowerInvariant();
        _shown = _items.Where(i => _filter.Contains(i.Status) && (q.Length == 0 || (i.Schema + "." + i.Name).ToLowerInvariant().Contains(q))).Take(1500).ToList();
        _loading = true;
        try
        {
            _list.Rows.Clear();
            foreach (var i in _shown)
            {
                var r = _list.Rows.Add(_sel.Contains(i.Key), Label[i.Status], i.Kind, i.Schema + "." + i.Name);
                if (!Sendable(i.Status)) _list.Rows[r].Cells[0].ReadOnly = true;
                _list.Rows[r].DefaultCellStyle.ForeColor = i.Status switch { ObjectDiffStatus.Different => AppColors.Warning, ObjectDiffStatus.OnlyLeft => AppColors.Success, ObjectDiffStatus.OnlyRight => AppColors.Danger, _ => AppColors.TextMuted };
            }
        }
        finally { _loading = false; }
        foreach (var (st, chip) in _chips)
        {
            chip.Text = Label[st] + " " + _items.Count(i => i.Status == st);
            chip.IsPrimary = _filter.Contains(st); chip.Invalidate();
        }
        UpdSel();
    }

    private void UpdSel() { _bScript.Text = $"Sinh script ALTER ({_sel.Count})"; _bScript.Enabled = _sel.Count > 0; _bScript.Invalidate(); }

    private void SelectAll() { foreach (var i in _items.Where(i => Sendable(i.Status))) _sel.Add(i.Key); View(); }

    private async Task DetailAsync(ObjectDiffItem it)
    {
        if (_session is not { } ses) return;
        _active = it.Key;
        _dh.Text = $"{it.Schema}.{it.Name}   ·   {it.Kind}   ·   {Label[it.Status]}";
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            var l = await _service.GetDefinitionAsync(ses, true, it.Key, ct);
            var r = await _service.GetDefinitionAsync(ses, false, it.Key, ct);
            var lines = l is not null && r is not null ? await Task.Run(() => _service.BuildDiff(l, r), ct)
                : (l ?? r ?? "").Replace("\r\n", "\n").Split('\n').Select((t, i) => new ObjectDiffLine(l is not null ? "+" : "-", i + 1, t)).Take(1500).ToList();
            if (_active != it.Key || IsDisposed) return;
            var note = l is null && r is null ? "Object mã hoá hoặc không đọc được nội dung." : l is null ? "Chỉ có ở bên phải." : r is null ? "Chỉ có ở bên trái." : "";
            _diff.Show(lines.Select(x => (x.K, x.N, x.T)), note.Length > 0 ? note : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("Lỗi: " + ex.Message, true); }
    }

    private async Task ScriptAsync()
    {
        if (_session is not { } ses || _sel.Count == 0) return;
        try
        {
            var script = await _service.BuildScriptAsync(ses, _sel.ToList(), _cts?.Token ?? CancellationToken.None);
            using var dlg = new ScriptPreviewDialog("Script ALTER → " + ses.Right.Name, script, "Bcode chỉ sinh script, không chạy. Xem kỹ trước khi chạy lên database đích.");
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK && dlg.OpenInSql) OpenSqlRequested?.Invoke(script, "ALTER → " + ses.Right.Name);
        }
        catch (Exception ex) { SetStatus("Lỗi: " + ex.Message, true); }
    }
}
