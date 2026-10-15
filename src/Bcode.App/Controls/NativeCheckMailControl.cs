using System.Text.Json;
using System.Text.Json.Nodes;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Check Mail" ở chế độ App/WinForms (thay checkmail.html): khai báo host / port / SSL-TLS / tài khoản gửi rồi Test kết nối hoặc gửi thử 1 email để biết SMTP có chạy không.
/// Lưu cấu hình (KHÔNG lưu mật khẩu) vào %AppData%\Bcode\checkmail.json — cùng file với bản Web; "Lấy từ Message.xml" điền sẵn từ source của project; log tiến trình tô màu.
/// Chạy bằng <see cref="MailTestService"/> như bản Web.
/// </summary>
public sealed class NativeCheckMailControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static string ConfigPath => Path.Combine(BcodePaths.AppData, "Bcode", "checkmail.json");

    private readonly Func<Workspace?> _workspace;
    private CancellationTokenSource? _cts;
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 500 };
    private bool _loading;

    private readonly RoundedInput _host = NK.Input("mail.domain.com"), _senderEmail = NK.Input("no-reply@domain.com"), _alias = NK.Input("Fast Business Online"),
        _account = NK.Input("(để trống nếu máy chủ không cần đăng nhập)"), _to = NK.Input("nhieu-nguoi@domain.com; phan-cach-bang-dau-cham-phay"), _cc = NK.Input(), _bcc = NK.Input(),
        _subject = NK.Input(), _body = NK.Memo(6, "", mono: false), _password = NK.Input();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 587, Width = NativeSkin.S(90) }, _timeout = new() { Minimum = 5, Maximum = 300, Value = 30, Width = NativeSkin.S(90) };
    private readonly CheckBox _ssl = NK.Check("SSL"), _tls = NK.Check("TLS"), _ignoreCert = NK.Check("Bỏ qua lỗi chứng chỉ"), _verbose = NK.Check("Ghi log SMTP chi tiết"), _showPw = NK.Check("Hiện");
    private readonly RadioButton _fmtText = new() { Text = "Text", AutoSize = true, Checked = true }, _fmtHtml = new() { Text = "HTML", AutoSize = true };
    private readonly DropDownButton _preset = new() { Width = NativeSkin.S(200) };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Top, Height = NativeSkin.S(220), ReadOnly = true, WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both, BorderStyle = BorderStyle.None };
    private readonly PillButton _bSend, _bTest, _bCancel;
    private readonly Label _state = new() { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(12, 8, 0, 0) };

    public NativeCheckMailControl(Func<Workspace?>? workspace = null)
    {
        _workspace = workspace ?? (() => null);
        Dock = DockStyle.Fill;

        _password.Inner.UseSystemPasswordChar = true;
        _showPw.CheckedChanged += (_, _) => _password.Inner.UseSystemPasswordChar = !_showPw.Checked;
        _subject.Text = "Test Mail từ Bcode"; _body.Text = "Nội dung mail: test";
        _preset.SetItems(new[] { "Mẫu nhanh...", "Gmail — smtp.gmail.com : 587", "Microsoft 365 — smtp.office365.com : 587", "Outlook.com — smtp-mail.outlook.com : 587", "Yahoo — smtp.mail.yahoo.com : 465", "Zoho — smtp.zoho.com : 465" }, 0);
        _preset.SelectionChanged += i =>
        {
            if (i <= 0) return;
            var presets = new[] { ("", 0), ("smtp.gmail.com", 587), ("smtp.office365.com", 587), ("smtp-mail.outlook.com", 587), ("smtp.mail.yahoo.com", 465), ("smtp.zoho.com", 465) };
            _host.Text = presets[i].Item1; _port.Value = presets[i].Item2; _ssl.Checked = true; _tls.Checked = false;
            _preset.Select(0, raise: false); QueueSave();
        };

        // ---- thẻ Server ----
        var cServer = new CardPanel("Máy chủ / Server");
        cServer.RightBar.Controls.Add(NK.Button("⟳ Lấy từ Message.xml", () => LoadFromProject(true), tip: "Lấy host / port / SSL / tài khoản từ App_Data\\Controllers\\Options\\Message.xml của project đang chọn (có entity EmailConfig thì lấy ở file đó)"));
        cServer.RightBar.Controls.Add(_preset);
        var portRow = NK.Row(_port, NK.Muted("25 · 465 (SSL) · 587 (STARTTLS)"));
        var secRow = NK.Row(_ssl, _tls, _ignoreCert);
        var toRow = NK.Row(_timeout, _verbose);
        var hint = NK.Wrap("Tick SSL hoặc TLS đều là mã hoá đường truyền: cổng 465 → SSL ngay khi kết nối, cổng khác → STARTTLS. Bỏ trống cả hai = không mã hoá.", cServer.Body, 40);
        hint.Dock = DockStyle.Top;
        cServer.Body.Controls.Add(hint);
        cServer.Body.Controls.Add(NK.FormGrid(130, ("Host / Máy chủ", _host), ("Port / Cổng", portRow), ("Bảo mật", secRow), ("Timeout (giây)", toRow)));
        cServer.Body.Controls.SetChildIndex(hint, 0);

        // ---- thẻ Sender ----
        var cSender = new CardPanel("Người gửi / Sender");
        var pwRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        pwRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pwRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _password.Dock = DockStyle.Fill; pwRow.Controls.Add(_password, 0, 0); pwRow.Controls.Add(_showPw, 1, 0);
        var pwHint = NK.Muted("Mật khẩu không được lưu — nhập lại mỗi lần mở tab."); pwHint.Dock = DockStyle.Top;
        cSender.Body.Controls.Add(pwHint);
        cSender.Body.Controls.Add(NK.FormGrid(130, ("Email", _senderEmail), ("Alias / Hiển thị", _alias), ("Account / Đăng nhập", _account), ("Password / Mật khẩu", pwRow)));
        cSender.Body.Controls.SetChildIndex(pwHint, 0);

        // ---- thẻ Receiver + Content ----
        var cRecv = new CardPanel("Người nhận / Receiver");
        cRecv.Body.Controls.Add(NK.FormGrid(130, ("Email", _to), ("Cc", _cc), ("Bcc", _bcc)));
        var cContent = new CardPanel("Nội dung / Content");
        var fmtRow = NK.Row(_fmtText, _fmtHtml);
        var bodyBox = new Panel { Dock = DockStyle.Fill, Height = NativeSkin.S(150) };
        _body.Dock = DockStyle.Top;
        var contentBody = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        cContent.Body.Controls.Add(NK.FormGrid(130, ("Subject / Tiêu đề", _subject), ("Content / Nội dung", NK.Row())));
        // ô nội dung + định dạng đặt riêng dưới tiêu đề (ô nhiều dòng cần chiều cao riêng)
        var bodyHost = new Panel { Dock = DockStyle.Top, Height = NativeSkin.S(190) };
        _body.Dock = DockStyle.Fill; fmtRow.Dock = DockStyle.Bottom;
        bodyHost.Controls.Add(_body); bodyHost.Controls.Add(fmtRow);
        cContent.Body.Controls.Clear();
        cContent.Body.Controls.Add(bodyHost);
        cContent.Body.Controls.Add(NK.FormGrid(130, ("Subject / Tiêu đề", _subject)));

        var cols = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 8, 0) };
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        left.Controls.AddRange(new Control[] { cServer, cSender }); right.Controls.AddRange(new Control[] { cRecv, cContent });
        cols.Controls.Add(left, 0, 0); cols.Controls.Add(right, 1, 0);
        foreach (var c in new Control[] { cServer, cSender, cRecv, cContent }) c.Width = NativeSkin.S(400);
        cols.SizeChanged += (_, _) => { var w = Math.Max(300, cols.Width / 2 - 14); foreach (var c in new Control[] { cServer, cSender, cRecv, cContent }) c.Width = w; };

        // ---- Result ----
        var cResult = new CardPanel("Result / Kết quả");
        cResult.RightBar.Controls.Add(NK.Button("Copy log", () => NK.CopyText(_log.Text)));
        cResult.RightBar.Controls.Add(NK.Button("Xoá log", () => { _log.Clear(); SetState(""); }));
        _log.Font = ThemeManager.MonoFont;
        cResult.Body.Controls.Add(_log);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 10, 12, 10) };
        cResult.Dock = DockStyle.Top;
        scroll.Controls.Add(cResult); scroll.Controls.Add(cols);

        // ---- thanh nút ----
        _bSend = NK.Button("✉ Send / Gửi", () => Run(true), primary: true, tip: "Kết nối, đăng nhập rồi gửi 1 email thử");
        _bTest = NK.Button("⚡ Test kết nối", () => Run(false), tip: "Chỉ kết nối và đăng nhập, không gửi thư");
        _bCancel = NK.Button("Huỷ", () => { try { _cts?.Cancel(); } catch { } }); _bCancel.Visible = false;
        var bar = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 7, 10, 7) };
        bar.Controls.AddRange(new Control[] { _bSend, _bTest, _bCancel, _state });
        Controls.Add(scroll); Controls.Add(bar);

        // ---- tự lưu ----
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveConfig(ReadConfig(false)); };
        foreach (var r in new[] { _host, _senderEmail, _alias, _account, _to, _cc, _bcc, _subject, _body }) r.TextChanged += (_, _) => QueueSave();
        foreach (var c in new[] { _ssl, _tls, _ignoreCert, _verbose }) c.CheckedChanged += (_, _) => QueueSave();
        _fmtHtml.CheckedChanged += (_, _) => QueueSave();
        _port.ValueChanged += (_, _) => QueueSave(); _timeout.ValueChanged += (_, _) => QueueSave();

        HandleCreated += (_, _) => BeginInvoke(new Action(Init));
        Disposed += (_, _) => { try { _cts?.Cancel(); } catch { } _saveTimer.Dispose(); };
    }

    private void QueueSave() { if (_loading) return; _saveTimer.Stop(); _saveTimer.Start(); }

    private MailConfig ReadConfig(bool withPassword) => new()
    {
        Host = _host.Text.Trim(), Port = (int)_port.Value, Ssl = _ssl.Checked, Tls = _tls.Checked, IgnoreCert = _ignoreCert.Checked, TimeoutSeconds = (int)_timeout.Value, VerboseLog = _verbose.Checked,
        SenderEmail = _senderEmail.Text.Trim(), Alias = _alias.Text.Trim(), Account = _account.Text.Trim(), Password = withPassword ? _password.Text : "",
        To = _to.Text.Trim(), Cc = _cc.Text.Trim(), Bcc = _bcc.Text.Trim(), Subject = _subject.Text, Body = _body.Text, Html = _fmtHtml.Checked,
    };

    private void Init()
    {
        MailConfig cfg;
        try { cfg = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<MailConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new MailConfig() : new MailConfig(); }
        catch { cfg = new MailConfig(); }
        _loading = true;
        if (cfg.Host.Length > 0) _host.Text = cfg.Host;
        if (cfg.Port > 0) _port.Value = Math.Clamp(cfg.Port, 1, 65535);
        _ssl.Checked = cfg.Ssl; _tls.Checked = cfg.Tls; _ignoreCert.Checked = cfg.IgnoreCert; _verbose.Checked = cfg.VerboseLog;
        if (cfg.TimeoutSeconds > 0) _timeout.Value = Math.Clamp(cfg.TimeoutSeconds, 5, 300);
        _senderEmail.Text = cfg.SenderEmail; _alias.Text = cfg.Alias; _account.Text = cfg.Account; _to.Text = cfg.To; _cc.Text = cfg.Cc; _bcc.Text = cfg.Bcc;
        if (cfg.Subject.Length > 0) _subject.Text = cfg.Subject;
        if (cfg.Body.Length > 0) _body.Text = cfg.Body;
        _fmtHtml.Checked = cfg.Html; _fmtText.Checked = !cfg.Html;
        _loading = false;
        // Mặc định theo project đang chọn (Message.xml): đè lên giá trị đã lưu lần trước; người dùng vẫn sửa tay được.
        LoadFromProject(false);
    }

    /// <summary>Đọc Message.xml (và EmailConfig nếu có) của project đang chọn rồi điền vào form.</summary>
    private void LoadFromProject(bool announceFailure)
    {
        var sourcePath = _workspace()?.SourcePath;
        _ = Task.Run(() =>
        {
            var (cfg, note) = MailSettingsReader.Read(sourcePath);
            if (IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                if (cfg is not null)
                {
                    _loading = true;
                    _host.Text = cfg.Host ?? ""; _port.Value = cfg.Port > 0 ? Math.Clamp(cfg.Port, 1, 65535) : 587;
                    _ssl.Checked = cfg.Ssl; _tls.Checked = cfg.Tls;
                    if (cfg.TimeoutSeconds > 0) _timeout.Value = Math.Clamp(cfg.TimeoutSeconds, 5, 300);
                    _senderEmail.Text = cfg.SenderEmail ?? ""; _alias.Text = cfg.Alias ?? ""; _account.Text = cfg.Account ?? "";
                    if (!string.IsNullOrEmpty(cfg.Password)) _password.Text = cfg.Password;
                    _fmtHtml.Checked = cfg.Html; _fmtText.Checked = !cfg.Html;
                    _loading = false;
                    AddLog(note ?? "Đã lấy cấu hình từ project.", "dim");
                }
                else if (announceFailure) AddLog(note ?? "Không đọc được cấu hình.", "err");
            }));
        });
    }

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

    private async void Run(bool sendMail)
    {
        if (_cts is not null) return;   // đang chạy 1 lần rồi
        var c = ReadConfig(true);
        if (c.Host.Length == 0) { AddLog("Chưa nhập Host / máy chủ.", "err"); _host.Inner.Focus(); return; }
        if (c.Port < 1 || c.Port > 65535) { AddLog("Port / cổng phải từ 1 đến 65535.", "err"); return; }
        SaveConfig(c);
        _cts = new CancellationTokenSource();
        _bSend.Enabled = _bTest.Enabled = false; _bCancel.Visible = true; SetState("Đang chạy...");
        var ok = false;
        try
        {
            ok = await MailTestService.RunAsync(c, sendMail, (line, kind) => { try { if (!IsDisposed) BeginInvoke(new Action(() => AddLog(line, kind))); } catch { } }, _cts.Token);
        }
        catch (Exception ex) { AddLog(ex.Message, "err"); }
        finally
        {
            _cts?.Dispose(); _cts = null;
            _bSend.Enabled = _bTest.Enabled = true; _bCancel.Visible = false;
            SetState(ok ? "✔ Thành công" : "✘ Không thành công", error: !ok, ok: ok);
            AddLog("— " + (ok ? "THÀNH CÔNG" : "KHÔNG THÀNH CÔNG") + " —", ok ? "ok" : "err");
        }
    }

    private static void SaveConfig(MailConfig cfg)
    {
        try
        {
            // Xoá mật khẩu trên bản sao: cfg gốc còn được dùng để đăng nhập ngay sau khi lưu.
            var json = JsonSerializer.SerializeToNode(cfg, JsonOpts)!.AsObject();
            json["password"] = "";   // không ghi mật khẩu ra đĩa
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, json.ToJsonString(JsonOpts));
        }
        catch { /* không lưu được thì lần sau nhập lại */ }
    }
}
