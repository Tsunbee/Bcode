using System.Text;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Engine = SqlDecryptor.Core;

namespace Bcode.App.Forms;

/// <summary>
/// Decrypt SQL Object — khôi phục source của stored procedure / view / function / trigger
/// được tạo bằng <c>WITH ENCRYPTION</c> trên SQL Server của CHÍNH bạn.
///
/// Kỹ thuật: known-plaintext qua DAC (Dedicated Admin Connection) + bảng hệ thống
/// <c>sys.sysobjvalues</c> — đây là cách công khai, hợp lệ (KHÔNG reverse-engineer thuật
/// toán độc quyền của FCode). Toàn bộ do engine <c>SqlDecryptor.Core</c> đảm nhiệm; mọi
/// thao tác ghi (ALTER giả) chạy trong transaction và LUÔN rollback, object không đổi.
///
/// Yêu cầu: login <b>sysadmin</b> (DAC bắt buộc). DAC chỉ cho 1 kết nối/instance nên
/// "Giải mã tất cả" chạy tuần tự trên một kết nối dùng chung.
/// </summary>
public class DecryptSqlObjectForm : ThemedForm
{
    private readonly TextBox _serverBox = new();
    private readonly CheckBox _integratedCheck = new()
        { Text = "Windows Auth", AutoSize = true, Checked = true };
    private readonly TextBox _userBox = new();
    private readonly TextBox _passBox = new() { UseSystemPasswordChar = true };
    private readonly ComboBox _dbCombo = new()
        { DropDownStyle = ComboBoxStyle.DropDown, Width = 240 };
    private readonly CheckBox _formatCheck = new()
        { Text = "Format", AutoSize = true, Checked = true, Margin = new Padding(10, 6, 0, 0) };

    private readonly SqlFormatterService _formatter = new();
    // Kết quả thô (chưa format) của lần giải mã gần nhất — để render lại khi bật/tắt Format.
    private readonly List<ResultEntry> _lastResults = new();
    private sealed record ResultEntry(string Banner, string? Sql, string? Warning, string? Error);

    private readonly ListBox _objList = new()
        { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _objCountLabel = new()
        { Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
          ForeColor = AppColors.TextMuted };

    private readonly TextBox _output = new()
        { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
          ScrollBars = ScrollBars.Both, WordWrap = false, Font = ThemeManager.MonoFont,
          BackColor = AppColors.Input };
    private readonly Label _warnLabel = new()
        { Dock = DockStyle.Top, AutoSize = false, Height = 0, ForeColor = AppColors.Warning,
          TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 4, 0) };

    private readonly Label _statusLabel = new()
        { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
          ForeColor = AppColors.TextMuted, AutoEllipsis = true };

    private readonly PillButton _loadDbBtn = PillButton.Flat("Tải DB");
    private readonly PillButton _loadObjBtn = PillButton.Flat("Tải object mã hóa");
    private readonly PillButton _decryptBtn = PillButton.Flat("Giải mã", primary: true);
    private readonly PillButton _decryptAllBtn = PillButton.Flat("Giải mã tất cả");
    private readonly PillButton _diagBtn = PillButton.Flat("Chẩn đoán");
    private readonly PillButton _copyBtn = PillButton.Flat("Copy");
    private readonly PillButton _saveBtn = PillButton.Flat("Lưu .sql");

    public DecryptSqlObjectForm(AppSettings settings, DbConnectionService connections)
    {
        Text = "Decrypt SQL Object (WITH ENCRYPTION — qua DAC)";
        Width = 1040;
        Height = 720;
        MinimumSize = new Size(880, 560);
        StartPosition = FormStartPosition.CenterParent;

        // Prefill từ workspace đang chọn (nếu có).
        var ws = connections.Current ?? settings.Workspaces.FirstOrDefault();
        if (ws is not null)
        {
            _serverBox.Text = ws.Server;
            _integratedCheck.Checked = ws.IntegratedSecurity;
            _userBox.Text = ws.User;
            _passBox.Text = ws.Password;
            if (!string.IsNullOrWhiteSpace(ws.AppDatabase)) _dbCombo.Items.Add(ws.AppDatabase);
            if (!string.IsNullOrWhiteSpace(ws.SysDatabase)) _dbCombo.Items.Add(ws.SysDatabase);
            if (_dbCombo.Items.Count > 0) _dbCombo.SelectedIndex = 0;
        }

        Controls.Add(BuildBody());
        Controls.Add(BuildConnectionBar());   // Dock Top (thêm sau -> nằm trên body)
        Controls.Add(BuildBottomBar());        // Dock Bottom

        WireEvents();
        ToggleAuthFields();
        UpdateButtons();
    }

