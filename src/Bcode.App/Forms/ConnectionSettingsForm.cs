using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// File > Choose Server / Workspaces (Edit Project): quản lý danh sách các Workspace (WS)
/// với giao diện hoàn toàn responsive, tự động co giãn theo kích thước cửa sổ.
/// </summary>
public class ConnectionSettingsForm : ThemedForm
{
    private readonly AppSettings _settings;
    private readonly DbConnectionService _connections;
    private readonly ListBox _list;
    private readonly TextBox _nameBox, _serverBox, _userBox, _passBox;
    private readonly TextBox _sysDbBox, _appDbBox, _idBox, _loginWLinkBox;
    private readonly TextBox _programPathBox, _sourcePathBox, _mobilePathBox, _workingPathBox, _registryNameBox;
    private readonly CheckBox _integratedCheck;
    
    private readonly Label _statusLabel;
    private readonly PillButton _testBtn, _applyBtn, _saveBtn, _closeBtn;
    private readonly PillButton _newBtn, _deleteBtn;

    private readonly PillButton? _syncBtn;
    private readonly bool _allowSync;

    /// <param name="allowSync">true CHỈ khi mở từ màn hình Projects lúc mới mở Bcode: hiện khối "FSG — Đồng bộ dự án" và cho
    /// Ctrl+F5 — thêm/ghi đè project theo mã từ danh mục dự án FSG. Mở từ File &gt; Choose Server thì không có.</param>
    /// <param name="select">Project cần chọn sẵn ở danh sách bên trái (nút Edit của màn hình Projects).</param>
    /// <param name="startNew">Tạo ngay 1 project mới và đứng ở đó (nút New của màn hình Projects).</param>
    /// <param name="autoSync">Mở xong chạy luôn Synchronize (Ctrl+F5 bấm ngay từ màn hình Projects).</param>
    public ConnectionSettingsForm(AppSettings settings, DbConnectionService connections,
        bool allowSync = false, Workspace? select = null, bool startNew = false, bool autoSync = false)
    {
        _settings = settings;
        _connections = connections;
        _allowSync = allowSync;
        KeyPreview = true;

        Text = "Edit Project (Workspaces)";
        Width = 1120;
        Height = 760;
        MinimumSize = new Size(920, 640);
        StartPosition = FormStartPosition.CenterParent;
        
        // Cho phép Form co giãn và phóng to thu nhỏ thoải mái
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;

        // ---- Left: workspace list ----
        _list = new ListBox { Dock = DockStyle.Fill };
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        foreach (var ws in _settings.Workspaces) _list.Items.Add(ws);

        var leftButtonPanel = new FlowLayoutPanel 
        { 
            Dock = DockStyle.Bottom, 
            Height = 46, 
            FlowDirection = FlowDirection.LeftToRight, 
            Padding = new Padding(4)
        };
        _newBtn = PillButton.Flat("+ New");
        _newBtn.Click += (_, _) => AddNew();
        
        _deleteBtn = PillButton.Flat("Delete");
        _deleteBtn.ForeColor = AppColors.Danger;
        _deleteBtn.Click += (_, _) => DeleteSelected();
        
        leftButtonPanel.Controls.Add(_newBtn);
        leftButtonPanel.Controls.Add(_deleteBtn);

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 240, Padding = new Padding(10, 12, 4, 8) };
        leftPanel.Controls.Add(_list);
        leftPanel.Controls.Add(leftButtonPanel);
        _list.SendToBack();

