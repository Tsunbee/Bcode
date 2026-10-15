using System.Text.Json;
using Bcode.App.Forms;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Check LCTT / CĐKT (cfsdiag.html): chọn file đính kèm → kiểm tra → kết quả theo mục; xem / sửa mẫu chuẩn TT99; thư viện mẫu chỉ tiêu của khách.</summary>
internal sealed class CfsDiagPage : NativePage
{
    private sealed record Slot(string Id, string Grp, string Need, string Group, string Label);

    private static readonly Slot[] Slots =
    {
        new("bsCfg", "balance", "", "Cân đối kế toán · TT99", "Khai báo chỉ tiêu CĐKT"),
        new("bsRep", "balance", "", "Cân đối kế toán · TT99", "Báo cáo CĐKT hiện tại"),
        new("cfCfg", "indirect", "", "LCTT gián tiếp · TT99", "Khai báo chỉ tiêu LCTT gián tiếp"),
        new("cfRep", "indirect", "", "LCTT gián tiếp · TT99", "Báo cáo LCTT gián tiếp hiện tại"),
        new("dirCfg", "direct", "", "LCTT trực tiếp · TT99", "Khai báo chỉ tiêu LCTT trực tiếp"),
        new("dirRep", "direct", "", "LCTT trực tiếp · TT99", "Báo cáo LCTT trực tiếp hiện tại"),
        new("tb", "", "any", "Số liệu sổ (dùng chung)", "Bảng cân đối phát sinh tài khoản"),
        new("journal", "", "cf", "Số liệu sổ (dùng chung)", "Bảng kê chứng từ (LCTT cần)"),
        new("dmtk", "", "any", "Số liệu sổ (dùng chung)", "Danh mục tài khoản của khách (tuỳ chọn — bắt TK gõ sai, TK công nợ)"),
    };

    private const string SqlText = "-- 1) Mẫu báo cáo khách đang có (form → thông tư): chọn đúng form khách dùng\r\nselect ma_maubc, form, ten_maubc from v20dmmaubc where ma_maubc in ('V20GLTC1', 'V20GLTC5', 'V20GLTC6') order by ma_maubc, form\r\n\r\n-- 2) Cân đối kế toán (thay <form>, vd V20GLTC108 = TT99, V20GLTC103 = TT200)\r\nselect * from v20gltc1 where form = '<form>' order by form, stt, ma_so\r\n\r\n-- 3) LCTT gián tiếp (vd V20GLTC605 = TT99, V20GLTC602 = TT200)\r\nselect * from v20gltc6 where form = '<form>' order by form, stt, ma_so\r\n\r\n-- 4) LCTT trực tiếp (vd V20GLTC507 = TT99, V20GLTC503 = TT200)\r\nselect * from v20gltc5 where form = '<form>' order by form, stt, ma_so\r\n\r\n-- 5) Danh mục tài khoản (dán vào ô Danh mục tài khoản của khách)\r\nselect tk, ten_tk, tk_me, bac, loai_tk, tk_cn from dmtk order by tk";

