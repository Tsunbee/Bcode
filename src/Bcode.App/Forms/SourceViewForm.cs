using Bcode.App.Controls;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Cửa sổ xem nhanh (chỉ đọc) 1 file hoặc 1 giá trị entity, dùng chung khung xem của File Lookup (<see cref="MonacoPreviewControl"/>: Monaco ở chế độ Web,
/// Scintilla ở chế độ App, tô FCode như BcodeViewer). Dùng cho:
/// - "View source" của Cấp source / Add Source (file trong kho SourceCollection sắp được cấp);
/// - F12 trong File Lookup / trong chính cửa sổ này: entity SYSTEM / đường dẫn Include → file đó; entity giá trị → nội dung khai báo (<see cref="OpenSnippet"/>).
/// F12 luôn mở CỬA SỔ MỚI lệch xuống 30px (cửa sổ đang xem giữ nguyên). File không có trong source thì lấy bản ở Program Path của dự án đang chọn
/// (<see cref="ScriptEditorControl.ProgramFallback"/>). Cửa sổ độc lập, có nút trên taskbar: Alt+Tab qua lại với Bcode được. Esc đóng, Ctrl+F tìm.
/// </summary>
public sealed class SourceViewForm : ThemedForm
{
    private readonly MonacoPreviewControl _view = new();
    private readonly Label _info = new() { Dock = DockStyle.Top, AutoSize = false, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0), AutoEllipsis = true };
    private readonly ToolTip _tip = new();
    private readonly string? _note;
    private readonly Action<List<EntityPreviewItem>>? _multiPeek;
    private string? _basePath;   // file đang xem (hoặc file khai báo entity đang xem)

    /// <param name="note">Ghi chú hiện trước đường dẫn ở dòng trên cùng (vd phiên bản lấy source).</param>
    /// <param name="multiPeek">F12 khi bôi đen nhiều entity (cửa sổ xem lần lượt — cần cài đặt của nơi gọi); null = bỏ qua.</param>
    private SourceViewForm(string? note, Action<List<EntityPreviewItem>>? multiPeek)
    {
        _note = note;
        _multiPeek = multiPeek;
        Width = 1000;
        Height = 700;
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { ShowIcon = false; }
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
            else if (e.Control && e.KeyCode == Keys.F) { e.Handled = e.SuppressKeyPress = true; _view.ShowFind(); }
        };
        // BeginInvoke: F12 đến từ callback của trang WebView2 — tạo cửa sổ có WebView2 mới NGAY trong callback đó dễ lỗi E_ABORT.
        _view.EntityNavigationRequested += p => BeginInvoke(new Action(() =>
        {
            if (!File.Exists(p)) return;
            // Đang xem file ngoài Program Path (kho source) mà F12 phải lấy bản trong Program Path → ghi rõ.
            var note = _basePath is not null && !ScriptEditorControl.IsInProgram(_basePath) && ScriptEditorControl.IsInProgram(p) ? "Program Path của dự án đang chọn (không có trong source)" : _note;
            Open(p, note, this, cascade: true, _multiPeek);
        }));
        _view.EntityValuePeekRequested += (name, value, declaringPath) => BeginInvoke(new Action(() => OpenSnippet(name, value, declaringPath, this, cascade: true, _multiPeek)));
        _view.EntityMultiPeekRequested += items => { if (_multiPeek is { } mp) BeginInvoke(new Action(() => mp(items))); };
        Controls.Add(_view);
        Controls.Add(_info);
        FormClosed += (_, _) => _tip.Dispose();
    }

    /// <summary>Mở cửa sổ xem file <paramref name="path"/> — KHÔNG gắn owner (cửa sổ độc lập, Alt+Tab được); đặt giữa <paramref name="near"/>,
    /// hoặc lệch xuống 30px so với nó (<paramref name="cascade"/>: mở từ F12 trong 1 cửa sổ xem khác).</summary>
    public static SourceViewForm Open(string path, string? note, Form? near, bool cascade = false, Action<List<EntityPreviewItem>>? multiPeek = null)
    {
        var f = new SourceViewForm(note, multiPeek);
        f.ShowNear(near, cascade);
        f.LoadFile(path);   // sau khi hiện (đã có handle) — đọc file ở nền rồi BeginInvoke về
        return f;
    }

    /// <summary>Mở cửa sổ xem GIÁ TRỊ của entity <paramref name="name"/> (khai báo trong <paramref name="declaringPath"/>) — F12 bên trong tiếp tục tra từ file khai báo.</summary>
    public static SourceViewForm OpenSnippet(string name, string value, string declaringPath, Form? near, bool cascade = false, Action<List<EntityPreviewItem>>? multiPeek = null)
    {
        var f = new SourceViewForm(null, multiPeek);
        f.Text = "&" + name + "; — " + Path.GetFileName(declaringPath);
        f._info.Text = "&" + name + ";  ·  khai báo tại " + declaringPath + "  ·  " + value.Split('\n').Length + " dòng";
        f._tip.SetToolTip(f._info, declaringPath);
        // Đường dẫn giả đuôi .txt: khung xem tô màu theo nội dung (SQL / JS / XML của FCode) như file .txt include.
        f._basePath = declaringPath;
        f._view.LoadContent("&" + name + ";.txt", value, declaringPath);
        return f.ShowNear(near, cascade);
    }

    private SourceViewForm ShowNear(Form? near, bool cascade)
    {
        var area = Screen.FromControl((Control?)near ?? this).WorkingArea;
        Point loc;
        if (near is null) loc = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        else if (cascade) { Size = near.Size; loc = new Point(near.Left + 30, near.Top + 30); }
        else loc = new Point(near.Left + (near.Width - Width) / 2, near.Top + (near.Height - Height) / 2);
        Location = new Point(Math.Clamp(loc.X, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(loc.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        Show();
        Activate();
        return this;
    }

    private void LoadFile(string path)
    {
        _basePath = path;
        Text = Path.GetFileName(path);
        string modified = "";
        try { modified = " · sửa " + File.GetLastWriteTime(path).ToString("dd/MM/yyyy HH:mm"); } catch { }
        _info.Text = (string.IsNullOrEmpty(_note) ? "" : _note + " · ") + path + modified;
        _tip.SetToolTip(_info, path);
        _view.LoadContent(null, "Đang tải...");
        // Đọc file ở nền (ổ mạng chậm thì cửa sổ vẫn hiện ngay).
        Task.Run(() =>
        {
            string? content = null; Exception? error = null;
            try { content = new ScriptFileService().ReadFile(path); } catch (Exception ex) { error = ex; }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    if (error is null) _view.LoadContent(path, content!);
                    else _view.LoadContent(null, "Không đọc được file:\r\n" + error.Message);
                }));
            }
            catch (InvalidOperationException) { /* cửa sổ đã đóng / chưa có handle */ }
        });
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _info.ForeColor = AppColors.TextMuted;
    }
}