        // ---- Right: scrollable page of cards (2 columns; FSG card full width underneath) ----
        // Trước đây mọi GroupBox xếp 1 cột dọc, cột nhãn AutoSize hẹp nên ô nhập bị bó, còn khối
        // FSG (list 140px + 4 hàng nút) đẩy phần cấu hình chính xuống dưới. Giờ: "Kết nối" +
        // "Database" bên trái, "Project" (đường dẫn) bên phải — cùng 1 tầm nhìn, không phải cuộn;
        // cột nhãn cố định 120px để mọi ô nhập thẳng hàng giữa các card.
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14, 12, 14, 8) };

        var page = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var connTable = NewFieldTable();
        _nameBox = AddRow(connTable, "Tên WS");
        _serverBox = AddRow(connTable, "Server Name");
        _integratedCheck = new CheckBox { Text = "Integrated Security (Windows Auth)", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 6) };
        _integratedCheck.CheckedChanged += (_, _) => ToggleAuthFields();
        AddFullWidthRow(connTable, _integratedCheck);
        _userBox = AddRow(connTable, "Login User");
        _passBox = AddRow(connTable, "Password");
        _passBox.UseSystemPasswordChar = true;
        var connGroup = NewCard("Kết nối", connTable);

        var dbTable = NewFieldTable();
        _sysDbBox = AddRow(dbTable, "Sys Data");
        _appDbBox = AddRow(dbTable, "App Data");
        var dbGroup = NewCard("Database", dbTable);

        var projTable = NewFieldTable();
        _idBox = AddRow(projTable, "ID");
        _loginWLinkBox = AddRow(projTable, "Login WLink");
        _programPathBox = AddRow(projTable, "Program Path");
        _sourcePathBox = AddRow(projTable, "Source Path");
        _sourcePathBox.PlaceholderText = @"UNC, dùng cho File Lookup — \server\...\FBISP23";
        _mobilePathBox = AddRow(projTable, "Mobile Path");
        _workingPathBox = AddRow(projTable, "Working Path");
        _registryNameBox = AddRow(projTable, "Registry Name");
        var projGroup = NewCard("Project", projTable);

        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(connGroup, 0, 0);
        page.Controls.Add(projGroup, 1, 0);
        page.SetRowSpan(projGroup, 2);
        page.Controls.Add(dbGroup, 0, 1);

        // ---- FSG — Đồng bộ dự án: chỉ có khi mở từ màn hình Projects lúc mới mở Bcode (xem tham số allowSync) ----
        if (allowSync)
        {
            _syncBtn = PillButton.Flat("⇅ Synchronize... (Ctrl+F5)");
            _syncBtn.Click += async (_, _) => await SynchronizeAsync();
            var syncRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
            syncRow.Controls.Add(_syncBtn);
            var menuBtn = PillButton.Flat("☰ Sync menu (ns_createCommand)");
            menuBtn.Click += async (_, _) => await CreateMenuAsync(menuBtn);
            syncRow.Controls.Add(menuBtn);
            syncRow.Controls.Add(new Label
            {
                Text = "Thêm dự án mới / ghi đè dự án đã có / chỉ 1 mã dự án — lấy từ danh mục dự án FSG.",
                AutoSize = true,
                Margin = new Padding(10, 9, 0, 0),
                ForeColor = AppColors.TextMuted,
            });
            var syncGroup = NewCard("FSG — Đồng bộ dự án", syncRow);
            syncGroup.Margin = new Padding(0, 0, 0, 10);
            page.Controls.Add(syncGroup, 0, 2);
            page.SetColumnSpan(syncGroup, 2);
        }

        scroll.Controls.Add(page);

        // ---- Bottom action bar panel ----
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        
        _statusLabel = new Label 
        { 
            Dock = DockStyle.Fill, 
            TextAlign = ContentAlignment.MiddleLeft, 
            ForeColor = AppColors.TextMuted,
            AutoEllipsis = true
        };

        var rightButtonFlow = new FlowLayoutPanel 
        { 
            Dock = DockStyle.Right, 
            AutoSize = true, 
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _testBtn = PillButton.Flat("Test Connection");
        _testBtn.Click += async (_, _) => await TestAsync();
        
        _closeBtn = PillButton.Flat("Đóng");
        _closeBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _applyBtn = PillButton.Flat("Apply");
        _applyBtn.Click += (_, _) => {
            SaveCurrentEdit();
            SetStatus("Đã lưu (Apply).", ok: true);
        };

        _saveBtn = PillButton.Flat("Save & Close", primary: true);
        _saveBtn.Click += (_, _) => {
            SaveAll();
            DialogResult = DialogResult.OK;
            Close();
        };

        rightButtonFlow.Controls.Add(_testBtn);
        rightButtonFlow.Controls.Add(_closeBtn);
        rightButtonFlow.Controls.Add(_applyBtn);
        rightButtonFlow.Controls.Add(_saveBtn);

        bottomBar.Controls.Add(_statusLabel);
        bottomBar.Controls.Add(rightButtonFlow);
        _statusLabel.SendToBack();

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(scroll);
        rightPanel.Controls.Add(bottomBar);
        scroll.SendToBack();

        Controls.Add(rightPanel);
        Controls.Add(leftPanel);

        ToggleAuthFields();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        if (select is not null && _list.Items.Contains(select)) _list.SelectedItem = select;
        if (startNew)
        {
            AddNew();
            _serverBox.Text = _settings.DefaultProjectServer;
            _integratedCheck.Checked = false;
            Shown += (_, _) => _idBox.Focus();
        }
        if (autoSync) Shown += async (_, _) => await SynchronizeAsync();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F5) && _allowSync)
        {
            _ = SynchronizeAsync();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Synchronize (Ctrl+F5): đồng bộ project từ database FSG_A (xem FsgProjectLookupService) với 3 chế độ — thêm 1 mã dự án /
    /// chỉ thêm dự án mới / ghi đè tất cả (đã có thì cập nhật, chưa có thì thêm). Kết quả nằm trong danh sách bên trái, còn phải bấm
    /// Apply / Save &amp; Close để lưu xuống file.
    /// </summary>
    private async Task SynchronizeAsync()
    {
        if (!_allowSync || _syncBtn is null || !_syncBtn.Enabled) return;

        var currentId = SelectedWorkspace is { } sel && !string.IsNullOrWhiteSpace(sel.ProjectId) ? sel.ProjectId : "";
        if (!AskSyncMode(currentId, out var mode, out var code)) return;

        if (mode == FsgProjectLookupService.SyncMode.OverwriteAll &&
            MessageBox.Show(this,
                "Ghi đè TẤT CẢ dự án đã có bằng thông tin từ FSG_A?\n\n" +
                "Chỉ ghi đè những giá trị FSG có; mật khẩu web, Mobile Path và cấu hình Profiler giữ nguyên.",
                "Synchronize — FSG", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var keepName = SelectedWorkspace?.Name;
        SaveCurrentEdit(); // chưa Apply mà đang sửa dở thì giữ lại trước khi danh sách bị nạp lại
        _syncBtn.Enabled = false;
        Cursor = Cursors.WaitCursor;
        SetStatus("Đang đồng bộ từ FSG_A...");
        try
        {
            var result = await new FsgProjectLookupService().SyncAsync(mode, code, _settings.Workspaces);
            if (!result.Success)
            {
                SetStatus(result.Error!, ok: false);
                return;
            }

            // Nạp lại danh sách bên trái từ _settings.Workspaces (đã thêm/cập nhật tại chỗ) và chọn lại project cũ / project vừa thêm.
            var select = mode == FsgProjectLookupService.SyncMode.AddOne
                ? _settings.Workspaces.FirstOrDefault(w => w.ProjectId.Equals(code, StringComparison.OrdinalIgnoreCase))
                : _settings.Workspaces.FirstOrDefault(w => w.Name == keepName);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var w in _settings.Workspaces) _list.Items.Add(w);
            _list.EndUpdate();
            if (select is not null) _list.SelectedItem = select;
            else if (_list.Items.Count > 0) _list.SelectedIndex = 0;

            SetStatus(result.Summary + (result.Notes.Count > 0 ? "  " + string.Join(" ", result.Notes) : "") + "  Bấm Apply/Save để lưu.", ok: true);
        }
        finally
        {
            Cursor = Cursors.Default;
            _syncBtn.Enabled = true;
        }
    }

    /// <summary>Sync menu: chạy <c>exec ns_createCommand N'mã dự án'</c> trên FSG_A cho project đang chọn (mã sửa được trong hộp
    /// xác nhận). Là lệnh ghi trên FSG_A nên luôn hỏi trước.</summary>
    private async Task CreateMenuAsync(Control button)
    {
        // Chạy luôn cho project đang chọn, không hỏi lại.
        var code = SelectedWorkspace is { } sel ? (string.IsNullOrWhiteSpace(sel.ProjectId) ? sel.Name : sel.ProjectId).Trim() : "";
        if (code.Length == 0) { SetStatus("Chưa chọn project nào.", ok: false); return; }

        button.Enabled = false;
        Cursor = Cursors.WaitCursor;
        SetStatus($"Đang chạy ns_createCommand cho \"{code}\"...");
        try
        {
            var (ok, message) = await new FsgProjectLookupService().CreateMenuAsync(code);
            SetStatus(message, ok);
        }
        finally
        {
            Cursor = Cursors.Default;
            button.Enabled = true;
        }
    }

    /// <summary>Hộp chọn chế độ Synchronize (3 lựa chọn + ô mã dự án cho chế độ 1).</summary>
    private bool AskSyncMode(string defaultCode, out FsgProjectLookupService.SyncMode mode, out string code)
    {
        using var dlg = new Form
        {
            Text = "Synchronize — FSG_A",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowIcon = false,
            ClientSize = new Size(520, 250),
        };
        var rbOne = new RadioButton { Text = "Thêm mới 1 mã dự án (ma_da)", Left = 16, Top = 16, Width = 480, Checked = true };
        var codeBox = new TextBox { Left = 40, Top = 44, Width = 300, Text = defaultCode, PlaceholderText = "Mã dự án" };
        var rbNew = new RadioButton { Text = "Chỉ sync dự án mới — chỉ thêm những project chưa có trong danh sách", Left = 16, Top = 84, Width = 490 };
        var rbAll = new RadioButton { Text = "Overwrite tất cả — project đã có thì cập nhật, chưa có thì thêm", Left = 16, Top = 114, Width = 490 };
        var note = new Label
        {
            Left = 16, Top = 148, Width = 488, Height = 48, ForeColor = AppColors.TextMuted,
            Text = "Nguồn: database FSG_A (chỉ đọc). Chế độ cập nhật chỉ ghi đè bằng những giá trị FSG có; mật khẩu web, Mobile Path và cấu hình Profiler được giữ nguyên.",
        };
        var ok = new Button { Text = "Đồng bộ", Left = 316, Top = 208, Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Hủy", Left = 414, Top = 208, Width = 90, DialogResult = DialogResult.Cancel };
        rbOne.CheckedChanged += (_, _) => codeBox.Enabled = rbOne.Checked;
        dlg.Controls.AddRange(new Control[] { rbOne, codeBox, rbNew, rbAll, note, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        ThemeManager.Apply(dlg);

        mode = FsgProjectLookupService.SyncMode.AddOne;
        code = "";
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;

        mode = rbOne.Checked ? FsgProjectLookupService.SyncMode.AddOne
             : rbNew.Checked ? FsgProjectLookupService.SyncMode.NewOnly
             : FsgProjectLookupService.SyncMode.OverwriteAll;
        code = codeBox.Text.Trim();
        if (mode == FsgProjectLookupService.SyncMode.AddOne && code.Length == 0)
        {
            SetStatus("Chưa nhập mã dự án.", ok: false);
            return false;
        }
        return true;
    }

    private void SetStatus(string text, bool? ok = null)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = ok is null ? AppColors.TextMuted : ok.Value ? AppColors.Success : AppColors.Danger;
    }

    /// <summary>Bảng nhãn + ô nhập: cột nhãn rộng cố định (mọi card thẳng hàng), ô nhập co giãn hết phần còn lại.</summary>
    private static TableLayoutPanel NewFieldTable()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    /// <summary>Một "card" của trang: GroupBox tự co theo nội dung, chiếm trọn ô lưới, cách đều các card khác.</summary>
    private static GroupBox NewCard(string title, Control content)
    {
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 6, 12, 12),
            Margin = new Padding(0, 0, 10, 10),
        };
        box.Controls.Add(content);
        return box;
    }

    private static TextBox AddRow(TableLayoutPanel table, string label)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, row);
        
        // Đặt Anchor Left | Right để ô TextBox tự động co giãn theo chiều ngang của bảng
        var box = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 0, 4) };
        table.Controls.Add(box, 1, row);
        return box;
    }

    private static void AddFullWidthRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private void ToggleAuthFields()
    {
        _userBox.Enabled = !_integratedCheck.Checked;
        _passBox.Enabled = !_integratedCheck.Checked;
    }

    private Workspace? SelectedWorkspace => _list.SelectedItem as Workspace;

    private void AddNew()
    {
        var ws = new Workspace { Name = $"WS{_settings.Workspaces.Count + 1}" };
        _settings.Workspaces.Add(ws);
        _list.Items.Add(ws);
        _list.SelectedItem = ws;
    }

    private void DeleteSelected()
    {
        if (SelectedWorkspace is not { } ws) return;
        _settings.Workspaces.Remove(ws);
        _list.Items.Remove(ws);
    }

    private void LoadSelected()
    {
        if (SelectedWorkspace is not { } ws) return;
        _nameBox.Text = ws.Name;
        _serverBox.Text = ws.Server;
        _integratedCheck.Checked = ws.IntegratedSecurity;
        _userBox.Text = ws.User;
        _passBox.Text = ws.Password;
        _sysDbBox.Text = ws.SysDatabase;
        _appDbBox.Text = ws.AppDatabase;
        _idBox.Text = ws.ProjectId;
        _loginWLinkBox.Text = ws.LoginWLink;
        _programPathBox.Text = ws.ProgramPath;
        _sourcePathBox.Text = ws.SourcePath;
        _mobilePathBox.Text = ws.MobilePath;
        _workingPathBox.Text = ws.WorkingPath;
        _registryNameBox.Text = ws.RegistryName;
        SetStatus("");
    }

    private void SaveCurrentEdit()
    {
        if (SelectedWorkspace is not { } ws) return;
        ws.Name = _nameBox.Text.Trim();
        ws.Server = _serverBox.Text.Trim();
        ws.IntegratedSecurity = _integratedCheck.Checked;
        ws.User = _userBox.Text.Trim();
        ws.Password = _passBox.Text;
        ws.SysDatabase = _sysDbBox.Text.Trim();
        ws.AppDatabase = _appDbBox.Text.Trim();
        ws.ProjectId = _idBox.Text.Trim();
        ws.LoginWLink = _loginWLinkBox.Text.Trim();
        ws.ProgramPath = _programPathBox.Text.Trim();
        ws.SourcePath = _sourcePathBox.Text.Trim();
        ws.MobilePath = _mobilePathBox.Text.Trim();
        ws.WorkingPath = _workingPathBox.Text.Trim();
        ws.RegistryName = _registryNameBox.Text.Trim();

        var idx = _list.SelectedIndex;
        if (idx >= 0) _list.Items[idx] = ws;
    }

    private void SaveAll()
    {
        SaveCurrentEdit();
        _settings.Save();
    }

    private async Task TestAsync()
    {
        SaveCurrentEdit();
        if (SelectedWorkspace is not { } ws)
        {
            SetStatus("Chưa chọn Workspace nào.", ok: false);
            return;
        }

        _testBtn.Enabled = false;
        SetStatus("Đang kiểm tra kết nối...");
        try
        {
            var (sysOk, sysMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: true);
            var (appOk, appMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: false);

            SetStatus($"Sys Data: {sysMsg}   |   App Data: {appMsg}", ok: sysOk && appOk);
        }
        catch (Exception ex)
        {
            SetStatus($"Lỗi: {ex.Message}", ok: false);
        }
        finally
        {
            _testBtn.Enabled = true;
        }
    }
}