    private readonly Dictionary<string, RoundedInput> _slot = new();
    private readonly Dictionary<string, Control> _slotRow = new();
    private readonly Dictionary<string, Label> _grpLabel = new();
    private readonly CheckBox _cInd = NK.Check("LCTT gián tiếp"), _cDir = NK.Check("LCTT trực tiếp"), _cBal = NK.Check("Cân đối kế toán");
    private readonly CheckBox _fErr = NK.Check("Lệch"), _fWarn = NK.Check("Nghi vấn"), _fInfo = NK.Check("Ghi chú");
    private readonly RoundedInput _filter = NK.Input("Lọc theo chỉ tiêu / TK...");
    private readonly TreeView _result = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, ShowNodeToolTips = true };
    private readonly Label _resInfo = NK.Muted(""), _status = NK.Muted("");
    private readonly PillButton _bRun;
    private JsonElement _last;
    private StdDialog? _std;
    private ImportDialog? _imp;
    private bool _loading, _busy;

    public CfsDiagPage()
    {
        _cInd.Checked = _cDir.Checked = _cBal.Checked = true; _fErr.Checked = _fWarn.Checked = _fInfo.Checked = true;
        _bRun = NK.Button("▶ Kiểm tra", () => { SendAll(); Post(new { action = "run" }); }, primary: true);
        var head = new BarFlow { Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 4), AutoSize = true, WrapContents = true };
        head.Controls.AddRange(new Control[]
        {
            new Label { Text = "Check LCTT / CĐKT", AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 2, FontStyle.Bold), Margin = new Padding(0, 4, 10, 0) },
            _cInd, _cDir, _cBal,
            NK.Button("📚 Mẫu chuẩn TT99", OpenStd, tip: "Mẫu chuẩn Thông tư 99 dùng làm gốc để suy ra khách hàng sai ở đâu — xem, đối chiếu nguồn, ghi nhận thêm"), _bRun, _status,
        });
        foreach (var c in new[] { _cInd, _cDir, _cBal }) c.CheckedChanged += (_, _) => { if (_loading) return; ApplyVisibility(); SendAll(); };

        // ---- trái: file đính kèm ----
        var slots = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var stackItems = new List<Control>();
        string grp = "";
        foreach (var s in Slots)
        {
            if (s.Group != grp) { grp = s.Group; var gl = new Label { Text = grp, AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold), ForeColor = AppColors.Accent, Padding = new Padding(0, 8, 0, 2), Tag = (s.Grp, s.Need) }; _grpLabel[grp] = gl; stackItems.Add(gl); }
            var inp = NK.Input("Đường dẫn file .xlsx (bấm Chọn hoặc dán vào đây)"); _slot[s.Id] = inp; var id = s.Id;
            inp.Inner.Leave += (_, _) => CommitSlot(id);
            inp.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) CommitSlot(id); };
            var pick = NK.Button("Chọn…", () => Post(new { action = "pick", slot = id })); pick.Dock = DockStyle.Right;
            var x = NK.Button("✕", () => { inp.Text = ""; SendAll(); }, tip: "Bỏ file"); x.AutoWidth = false; x.Width = NativeSkin.S(30); x.Dock = DockStyle.Right;
            var row = new Panel { Height = NativeSkin.RowHeight(this, 56), Tag = (s.Grp, s.Need), Padding = new Padding(0, 0, 0, 4) };
            var line = new Panel { Dock = DockStyle.Bottom, Height = NativeSkin.RowHeight(this, 30) };
            inp.Dock = DockStyle.Fill; line.Controls.Add(inp);
            if (System.Text.RegularExpressions.Regex.IsMatch(s.Id, "Cfg$|^dmtk$")) { var paste = NK.Button("Dán…", () => PasteDialog(id), tip: "Dán kết quả select bộ chỉ tiêu (copy từ lưới SQL / Excel) thay cho file"); paste.Dock = DockStyle.Right; line.Controls.Add(paste); }
            line.Controls.Add(x); line.Controls.Add(pick);
            var lbl = NK.Muted(s.Label); lbl.Dock = DockStyle.Top;
            row.Controls.Add(line); row.Controls.Add(lbl);
            _slotRow[s.Id] = row; stackItems.Add(row);
        }
        NK.Stack(slots, stackItems.ToArray());
        var cL = new CardPanel("File đính kèm") { Dock = DockStyle.Fill };
        cL.RightBar.Controls.Add(NK.Button("Import mẫu chỉ tiêu", OpenImport, tip: "Import bộ chỉ tiêu của khách vào thư viện mẫu — không cần kết nối database của khách"));
        cL.RightBar.Controls.Add(NK.Button("SQL lấy bộ chỉ tiêu", SqlDialog, tip: "Câu lệnh SQL lấy ĐẦY ĐỦ bộ chỉ tiêu khai báo của khách"));
        cL.RightBar.Controls.Add(NK.Button("Xoá hết", () => { foreach (var i in _slot.Values) i.Text = ""; SendAll(); }, tip: "Bỏ chọn hết"));
        cL.Body.Controls.Add(slots); cL.Body.Dock = DockStyle.Fill;
        var left = new Panel { Dock = DockStyle.Left, Width = NativeSkin.S(470), Padding = new Padding(6) }; left.Controls.Add(cL);

        // ---- phải: kết quả ----
        _filter.Width = NativeSkin.S(220); _filter.TextChanged += (_, _) => Render();
        foreach (var c in new[] { _fErr, _fWarn, _fInfo }) c.CheckedChanged += (_, _) => Render();
        var tools = new BarFlow { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 2, 4, 4) };
        tools.Controls.AddRange(new Control[]
        {
            _filter, _fErr, _fWarn, _fInfo,
            NK.Button("Lưu làm mẫu của khách", SaveTpl, tip: "Khai báo LCTT gián tiếp đang chọn đã chạy đúng (70B = 0)? Lưu làm mẫu của khách"),
            NK.Button("Copy", () => { if (_last.ValueKind == JsonValueKind.Object) { Post(new { action = "copy", text = P(_last, "text") }); Toast("Đã copy báo cáo."); } }),
            NK.Button("Lưu .txt", () => { if (_last.ValueKind == JsonValueKind.Object) Post(new { action = "save", text = P(_last, "text") }); }),
        });
        var cR = new CardPanel("Kết quả") { Dock = DockStyle.Fill };
        cR.RightBar.Controls.Add(_resInfo);
        var rBody = new Panel { Dock = DockStyle.Fill }; rBody.Controls.Add(_result); rBody.Controls.Add(tools);
        cR.Body.Controls.Add(rBody); cR.Body.Dock = DockStyle.Fill;
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) }; right.Controls.Add(cR);
        Controls.Add(right); Controls.Add(left); Controls.Add(head);
        _result.Nodes.Add("Chọn các file rồi bấm “Kiểm tra”. LCTT cần: khai báo chỉ tiêu LCTT + báo cáo LCTT + bảng cân đối phát sinh + bảng kê chứng từ. CĐKT cần: khai báo chỉ tiêu CĐKT + báo cáo CĐKT + bảng cân đối phát sinh.");
        ApplyVisibility();
    }

    private void Toast(string t) { _status.Text = t; }

    private void CommitSlot(string id) { _slot[id].Text = _slot[id].Text.Trim().Trim('"'); SendAll(); }

    private void SendAll()
    {
        var paths = _slot.Where(k => k.Value.Text.Trim().Length > 0).ToDictionary(k => k.Key, k => k.Value.Text.Trim().Trim('"'));
        Post(new { action = "paths", paths, checks = new { indirect = _cInd.Checked, direct = _cDir.Checked, balance = _cBal.Checked } });
    }

    private void ApplyVisibility()
    {
        var anyCf = _cInd.Checked || _cDir.Checked; var any = anyCf || _cBal.Checked;
        var chk = new Dictionary<string, bool> { ["indirect"] = _cInd.Checked, ["direct"] = _cDir.Checked, ["balance"] = _cBal.Checked };
        bool Vis((string Grp, string Need) t) => t.Grp.Length > 0 ? chk[t.Grp] : t.Need == "cf" ? anyCf : any;
        foreach (var c in _slotRow.Values) c.Visible = Vis(((string, string))c.Tag!);
        foreach (var c in _grpLabel.Values) c.Visible = Vis(((string, string))c.Tag!);
    }

    private void SaveTpl()
    {
        if (_slot["cfCfg"].Text.Trim().Length == 0) { Toast("Chọn hoặc dán khai báo chỉ tiêu LCTT gián tiếp trước."); return; }
        var name = SimplePromptForm.Show(FindForm()!, "Lưu làm mẫu của khách", "Tên khách hàng / database cho mẫu này (khai báo đã chạy đúng, 70B = 0):", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        SendAll();
        Post(new { action = "saveTpl", customer = name.Trim() });
    }

    // ---- kết quả ----
    private static string Fmt(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDouble()).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "";

    private bool SevOn(string s) => s == "error" ? _fErr.Checked : s == "warn" ? _fWarn.Checked : _fInfo.Checked;

    private void Render()
    {
        _result.BeginUpdate(); _result.Nodes.Clear();
        var r = _last;
        if (r.ValueKind != JsonValueKind.Object) { _result.EndUpdate(); return; }
        var q = _filter.Text.Trim().ToLowerInvariant();
        foreach (var m in PA(r, "messages")) _result.Nodes.Add(new TreeNode("ℹ " + (m.GetString() ?? "")) { ForeColor = AppColors.Warning });
        var ov = PA(r, "overview").ToList();
        if (ov.Count > 0)
        {
            var on = new TreeNode("Tổng quan") { NodeFont = new Font(ThemeManager.BaseFont, FontStyle.Bold) };
            foreach (var o in ov) on.Nodes.Add(new TreeNode($"{P(o, "label")}:   {P(o, "value")}") { ForeColor = SevColor(P(o, "severity")) });
            _result.Nodes.Add(on); on.Expand();
        }
        var shown = 0; var total = 0;
        foreach (var s in PA(r, "sections"))
        {
            var items = PA(s, "items").ToList(); total += items.Count;
            var vis = items.Where(i => SevOn(P(i, "severity")) && (q.Length == 0 || (P(i, "title") + " " + P(i, "detail")).ToLowerInvariant().Contains(q))).ToList();
            if (vis.Count == 0) continue;
            shown += vis.Count;
            var tot = Fmt(s, "total");
            var sn = new TreeNode($"{P(s, "title")}  ({vis.Count}){(tot.Length > 0 ? "      " + tot : "")}") { NodeFont = new Font(ThemeManager.BaseFont, FontStyle.Bold), ToolTipText = P(s, "note") };
            if (P(s, "note").Length > 0) sn.Nodes.Add(new TreeNode(P(s, "note")) { ForeColor = AppColors.TextMuted });
            foreach (var i in vis)
            {
                var sev = P(i, "severity"); var amt = Fmt(i, "amount");
                var n = new TreeNode($"{(sev == "error" ? "✖" : sev == "warn" ? "⚠" : "ℹ")} {P(i, "title")}{(amt.Length > 0 ? "      " + amt : "")}") { ForeColor = SevColor(sev), ToolTipText = P(i, "detail") };
                if (P(i, "detail").Length > 0) n.Nodes.Add(new TreeNode(P(i, "detail")) { ForeColor = AppColors.TextMuted });
                sn.Nodes.Add(n);
            }
            _result.Nodes.Add(sn); sn.Expand();
        }
        if (shown == 0 && PA(r, "sections").Any()) _result.Nodes.Add("Không có mục nào khớp bộ lọc.");
        if (!PA(r, "sections").Any() && !PA(r, "messages").Any()) _result.Nodes.Add("Không phát hiện lệch.");
        _result.EndUpdate();
        _resInfo.Text = total + " mục";
    }

    private static Color SevColor(string s) => s == "error" ? AppColors.Danger : s == "warn" ? AppColors.Warning : AppColors.Text;

    // ---- hộp thoại phụ ----
    private void PasteDialog(string slot)
    {
        var def = Slots.First(s => s.Id == slot);
        using var f = new ThemedForm { Text = (slot == "dmtk" ? "Dán danh mục tài khoản — " : "Dán bộ chỉ tiêu khai báo — ") + def.Label, Width = NativeSkin.S(860), Height = NativeSkin.S(640), StartPosition = FormStartPosition.CenterParent, ShowIcon = false };
        var hint = NK.Muted("Chạy câu select * from v20gltc1 / v20gltc6 / v20gltc5 where form = '…' trên database của khách (nút “SQL lấy bộ chỉ tiêu” có sẵn câu lệnh), chọn cả bảng kết quả (kèm dòng tiêu đề cột) rồi Copy và dán vào đây. Nhận kết quả ngăn bằng Tab (copy từ lưới / Excel), dấu | (sqlcmd) hoặc dấu phẩy / chấm phẩy.", true);
        hint.Dock = DockStyle.Top; hint.Padding = new Padding(12, 10, 12, 4);
        var text = NK.Memo(14, "form<TAB>stt<TAB>ma_so<TAB>chi_tieu<TAB>cach_tinh<TAB>tk<TAB>tk_du<TAB>..."); text.Dock = DockStyle.Fill;
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) }; host.Controls.Add(text);
        var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
        foot.Controls.Add(NK.Button("Dùng bộ chỉ tiêu này", () =>
        {
            var t = text.Text; if (t.Trim().Length == 0) { Toast("Chưa dán gì."); return; }
            var first = t.Split('\n')[0];
            var ok = slot == "dmtk" ? System.Text.RegularExpressions.Regex.IsMatch(first, @"(^|[\t|;,\s])(tk|Tài khoản|Số hiệu)([\t|;,\s]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                                    : System.Text.RegularExpressions.Regex.IsMatch(first, @"(^|[\t|;,\s])(ma_so|Mã số)([\t|;,\s]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!ok) { Toast(slot == "dmtk" ? "Dòng đầu phải là tiêu đề cột (có cột tk / Tài khoản). Dán cả dòng tiêu đề." : "Dòng đầu phải là tiêu đề cột (có cột ma_so). Dán cả dòng tiêu đề của kết quả select."); return; }
            Post(new { action = "pasteCfg", slot, text = t }); f.DialogResult = DialogResult.OK;
        }, primary: true));
        foot.Controls.Add(NK.Button("Huỷ", () => f.DialogResult = DialogResult.Cancel));
        f.Controls.Add(host); f.Controls.Add(hint); f.Controls.Add(foot);
        f.ShowDialog(FindForm());
    }

    private void SqlDialog()
    {
        using var f = new ThemedForm { Text = "Xuất ĐẦY ĐỦ bộ chỉ tiêu khai báo của khách hàng", Width = NativeSkin.S(900), Height = NativeSkin.S(680), StartPosition = FormStartPosition.CenterParent, ShowIcon = false };
        var hint = NK.Muted("File Excel báo cáo chỉ có số liệu nên chỉ đoán được gần đúng. Để kiểm tra chính xác (TK nguồn, TK đối ứng, các cờ công nợ / không âm / đầu-cuối kỳ…), chạy các câu lệnh dưới trên database của khách (tab SQL của Bcode, chỉ select), xuất kết quả ra Excel (giữ nguyên tên cột) rồi chọn file đó ở ô “Khai báo chỉ tiêu …”. Một file có thể chứa nhiều mẫu (cột form) — chương trình tự lấy mẫu có nhiều dòng nhất.", true);
        hint.Dock = DockStyle.Top; hint.Padding = new Padding(12, 10, 12, 4);
        var text = NK.Memo(12, "", mono: true); text.ReadOnly = true; text.Text = SqlText; text.Dock = DockStyle.Fill;
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) }; host.Controls.Add(text);
        var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
        foot.Controls.Add(NK.Button("Đóng", () => f.DialogResult = DialogResult.Cancel));
        foot.Controls.Add(NK.Button("Copy", () => { Post(new { action = "copy", text = SqlText }); Toast("Đã copy các câu lệnh SQL."); }, primary: true));
        f.Controls.Add(host); f.Controls.Add(hint); f.Controls.Add(foot);
        f.ShowDialog(FindForm());
    }

    private void OpenImport()
    {
        _imp = new ImportDialog(Post, Toast);
        Post(new { action = "tplList" });
        _imp.FormClosed += (_, _) => _imp = null;
        _imp.Show(FindForm());
    }

    private void OpenStd()
    {
        if (_std is { IsDisposed: false }) { _std.Activate(); return; }
        _std = new StdDialog(Post, Toast);
        _std.FormClosed += (_, _) => _std = null;
        Post(new { action = "stdList" });
        _std.Show(FindForm());
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "cfsDiag.init":
            {
                var d = Arg(a, 0);
                _loading = true;
                var paths = PO(d, "paths");
                foreach (var kv in _slot) kv.Value.Text = P(paths, kv.Key);
                var c = PO(d, "checks"); if (c.ValueKind == JsonValueKind.Object) { _cInd.Checked = !PBF(c, "indirect"); _cDir.Checked = !PBF(c, "direct"); _cBal.Checked = !PBF(c, "balance"); }
                _loading = false; ApplyVisibility();
                break;
            }
            case "cfsDiag.onPicked": if (_slot.TryGetValue(Str(a, 0), out var i)) i.Text = Str(a, 1); break;
            case "cfsDiag.onResult": _last = a.Count > 0 ? a[0].Clone() : default; Render(); break;
            case "cfsDiag.onError": Busy(false); Toast(Str(a, 0)); break;
            case "cfsDiag.busy": Busy(Bool(a, 0)); break;
            case "cfsDiag.onTplList": _imp?.SetList(a.Count > 0 ? a[0] : default); break;
            case "cfsDiag.onFastForms": _std?.SetFast(a.Count > 0 ? a[0] : default); break;
            case "cfsDiag.onStd": _std?.SetStd(a.Count > 0 ? a[0] : default, Str(a, 2)); if (Str(a, 1).Length > 0) Toast(Str(a, 1)); break;
        }
    }

    private static bool PBF(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.False;
    private void Busy(bool on) { _busy = on; _bRun.Enabled = !on; Cursor = on ? Cursors.AppStarting : Cursors.Default; if (on) Toast("Đang kiểm tra..."); }

    // ====================== Mẫu chuẩn ======================
    private sealed class StdDialog : ThemedForm
    {
        private static readonly Dictionary<string, string> StatName = new() { ["std"] = "chuẩn", ["carried"] = "suy từ TT200", ["guide"] = "thông lệ", ["new"] = "mới TT99", ["confirm"] = "CHƯA xác nhận" };
        private readonly Action<object> _post; private readonly Action<string> _toast;
        private readonly DropDownButton _sel = new() { Width = NativeSkin.S(420) };
        private readonly RoundedInput _flt = NK.Input("Lọc theo mã / tên / TK…");
        private readonly CheckBox _noSrc = NK.Check("Chưa có TK nguồn");
        private readonly Label _info = NK.Muted("");
        private readonly DataGridView _grid;
        private readonly RoundedInput _meta = NK.Memo(8, "", mono: false);
        private List<JsonElement> _stds = new(); private List<JsonElement> _fast = new();
        private string _cur = "";
        private bool _loading;

        public StdDialog(Action<object> post, Action<string> toast)
        {
            _post = post; _toast = toast;
            Text = "Mẫu chuẩn"; Width = NativeSkin.S(1280); Height = NativeSkin.S(800); StartPosition = FormStartPosition.CenterParent; ShowIcon = false;
            _flt.Width = NativeSkin.S(220); _meta.ReadOnly = true; _meta.Dock = DockStyle.Bottom; _meta.Height = NativeSkin.S(150);
            var head = new BarFlow { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 6, 8, 4) };
            head.Controls.AddRange(new Control[]
            {
                _sel, _flt, _info, _noSrc,
                NK.Button("＋ Chỉ tiêu", () => EditLine(null), tip: "Thêm một chỉ tiêu còn thiếu vào mẫu đang xem"),
                NK.Button("Tạo mẫu mới…", NewStd, tip: "Tạo mẫu mới (vd thông tư mới) từ mẫu đang xem"),
                NK.Button("Dùng mẫu này", () => _post(new { action = "stdActive", id = _cur }), tip: "Dùng mẫu này làm chuẩn cho các lần Kiểm tra sau"),
                NK.Button("Mở thư mục", () => _post(new { action = "stdOpenDir" })),
                NK.Button("Khôi phục gốc", () => { if (NK.Confirm(this, "Bỏ bản mẫu chuẩn của bạn (mọi ghi nhận) và quay về mẫu có sẵn?", "Mẫu chuẩn")) _post(new { action = "stdReset", id = _cur }); }),
                NK.Button("Đóng", Close, primary: true),
            });
            _sel.SelectionChanged += i => { if (_loading || i < 0 || i >= _stds.Count) return; _cur = P(_stds[i], "id"); Render(); };
            _flt.TextChanged += (_, _) => Render(); _noSrc.CheckedChanged += (_, _) => Render();
            _grid = NK.Grid(("code", "Mã", 70), ("name", "Chỉ tiêu", 280), ("formula", "Công thức tổng", 170), ("src", "TK nguồn (chuẩn)", 240), ("fast", "Fast khai báo", 180), ("st", "Trạng thái", 120), ("note", "Ghi chú", 0));
            _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _view.Count) EditLine(_view[e.RowIndex]); };
            _grid.Dock = DockStyle.Fill;
            Controls.Add(_grid); Controls.Add(_meta); Controls.Add(head);
        }

        private static string P(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Null ? "" : v.ToString()) : "";
        private static bool PB(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
        private static IEnumerable<JsonElement> PA(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

        public void SetFast(JsonElement list) { _fast = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(x => x.Clone()).ToList() : new(); Render(); }

        public void SetStd(JsonElement list, string selectId)
        {
            _stds = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(x => x.Clone()).ToList() : new();
            if (selectId.Length > 0) _cur = selectId;
            Render();
        }

        private List<JsonElement> _view = new();

        private void Render()
        {
            if (_stds.Count == 0) { _meta.Text = "Chưa đọc được mẫu chuẩn (Templates\\Cfs\\standard)."; return; }
            var s = _stds.FirstOrDefault(x => P(x, "id") == _cur); if (s.ValueKind != JsonValueKind.Object) s = _stds[0];
            _cur = P(s, "id");
            _loading = true;
            _sel.SetItems(_stds.Select(x => (PB(x, "active") ? "● " : "") + P(x, "title") + " — " + P(x, "circular")).ToList(), Math.Max(0, _stds.FindIndex(x => P(x, "id") == _cur)));
            _loading = false;
            var q = _flt.Text.Trim().ToLowerInvariant();
            var lines = PA(s, "lines").ToList();
            _view = lines.Where(l => (q.Length == 0 || (P(l, "code") + " " + P(l, "name") + " " + P(l, "sources") + " " + P(l, "note")).ToLowerInvariant().Contains(q)) && (!_noSrc.Checked || PB(l, "noSource"))).ToList();
            _info.Text = $"{_view.Count}/{lines.Count} chỉ tiêu" + (PB(s, "userCopy") ? " · BẢN CỦA BẠN" : "") + (PB(s, "active") ? " · ĐANG DÙNG ĐỂ KIỂM TRA" : "");
            _grid.Rows.Clear();
            foreach (var l in _view)
            {
                var i = _grid.Rows.Add(P(l, "code"), P(l, "name") + (PB(l, "negative") ? " (số âm)" : ""), P(l, "formula"), P(l, "sources"), P(l, "fast"), StatName.GetValueOrDefault(P(l, "status"), P(l, "status")), P(l, "note"));
                if (P(l, "status") == "confirm") _grid.Rows[i].Cells["st"].Style.ForeColor = AppColors.Warning;
                if (P(l, "level") is "0" or "1") _grid.Rows[i].DefaultCellStyle.Font = new Font(ThemeManager.BaseFont, FontStyle.Bold);
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{P(s, "form")} · {P(s, "circular")} — {P(s, "status")}");
            sb.AppendLine("Nguồn đã học: " + string.Join(" · ", PA(s, "sources").Select(x => P(x, "title") + (P(x, "url").Length > 0 ? " <" + P(x, "url") + ">" : ""))));
            if (P(s, "fastForm").Length > 0)
            {
                var issues = PA(s, "fastIssues").Select(x => x.GetString() ?? "").ToList();
                sb.AppendLine($"So với cách Fast khai báo ({P(s, "fastForm")}): " + (issues.Count > 0 ? $"bản Fast release lệch mẫu chuẩn ở {issues.Count} chỗ — khách dùng nguyên bản Fast sẽ bị lệch theo:" : "công thức tổng của bản Fast khớp mẫu chuẩn."));
                foreach (var x in issues) sb.AppendLine("  • " + x);
            }
            var kf = _fast.Where(x => P(x, "kind") == P(s, "kind")).ToList();
            if (kf.Count > 0) { sb.AppendLine($"Mẫu Fast đã học cho loại này ({kf.Count}):"); foreach (var x in kf) sb.AppendLine($"  {P(x, "form")} · {P(x, "circular")} · {P(x, "title")} ({P(x, "lines")} dòng khai báo)"); }
            var diffs = PA(s, "diffs").ToList();
            if (diffs.Count > 0) { sb.AppendLine("Thay đổi so với TT200:"); foreach (var d in diffs) sb.AppendLine($"  {P(d, "change")}: {(P(d, "from").Length > 0 ? P(d, "from") : "—")} → {(P(d, "to").Length > 0 ? P(d, "to") : "—")} · {P(d, "text")}"); }
            var learned = PA(s, "learned").Reverse().ToList();
            if (learned.Count > 0) { sb.AppendLine("Nhật ký đã học / ghi nhận:"); foreach (var d in learned) sb.AppendLine($"  {P(d, "date")} — {P(d, "what")}{(P(d, "source").Length > 0 ? " [" + P(d, "source") + "]" : "")}"); }
            _meta.Text = sb.ToString().Replace("\n", "\r\n");
        }

        private void NewStd()
        {
            var s = _stds.FirstOrDefault(x => P(x, "id") == _cur);
            using var f = new ThemedForm { Text = "Tạo mẫu chuẩn mới", Width = NativeSkin.S(560), Height = NativeSkin.S(330), StartPosition = FormStartPosition.CenterParent, ShowIcon = false, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var id = NK.Input("vd bs-tt-moi-2027"); var title = NK.Input(""); var circ = NK.Input("vd TT xx/2027/TT-BTC");
            title.Text = s.ValueKind == JsonValueKind.Object ? P(s, "title") + " (thông tư mới)" : "";
            var grid = NK.FormGrid(220, ("Mã mẫu (chữ thường, số, gạch ngang)", id), ("Tên mẫu", title), ("Thông tư / văn bản", circ)); grid.Dock = DockStyle.Top; grid.Padding = new Padding(12, 10, 12, 0);
            var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
            foot.Controls.Add(NK.Button("Tạo từ mẫu đang xem", () => { if (id.Text.Trim().Length == 0) { f.Text = "Nhập mã mẫu mới."; return; } f.DialogResult = DialogResult.OK; }, primary: true));
            foot.Controls.Add(NK.Button("Huỷ", () => f.DialogResult = DialogResult.Cancel));
            f.Controls.Add(grid); f.Controls.Add(foot);
            if (f.ShowDialog(this) == DialogResult.OK) _post(new { action = "stdClone", from = _cur, id = id.Text.Trim(), title = title.Text, circular = circ.Text });
        }

        private void EditLine(JsonElement? l)
        {
            var isNew = l is null; var line = l ?? default;
            var raw = PO(line, "raw");
            using var f = new ThemedForm { Text = isNew ? "Thêm chỉ tiêu vào mẫu " + _cur : $"Khai báo lại chỉ tiêu {P(line, "code")} — {P(line, "name")}", Width = NativeSkin.S(880), Height = NativeSkin.S(760), StartPosition = FormStartPosition.CenterParent, ShowIcon = false };
            var code = NK.Input(""); var name = NK.Input(""); var level = new DropDownButton { Width = NativeSkin.S(220) }; var formula = NK.Input("vd 111 + 112 (trống nếu lấy từ TK)"); var after = NK.Input("vd 126");
            var sources = NK.Memo(4, "N|131,331|ngan|dư Nợ chi tiết theo đối tượng", mono: true); var status = new DropDownButton { Width = NativeSkin.S(260) }; var flow = new DropDownButton { Width = NativeSkin.S(200) }; var pair = NK.Input(""); var neg = NK.Check("Hiện số âm (*)");
            var note = NK.Memo(2, ""); var src = NK.Input("vd Điều … TT99, hoặc URL");
            level.SetItems(new[] { "0 — tổng lớn", "1 — nhóm", "2 — chỉ tiêu", "3 — chi tiết" }, isNew ? 2 : Math.Clamp(int.TryParse(P(line, "level"), out var lv) ? lv : 2, 0, 3));
            var stKeys = new[] { "std", "carried", "guide", "new", "confirm" }; status.SetItems(new[] { "std — khớp biểu mẫu", "carried — suy từ cũ", "guide — thông lệ", "new — mới", "confirm — chưa xác nhận" }, isNew ? 0 : Math.Max(0, Array.IndexOf(stKeys, P(line, "status"))));
            var flKeys = new[] { "", "thu", "chi" }; flow.SetItems(new[] { "—", "thu (dương)", "chi (âm)" }, isNew ? 0 : Math.Max(0, Array.IndexOf(flKeys, P(line, "flow"))));
            code.Text = isNew ? "" : P(line, "code"); code.ReadOnly = !isNew; name.Text = isNew ? "" : P(line, "name");
            formula.Text = string.Join(" + ", PA(raw, "formula").Select(x => x.GetString() ?? ""));
            sources.Text = string.Join("\r\n", PA(raw, "sources").Select(x => string.Join("|", P(x, "side"), string.Join(",", PA(x, "acc").Select(y => y.GetString())), P(x, "term"), P(x, "note"))));
            pair.Text = isNew ? "" : P(line, "pair"); neg.Checked = !isNew && PB(line, "negative"); note.Text = isNew ? "" : P(line, "note");
            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 8, 12, 8) };
            body.Controls.Add(NK.FormGrid(250, ("Mã số", code), ("Tên chỉ tiêu", name), ("Cấp", level), ("Công thức tổng — các mã con (cách nhau dấu +)", formula), ("Chèn sau mã (trống = giữ chỗ cũ / thêm cuối)", after),
                ("TK nguồn — mỗi dòng: bên|TK,TK|kỳ hạn|ghi chú  (N = dư Nợ, C = dư Có)", sources), ("Trạng thái", status), ("LCTT: thu / chi", flow), ("Cùng TK khác kỳ hạn (mã)", pair), ("", neg), ("Ghi chú / cách xử lý theo thông tư", note), ("Nguồn đã học (điều khoản, link)", src)));
            var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
            foot.Controls.Add(NK.Button("Lưu vào mẫu", () => { if (code.Text.Trim().Length == 0) { f.Text = "Nhập mã số chỉ tiêu."; return; } f.DialogResult = DialogResult.OK; }, primary: true));
            foot.Controls.Add(NK.Button("Huỷ", () => f.DialogResult = DialogResult.Cancel));
            if (!isNew) foot.Controls.Add(NK.Button("Xoá chỉ tiêu", () => { if (NK.Confirm(f, $"Xoá chỉ tiêu {P(line, "code")} khỏi mẫu (và khỏi công thức các chỉ tiêu cha)?", "Mẫu chuẩn")) { f.Tag = "delete"; f.DialogResult = DialogResult.OK; } }));
            f.Controls.Add(body); f.Controls.Add(foot);
            if (f.ShowDialog(this) != DialogResult.OK) return;
            if ((string?)f.Tag == "delete") { _post(new { action = "stdDeleteLine", id = _cur, code = P(line, "code") }); return; }
            _post(new { action = "stdSaveLine", id = _cur, line = new { code = code.Text.Trim(), name = name.Text, level = level.SelectedIndex, formula = formula.Text, sources = sources.Text.Replace("\r", ""), status = stKeys[Math.Max(0, status.SelectedIndex)], flow = flKeys[Math.Max(0, flow.SelectedIndex)], pair = pair.Text, negative = neg.Checked, note = note.Text, after = after.Text, source = src.Text } });
        }
    }

    // ====================== Import mẫu chỉ tiêu ======================
    private sealed class ImportDialog : ThemedForm
    {
        private static readonly string[] Kinds = { "cf-indirect", "cf-direct", "bs" };
        private static readonly Dictionary<string, string> KindName = new() { ["cf-indirect"] = "LCTT gián tiếp", ["cf-direct"] = "LCTT trực tiếp", ["bs"] = "CĐKT" };
        private readonly Action<object> _post; private readonly Action<string> _toast;
        private readonly RoundedInput _cus = NK.Input("Tên khách hàng / database"), _text = NK.Memo(9, "Dán kết quả select (kèm dòng tiêu đề cột) — hoặc để trống rồi bấm “Import từ file…”", mono: true);
        private readonly DropDownButton _kind = new() { Width = NativeSkin.S(200) };
        private readonly CheckBox _ver = NK.Check("Mẫu này đã chạy đúng");
        private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
        private List<string> _ids = new();

        public ImportDialog(Action<object> post, Action<string> toast)
        {
            _post = post; _toast = toast;
            Text = "Import mẫu chỉ tiêu của khách"; Width = NativeSkin.S(900); Height = NativeSkin.S(780); StartPosition = FormStartPosition.CenterParent; ShowIcon = false;
            _kind.SetItems(new[] { "LCTT gián tiếp", "LCTT trực tiếp", "Cân đối kế toán" }, 0);
            var hint = NK.Muted("Khi không kết nối được database của khách: nhờ khách chạy select * from v20gltc6 / v20gltc5 / v20gltc1 where form = '…' (nút “SQL lấy bộ chỉ tiêu”), gửi file Excel hoặc copy kết quả về rồi import vào thư viện. Lần sau khai báo giống mẫu này, tool đối chiếu và đề xuất sửa theo nó. Đánh dấu “đã chạy đúng” nếu mẫu cho báo cáo khớp (70B = 0).", true);
            hint.Dock = DockStyle.Top; hint.Padding = new Padding(12, 10, 12, 4);
            _cus.Width = NativeSkin.S(340);
            var row = new BarFlow { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 2, 12, 2), WrapContents = true }; row.Controls.AddRange(new Control[] { _cus, _kind, _ver });
            _text.Dock = DockStyle.Top; _text.Height = NativeSkin.S(220);
            var btns = new BarFlow { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 4, 12, 4) };
            btns.Controls.Add(NK.Button("Đóng", Close)); btns.Controls.Add(NK.Button("Import từ file…", () => Send(false))); btns.Controls.Add(NK.Button("Import đoạn đã dán", () => Send(true), primary: true));
            var del = NK.Button("Xoá mẫu đang chọn", () => { var i = _list.SelectedIndex; if (i >= 0 && i < _ids.Count && NK.Confirm(this, "Xoá mẫu này khỏi thư viện?", "Thư viện mẫu")) _post(new { action = "delTpl", id = _ids[i] }); });
            var libHead = NK.Muted("Mẫu trong thư viện"); libHead.Padding = new Padding(12, 6, 12, 2);
            var lib = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 8) };
            del.Dock = DockStyle.Bottom; lib.Controls.Add(_list); lib.Controls.Add(del);
            NK.Stack(this, hint, row, _text, btns, libHead);
            Controls.Add(lib); lib.BringToFront();
        }

        private void Send(bool withText)
        {
            var customer = _cus.Text.Trim(); if (customer.Length == 0) { _toast("Nhập tên khách hàng / database."); return; }
            var text = withText ? _text.Text : "";
            if (withText && text.Trim().Length == 0) { _toast("Chưa dán gì — hoặc bấm “Import từ file…”."); return; }
            if (withText && !System.Text.RegularExpressions.Regex.IsMatch(text.Split('\n')[0], @"(^|[\t|;,\s])(ma_so|Mã số)([\t|;,\s]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { _toast("Dòng đầu phải là tiêu đề cột (có cột ma_so / Mã số)."); return; }
            _post(new { action = "importTpl", customer, kind = Kinds[Math.Max(0, _kind.SelectedIndex)], verified = _ver.Checked, text });
        }

        public void SetList(JsonElement list)
        {
            _list.Items.Clear(); _ids = new();
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) { _list.Items.Add("Chưa có mẫu nào."); return; }
            foreach (var r in list.EnumerateArray())
            {
                string S(string n) => r.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
                _ids.Add(S("id"));
                _list.Items.Add($"{KindName.GetValueOrDefault(S("kind"), S("kind"))} · {S("customer")} · {S("lines")} dòng" + (r.TryGetProperty("verified", out var vf) && vf.ValueKind == JsonValueKind.True ? " · ✔ đã chạy đúng" : ""));
            }
        }
    }
}
