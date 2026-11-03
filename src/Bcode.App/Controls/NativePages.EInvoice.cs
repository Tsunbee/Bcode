using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Setup eInvoice (setupeinvoice.html): khai báo thông tin hoá đơn điện tử → Step 1 xem script / Step 2 chạy vào Proxy + App DB / Update Key qua API.</summary>
internal sealed class SetupEInvoicePage : NativePage
{
    private static readonly string[] Keys = { "project", "product", "unit", "clientCode", "proxyCode", "portalKey", "portalLink", "username", "password", "middleLink", "dbProxy" };

    private readonly Dictionary<string, RoundedInput> _f = new();
    private readonly CheckBox _hsm = NK.Check("Ký HSM");
    private readonly CheckBox _own = NK.Check("Nhóm dịch vụ riêng (tắt = nhóm chung của Fast 005500, không cần Proxy setting)");
    private string _ownCode = "";
    private string _pending = "";
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both };
    private readonly Label _state = NK.Muted("");
    private readonly Panel _confirm = new() { Dock = DockStyle.Top, AutoSize = true, Visible = false, Padding = new Padding(12, 8, 12, 8) };
    private readonly Label _confirmText = new() { AutoSize = true, BackColor = Color.Transparent, ForeColor = AppColors.Warning, Dock = DockStyle.Top };
    private readonly PillButton _bStep1, _bStep2, _bUpdate, _bGo, _bCopy;
    private string _plain = "";
    private bool _busy;

    public SetupEInvoicePage()
    {
        foreach (var k in Keys) _f[k] = NK.Input("", mono: k == "portalKey");
        _f["portalKey"] = NK.Memo(6, "", mono: true);
        _f["password"].Inner.UseSystemPasswordChar = true;
        _f["dbProxy"].PlaceholderText = "Tên database Proxy (chỉ cần khi nhóm dịch vụ riêng)";
        _f["unit"].PlaceholderText = "vd 05";
        _own.CheckedChanged += (_, _) => SyncProxy();
        var show = NK.Button("Hiện", () => { }); show.Dock = DockStyle.Right;
        show.Click += (_, _) => { var on = !_f["password"].Inner.UseSystemPasswordChar; _f["password"].Inner.UseSystemPasswordChar = on; show.Text = on ? "Hiện" : "Ẩn"; };
        var pw = new Panel { Height = NativeSkin.RowHeight(this, 28) }; _f["password"].Dock = DockStyle.Fill; pw.Controls.Add(_f["password"]); pw.Controls.Add(show);

        var c1 = new CardPanel("Kết nối");
        c1.Body.Controls.Add(NK.FormGrid(190, ("Link trung gian (API UpdateProxy)", _f["middleLink"]), ("CSDL (Database Proxy)", _f["dbProxy"])));
        var c2 = new CardPanel("Dự án / Doanh nghiệp");
        c2.Body.Controls.Add(NK.FormGrid(190, ("Mã dự án (Project)", _f["project"]), ("Sản phẩm (Product)", _f["product"]), ("Đơn vị (ma_kn — nhiều đơn vị: khai lần lượt)", _f["unit"]), ("Mã doanh nghiệp (ClientCode)", _f["clientCode"]), ("Nhóm dịch vụ (ProxyCode)", _f["proxyCode"]), ("", _own)));
        var c3 = new CardPanel("Portal");
        c3.Body.Controls.Add(NK.FormGrid(190, ("Portal Key nhận từ mail", _f["portalKey"]), ("Link Portal", _f["portalLink"]), ("Tài khoản portal (Username)", _f["username"]), ("Mật khẩu (không được lưu)", pw), ("", _hsm)));
        c1.Dock = DockStyle.Top; c2.Dock = DockStyle.Top; c3.Dock = DockStyle.Top;
        var cols = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 8, 10, 0) };
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = new Panel { Dock = DockStyle.Fill, AutoSize = true }; NK.Stack(left, c1, c2);
        var right = new Panel { Dock = DockStyle.Fill, AutoSize = true }; NK.Stack(right, c3);
        cols.Controls.Add(left, 0, 0); cols.Controls.Add(right, 1, 0);

        _confirm.Controls.Add(_confirmText);
        _bGo = NK.Button("Chạy Step 2", () => { _confirm.Visible = false; if (_pending.Length > 0) Post(new { action = _pending, inputs = Inputs() }); }); _bGo.ForeOverride = AppColors.Danger;
        var confirmBtn = NK.Row(_bGo, NK.Button("Huỷ", () => _confirm.Visible = false)); confirmBtn.Dock = DockStyle.Top;
        _confirm.Controls.Add(confirmBtn);

        _bStep1 = NK.Button("Step 1: View UpdateKey Script", () => { _confirm.Visible = false; Post(new { action = "step1", inputs = Inputs() }); }, tip: "Dựng script để xem / copy (App setting; thêm Proxy setting khi nhóm dịch vụ riêng), chưa chạy gì");
        _bStep2 = NK.Button("Step 2: Exec Insert in Server (SQL)", () => AskConfirm("step2"), tip: "Chạy script: App setting vào App Data; Proxy setting vào database Proxy (khi nhóm dịch vụ riêng)");
        _bUpdate = NK.Button("Cập nhật / Update Key", () => AskConfirm("update"), primary: true, tip: "Tính pass_hddt từ mật khẩu portal, gọi API UpdateProxy ở Link trung gian, thành công thì chạy luôn script App (+ Proxy khi nhóm dịch vụ riêng) vào database");
        var acts = new BarFlow { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 6, 10, 4) };
        acts.Controls.AddRange(new Control[] { _bStep1, _bStep2, _bUpdate });

        _bCopy = NK.Button("Copy", () => Post(new { action = "copy", text = _plain })); _bCopy.Enabled = false;
        var resHead = new BarFlow { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 4, 10, 2) };
        resHead.Controls.AddRange(new Control[] { NK.Muted("Kết quả / Result"), _state, _bCopy, NK.Button("Xoá", Clear) });
        _log.Font = ThemeManager.MonoFont;
        var res = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 8) };
        res.Controls.Add(_log); res.Controls.Add(resHead);
        ThemeManager.Subscribe(this, () => { if (!_log.IsDisposed) { _log.BackColor = AppColors.Input; _log.ForeColor = AppColors.Text; } });
        _log.BackColor = AppColors.Input; _log.ForeColor = AppColors.Text;

        var scroll = new Panel { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true };
        NK.Stack(scroll, cols, _confirm, acts);
        var main = new Panel { Dock = DockStyle.Fill };
        main.Controls.Add(res); main.Controls.Add(scroll);
        Controls.Add(main);
    }

    private object Inputs()
    {
        var d = new Dictionary<string, object>();
        foreach (var k in Keys) d[k] = _f[k].Text;
        d["hsm"] = _hsm.Checked;
        d["ownProxy"] = _own.Checked;
        return d;
    }

    /// <summary>Như tool VB: không tick "Nhóm dịch vụ riêng" → ProxyCode khoá ở 005500; tick → nhập mã riêng (nhớ lại mã đã gõ).</summary>
    private void SyncProxy()
    {
        var p = _f["proxyCode"];
        if (_own.Checked) { p.Enabled = true; if (p.Text == Services.EInvoiceSetupService.FastProxyCode) p.Text = _ownCode; }
        else { if (p.Enabled && p.Text != Services.EInvoiceSetupService.FastProxyCode) _ownCode = p.Text; p.Enabled = false; p.Text = Services.EInvoiceSetupService.FastProxyCode; }
    }

    /// <summary>Step 2 và Cập nhật / Update Key đều GHI xuống database → xác nhận ngay trong trang; _pending = action gửi khi bấm nút đỏ.</summary>
    private void AskConfirm(string action)
    {
        var own = _own.Checked; var db = _f["dbProxy"].Text.Trim();
        if (own && db.Length == 0)
        {
            Clear(); Line("Error: chưa khai \"CSDL / Database Proxy\" — nhóm dịch vụ riêng cần biết chạy phần Proxy setting vào database nào.", "err");
            _state.Text = "Thiếu thông tin"; _state.ForeColor = AppColors.Danger; _f["dbProxy"].Inner.Focus(); return;
        }
        _pending = action;
        var kn = Services.EInvoiceSetupService.Units(_f["unit"].Text).FirstOrDefault() ?? "?";
        _bGo.Text = action == "update" ? "Cập nhật + chạy script" : "Chạy Step 2";
        _confirmText.Text = (action == "update" ? "Cập nhật / Update Key sẽ gọi API UpdateProxy rồi GHI vào database: " : "Chạy Step 2 sẽ GHI vào database: ")
            + $"tài khoản HĐĐT của đơn vị {kn} (tkhddt, tkhddtdv — đơn vị khác giữ nguyên), dmstthddt (khoá dùng chung), options m_sd_hddt = 1, m_ky_hddt = "
            + (_hsm.Checked ? "0 (Ký HSM)" : "1") + " trong App Data của workspace đang chọn"
            + (own ? $", và tkhddt của đơn vị {kn}, edmsp, ekey, edmkh trong database Proxy \"{db}\"." : ". Nhóm dịch vụ chung của Fast: không chạy Proxy setting.");
        _confirmText.MaximumSize = new Size(Math.Max(300, Width - 60), 0);
        _confirm.Visible = true;
    }

    private void SetBusy(bool b)
    {
        _busy = b; _bStep1.Enabled = _bStep2.Enabled = _bUpdate.Enabled = _bGo.Enabled = !b;
        if (b) { _state.Text = "Đang chạy..."; _state.ForeColor = AppColors.Accent; }
    }

    private void Clear() { _log.Clear(); _plain = ""; _bCopy.Enabled = false; _state.Text = ""; }

    private void Line(string text, string kind)
    {
        var color = kind switch { "err" => AppColors.Danger, "ok" => AppColors.Success, "warn" => AppColors.Warning, _ => AppColors.Text };
        _log.SelectionStart = _log.TextLength; _log.SelectionColor = color; _log.AppendText(text + "\n");
        _plain += (_plain.Length > 0 ? "\n" : "") + text; _bCopy.Enabled = true;
        _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "setupEInvoice.init":
            {
                var o = Arg(a, 0);
                foreach (var k in Keys) _f[k].Text = P(o, k);
                _hsm.Checked = PB(o, "hsm");
                _ownCode = ""; _f["proxyCode"].Enabled = true; _own.Checked = PB(o, "ownProxy"); SyncProxy();
                break;
            }
            case "setupEInvoice.setDbProxy": _f["dbProxy"].Text = Str(a, 0); break;
            case "setupEInvoice.setUnits":
            {
                // Gợi ý ô Đơn vị = dmdvcs của App Data (mã + tên); chọn xong chỉ giữ mã.
                var src = new AutoCompleteStringCollection();
                if (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array)
                    foreach (var u in a[0].EnumerateArray()) src.Add(P(u, "code"));
                var tb = _f["unit"].Inner;
                tb.AutoCompleteCustomSource = src;
                tb.AutoCompleteSource = AutoCompleteSource.CustomSource;
                tb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                break;
            }
            case "setupEInvoice.onBusy": SetBusy(Bool(a, 0)); break;
            case "setupEInvoice.onClear": Clear(); break;
            case "setupEInvoice.onLog":
                Line(Str(a, 0), Str(a, 1));
                if (Str(a, 1) == "err") { _state.Text = "Có lỗi"; _state.ForeColor = AppColors.Danger; }
                break;
            case "setupEInvoice.onScript":
            {
                Clear(); var t = Str(a, 0); _plain = t;
                _log.Text = t; _bCopy.Enabled = true; _state.Text = "Script UpdateKey (chưa chạy)"; _state.ForeColor = AppColors.Success;
                _log.SelectionStart = 0; _log.ScrollToCaret();
                break;
            }
            case "setupEInvoice.onDone": _state.Text = Bool(a, 0) ? "Hoàn tất" : "Có lỗi"; _state.ForeColor = Bool(a, 0) ? AppColors.Success : AppColors.Danger; break;
        }
    }

    public override void FocusPrimary() => _f["middleLink"].Inner.Focus();
}
