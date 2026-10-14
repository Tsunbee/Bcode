using System.Net;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Mail kế hoạch tuần" ở chế độ App/WinForms (thay weeklymail.html): danh sách mẫu mail theo dự án (To/Cc/Bcc, tiêu đề, nội dung Text hoặc HTML, chữ ký HTML), biến tự thay
/// ({du_an}, {tuan_truoc_tu/den}, {tuan_sau_tu/den}, {ngay}), ngày tuần vừa rồi / tuần tới tự điền, Xem trước, Gửi (luôn hỏi xác nhận người nhận) qua cùng cấu hình SMTP với Check Mail.
/// Khác bản Web: chữ ký HTML nhập bằng nút "Dán chữ ký từ clipboard" (lấy HTML Outlook/Gmail giữ nguyên định dạng) hoặc sửa mã HTML; xem trước hiện chữ thuần (chữ ký HTML không dựng hình).
/// </summary>
public sealed class NativeWeeklyMailControl : UserControl
{
    private const string SampleSubject = "V/v: [FAST - {du_an}] KẾ HOẠCH TRIỂN KHAI TUẦN DỰ ÁN";
    private const string SampleBody = "Gửi Anh/Chị Đội dự án {du_an},\n\nEm xin gửi báo cáo kế hoạch tuần dự án\nI. THỰC HIỆN TRONG TUẦN: {tuan_truoc_tu} - {tuan_truoc_den}\n- \nII. KẾ HOẠCH TUẦN TIẾP THEO: {tuan_sau_tu} - {tuan_sau_den}\n- \nEm cảm ơn Anh/Chị.\nTrân trọng.\n---\n";

    private readonly Func<IReadOnlyList<string>> _projects;
    private readonly Func<string?> _currentProject;
    private List<WeeklyMailTemplate> _templates = new();
    private WeeklyMailTemplate? _cur;
    private CancellationTokenSource? _cts;
    private bool _loading;
    private readonly System.Windows.Forms.Timer _smtpTimer = new() { Interval = 600 };

