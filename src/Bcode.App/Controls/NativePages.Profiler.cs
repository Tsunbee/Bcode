using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Thanh nút của SQL Profiler (sqlprofilerbar.html): đường dẫn Profiler.exe + đăng nhập + Trace Template, bung/đóng Profiler, tra u_id,
/// gợi ý lọc DatabaseName, file trace. 3 hàng thẳng cột (NK.GridRow), thanh tự cao theo nội dung (WebBarHost.FitNative).</summary>
internal sealed class SqlProfilerBarPage : NativePage
{
    private readonly RoundedInput _exe = NK.Input("Đường dẫn Profiler.exe", mono: true), _user = NK.Input(), _pass = NK.Input(), _template = NK.Input(),
        _target = NK.Input("u_name"), _uid = NK.Input("", mono: true), _db = NK.Input("(chưa có Workspace)", mono: true);
    private readonly Label _dbLbl = Lbl("Lọc DatabaseName gợi ý"), _traceFile = Hint("(chưa có Workspace)"), _status = NK.Muted("Chưa chạy Profiler.");
    private readonly PillButton _bClose;
    private readonly ToolTip _tip = new();
    private bool _uidManual, _settingUid;
    // Ô Lọc DatabaseName: _dbSuggested = gợi ý theo workspace, _dbSent = giá trị C# đang dùng (so để biết người dùng có sửa không).
    private string _dbSuggested = "", _dbSent = "";

