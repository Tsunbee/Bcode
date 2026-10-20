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
/// Tab con "Đã gửi &amp; phản hồi" (như weeklymail.html, logic ở <see cref="WeeklyMailHistory"/>): thư đã gửi, Kiểm tra phản hồi (IMAP), xem thư (dạng chữ — "Mở .eml" để xem HTML),
/// Reply / Reply all, Gửi lại; IMAP + "Nhớ mật khẩu" (DPAPI) ở mục Máy chủ mail.
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

    // ---- IMAP + nhớ mật khẩu (weeklymail-imap.json) ----
    private readonly CheckBox _rememberPw = NK.Check("Nhớ mật khẩu (mã hoá DPAPI)"), _imapSsl = NK.Check("SSL (993) / STARTTLS (143)"), _copySent = NK.Check("Chép thư đã gửi lên thư mục Sent của hộp thư");
    private readonly RoundedInput _imapHost = NK.Input("(trống = cùng host SMTP)");
    private readonly NumericUpDown _imapPort = new() { Minimum = 1, Maximum = 65535, Value = 993, Width = NativeSkin.S(90) };
    private readonly System.Windows.Forms.Timer _imapTimer = new() { Interval = 600 };

    // ---- tab con "Đã gửi & phản hồi" ----
    private readonly Panel _composeView = new() { Dock = DockStyle.Fill }, _histView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly SegmentedBar _tabs = new(("compose", "✎ Soạn mail"), ("hist", "📨 Đã gửi & phản hồi"));
    private readonly RoundedInput _hSearch = NK.Input("Tìm theo tiêu đề / người nhận / dự án...");
    private readonly ThemedListBox _hList = new() { Dock = DockStyle.Top }, _thList = new() { Dock = DockStyle.Top };
    private readonly Label _hLast = NK.Muted("", wrap: true), _vHead = NK.Muted("Chưa chọn thư.", wrap: true), _rSigInfo = NK.Muted("");
    private readonly RichTextBox _vText = new() { Dock = DockStyle.Top, Height = NativeSkin.S(320), ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly RichTextBox _rQuote = new() { Dock = DockStyle.Top, Height = NativeSkin.S(140), ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly RichTextBox _log2 = new() { Dock = DockStyle.Top, Height = NativeSkin.S(130), ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false };
    private readonly RoundedInput _rTo = NK.Input(), _rCc = NK.Input(), _rSubject = NK.Input(), _rBody = NK.Memo(8, "Nội dung trả lời (chữ thuần — tự xuống dòng đúng khi gửi)...", mono: false);
    private readonly CheckBox _rSig = NK.Check("Kèm chữ ký của mẫu");
    private readonly CardPanel _cReply = new("Trả lời");
    private readonly PillButton _bCheck, _bReply, _bReplyAll, _bResend, _bEml, _bDel, _rSend;
    private WeeklyMailIndex _idx = new();
    private List<WeeklyMailSentEntry> _hRows = new();
    private List<(string Type, string Id, bool Seen)> _thRows = new();
    private WeeklyMailSentEntry? _curThread;
    private (string Type, string Id)? _curMsg, _replyTarget;
    private WeeklyMailReplyDraft? _draft;

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

        _cSmtp = new CardPanel("▸ Máy chủ mail (SMTP / IMAP)");
        _cSmtp.TitleLabel.Cursor = Cursors.Hand;
        _password.Inner.UseSystemPasswordChar = true; _showPw.CheckedChanged += (_, _) => _password.Inner.UseSystemPasswordChar = !_showPw.Checked;
        var portRow = NK.Row(_port, _ssl, _tls, _ignoreCert);
        var pwRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        pwRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _password.Dock = DockStyle.Fill; pwRow.Controls.Add(_password, 0, 0); pwRow.Controls.Add(_showPw, 1, 0);
        var smtpBody = new Panel { Dock = DockStyle.Top, AutoSize = true, Visible = false };
        _imapSsl.Checked = true; _copySent.Checked = true;
        NK.Tip(_copySent, "SMTP không tự lưu vào Sent nên Outlook / webmail không thấy thư Bcode gửi — tick để chép thư lên thư mục Sent của hộp thư qua IMAP");
        var smtpGrid = NK.FormGrid(90, ("Host", _host), ("Port", portRow), ("Email gửi", _senderEmail), ("Tên hiển thị", _alias), ("Tài khoản", _account), ("Mật khẩu", pwRow), ("", _rememberPw),
            ("Host IMAP", _imapHost), ("Port IMAP", NK.Row(_imapPort, _imapSsl)), ("", _copySent));
        var smtpHint = NK.Wrap("SMTP dùng chung với tab Check Mail (cùng file cấu hình). IMAP (đọc phản hồi, chép thư lên Sent) dùng cùng tài khoản / mật khẩu. Mật khẩu không được lưu — trừ khi tick “Nhớ mật khẩu” (mã hoá theo tài khoản Windows, chỉ máy này đọc được).", _cSmtp.Body, 30); smtpHint.Dock = DockStyle.Top;
        smtpBody.Controls.Add(smtpGrid); smtpBody.Controls.Add(smtpHint);
        _cSmtp.Body.Controls.Add(smtpBody); _cSmtp.Body.Controls.Add(_smtpSum);
        _smtpSum.Dock = DockStyle.Top;
        void ToggleSmtp() { smtpBody.Visible = !smtpBody.Visible; _cSmtp.TitleLabel.Text = (smtpBody.Visible ? "▾ " : "▸ ") + "Máy chủ mail (SMTP / IMAP)"; }
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
        var bSave = NK.Button("💾 Lưu mẫu", () => { Persist(); RenderFilter(); RenderList(); }, tip: "Lưu mẫu đang soạn");
        _bCheck = NK.Button("🔍 Kiểm tra phản hồi", () => _ = CheckRepliesAsync(), primary: true, tip: "Đăng nhập hộp thư (IMAP), tìm ở INBOX các thư trả lời thư đã gửi từ Bcode"); _bCheck.Visible = false;
        bar.Controls.AddRange(new Control[] { _bSend, bSave, _bCheck, _bCancel, _state });

        // ---- tab con Đã gửi & phản hồi ----
        _bReply = NK.Button("↩ Reply", () => StartReply(false), tip: "Soạn thư trả lời người gửi");
        _bReplyAll = NK.Button("↩ Reply all", () => StartReply(true), tip: "Soạn thư trả lời mọi người trong thư");
        _bResend = NK.Button("⟳ Gửi lại", () => _ = ResendAsync(), tip: "Gửi lại thư này (cùng người nhận, tiêu đề, nội dung — sẽ hỏi xác nhận)");
        _bEml = NK.Button("Mở .eml", OpenEml, tip: "Mở file .eml bằng chương trình mặc định (Outlook...) — xem đúng định dạng HTML, ảnh, đính kèm");
        _bDel = NK.Button("Xoá", DeleteMsg, tip: "Xoá khỏi mục Đã gửi của Bcode (không đụng hộp thư)");
        _rSend = NK.Button("✉ Gửi trả lời", () => _ = SendReplyAsync(), primary: true, tip: "Gửi thư trả lời (sẽ hỏi xác nhận người nhận trước)");

        var cSent = new CardPanel("Đã gửi");
        cSent.RightBar.Controls.Add(NK.Button("📁", () => { Directory.CreateDirectory(WeeklyMailHistory.Dir); NK.OpenFolder(WeeklyMailHistory.Dir); }, tip: "Mở thư mục lưu thư đã gửi (.eml)"));
        _hSearch.TextChanged += (_, _) => RenderHist();
        _hList.SelectedIndexChanged += (_, _) => { if (!_loading && _hList.SelectedIndex >= 0 && _hList.SelectedIndex < _hRows.Count) SelectThread(_hRows[_hList.SelectedIndex]); };
        NK.Stack(cSent.Body, _hSearch, NK.Spacer(4), _hList, _hLast);
        var leftHist = new FlowLayoutPanel { Dock = DockStyle.Left, Width = NativeSkin.S(400), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10, 10, 4, 10) };
        leftHist.Controls.Add(cSent);
        leftHist.SizeChanged += (_, _) => { cSent.Width = Math.Max(200, leftHist.ClientSize.Width - 20 - SystemInformation.VerticalScrollBarWidth); };

        var cThread = new CardPanel("Chuỗi thư");
        _thList.SelectedIndexChanged += (_, _) => { if (!_loading && _thList.SelectedIndex >= 0 && _thList.SelectedIndex < _thRows.Count) OpenMsg(_thRows[_thList.SelectedIndex].Type, _thRows[_thList.SelectedIndex].Id); };
        cThread.Body.Controls.Add(_thList);
        var cView = new CardPanel("Nội dung");
        var viewBtns = NK.Row(_bReply, _bReplyAll, _bResend, _bEml, _bDel); viewBtns.Dock = DockStyle.Top;
        NK.Stack(cView.Body, viewBtns, _vHead, NK.Spacer(4), _vText);
        _cReply.RightBar.Controls.Add(NK.Button("✕", () => { _cReply.Visible = false; _replyTarget = null; }, tip: "Bỏ thư trả lời"));
        var sigRow2 = NK.Row(_rSig, _rSigInfo); _rBody.Dock = DockStyle.Top;
        var sendRow = NK.Row(_rSend);
        NK.Stack(_cReply.Body, NK.FormGrid(90, ("To", _rTo), ("Cc", _rCc), ("Tiêu đề", _rSubject)), _rBody, sigRow2, NK.Muted("Trích dẫn thư gốc (gửi dạng HTML như thư gốc):"), _rQuote, sendRow);
        _cReply.Visible = false;
        var cLog2 = new CardPanel("Kết quả");
        cLog2.RightBar.Controls.Add(NK.Button("Xoá log", () => { _log.Clear(); _log2.Clear(); SetState(""); }));
        _log2.Font = ThemeManager.MonoFont; cLog2.Body.Controls.Add(_log2);
        var rightHist = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(4, 10, 10, 10) };
        var histCards = new Control[] { cThread, cView, _cReply, cLog2 };
        foreach (var c in histCards) rightHist.Controls.Add(c);
        NK.FitWidth(rightHist, histCards);
        _histView.Controls.Add(rightHist); _histView.Controls.Add(leftHist);

        _composeView.Controls.Add(rightCol); _composeView.Controls.Add(leftCol);
        var tabBar = new BarFlow { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 7, 10, 7) };
        tabBar.Controls.Add(_tabs);
        _tabs.Changed += v =>
        {
            var h = v == "hist";
            _composeView.Visible = !h; _histView.Visible = h;
            _bSend.Visible = !h; bSave.Visible = !h; _bCheck.Visible = h;
            if (h) LoadHistory();
        };

        Controls.Add(_composeView); Controls.Add(_histView); Controls.Add(tabBar); Controls.Add(bar);

        foreach (var r in new[] { _to, _cc, _bcc, _subject, _body, _project }) r.TextChanged += (_, _) => Refresh();
        foreach (var d in new[] { _d1, _d2, _d3, _d4 }) d.ValueChanged += (_, _) => Refresh();
        _fmtText.CheckedChanged += (_, _) => Refresh(); _fmtHtml.CheckedChanged += (_, _) => Refresh();
        foreach (var r in new[] { _name, _project }) r.TextChanged += (_, _) => { if (!_loading) { Commit(); RenderList(); } };
        foreach (var r in new[] { _host, _senderEmail, _alias, _account }) r.TextChanged += (_, _) => SmtpChanged();
        foreach (var c in new[] { _ssl, _tls, _ignoreCert }) c.CheckedChanged += (_, _) => SmtpChanged();
        _port.ValueChanged += (_, _) => SmtpChanged();
        _smtpTimer.Tick += (_, _) => { _smtpTimer.Stop(); WeeklyMailStore.SaveSmtp(ReadSmtp(false)); };
        _imapHost.TextChanged += (_, _) => ImapChanged(); _password.TextChanged += (_, _) => ImapChanged(); _imapPort.ValueChanged += (_, _) => ImapChanged();
        foreach (var c in new[] { _imapSsl, _copySent, _rememberPw }) c.CheckedChanged += (_, _) => ImapChanged();
        _imapSsl.CheckedChanged += (_, _) => { if (!_loading && _imapPort.Value is 993 or 143) _imapPort.Value = _imapSsl.Checked ? 993 : 143; };
        _imapTimer.Tick += (_, _) => { _imapTimer.Stop(); SaveImapNow(); };

        HandleCreated += (_, _) => BeginInvoke(new Action(Init));
        Disposed += (_, _) => { try { _cts?.Cancel(); } catch { } _smtpTimer.Dispose(); _imapTimer.Dispose(); };
    }

    // =====================================================================  khởi tạo / ngày

    private void Init()
    {
        _loading = true;
        _templates = WeeklyMailStore.LoadTemplates();
        var s = WeeklyMailStore.LoadSmtp() ?? new MailConfig();
        _host.Text = s.Host; _port.Value = s.Port > 0 ? Math.Clamp(s.Port, 1, 65535) : 587; _ssl.Checked = s.Ssl; _tls.Checked = s.Tls; _ignoreCert.Checked = s.IgnoreCert;
        _senderEmail.Text = s.SenderEmail; _alias.Text = s.Alias; _account.Text = s.Account;
        var im = WeeklyMailStore.LoadImap();
        _imapHost.Text = im.Host; _imapPort.Value = im.Port is > 0 and <= 65535 ? im.Port : 993; _imapSsl.Checked = im.Ssl; _copySent.Checked = im.CopyToSent; _rememberPw.Checked = im.RememberPassword;
        if (im.RememberPassword) _password.Text = WeeklyMailStore.LoadRememberedPassword();
        SmtpSummary();
        RenderFilter();
        var cur = _currentProject() ?? "";
        if (cur.Length > 0 && _templates.Any(t => t.Project == cur)) _filter.Select(Math.Max(0, _filterValues.IndexOf(cur) + 1), raise: false);
        _loading = false;
        var first = VisibleTemplates().FirstOrDefault();
        _cur = first; WriteForm(first); RenderList(); AutoDates();
        LoadHistory();
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
        foreach (var log in new[] { _log, _log2 })   // 2 tab con mỗi tab 1 khung kết quả — ghi cả hai
        {
            log.SelectionStart = log.TextLength; log.SelectionLength = 0; log.SelectionColor = color;
            log.AppendText((kind == "dim" ? "" : DateTime.Now.ToString("HH:mm:ss") + "  ") + text + Environment.NewLine);
            log.ScrollToCaret();
        }
    }

    private void LogAsync(string line, string kind) { try { if (!IsDisposed) BeginInvoke(new Action(() => AddLog(line, kind))); } catch { /* đang đóng */ } }

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
        var imap = SaveImapNow();
        var meta = new WeeklyMailSendMeta { Project = _project.Text.Trim(), TemplateId = _cur?.Id ?? "", TemplateName = _name.Text.Trim() };
        _cts = new CancellationTokenSource();
        _bSend.Enabled = false; _bCancel.Visible = true; SetState("Đang gửi...");
        var ok = false;
        try { ok = await WeeklyMailHistory.SendAndSaveAsync(c, meta, imap, null, LogAsync, _cts.Token); }
        catch (Exception ex) { AddLog(ex.Message, "err"); }
        finally
        {
            _cts?.Dispose(); _cts = null;
            _bSend.Enabled = true; _bCancel.Visible = false;
            SetState(ok ? "✔ Đã gửi" : "✘ Không thành công", error: !ok, ok: ok);
            LoadHistory();
        }
    }

    // =====================================================================  IMAP + nhớ mật khẩu

    private WeeklyMailImapSettings ReadImap() => new()
    {
        Host = _imapHost.Text.Trim(), Port = (int)_imapPort.Value, Ssl = _imapSsl.Checked, CopyToSent = _copySent.Checked, RememberPassword = _rememberPw.Checked,
    };

    private void ImapChanged() { if (_loading) return; _imapTimer.Stop(); _imapTimer.Start(); }

    /// <summary>Lưu cấu hình IMAP (kèm mật khẩu mã hoá nếu đang tick "Nhớ mật khẩu"; bỏ tick = xoá mật khẩu đã nhớ).</summary>
    private WeeklyMailImapSettings SaveImapNow()
    {
        var s = ReadImap();
        try { WeeklyMailStore.SaveImap(s, _rememberPw.Checked ? _password.Text : ""); } catch { /* không lưu được thì lần sau nhập lại */ }
        return s;
    }

    // =====================================================================  Đã gửi & phản hồi

    private void LoadHistory()
    {
        try { _idx = WeeklyMailHistory.Load(); } catch { _idx = new(); }
        if (_curThread is not null) _curThread = _idx.Sent.FirstOrDefault(e => e.Id == _curThread.Id);
        if (_curThread is null) { _curMsg = null; ShowMsg(null); }
        RenderHist(); RenderThread();
    }

    private void RenderHist()
    {
        var q = _hSearch.Text.Trim();
        _hRows = _idx.Sent.Where(e => e.ThreadId == e.Id)
            .Where(e => q.Length == 0 || (e.Subject + " " + e.To + " " + e.Cc + " " + e.Project + " " + e.TemplateName).Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(e => e.SentAt).ToList();
        var wasLoading = _loading; _loading = true;
        _hList.BeginUpdate(); _hList.Items.Clear();
        foreach (var e in _hRows)
        {
            var reps = _idx.Replies.Where(r => r.ThreadId == e.Id).ToList();
            var unseen = reps.Count(r => !r.Seen);
            // số phản hồi đứng trước tiêu đề — dòng dài bị cắt "…" ở cuối
            _hList.Items.Add($"{e.SentAt.LocalDateTime:dd/MM HH:mm}   " + (reps.Count > 0 ? $"💬 {reps.Count}" + (unseen > 0 ? $" ({unseen} mới)" : "") + "   " : "")
                + $"{(e.Kind == "resend" ? "⟳ " : "")}{(string.IsNullOrEmpty(e.Subject) ? "(không tiêu đề)" : e.Subject)}" + (string.IsNullOrEmpty(e.Project) ? "" : "   · " + e.Project));
        }
        if (_hRows.Count == 0) _hList.Items.Add(_idx.Sent.Count == 0 ? "Chưa có thư nào — thư gửi từ tab Soạn mail sẽ được lưu ở đây." : "Không có thư khớp.");
        _hList.EndUpdate();
        var i = _curThread is null ? -1 : _hRows.FindIndex(e => e.Id == _curThread.Id);
        _hList.SelectedIndex = i;
        _loading = wasLoading;
        _hList.Height = Math.Clamp(Math.Max(1, _hList.Items.Count), 6, 20) * _hList.ItemHeight + 6;
        var unseenAll = _idx.Replies.Count(r => !r.Seen);
        _hLast.Text = (_idx.LastCheck is { } lc ? $"Kiểm tra phản hồi lần cuối: {lc.LocalDateTime:dd/MM/yyyy HH:mm}" : "Chưa kiểm tra phản hồi lần nào.") + (unseenAll > 0 ? $"  ·  {unseenAll} phản hồi chưa xem." : "");
    }

    private void SelectThread(WeeklyMailSentEntry e)
    {
        _curThread = e; RenderThread();
        var pick = _thRows.FirstOrDefault(x => x.Type == "reply" && !x.Seen);
        if (pick.Id is null && _thRows.Count > 0) pick = _thRows[^1];
        if (pick.Id is not null) OpenMsg(pick.Type, pick.Id); else { _curMsg = null; ShowMsg(null); }
    }

    private void RenderThread()
    {
        var wasLoading = _loading; _loading = true;
        _thList.BeginUpdate(); _thList.Items.Clear(); _thRows.Clear();
        if (_curThread is { } t)
        {
            var rows = _idx.Sent.Where(s => s.ThreadId == t.Id).Select(s => (At: s.SentAt, Type: "sent", s.Id, Seen: true,
                    Text: $"→  {s.SentAt.LocalDateTime:dd/MM HH:mm}   {(s.Kind == "reply" ? "Bcode trả lời" : s.Kind == "resend" ? "Bcode gửi lại" : "Bcode gửi")} · Tới: {s.To}"))
                .Concat(_idx.Replies.Where(r => r.ThreadId == t.Id).Select(r => (At: r.Date, Type: "reply", r.Id, r.Seen,
                    Text: $"←  {r.Date.LocalDateTime:dd/MM HH:mm}   {(r.Seen ? "" : "● MỚI · ")}Phản hồi · {r.From}{(r.ByHeader ? "" : " · đoán theo tiêu đề")}")))
                .OrderBy(x => x.At).ToList();
            foreach (var r in rows) { _thRows.Add((r.Type, r.Id, r.Seen)); _thList.Items.Add(r.Text); }
        }
        else _thList.Items.Add("Chọn 1 thư ở danh sách “Đã gửi”.");
        _thList.EndUpdate();
        _thList.SelectedIndex = _curMsg is { } m ? _thRows.FindIndex(x => x.Type == m.Type && x.Id == m.Id) : -1;
        _loading = wasLoading;
        _thList.Height = Math.Clamp(Math.Max(1, _thList.Items.Count), 3, 8) * _thList.ItemHeight + 6;
    }

    private void OpenMsg(string type, string id)
    {
        _curMsg = (type, id);
        var v = WeeklyMailHistory.View(type, id);   // phản hồi: đánh dấu đã xem
        if (type == "reply") { foreach (var r in _idx.Replies.Where(r => r.Id == id)) r.Seen = true; RenderHist(); }
        RenderThread();
        ShowMsg(v);
    }

    private void ShowMsg(WeeklyMailView? v)
    {
        foreach (var b in new[] { _bReply, _bReplyAll, _bEml, _bDel }) b.Enabled = v is not null;
        _bResend.Enabled = v is not null && _curMsg?.Type == "sent";
        _vText.BackColor = AppColors.Input; _vText.ForeColor = AppColors.Text;
        if (v is null) { _vHead.Text = _curMsg is null ? "Chưa chọn thư." : "Không đọc được file .eml của thư này."; _vText.Clear(); return; }
        _vHead.Text = $"{(string.IsNullOrEmpty(v.Subject) ? "(không tiêu đề)" : v.Subject)}\r\nTừ: {v.From}   ·   {v.Date}\r\nTo: {v.To}" + (v.Cc.Length > 0 ? $"\r\nCc: {v.Cc}" : "") + (v.Bcc.Length > 0 ? $"\r\nBcc: {v.Bcc}" : "")
            + (v.Attachments.Count > 0 ? $"\r\n📎 Đính kèm: {string.Join(", ", v.Attachments)}" : "") + (v.Html.Length > 0 ? "\r\n(Thư HTML — đang hiện dạng chữ; bấm “Mở .eml” để xem đúng định dạng.)" : "");
        _vText.Text = v.Text;
    }

    private void StartReply(bool all)
    {
        if (_curMsg is not { } m) return;
        var d = WeeklyMailHistory.DraftReply(m.Type, m.Id, all, _senderEmail.Text.Trim());
        if (d is null) { AddLog("Không đọc được thư gốc để trả lời.", "err"); return; }
        _draft = d; _replyTarget = m;
        _rTo.Text = d.To; _rCc.Text = d.Cc; _rSubject.Text = d.Subject; _rBody.Text = "";
        _rSig.Checked = d.Signature.Length > 0; _rSig.Enabled = d.Signature.Length > 0;
        _rSigInfo.Text = d.Signature.Length > 0 ? "" : "(mẫu của thư này không có chữ ký)";
        _rQuote.BackColor = AppColors.Input; _rQuote.ForeColor = AppColors.TextMuted; _rQuote.Text = d.QuoteText;
        _cReply.Visible = true;
        _rBody.Inner.Focus();
        (_cReply.Parent as ScrollableControl)?.ScrollControlIntoView(_cReply);
    }

    /// <summary>Kiểm tra thông tin máy chủ trước khi gửi / đọc hộp thư; thiếu thì mở mục máy chủ ở tab Soạn.</summary>
    private MailConfig? NeedServer(bool needPassword)
    {
        var c = ReadSmtp(true);
        var miss = c.Host.Length == 0 ? "Chưa khai báo máy chủ mail" : c.SenderEmail.Length == 0 ? "Chưa nhập Email gửi" : needPassword && c.Password.Length == 0 ? "Chưa nhập mật khẩu (IMAP luôn cần đăng nhập)" : "";
        if (miss.Length == 0) return c;
        AddLog(miss + " — mở mục “Máy chủ mail (SMTP / IMAP)” ở tab Soạn mail.", "err");
        _tabs.Select("compose", raise: true);
        if (!_cSmtp.TitleLabel.Text.StartsWith("▾")) _cSmtp.OpenToggle?.Invoke();
        (c.Host.Length == 0 ? _host : c.SenderEmail.Length == 0 ? _senderEmail : _password).Inner.Focus();
        return null;
    }

    private static string Summary(MailConfig c) =>
        $"Người gửi: {c.SenderEmail}\nTo: {c.To}" + (string.IsNullOrWhiteSpace(c.Cc) ? "" : $"\nCc: {c.Cc}") + (string.IsNullOrWhiteSpace(c.Bcc) ? "" : $"\nBcc: {c.Bcc}") + $"\nTiêu đề: {c.Subject}";

    /// <summary>Chạy 1 việc dài (gửi lại / trả lời / kiểm tra phản hồi) — 1 việc 1 lúc, nút Huỷ, log về 2 khung kết quả. <paramref name="op"/> trả chữ trạng thái khi thành công, null khi lỗi.</summary>
    private async Task RunOpAsync(string busy, Func<CancellationToken, Task<string?>> op)
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        foreach (var b in new[] { _bSend, _bCheck, _rSend, _bResend }) b.Enabled = false;
        _bCancel.Visible = true; SetState(busy);
        string? done = null;
        try { done = await op(_cts.Token); }
        catch (OperationCanceledException) { AddLog("Đã huỷ.", "err"); }
        catch (Exception ex) { AddLog(ex.Message, "err"); }
        finally
        {
            _cts?.Dispose(); _cts = null;
            foreach (var b in new[] { _bSend, _bCheck, _rSend }) b.Enabled = true;
            _bCancel.Visible = false;
            SetState(done ?? "✘ Không thành công", error: done is null, ok: done is not null);
            LoadHistory();
            ShowMsg(_curMsg is { } m ? WeeklyMailHistory.View(m.Type, m.Id) : null);
        }
    }

    private async Task CheckRepliesAsync()
    {
        if (_cts is not null || NeedServer(true) is not { } c) return;
        var imap = SaveImapNow();
        await RunOpAsync("Đang kiểm tra phản hồi...", async ct =>
        {
            try { var n = await WeeklyMailHistory.CheckRepliesAsync(c, imap, LogAsync, ct); return n > 0 ? $"✔ {n} phản hồi mới" : "Không có phản hồi mới"; }
            catch (Exception ex) when (ex is not OperationCanceledException) { LogAsync("Không kiểm tra được phản hồi: " + ex.Message, "err"); return null; }
        });
    }

    private async Task ResendAsync()
    {
        if (_cts is not null || _curMsg is not { Type: "sent" } m || NeedServer(false) is not { } server) return;
        if (WeeklyMailHistory.ResendConfig(m.Id, server) is not { } c) { AddLog("Không đọc được file .eml của thư này.", "err"); return; }
        if (MessageBox.Show(FindForm(), "Gửi lại thư này?\n\n" + Summary(c), "Bcode — Mail kế hoạch tuần", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { AddLog("Đã huỷ — chưa gửi.", "dim"); return; }
        WeeklyMailStore.SaveSmtp(c);
        var imap = SaveImapNow();
        await RunOpAsync("Đang gửi lại...", async ct => await WeeklyMailHistory.SendAndSaveAsync(c, WeeklyMailHistory.MetaOf("sent", m.Id, "resend"), imap, null, LogAsync, ct) ? "✔ Đã gửi lại" : null);
    }

    private async Task SendReplyAsync()
    {
        if (_cts is not null || _replyTarget is not { } t || _draft is null || NeedServer(false) is not { } c) return;
        if (_rTo.Text.Trim().Length == 0) { AddLog("Chưa nhập người nhận (To).", "err"); _rTo.Inner.Focus(); return; }
        c.To = _rTo.Text.Trim(); c.Cc = _rCc.Text.Trim(); c.Bcc = ""; c.Subject = _rSubject.Text; c.Html = true;
        c.Body = WeeklyMailHistory.ComposeReply(_rBody.Text, _rSig.Checked ? _draft.Signature : "", _draft.QuoteHtml);
        if (MessageBox.Show(FindForm(), "Gửi thư trả lời này?\n\n" + Summary(c), "Bcode — Mail kế hoạch tuần", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { AddLog("Đã huỷ — chưa gửi.", "dim"); return; }
        WeeklyMailStore.SaveSmtp(c);
        var imap = SaveImapNow();
        var headers = WeeklyMailHistory.ReplyHeaders(t.Type, t.Id);
        await RunOpAsync("Đang gửi trả lời...", async ct =>
        {
            var ok = await WeeklyMailHistory.SendAndSaveAsync(c, WeeklyMailHistory.MetaOf(t.Type, t.Id, "reply"), imap, headers, LogAsync, ct);
            if (ok) BeginInvoke(new Action(() => { _cReply.Visible = false; _replyTarget = null; }));
            return ok ? "✔ Đã gửi trả lời" : null;
        });
    }

    private void OpenEml()
    {
        if (_curMsg is not { } m) return;
        var p = WeeklyMailHistory.PathOf(m.Type, m.Id);
        try
        {
            if (p.Length > 0 && File.Exists(p)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
            else AddLog("Không thấy file .eml.", "err");
        }
        catch (Exception ex) { AddLog("Không mở được file .eml: " + ex.Message, "err"); }
    }

    private void DeleteMsg()
    {
        if (_curMsg is not { } m) return;
        var isRoot = m.Type == "sent" && _idx.Sent.FirstOrDefault(e => e.Id == m.Id) is { } e && e.ThreadId == e.Id;
        var ask = m.Type == "reply" ? "Xoá phản hồi này khỏi Bcode?" : isRoot ? "Xoá thư này cùng cả chuỗi trả lời / phản hồi khỏi mục Đã gửi của Bcode?" : "Xoá thư này khỏi mục Đã gửi của Bcode?";
        if (MessageBox.Show(FindForm(), ask + "\n\n(Chỉ xoá bản lưu trong Bcode, không đụng hộp thư trên máy chủ.)", "Bcode — Mail kế hoạch tuần", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        WeeklyMailHistory.Delete(m.Type, m.Id);
        _curMsg = null; ShowMsg(null);
        if (isRoot) _curThread = null;
        LoadHistory();
    }
}
