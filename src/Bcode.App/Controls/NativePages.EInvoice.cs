using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Setup eInvoice (setupeinvoice.html): khai báo thông tin hoá đơn điện tử → Step 1 xem script / Step 2 chạy vào Proxy + App DB / Update Key qua API.</summary>
internal sealed class SetupEInvoicePage : NativePage
{
    private static readonly string[] Keys = { "project", "product", "unit", "clientCode", "proxyCode", "portalKey", "portalLink", "username", "password", "middleLink", "dbProxy" };

    private readonly Dictionary<string, RoundedInput> _f = new();
    private readonly CheckBox _hsm = NK.Check("Ký HSM");
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
        _f["dbProxy"].PlaceholderText = "Tên database Proxy (cần cho Step 2)";
        var show = NK.Button("Hiện", () => { }); show.Dock = DockStyle.Right;
        show.Click += (_, _) => { var on = !_f["password"].Inner.UseSystemPasswordChar; _f["password"].Inner.UseSystemPasswordChar = on; show.Text = on ? "Hiện" : "Ẩn"; };
        var pw = new Panel { Height = NativeSkin.RowHeight(this, 28) }; _f["password"].Dock = DockStyle.Fill; pw.Controls.Add(_f["password"]); pw.Controls.Add(show);

        var c1 = new CardPanel("Kết nối");
        c1.Body.Controls.Add(NK.FormGrid(190, ("Link trung gian (Middle Link)", _f["middleLink"]), ("CSDL (Database Proxy)", _f["dbProxy"])));
        var c2 = new CardPanel("Dự án / Doanh nghiệp");
        c2.Body.Controls.Add(NK.FormGrid(190, ("Mã dự án (Project)", _f["project"]), ("Sản phẩm (Product)", _f["product"]), ("Đơn vị (Unit)", _f["unit"]), ("Mã doanh nghiệp (ClientCode)", _f["clientCode"]), ("Nhóm dịch vụ (ProxyCode)", _f["proxyCode"])));
        var c3 = new CardPanel("Portal");
        c3.Body.Controls.Add(NK.FormGrid(190, ("Portal Key nhận từ mail", _f["portalKey"]), ("Link Portal", _f["portalLink"]), ("Tài khoản portal (Username)", _f["username"]), ("Mật khẩu (không được lưu)", pw), ("", _hsm)));
        c1.Dock = DockStyle.Top; c2.Dock = DockStyle.Top; c3.Dock = DockStyle.Top;
        var cols = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 8, 10, 0) };
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = new Panel { Dock = DockStyle.Fill, AutoSize = true }; NK.Stack(left, c1, c2);
        var right = new Panel { Dock = DockStyle.Fill, AutoSize = true }; NK.Stack(right, c3);
        cols.Controls.Add(left, 0, 0); cols.Controls.Add(right, 1, 0);

        _confirm.Controls.Add(_confirmText);
        _bGo = NK.Button("Chạy Step 2", () => { _confirm.Visible = false; Post(new { action = "step2", inputs = Inputs() }); }); _bGo.ForeOverride = AppColors.Danger;
        var confirmBtn = NK.Row(_bGo, NK.Button("Huỷ", () => _confirm.Visible = false)); confirmBtn.Dock = DockStyle.Top;
        _confirm.Controls.Add(confirmBtn);

        _bStep1 = NK.Button("Step 1: View UpdateKey Script", () => { _confirm.Visible = false; Post(new { action = "step1", inputs = Inputs() }); }, tip: "Dựng script UpdateKey để xem / copy, chưa chạy gì");
        _bStep2 = NK.Button("Step 2: Exec Insert in Server (SQL)", Step2, tip: "Chạy script: Proxy setting vào database Proxy, App setting vào App Data");
        _bUpdate = NK.Button("Cập nhật / Update Key", () => { _confirm.Visible = false; Post(new { action = "update", inputs = Inputs() }); }, primary: true, tip: "Lấy pass_hddt từ Portal rồi gọi API UpdateKey ở Link trung gian (không chạy SQL trực tiếp)");
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
        return d;
    }

    private void Step2()
    {
        if (_f["dbProxy"].Text.Trim().Length == 0)
        {
            Clear(); Line("Error: chưa khai \"CSDL / Database Proxy\" — cần biết chạy phần Proxy setting vào database nào.", "err");
            _state.Text = "Thiếu thông tin"; _state.ForeColor = AppColors.Danger; _f["dbProxy"].Inner.Focus(); return;
        }
        _confirmText.Text = $"Chạy Step 2 sẽ GHI vào database: XOÁ rồi INSERT lại các bảng cấu hình hoá đơn điện tử (tkhddt, edmsp, ekey, edmkh) trong database Proxy \"{_f["dbProxy"].Text.Trim()}\", và (dmstthddt, tkhddt, options) trong App Data của workspace đang chọn.";
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