    public SqlProfilerBarPage()
    {
        _pass.Inner.UseSystemPasswordChar = true;
        // Ô u_id sửa / xoá được: người dùng tự đổi (_uidManual) thì Bcode dùng đúng giá trị đó, trống = không lọc ID; Tra u_id / đổi tên user = quay về tự tra.
        _uid.Inner.TextChanged += (_, _) => { if (!_settingUid) _uidManual = true; };
        _target.Inner.TextChanged += (_, _) => _uidManual = false;
        _tip.SetToolTip(_uid.Inner, "u_id dùng làm bộ lọc ApplicationName (= ID) của trace. Để TRỐNG = không lọc theo ID, chỉ lọc theo Database.");
        foreach (var i in new[] { _exe, _user, _pass, _template }) i.Inner.Leave += (_, _) => SaveConfig();
        _template.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; SaveConfig(); } };
        _target.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Lookup(); } };
        // Ô Lọc DatabaseName sửa được: rời ô / Enter thì C# nhớ theo workspace (trống = về gợi ý), Esc = bỏ sửa; Copy và Bung Profiler dùng giá trị đang hiện.
        _tip.SetToolTip(_db.Inner, "Bộ lọc DatabaseName (Like) của trace — tự gợi ý %mã dự án% theo workspace. Sửa được (vd %KOG_FBISP24% hoặc đúng tên database): Bcode nhớ theo workspace, dùng cho Copy và filter khi Bung Profiler. Để trống hoặc bấm ⟳ = về giá trị gợi ý.");
        _db.Inner.Leave += (_, _) => SaveDb();
        _db.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; SaveDb(); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; _db.Text = _dbSent; }
        };

        var bRun = NK.Button("▶ Bung & Đăng nhập", () => Post(new { action = "run", exePath = _exe.Text, loginUser = _user.Text, loginPass = _pass.Text, template = _template.Text, targetUser = _target.Text, uid = _uid.Text, uidManual = _uidManual, dbLike = _db.Text }), primary: true);
        _bClose = NK.Button("✕ Đóng Profiler", () => Post(new { action = "close" })); _bClose.Enabled = false;
        _traceFile.AutoSize = false; _traceFile.AutoEllipsis = true; _traceFile.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoSize = false; _status.AutoEllipsis = true; _status.TextAlign = ContentAlignment.MiddleLeft;

        // 3 hàng thẳng cột (NK.GridRow: nhãn / nút vừa chữ, ô nhập chia phần còn lại) — trước đây là 1 FlowLayout tự xuống dòng nên nút / ô lộn xộn.
        var row1 = NK.GridRow(8,
            (Lbl("Profiler.exe"), 0), (_exe, 44), (NK.Button("…", () => Post(new { action = "browse" }), tip: "Chọn file..."), 0),
            (Lbl("Login user"), 0), (_user, 14), (Lbl("Pass"), 0), (_pass, 12), (Lbl("Trace Template"), 0), (_template, 18),
            (NK.Button("▴", () => Post(new { action = "hide-bar" }), tip: "Ẩn thanh này cho Profiler rộng hơn — bấm \"▾ Hiện thanh Profiler\" ở dải trên cùng để hiện lại"), 0));
        var row2 = NK.GridRow(8,
            (Lbl("User theo dõi (u_name)"), 0), (_target, 16), (NK.Button("Tra u_id", Lookup), 0),
            (Lbl("→ u_id"), 0), (_uid, 12),
            (NK.Button("✕", () => { _uid.Text = ""; _uidManual = true; Post(new { action = "uid-cleared" }); }, tip: "Xoá u_id — trace không lọc theo ID (ApplicationName)"), 0),
            (NK.Button("Copy", () => Post(new { action = "copy", value = _uid.Text })), 0),
            (Sep(), 0), (_dbLbl, 0), (_db, 16), (NK.Button("Copy", () => Post(new { action = "copy", value = DbValue() })), 0),
            (NK.Button("⟳", () => Post(new { action = "refresh-hint" }), tip: "Về giá trị gợi ý theo workspace"), 0));
        var row3 = NK.GridRow(8,
            (bRun, 0), (_bClose, 0), (Sep(), 0),
            (NK.Button("📋 Copy SQL Command", () => Post(new { action = "copy-sql" }), tip: "Copy câu SQL (TextData) của dòng đang chọn trong Profiler để dán sang SQL Query chạy"), 0),
            (NK.Button("🐞 Debug store", () => Post(new { action = "debug-sql" }), tip: "Debug store được gọi trong dòng đang chọn: mở định nghĩa store + câu gọi này rồi bật Debug từng bước (tham số, bảng tạm lấy từ câu gọi)"), 0),
            (NK.Button("📂 Mở thư mục Trace", () => Post(new { action = "open-trace-folder" }), tip: "Mở thư mục chứa file trace"), 0),
            (Lbl("File trace"), 0), (_traceFile, 60), (_status, 40));
        NK.Stack(this, NK.Padded(row1, 12, 8, 12, 4), NK.Padded(row2, 12, 4, 12, 4), NK.Padded(row3, 12, 4, 12, 8));
    }

    private static Label Lbl(string text) => NK.Muted(text);
    private static Label Hint(string text) => new() { Text = text, AutoSize = true, BackColor = Color.Transparent, Font = ThemeManager.MonoFont, UseMnemonic = false };
    private static Control Sep() => new Panel { Width = 1, Height = NativeSkin.S(22), BackColor = AppColors.Border };

    private void SaveConfig() => Post(new { action = "save-config", exePath = _exe.Text, loginUser = _user.Text, loginPass = _pass.Text, template = _template.Text });
    private void Lookup() => Post(new { action = "lookup-uid", targetUser = _target.Text });
    private string DbValue() => _db.Text.Trim().Length > 0 ? _db.Text.Trim() : _dbSuggested;
    private void SaveDb()
    {
        var v = _db.Text.Trim();
        if (v == _dbSent) return;
        _dbSent = v;
        Post(new { action = "set-db-filter", value = v });
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.setConfig":
            {
                var c = Arg(a, 0);
                _exe.Text = P(c, "exePath"); _user.Text = P(c, "loginUser"); _pass.Text = P(c, "loginPass"); _template.Text = P(c, "template");
                if (P(c, "targetUser").Length > 0 && _target.Text.Length == 0) _target.Text = P(c, "targetUser");
                var tf = P(c, "traceFile");
                _traceFile.Text = tf.Length > 0 ? tf : "(chưa có Workspace)"; _tip.SetToolTip(_traceFile, tf);
                break;
            }
            case "window.setStatus": _status.Text = Str(a, 0); break;
            case "window.setUid": _settingUid = true; _uid.Text = Str(a, 0); _settingUid = false; _uidManual = false; break;
            case "window.setDbHint":
            {
                // (giá trị đang dùng, gợi ý theo workspace) — nhãn ghi "đã sửa" khi khác gợi ý.
                var suggested = Str(a, 1);
                _dbSuggested = suggested.StartsWith("(") ? "" : suggested;
                _db.PlaceholderText = suggested;
                _db.Text = _dbSent = Str(a, 0);
                var custom = _dbSent.Length > 0 && !string.Equals(_dbSent, _dbSuggested, StringComparison.OrdinalIgnoreCase);
                _dbLbl.Text = custom ? "Lọc DatabaseName (đã sửa)" : "Lọc DatabaseName gợi ý";
                break;
            }
            case "window.setRunning": _bClose.Enabled = Bool(a, 0); break;
        }
    }
}