    private readonly DropDownButton _filter = new();
    private readonly ThemedListBox _list = new() { Height = NativeSkin.S(200) };
    private readonly RoundedInput _name = NK.Input("Kế hoạch tuần — Tây Hồ"), _project = NK.Input("Chọn / gõ tên dự án"), _to = NK.Input("nhieu-nguoi@domain.com; phan-cach-bang-dau-cham-phay"),
        _cc = NK.Input(), _bcc = NK.Input(), _subject = NK.Input(), _body = NK.Memo(14, "", mono: false), _sigSrc = NK.Memo(6, "Mã HTML chữ ký...");
    private readonly RadioButton _fmtText = new() { Text = "Text", AutoSize = true, Checked = true }, _fmtHtml = new() { Text = "HTML", AutoSize = true };
    private readonly DateTimePicker _d1 = new() { Format = DateTimePickerFormat.Short, Width = NativeSkin.S(130) }, _d2 = new() { Format = DateTimePickerFormat.Short, Width = NativeSkin.S(130) },
        _d3 = new() { Format = DateTimePickerFormat.Short, Width = NativeSkin.S(130) }, _d4 = new() { Format = DateTimePickerFormat.Short, Width = NativeSkin.S(130) };
    private readonly Label _pvHead = NK.Muted("", wrap: true), _sigInfo = NK.Muted(""), _smtpSum = NK.Muted("");
    private readonly RichTextBox _preview = new() { Dock = DockStyle.Top, Height = NativeSkin.S(220), ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Top, Height = NativeSkin.S(150), ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false };
    private readonly RoundedInput _host = NK.Input("mail.domain.com"), _senderEmail = NK.Input("ten@domain.com"), _alias = NK.Input(), _account = NK.Input("(để trống nếu không cần đăng nhập)"), _password = NK.Input();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 587, Width = NativeSkin.S(90) };
    private readonly CheckBox _ssl = NK.Check("SSL"), _tls = NK.Check("TLS"), _ignoreCert = NK.Check("Bỏ qua lỗi chứng chỉ"), _showPw = NK.Check("Hiện");
    private readonly CardPanel _cSmtp;
    private readonly PillButton _bSend, _bCancel;
    private readonly Label _state = new() { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(12, 8, 0, 0) };
    private List<string> _filterValues = new();
    private string _signature = "";

    public NativeWeeklyMailControl(Func<IReadOnlyList<string>> projects, Func<string?> currentProject)
    {
        _projects = projects; _currentProject = currentProject;
        Dock = DockStyle.Fill;

        // ---- cột trái: danh sách mẫu + SMTP ----
        var cList = new CardPanel("Mẫu mail");
        cList.RightBar.Controls.Add(NK.Button("＋ Mới", NewTemplate, tip: "Tạo mẫu mới (có sẵn nội dung ví dụ kế hoạch tuần)"));
        _filter.Dock = DockStyle.Top; _filter.Height = NativeSkin.RowHeight(_filter, 28);
        _filter.SelectionChanged += _ => RenderList();
        _list.Dock = DockStyle.Top;
        _list.SelectedIndexChanged += (_, _) => { if (!_loading && _list.SelectedIndex >= 0) { var rows = VisibleTemplates(); if (_list.SelectedIndex < rows.Count) Select(rows[_list.SelectedIndex]); } };
        var listBtns = NK.Row(NK.Button("Nhân bản", Duplicate), NK.Button("Xoá mẫu", DeleteTemplate)); listBtns.Dock = DockStyle.Top;
        cList.Body.Controls.Add(listBtns); cList.Body.Controls.Add(_list); cList.Body.Controls.Add(NK.Spacer(4)); cList.Body.Controls.Add(_filter);

        _cSmtp = new CardPanel("▸ Máy chủ gửi mail (SMTP)");
        _cSmtp.TitleLabel.Cursor = Cursors.Hand;
        _password.Inner.UseSystemPasswordChar = true; _showPw.CheckedChanged += (_, _) => _password.Inner.UseSystemPasswordChar = !_showPw.Checked;
        var portRow = NK.Row(_port, _ssl, _tls, _ignoreCert);
        var pwRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        pwRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _password.Dock = DockStyle.Fill; pwRow.Controls.Add(_password, 0, 0); pwRow.Controls.Add(_showPw, 1, 0);
        var smtpBody = new Panel { Dock = DockStyle.Top, AutoSize = true, Visible = false };
        var smtpGrid = NK.FormGrid(90, ("Host", _host), ("Port", portRow), ("Email gửi", _senderEmail), ("Tên hiển thị", _alias), ("Tài khoản", _account), ("Mật khẩu", pwRow));
        var smtpHint = NK.Wrap("Dùng chung với tab Check Mail (cùng file cấu hình). Mật khẩu không được lưu — nhập lại mỗi lần mở tab.", _cSmtp.Body, 30); smtpHint.Dock = DockStyle.Top;
        smtpBody.Controls.Add(smtpGrid); smtpBody.Controls.Add(smtpHint);
        _cSmtp.Body.Controls.Add(smtpBody); _cSmtp.Body.Controls.Add(_smtpSum);
        _smtpSum.Dock = DockStyle.Top;
        void ToggleSmtp() { smtpBody.Visible = !smtpBody.Visible; _cSmtp.TitleLabel.Text = (smtpBody.Visible ? "▾ " : "▸ ") + "Máy chủ gửi mail (SMTP)"; }
        _cSmtp.TitleLabel.Click += (_, _) => ToggleSmtp();
        _cSmtp.OpenToggle = ToggleSmtp;

        var leftCol = new FlowLayoutPanel { Dock = DockStyle.Left, Width = NativeSkin.S(340), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10, 10, 4, 10) };
        leftCol.Controls.Add(cList); leftCol.Controls.Add(_cSmtp);
        leftCol.SizeChanged += (_, _) => { cList.Width = _cSmtp.Width = Math.Max(200, leftCol.ClientSize.Width - 20 - SystemInformation.VerticalScrollBarWidth); };

        // ---- cột phải: soạn / tuần / xem trước / log ----
        var cCompose = new CardPanel("Soạn mail");
        var sigRow = NK.Row(NK.Button("Dán chữ ký từ clipboard", PasteSignature, tip: "Dán chữ ký đã copy từ Outlook / Gmail / trang web — giữ nguyên định dạng, màu, logo, link"), NK.Button("Xoá chữ ký", () => { SetSignature(""); Refresh(); }));
        var sigCode = NK.Check("Sửa mã HTML chữ ký");
        _sigSrc.Visible = false; _sigSrc.Dock = DockStyle.Top; _sigSrc.Height = NativeSkin.S(120);
        sigCode.CheckedChanged += (_, _) => { _sigSrc.Visible = sigCode.Checked; if (sigCode.Checked) _sigSrc.Text = _signature; };
        _sigSrc.TextChanged += (_, _) => { if (_sigSrc.Visible && !_loading) { _signature = Clean(_sigSrc.Text); Refresh(); } };
        var vars = NK.Wrap("Biến tự thay khi xem trước / gửi: {du_an} {tuan_truoc_tu} {tuan_truoc_den} {tuan_sau_tu} {tuan_sau_den} {ngay}", cCompose.Body, 30); vars.Dock = DockStyle.Top;
        var fmtRow = NK.Row(_fmtText, _fmtHtml); fmtRow.Dock = DockStyle.Top;
        _body.Dock = DockStyle.Top;
        var sigPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        sigPanel.Controls.Add(NK.Muted("Chữ ký (HTML) — có chữ ký thì thư gửi dạng HTML")); sigPanel.Controls.Add(sigRow); sigPanel.Controls.Add(_sigInfo); sigPanel.Controls.Add(sigCode);
        cCompose.Body.Controls.Add(sigPanel); cCompose.Body.Controls.Add(_sigSrc); cCompose.Body.Controls.Add(vars); cCompose.Body.Controls.Add(fmtRow); cCompose.Body.Controls.Add(_body);
        cCompose.Body.Controls.Add(NK.FormGrid(110, ("Tên mẫu", _name), ("Dự án", _project), ("To", _to), ("Cc", _cc), ("Bcc", _bcc), ("Tiêu đề", _subject)));
        // thứ tự Dock Top: control thêm sau nằm trên cùng → đảo lại để Tên mẫu… ở đầu, rồi nội dung, rồi chữ ký
        cCompose.Body.Controls.SetChildIndex(sigPanel, 0);

        var cWeek = new CardPanel("Tuần báo cáo");
        cWeek.RightBar.Controls.Add(NK.Button("Tự điền theo hôm nay", AutoDates, tip: "Tính lại theo hôm nay: tuần vừa rồi = Thứ 2→Chủ nhật trước đó; tuần tới = Thứ 2→Thứ 6 của tuần sau"));
        cWeek.Body.Controls.Add(NK.Row(NK.Text("Thực hiện: từ"), _d1, NK.Text("đến"), _d2, NK.Text("   Kế hoạch tuần tiếp theo: từ"), _d3, NK.Text("đến"), _d4));

        var cPreview = new CardPanel("Xem trước");
        cPreview.RightBar.Controls.Add(NK.Button("Copy nội dung", () => NK.CopyText(Fill(_subject.Text) + "\r\n\r\n" + Compose().Body)));
        _pvHead.Dock = DockStyle.Top; _preview.Dock = DockStyle.Top;
        cPreview.Body.Controls.Add(_preview); cPreview.Body.Controls.Add(_pvHead);

        var cLog = new CardPanel("Kết quả");
        cLog.RightBar.Controls.Add(NK.Button("Xoá log", () => { _log.Clear(); SetState(""); }));
        _log.Font = ThemeManager.MonoFont; cLog.Body.Controls.Add(_log);

        var rightCol = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(4, 10, 10, 10) };
        var cards = new Control[] { cCompose, cWeek, cPreview, cLog };
        foreach (var c in cards) rightCol.Controls.Add(c);
        NK.FitWidth(rightCol, cards);

        _bSend = NK.Button("✉ Gửi mail", () => _ = SendAsync(), primary: true, tip: "Gửi mail (sẽ hỏi xác nhận người nhận trước)");
        _bCancel = NK.Button("Huỷ", () => { try { _cts?.Cancel(); } catch { } }); _bCancel.Visible = false;
        var bar = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 7, 10, 7) };
        bar.Controls.AddRange(new Control[] { _bSend, NK.Button("💾 Lưu mẫu", () => { Persist(); RenderFilter(); RenderList(); }, tip: "Lưu mẫu đang soạn"), _bCancel, _state });

        Controls.Add(rightCol); Controls.Add(leftCol); Controls.Add(bar);

        foreach (var r in new[] { _to, _cc, _bcc, _subject, _body, _project }) r.TextChanged += (_, _) => Refresh();
        foreach (var d in new[] { _d1, _d2, _d3, _d4 }) d.ValueChanged += (_, _) => Refresh();
        _fmtText.CheckedChanged += (_, _) => Refresh(); _fmtHtml.CheckedChanged += (_, _) => Refresh();
        foreach (var r in new[] { _name, _project }) r.TextChanged += (_, _) => { if (!_loading) { Commit(); RenderList(); } };
        foreach (var r in new[] { _host, _senderEmail, _alias, _account }) r.TextChanged += (_, _) => SmtpChanged();
        foreach (var c in new[] { _ssl, _tls, _ignoreCert }) c.CheckedChanged += (_, _) => SmtpChanged();
        _port.ValueChanged += (_, _) => SmtpChanged();
        _smtpTimer.Tick += (_, _) => { _smtpTimer.Stop(); WeeklyMailStore.SaveSmtp(ReadSmtp(false)); };

        HandleCreated += (_, _) => BeginInvoke(new Action(Init));
        Disposed += (_, _) => { try { _cts?.Cancel(); } catch { } _smtpTimer.Dispose(); };
    }

    // =====================================================================  khởi tạo / ngày

    private void Init()
    {
        _loading = true;
        _templates = WeeklyMailStore.LoadTemplates();
        var s = WeeklyMailStore.LoadSmtp() ?? new MailConfig();
        _host.Text = s.Host; _port.Value = s.Port > 0 ? Math.Clamp(s.Port, 1, 65535) : 587; _ssl.Checked = s.Ssl; _tls.Checked = s.Tls; _ignoreCert.Checked = s.IgnoreCert;
        _senderEmail.Text = s.SenderEmail; _alias.Text = s.Alias; _account.Text = s.Account;
        SmtpSummary();
        RenderFilter();
        var cur = _currentProject() ?? "";
        if (cur.Length > 0 && _templates.Any(t => t.Project == cur)) _filter.Select(Math.Max(0, _filterValues.IndexOf(cur) + 1), raise: false);
        _loading = false;
        var first = VisibleTemplates().FirstOrDefault();
        _cur = first; WriteForm(first); RenderList(); AutoDates();
    }

    private void AutoDates()
    {
        var t = DateTime.Today;
        var nextMon = t.AddDays(((int)DayOfWeek.Monday - (int)t.DayOfWeek + 7) % 7);   // thứ Hai gần nhất tính từ hôm nay (hôm nay là thứ Hai thì lấy luôn hôm nay)
        _loading = true;
        _d1.Value = nextMon.AddDays(-7); _d2.Value = nextMon.AddDays(-1); _d3.Value = nextMon; _d4.Value = nextMon.AddDays(4);
        _loading = false;
        Refresh();
    }

    private string Fill(string text)
    {
        var t = DateTime.Now;
        string Vn(DateTime d) => d.ToString("dd/MM/yyyy");
        return Regex.Replace(text ?? "", @"\{(du_an|tuan_truoc_tu|tuan_truoc_den|tuan_sau_tu|tuan_sau_den|ngay)\}", m => m.Groups[1].Value switch
        {
            "du_an" => _project.Text.Trim(), "tuan_truoc_tu" => Vn(_d1.Value), "tuan_truoc_den" => Vn(_d2.Value), "tuan_sau_tu" => Vn(_d3.Value), "tuan_sau_den" => Vn(_d4.Value), _ => Vn(t),
        });
    }

    // =====================================================================  danh sách mẫu

    private List<WeeklyMailTemplate> VisibleTemplates()
    {
        var f = _filter.SelectedIndex <= 0 || _filter.SelectedIndex - 1 >= _filterValues.Count ? "*" : _filterValues[_filter.SelectedIndex - 1];
        return _templates.Where(t => f == "*" || t.Project == f).ToList();
    }

    private void RenderFilter()
    {
        var keep = _filter.SelectedIndex > 0 && _filter.SelectedIndex - 1 < _filterValues.Count ? _filterValues[_filter.SelectedIndex - 1] : "*";
        _filterValues = _templates.Select(t => t.Project).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
        var items = new List<string> { "Tất cả dự án" }; items.AddRange(_filterValues);
        _filter.SetItems(items, keep == "*" ? 0 : Math.Max(0, _filterValues.IndexOf(keep) + 1));
    }

    private void RenderList()
    {
        var rows = VisibleTemplates();
        var wasLoading = _loading; _loading = true;
        _list.BeginUpdate(); _list.Items.Clear();
        foreach (var t in rows) _list.Items.Add((string.IsNullOrEmpty(t.Name) ? "(chưa đặt tên)" : t.Name) + "   ·   " + (string.IsNullOrEmpty(t.Project) ? "(chưa chọn dự án)" : t.Project));
        _list.EndUpdate();
        var i = _cur is null ? -1 : rows.IndexOf(_cur);
        if (i >= 0) _list.SelectedIndex = i;
        _loading = wasLoading;
        _list.Height = Math.Clamp(rows.Count, 3, 10) * _list.ItemHeight + 6;
    }

    private void Select(WeeklyMailTemplate t) { Commit(); _cur = t; WriteForm(t); RenderList(); }

    private void NewTemplate()
    {
        Commit();
        var cur = _currentProject() ?? "";
        var f = _filter.SelectedIndex > 0 && _filter.SelectedIndex - 1 < _filterValues.Count ? _filterValues[_filter.SelectedIndex - 1] : cur;
        var t = new WeeklyMailTemplate { Name = "Kế hoạch tuần", Subject = SampleSubject, Body = SampleBody, Project = f };
        _templates.Add(t); _cur = t; WriteForm(t); RenderFilter(); RenderList(); _name.Inner.Focus();
    }

    private void Duplicate()
    {
        if (_cur is null) return;
        Commit();
        var c = _cur;
        var t = new WeeklyMailTemplate { Name = c.Name + " (bản sao)", Project = c.Project, To = c.To, Cc = c.Cc, Bcc = c.Bcc, Subject = c.Subject, Body = c.Body, Html = c.Html, Signature = c.Signature };
        _templates.Add(t); _cur = t; WriteForm(t); RenderFilter(); RenderList();
    }

    private void DeleteTemplate()
    {
        if (_cur is null) return;
        _templates.Remove(_cur); _cur = null; WriteForm(null); RenderFilter(); RenderList();
        WeeklyMailStore.SaveTemplates(_templates); SetState("Đã xoá mẫu", ok: true);
    }

    private void WriteForm(WeeklyMailTemplate? t)
    {
        _loading = true;
        _name.Text = t?.Name ?? ""; _project.Text = t?.Project ?? ""; _to.Text = t?.To ?? ""; _cc.Text = t?.Cc ?? ""; _bcc.Text = t?.Bcc ?? ""; _subject.Text = t?.Subject ?? ""; _body.Text = t?.Body ?? "";
        _fmtHtml.Checked = t?.Html == true; _fmtText.Checked = t?.Html != true;
        SetSignature(t?.Signature ?? "");
        _loading = false;
        Refresh();
    }

    private void Commit()   // đưa nội dung form vào mẫu đang chọn (chưa ghi đĩa)
    {
        if (_cur is null) return;
        _cur.Name = _name.Text; _cur.Project = _project.Text; _cur.To = _to.Text; _cur.Cc = _cc.Text; _cur.Bcc = _bcc.Text; _cur.Subject = _subject.Text; _cur.Body = _body.Text;
        _cur.Html = _fmtHtml.Checked; _cur.Signature = _signature;
    }

    private void Persist()
    {
        if (_cur is null) { _cur = new WeeklyMailTemplate(); _templates.Add(_cur); }   // đang soạn mà chưa có mẫu → tạo mẫu từ nội dung form
        Commit();
        WeeklyMailStore.SaveTemplates(_templates);
        SetState("Đã lưu mẫu", ok: true);
    }

    // =====================================================================  chữ ký HTML

    /// <summary>Làm sạch HTML chữ ký: bỏ script / iframe / object / embed / link / meta, thuộc tính on*, link javascript: (mã lạ không được chạy trong mail).</summary>
    private static string Clean(string html)
    {
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        html = Regex.Replace(html, @"<\s*(script|iframe|object|embed|style\s+onload)\b[^>]*>.*?<\s*/\s*\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<\s*(link|meta|script|iframe|object|embed)\b[^>]*/?>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"(href|src|xlink:href)\s*=\s*(""\s*javascript:[^""]*""|'\s*javascript:[^']*')", "", RegexOptions.IgnoreCase);
        return Regex.IsMatch(html, @"^(\s|<br\s*/?>)*$", RegexOptions.IgnoreCase) ? "" : html;
    }

    private void SetSignature(string html)
    {
        _signature = html ?? "";
        if (_sigSrc.Visible) _sigSrc.Text = _signature;
        _sigInfo.Text = _signature.Length > 0 ? $"Đã có chữ ký HTML ({_signature.Length:N0} ký tự)." : "Chưa có chữ ký.";
    }

    private void PasteSignature()
    {
        string? frag = null;
        try
        {
            if (Clipboard.ContainsText(TextDataFormat.Html))
            {
                // định dạng "HTML Format" của Windows có phần đầu (Version/StartFragment...) — lấy đúng đoạn nội dung giữa <!--StartFragment--> và <!--EndFragment-->
                var raw = Clipboard.GetText(TextDataFormat.Html);
                var s = raw.IndexOf("<!--StartFragment-->", StringComparison.Ordinal); var e = raw.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
                frag = s >= 0 && e > s ? raw[(s + 20)..e] : raw[(raw.IndexOf('<') is var i && i >= 0 ? i : 0)..];
            }
            else if (Clipboard.ContainsText()) frag = WebUtility.HtmlEncode(Clipboard.GetText()).Replace("\r\n", "<br>").Replace("\n", "<br>");
        }
        catch { /* clipboard bận */ }
        if (string.IsNullOrWhiteSpace(frag)) { SetState("Clipboard chưa có chữ ký (copy từ Outlook / Gmail trước).", error: true); return; }
        SetSignature(Clean(frag)); Refresh();
    }

    /// <summary>Nội dung cuối cùng: có chữ ký → luôn gửi HTML (Text được escape + xuống dòng thành &lt;br&gt;); không có → như đã chọn Text/HTML.</summary>
    private (bool Html, string Body) Compose()
    {
        var body = Fill(_body.Text);
        if (_signature.Length == 0 && !_fmtHtml.Checked) return (false, body);
        var main = _fmtHtml.Checked ? body : WebUtility.HtmlEncode(body).Replace("\r\n", "\n").Replace("\n", "<br>\n");
        return (true, main + (_signature.Length > 0 ? "<br>\n" + _signature : ""));
    }

    private void Refresh()
    {
        if (_loading) return;
        _pvHead.Text = "To: " + _to.Text + (_cc.Text.Length > 0 ? "   Cc: " + _cc.Text : "") + (_bcc.Text.Length > 0 ? "   Bcc: " + _bcc.Text : "") + "\r\nTiêu đề: " + Fill(_subject.Text);
        var c = Compose();
        _preview.Text = c.Html && _signature.Length > 0 ? Fill(_body.Text) + "\r\n\r\n[ + chữ ký HTML ]" : c.Body;
        _preview.BackColor = AppColors.Input; _preview.ForeColor = AppColors.Text;
    }

    // =====================================================================  SMTP + gửi

    private MailConfig ReadSmtp(bool withPassword) => new()
    {
        Host = _host.Text.Trim(), Port = (int)_port.Value, Ssl = _ssl.Checked, Tls = _tls.Checked, IgnoreCert = _ignoreCert.Checked, TimeoutSeconds = 30,
        SenderEmail = _senderEmail.Text.Trim(), Alias = _alias.Text.Trim(), Account = _account.Text.Trim(), Password = withPassword ? _password.Text : "",
    };

    private void SmtpSummary() => _smtpSum.Text = _host.Text.Trim().Length > 0 ? _host.Text.Trim() + ":" + _port.Value : "chưa khai báo";

    private void SmtpChanged() { if (_loading) return; SmtpSummary(); _smtpTimer.Stop(); _smtpTimer.Start(); }

    private void SetState(string text, bool error = false, bool ok = false)
    {
        _state.Text = text;
        _state.ForeColor = error ? AppColors.Danger : ok ? AppColors.Success : AppColors.TextMuted;
    }

    private void AddLog(string text, string? kind)
    {
        if (IsDisposed) return;
        var color = kind switch { "err" => AppColors.Danger, "ok" => AppColors.Success, "dim" => AppColors.TextMuted, _ => AppColors.Text };
        _log.SelectionStart = _log.TextLength; _log.SelectionLength = 0; _log.SelectionColor = color;
        _log.AppendText((kind == "dim" ? "" : DateTime.Now.ToString("HH:mm:ss") + "  ") + text + Environment.NewLine);
        _log.ScrollToCaret();
    }

    private async Task SendAsync()
    {
        if (_cts is not null) return;
        var c = ReadSmtp(true);
        if (c.Host.Length == 0) { AddLog("Chưa khai báo máy chủ gửi mail (mở mục “Máy chủ gửi mail”).", "err"); _cSmtp.OpenToggle?.Invoke(); return; }
        if (c.SenderEmail.Length == 0) { AddLog("Chưa nhập Email gửi.", "err"); return; }
        if (_to.Text.Trim().Length == 0) { AddLog("Chưa nhập người nhận (To).", "err"); _to.Inner.Focus(); return; }
        Persist();
        var comp = Compose();
        c.To = _to.Text.Trim(); c.Cc = _cc.Text.Trim(); c.Bcc = _bcc.Text.Trim(); c.Subject = Fill(_subject.Text); c.Body = comp.Body; c.Html = comp.Html;
        // Gửi mail ra ngoài là thao tác không thu hồi được → luôn hỏi lại, hiện rõ người nhận.
        var summary = $"Người gửi: {c.SenderEmail}\nTo: {c.To}" + (string.IsNullOrWhiteSpace(c.Cc) ? "" : $"\nCc: {c.Cc}") + (string.IsNullOrWhiteSpace(c.Bcc) ? "" : $"\nBcc: {c.Bcc}") + $"\nTiêu đề: {c.Subject}";
        if (MessageBox.Show(FindForm(), "Gửi mail này?\n\n" + summary, "Bcode — Mail kế hoạch tuần", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { AddLog("Đã huỷ — chưa gửi.", "dim"); return; }
        WeeklyMailStore.SaveSmtp(c);
        _cts = new CancellationTokenSource();
        _bSend.Enabled = false; _bCancel.Visible = true; SetState("Đang gửi...");
        var ok = false;
        try { ok = await MailTestService.RunAsync(c, sendMail: true, (line, kind) => { try { if (!IsDisposed) BeginInvoke(new Action(() => AddLog(line, kind))); } catch { } }, _cts.Token); }
        catch (Exception ex) { AddLog(ex.Message, "err"); }
        finally
        {
            _cts?.Dispose(); _cts = null;
            _bSend.Enabled = true; _bCancel.Visible = false;
            SetState(ok ? "✔ Đã gửi" : "✘ Không thành công", error: !ok, ok: ok);
        }
    }
}
