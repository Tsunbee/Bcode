using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Tab SqlWatch — Ctrl+3 khi Template → Cửa sổ → SQL Profiler chọn công cụ "SqlWatch" (AppSettings.ProfilerEngine).
/// Nhúng nguyên form của src/SqlWatch (link mã, TopLevel = false): đọc DMV theo nhịp thay cho trace của Profiler.exe.
/// Project = workspace đang chọn ở Bcode (đổi workspace khi chưa bắt thì đổi theo); chế độ Tự động = Ctrl+3 bấm "Bắt đầu" luôn.
/// Màu: SqlWatch tự tô theo bảng màu Bcode truyền vào (ApplyTheme) — IThemeOptOut để ThemeManager.Apply không tô đè từng control.</summary>
public sealed class SqlWatchControl : UserControl, IThemeOptOut
{
    private readonly AppSettings _settings;
    private readonly DbConnectionService _connections;
    private readonly SqlWatch.MainForm _form;
    private bool _loaded, _pendingAuto;

    public SqlWatchControl(AppSettings settings, DbConnectionService connections)
    {
        _settings = settings;
        _connections = connections;
        Dock = DockStyle.Fill;

        // SqlWatch đọc danh sách project từ settings.json của Bcode — chỉ đúng file Bcode đang dùng (BCODE_DATA_DIR...).
        SqlWatch.BcodeCatalog.SettingsPathOverride = Path.Combine(BcodePaths.AppData, "Bcode", "settings.json");
        _form = new SqlWatch.MainForm { TopLevel = false, FormBorderStyle = FormBorderStyle.None, MinimumSize = Size.Empty, Dock = DockStyle.Fill };
        // Nút "Copy SQL Command" / "Debug store" của SqlWatch → cùng xử lý như thanh SQL Profiler (MainForm.OpenSqlWatchTab nối sự kiện).
        _form.OpenInSqlQuery = (sql, database) => OpenInSqlQueryRequested?.Invoke(sql, database);
        _form.DebugStore = sql => DebugRequested?.Invoke(sql);
        // Login SQL sửa trên tab (dùng bên khách hàng) nhớ theo workspace — mật khẩu ghi settings.json dạng DPAPI.
        _form.LoginLookup = name => _settings.Workspaces.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } w
            ? (w.SqlWatchUser, w.SqlWatchPassword) : null;
        _form.LoginSaved = (name, user, password) =>
        {
            if (_settings.Workspaces.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } w) return;
            if (w.SqlWatchUser == user && w.SqlWatchPassword == password) return;
            w.SqlWatchUser = user;
            w.SqlWatchPassword = password;
            try { _settings.Save(); } catch { /* chỉ nhớ trong phiên */ }
        };
        // Bộ lọc (Login chứa, USER ID, Nội dung chứa, Sự kiện) nhớ theo workspace; chưa lưu lần nào thì USER ID tự điền u_id của user
        // đang theo dõi ở SQL Profiler (ô "User cần theo dõi"), không có thì user đăng nhập web của workspace.
        _form.FiltersLookup = name => FindWorkspace(name)?.SqlWatchFilters is { } f ? new SqlWatch.WatchFilters(f.Login, f.UserId, f.Text, f.Events) : null;
        _form.FiltersSaved = (name, f) =>
        {
            if (FindWorkspace(name) is not { } w) return;
            w.SqlWatchFilters = new SqlWatchFilterState { Login = f.Login, UserId = f.UserId, Text = f.Text, Events = f.Events.ToList() };
            try { _settings.Save(); } catch { /* chỉ nhớ trong phiên */ }
        };
        _form.UserIdLookup = LookupUserIdAsync;
        // Chấm trên icon tab: đỏ = đang bắt, xám = tạm dừng, không chấm = đã dừng (nhìn thanh tab là biết SqlWatch còn chạy không).
        _form.CaptureStateChanged += () =>
        {
            if (Parent is TabPage page)
                ThemeManager.SetTabLive(page, !_form.IsCapturing ? ThemeManager.TabLive.None
                    : _form.IsPaused ? ThemeManager.TabLive.Paused : ThemeManager.TabLive.Running);
        };
        // Load của form (nạp project từ settings) chạy trước handler này → chọn workspace hiện tại rồi mới tự bắt nếu đang chờ.
        _form.Load += (_, _) =>
        {
            _loaded = true;
            SyncProject();
            if (_pendingAuto) { _pendingAuto = false; _ = _form.StartCaptureAsync(); }
        };
        Controls.Add(_form);
        ApplyCopyColor();
        CopyColorChanged += ApplyCopyColor;
        ApplyTheme();
        // Thanh cuộn lưới / khung Chi tiết theo sáng / tối (NK.DarkScroll tự theo dõi ThemeChanged).
        foreach (var c in Descendants(_form))
            if (c is DataGridView or TextBoxBase or ScrollBar) NK.DarkScroll(c);
        _form.Show();

        _connections.WorkspaceChanged += SyncProject;
        ThemeManager.ThemeChanged += ApplyTheme;
        Disposed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= ApplyTheme;
            CopyColorChanged -= ApplyCopyColor;
            _connections.WorkspaceChanged -= SyncProject;
            _form.FlushFilters();   // đóng tab ngay sau khi gõ lọc: lưu nốt (đóng tab không qua FormClosing của SqlWatch)
            _form.StopCapture();
            _form.Dispose();
        };
    }

    /// <summary>"📋 Copy SQL Command": câu SQL của dòng đang chọn (dòng SP:Starting thì cả batch có declare) — mở tab SQL Query mới dán sẵn, đúng database của dòng (cột Database).</summary>
    public event Action<string, string>? OpenInSqlQueryRequested;

    /// <summary>"🐞 Debug store": cùng câu SQL đó — MainForm tìm store được gọi, mở định nghĩa + GO + câu gọi rồi bật Debug từng bước.</summary>
    public event Action<string>? DebugRequested;

    /// <summary>Template → Cửa sổ đổi màu nút Copy SQL Command (AppSettings.SqlWatchCopyColor) — tab SqlWatch đang mở tô lại ngay.</summary>
    public static event Action? CopyColorChanged;
    public static void RaiseCopyColorChanged() => CopyColorChanged?.Invoke();

    /// <summary>"" = nút thường; mã màu sai thì về mặc định #80d3a1.</summary>
    private void ApplyCopyColor()
    {
        var hex = _settings.SqlWatchCopyColor?.Trim() ?? "";
        _form.CopyHighlight = hex.Length == 0 ? null : UiTemplate.ParseColor(hex) ?? ColorTranslator.FromHtml("#80d3a1");
    }

    private Workspace? FindWorkspace(string name) =>
        _settings.Workspaces.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>u_id (vsysuser ở App Data) của user đang theo dõi: ô "User cần theo dõi" của SQL Profiler, không có thì user đăng nhập web.
    /// Dùng kết nối của chính workspace (login dự án có quyền vào App Data, khác login profile của SqlWatch).</summary>
    private static async Task<string?> LookupUserIdAsync(Workspace? ws)
    {
        if (ws is null) return null;
        var user = !string.IsNullOrWhiteSpace(ws.ProfilerTargetUser) ? ws.ProfilerTargetUser.Trim() : ws.WebLoginUser.Trim();
        if (user.Length == 0) return null;
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(ws.BuildConnectionString(useSysDatabase: false));
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT u_id FROM vsysuser WHERE u_name = @name", conn) { CommandTimeout = 10 };
        cmd.Parameters.AddWithValue("@name", user);
        return (await cmd.ExecuteScalarAsync()) is { } v and not DBNull ? v.ToString() : null;
    }

    private Task<string?> LookupUserIdAsync(string name) => LookupUserIdAsync(FindWorkspace(name));

    /// <summary>Cho MainForm báo kết quả Debug store lên thanh trạng thái của SqlWatch.</summary>
    public void ReportStatus(string text) => _form.ReportStatus(text);

    /// <summary>Bảng màu SqlWatch theo theme Bcode đang dùng (giữ bố cục / màu nhấn riêng của SqlWatch, nền + chữ + viền theo Bcode).</summary>
    private void ApplyTheme()
    {
        var dark = AppColors.IsDark;
        BackColor = AppColors.Background;
        _form.ApplyPalette(new SqlWatch.Palette(
            Page: AppColors.Background,
            Surface: AppColors.Panel,
            Header: AppColors.PanelAlt,
            Ink: AppColors.Text,
            // TextMuted của Bcode tối (150) trên nền xám gần như chìm — SqlWatch dùng nhãn nhiều nên nâng sáng hơn.
            Muted: dark ? Blend(AppColors.TextMuted, AppColors.Text) : AppColors.TextMuted,
            Line: AppColors.Border,
            Accent: AppColors.Accent,
            AccentHover: AppColors.AccentHover,
            OnAccent: AppColors.OnAccent,
            Input: AppColors.Input,
            ReadOnly: AppColors.PanelAlt,
            Button: AppColors.ButtonBack,
            ButtonHover: AppColors.Selection,
            Selected: AppColors.Selection,
            AltRow: Blend(AppColors.Panel, AppColors.Background),
            Rpc: dark ? Color.FromArgb(120, 170, 255) : Color.FromArgb(30, 78, 140),
            Sp: dark ? Color.FromArgb(110, 205, 150) : Color.FromArgb(14, 110, 86)));
    }

    private static Color Blend(Color a, Color b) => Color.FromArgb((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2);

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private void SyncProject()
    {
        if (_loaded && _connections.Current is { } ws) _form.SelectProject(ws.Name);
    }

    /// <summary>Chế độ Tự động (AppSettings.ProfilerAutoStart): bắt đầu bắt câu SQL luôn; Thủ công / đang bắt → không làm gì.</summary>
    public void AutoStartIfEnabled()
    {
        if (!_settings.ProfilerAutoStart || _form.IsCapturing) return;
        if (_loaded) _ = _form.StartCaptureAsync();
        else _pendingAuto = true;
    }
}