    // ---------------------------------------------------------------------
    // Bố cục
    // ---------------------------------------------------------------------
    private Control BuildConnectionBar()
    {
        // Outer: 1 cột, 3 hàng AutoSize (conn row, db row, divider). TableLayoutPanel
        // AutoSize + Dock.Top tính chiều cao ổn định (khác Panel lồng nhau).
        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 8, 10, 4)
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Hàng 1: Server / auth
        var conn = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 7, AutoSize = true };
        conn.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        conn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        conn.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        conn.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        conn.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        conn.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        conn.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

        _serverBox.Dock = DockStyle.Fill;
        _userBox.Dock = DockStyle.Fill;
        _passBox.Dock = DockStyle.Fill;
        _integratedCheck.Margin = new Padding(8, 6, 8, 0);

        conn.Controls.Add(L("Server\\Instance"), 0, 0);
        conn.Controls.Add(_serverBox, 1, 0);
        conn.Controls.Add(_integratedCheck, 2, 0);
        conn.Controls.Add(L("User"), 3, 0);
        conn.Controls.Add(_userBox, 4, 0);
        conn.Controls.Add(L("Pass"), 5, 0);
        conn.Controls.Add(_passBox, 6, 0);

        // Hàng 2: Database + nút tải
        var dbRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = false,
            Margin = new Padding(0, 4, 0, 4)
        };
        dbRow.Controls.Add(L("Database", 6));
        dbRow.Controls.Add(_dbCombo);
        dbRow.Controls.Add(_loadDbBtn);
        dbRow.Controls.Add(_loadObjBtn);
        dbRow.Controls.Add(_formatCheck);

        var hr = new Label { Dock = DockStyle.Top, Height = 1, Margin = new Padding(0, 4, 0, 0),
                             BackColor = AppColors.Border };

        outer.Controls.Add(conn, 0, 0);
        outer.Controls.Add(dbRow, 0, 1);
        outer.Controls.Add(hr, 0, 2);
        return outer;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6
        };

        // Trái: danh sách object mã hóa
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 4, 10) };
        left.Controls.Add(_objList);
        left.Controls.Add(_objCountLabel);
        split.Panel1.Controls.Add(left);

        // Phải: cảnh báo + output
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 6, 10, 10) };
        right.Controls.Add(_output);
        right.Controls.Add(_warnLabel);
        split.Panel2.Controls.Add(right);

        // Đặt sau khi handle tạo để tránh lỗi SplitterDistance.
        split.HandleCreated += (_, _) => { try { split.SplitterDistance = 330; } catch { } };
        return split;
    }

    private Control BuildBottomBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 8, 10, 8) };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        flow.Controls.Add(_copyBtn);
        flow.Controls.Add(_saveBtn);
        flow.Controls.Add(_diagBtn);
        flow.Controls.Add(_decryptAllBtn);
        flow.Controls.Add(_decryptBtn);

        bar.Controls.Add(_statusLabel);
        bar.Controls.Add(flow);
        _statusLabel.SendToBack();
        return bar;
    }

    private static Label L(string text, int topPad = 0) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6 + topPad, 8, 0)
    };

    // ---------------------------------------------------------------------
    // Sự kiện
    // ---------------------------------------------------------------------
    private void WireEvents()
    {
        _integratedCheck.CheckedChanged += (_, _) => ToggleAuthFields();
        _objList.SelectedIndexChanged += (_, _) => UpdateButtons();
        _objList.DoubleClick += async (_, _) => await DecryptSelectedAsync();

        _loadDbBtn.Click += async (_, _) => await LoadDatabasesAsync();
        _loadObjBtn.Click += async (_, _) => await LoadObjectsAsync();
        _decryptBtn.Click += async (_, _) => await DecryptSelectedAsync();
        _decryptAllBtn.Click += async (_, _) => await DecryptAllAsync();
        _diagBtn.Click += async (_, _) => await RunDiagnoseAsync();
        _copyBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_output.Text)) Clipboard.SetText(_output.Text);
        };
        _saveBtn.Click += (_, _) => SaveOutput();
        _formatCheck.CheckedChanged += (_, _) => RenderOutput();
    }

    private void ToggleAuthFields()
    {
        _userBox.Enabled = !_integratedCheck.Checked;
        _passBox.Enabled = !_integratedCheck.Checked;
    }

    private void UpdateButtons()
    {
        bool hasDb = !string.IsNullOrWhiteSpace(_dbCombo.Text);
        _loadObjBtn.Enabled = hasDb;
        _decryptBtn.Enabled = hasDb && _objList.SelectedItem is Engine.EncryptedObject;
        _diagBtn.Enabled = hasDb && _objList.SelectedItem is Engine.EncryptedObject;
        _decryptAllBtn.Enabled = hasDb && _objList.Items.Count > 0;
        bool hasOut = !string.IsNullOrEmpty(_output.Text);
        _copyBtn.Enabled = hasOut;
        _saveBtn.Enabled = hasOut;
    }

    // ---------------------------------------------------------------------
    // Engine
    // ---------------------------------------------------------------------
    private Engine.SqlObjectDecryptor BuildEngine()
    {
        var server = _serverBox.Text.Trim();
        if (string.IsNullOrEmpty(server))
            throw new InvalidOperationException("Chưa nhập Server\\Instance.");

        var info = new Engine.SqlConnectionInfo
        {
            InstanceName   = server,
            UseWindowsAuth = _integratedCheck.Checked,
            Username       = _userBox.Text.Trim(),
            Password       = _passBox.Text,
        };
        if (!info.UseWindowsAuth && string.IsNullOrEmpty(info.Username))
            throw new InvalidOperationException("SQL Auth: cần nhập User (và Password).");

        return new Engine.SqlObjectDecryptor(info);
    }

    private async Task LoadDatabasesAsync()
    {
        await RunBusyAsync("Đang tải danh sách database...", async () =>
        {
            var engine = BuildEngine();
            var dbs = await engine.ListDatabasesAsync();
            _dbCombo.BeginUpdate();
            var keep = _dbCombo.Text;
            _dbCombo.Items.Clear();
            foreach (var d in dbs) _dbCombo.Items.Add(d);
            _dbCombo.EndUpdate();
            if (!string.IsNullOrEmpty(keep) && _dbCombo.Items.Contains(keep)) _dbCombo.Text = keep;
            else if (_dbCombo.Items.Count > 0) _dbCombo.SelectedIndex = 0;
            SetStatus($"Đã tải {dbs.Count} database.", ok: true);
        });
    }

    private async Task LoadObjectsAsync()
    {
        var db = _dbCombo.Text.Trim();
        if (string.IsNullOrEmpty(db)) { SetStatus("Chưa chọn database.", ok: false); return; }

        await RunBusyAsync($"Đang liệt kê object mã hóa trong [{db}]...", async () =>
        {
            var engine = BuildEngine();
            var objs = await engine.ListEncryptedObjectsAsync(db);
            _objList.BeginUpdate();
            _objList.Items.Clear();
            foreach (var o in objs) _objList.Items.Add(o);
            _objList.EndUpdate();
            _objList.DisplayMember = nameof(Engine.EncryptedObject.FullName);
            _objCountLabel.Text = $"  {objs.Count} object mã hóa";
            SetStatus(objs.Count == 0
                ? "Không có object WITH ENCRYPTION nào trong database này."
                : $"Đã tìm thấy {objs.Count} object mã hóa. Chọn 1 object rồi bấm Giải mã.", ok: objs.Count > 0);
        });
    }

    private async Task DecryptSelectedAsync()
    {
        if (_objList.SelectedItem is not Engine.EncryptedObject o) return;
        var db = _dbCombo.Text.Trim();

        await RunBusyAsync($"Đang giải mã {o.FullName} qua DAC...", async () =>
        {
            var engine = BuildEngine();
            var r = await engine.DecryptAsync(db, o.FullName);
            _lastResults.Clear();
            _lastResults.Add(new ResultEntry(Banner(r.Name, r.Type, r.WasEncrypted), r.Sql, r.Warning, null));
            RenderOutput();
            ShowWarning(r.Warning);
            SetStatus($"Xong: {r.Name}." + (r.Warning is null ? "" : "  (có cảnh báo)"),
                      ok: r.Warning is null);
        });
    }

    private async Task DecryptAllAsync()
    {
        var db = _dbCombo.Text.Trim();
        if (_objList.Items.Count == 0) { SetStatus("Chưa có object nào để giải mã.", ok: false); return; }

        await RunBusyAsync($"Đang giải mã toàn bộ [{db}] (tuần tự qua DAC)...", async () =>
        {
            var engine = BuildEngine();
            var progress = new Progress<string>(msg => SetStatus(msg));
            var all = await engine.DecryptAllAsync(db, progress);

            _lastResults.Clear();
            int ok = 0, fail = 0;
            foreach (var item in all)
            {
                if (item.Ok && item.Result is { } r)
                {
                    ok++;
                    _lastResults.Add(new ResultEntry(Banner(r.Name, r.Type, r.WasEncrypted), r.Sql, r.Warning, null));
                }
                else
                {
                    fail++;
                    _lastResults.Add(new ResultEntry(item.ObjectName, null, null, item.Error));
                }
            }
            RenderOutput();
            ShowWarning(fail > 0 ? $"{fail} object lỗi (xem chi tiết trong kết quả)." : null);
            SetStatus($"Hoàn tất: {ok} thành công, {fail} lỗi / tổng {all.Count}.", ok: fail == 0);
        });
    }

    private async Task RunDiagnoseAsync()
    {
        if (_objList.SelectedItem is not Engine.EncryptedObject o)
        { SetStatus("Chọn 1 object để chẩn đoán.", ok: false); return; }
        var db = _dbCombo.Text.Trim();

        await RunBusyAsync($"Đang chẩn đoán {o.FullName} (2 câu giả, có rollback)...", async () =>
        {
            var engine = BuildEngine();
            var rep = await engine.DiagnoseAsync(db, o.FullName);

            var sb = new StringBuilder();
            sb.AppendLine("=== CHẨN ĐOÁN GIẢI MÃ (cross-decrypt F1/F2) ===");
            sb.AppendLine($"Object       : {rep.ObjectName}  ({rep.Type})");
            sb.AppendLine($"encReal      : {rep.EncRealBytes} byte");
            sb.AppendLine($"encFake1 / 2 : {rep.EncFake1Bytes} / {rep.EncFake2Bytes} byte");
            sb.AppendLine($"So sánh      : {rep.ComparedChars} ký tự — lệch {rep.MismatchChars}");
            sb.AppendLine($"Lệch đầu tiên: {(rep.FirstMismatchChar < 0 ? "(không có)" : "ký tự " + rep.FirstMismatchChar)}");
            sb.AppendLine();
            sb.AppendLine(rep.Summary);
            if (rep.FirstMismatchChar >= 0)
            {
                sb.AppendLine();
                sb.AppendLine("--- Kỳ vọng F2 (quanh vị trí lệch) ---");
                sb.AppendLine(rep.ExpectedSnippet);
                sb.AppendLine("--- Thực nhận (cross-decrypt) ---");
                sb.AppendLine(rep.GotSnippet);
            }
            sb.AppendLine();
            sb.AppendLine("=== PROBE KHÔNG MÃ HÓA (SQL thực lưu chuỗi gì) ===");
            sb.AppendLine($"submit / stored : {rep.PlainSubmittedChars} / {rep.PlainStoredChars} ký tự");
            sb.AppendLine($"Khác đầu tiên   : {(rep.PlainFirstDiffChar < 0 ? "(giống hệt)" : "ký tự " + rep.PlainFirstDiffChar)}");
            if (rep.PlainInsertionInfo is not null)
                sb.AppendLine(rep.PlainInsertionInfo);

            _lastResults.Clear();          // báo cáo hiển thị trực tiếp, không qua Format
            _output.Text = sb.ToString();
            ShowWarning(rep.FirstMismatchChar >= 0
                ? "Có vùng lệch — Copy toàn bộ báo cáo này gửi lại để phân tích nguyên nhân."
                : null);
            SetStatus(rep.FirstMismatchChar < 0
                ? "Chẩn đoán: KHỚP hoàn toàn (method đúng cho object này)."
                : $"Chẩn đoán: lệch {rep.MismatchChars}/{rep.ComparedChars} ký tự tại ký tự {rep.FirstMismatchChar}.",
                ok: rep.FirstMismatchChar < 0);
            _copyBtn.Enabled = true;
        });
    }

    private void SaveOutput()
    {
        if (string.IsNullOrEmpty(_output.Text)) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "SQL script (*.sql)|*.sql|Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = (_objList.SelectedItem as Engine.EncryptedObject)?.FullName is { } n
                ? n.Replace('.', '_') + ".sql"
                : "decrypted.sql"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, _output.Text, new UTF8Encoding(false));
        SetStatus($"Đã lưu: {dlg.FileName}", ok: true);
    }

    // ---------------------------------------------------------------------
    // Helpers UI
    // ---------------------------------------------------------------------
    private static string Banner(string name, string type, bool wasEncrypted) =>
        $"-- {name}  ({type})  {(wasEncrypted ? "[decrypted]" : "[not encrypted]")}"
        + Environment.NewLine;

    /// <summary>Render lại output từ kết quả thô, áp dụng Format nếu checkbox bật.</summary>
    private void RenderOutput()
    {
        var sb = new StringBuilder();
        bool many = _lastResults.Count > 1;
        foreach (var e in _lastResults)
        {
            if (e.Error is not null)
            {
                sb.AppendLine($"-- [LỖI] {e.Banner}: {e.Error}").AppendLine();
                continue;
            }
            sb.Append(e.Banner);
            if (e.Warning is not null) sb.AppendLine($"-- [!] {e.Warning}");
            sb.AppendLine(_formatCheck.Checked ? SafeFormat(e.Sql ?? "") : e.Sql ?? "");
            if (many) sb.AppendLine().AppendLine("GO").AppendLine();
        }
        _output.Text = sb.ToString();
        UpdateButtons();
    }

    private string SafeFormat(string sql)
    {
        try { return _formatter.Format(sql); }
        catch { return sql; }   // formatter lỗi thì giữ nguyên bản thô
    }

    private void ShowWarning(string? warning)
    {
        if (string.IsNullOrEmpty(warning)) { _warnLabel.Text = ""; _warnLabel.Height = 0; }
        else { _warnLabel.Text = "⚠ " + warning; _warnLabel.Height = 24; }
    }

    private void SetStatus(string text, bool? ok = null)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = ok is null ? AppColors.TextMuted
                               : ok.Value ? AppColors.Success
                               : AppColors.Danger;
    }

    private async Task RunBusyAsync(string busyText, Func<Task> action)
    {
        SetBusy(true);
        SetStatus(busyText);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi: " + FriendlyError(ex), ok: false);
        }
        finally
        {
            SetBusy(false);
            UpdateButtons();
        }
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _loadDbBtn.Enabled = !busy;
        _loadObjBtn.Enabled = !busy;
        _decryptBtn.Enabled = !busy;
        _decryptAllBtn.Enabled = !busy;
        _diagBtn.Enabled = !busy;
    }

    private static string FriendlyError(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("dedicated administrator", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("DAC", StringComparison.OrdinalIgnoreCase))
            return msg + "  (DAC chỉ cho 1 kết nối/instance — đóng kết nối DAC khác, hoặc bật " +
                   "'remote admin connections' nếu chạy remote.)";
        if (msg.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
            return msg + "  (DAC yêu cầu quyền sysadmin.)";
        return msg;
    }
}
