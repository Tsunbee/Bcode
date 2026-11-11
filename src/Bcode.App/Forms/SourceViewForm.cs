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
    private readonly string? _sourceVersion;   // xem file trong kho source (SourceCollection) của phiên bản này → giấu đường dẫn kho
    private readonly Action<List<EntityPreviewItem>>? _multiPeek;
    private string? _basePath;   // file đang xem (hoặc file khai báo entity đang xem)

    /// <param name="note">Ghi chú hiện trước đường dẫn ở dòng trên cùng (vd phiên bản lấy source). Có <paramref name="sourceVersion"/> thì là
    /// phần chú thích thêm sau "View source &lt;phiên bản&gt;" (vd "(thấp hơn gần nhất)").</param>
    /// <param name="sourceVersion">File nằm trong kho source của phiên bản này: KHÔNG hiện đường dẫn kho (ổ mạng nội bộ) ở dòng trên cùng / tooltip —
    /// chỉ "View source &lt;phiên bản&gt; · &lt;đường dẫn tính từ thư mục phiên bản&gt;".</param>
    /// <param name="multiPeek">F12 khi bôi đen nhiều entity (cửa sổ xem lần lượt — cần cài đặt của nơi gọi); null = bỏ qua.</param>
    private SourceViewForm(string? note, Action<List<EntityPreviewItem>>? multiPeek, string? sourceVersion = null)
    {
        _note = note;
        _sourceVersion = string.IsNullOrWhiteSpace(sourceVersion) ? null : sourceVersion;
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
            var toProgram = _basePath is not null && !ScriptEditorControl.IsInProgram(_basePath) && ScriptEditorControl.IsInProgram(p);
            // Đang xem kho source: file F12 tới vẫn trong kho (cùng thư mục phiên bản) thì tiếp tục giấu đường dẫn; ra Program Path dự án thì hiện như cũ.
            var version = _sourceVersion is not null && !toProgram && RelativeToVersion(p, _sourceVersion) is not null ? _sourceVersion : null;
            var note = toProgram ? "Program Path của dự án đang chọn (không có trong source)" : version is not null ? null : _note;
            Open(p, note, this, cascade: true, _multiPeek, version);
        }));
        _view.EntityValuePeekRequested += (name, value, declaringPath) => BeginInvoke(new Action(() => OpenSnippet(name, value, declaringPath, this, cascade: true, _multiPeek)));
        _view.EntityMultiPeekRequested += items => { if (_multiPeek is { } mp) BeginInvoke(new Action(() => mp(items))); };
        Controls.Add(_view);
        Controls.Add(_info);
        FormClosed += (_, _) => _tip.Dispose();
    }

    /// <summary>Mở cửa sổ xem file <paramref name="path"/> — KHÔNG gắn owner (cửa sổ độc lập, Alt+Tab được); đặt giữa <paramref name="near"/>,
    /// hoặc lệch xuống 30px so với nó (<paramref name="cascade"/>: mở từ F12 trong 1 cửa sổ xem khác).</summary>
    /// <param name="sourceVersion">Xem file trong kho source của phiên bản này — giấu đường dẫn kho (xem constructor).</param>
    public static SourceViewForm Open(string path, string? note, Form? near, bool cascade = false, Action<List<EntityPreviewItem>>? multiPeek = null, string? sourceVersion = null)
    {
        var f = new SourceViewForm(note, multiPeek, sourceVersion);
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

    /// <summary>Xem BẢN GIẢI MÃ của file controller .f (khối &lt;Encrypted&gt;) — chỉ xem: không ghi ra đĩa, không đụng file / thư mục dự án.
    /// <paramref name="decrypt"/> chạy ở nền (gọi helper Bcode.FDecrypt), trả nội dung đã giải hoặc lý do không giải được. F12 tra entity theo thư mục file gốc.</summary>
    public static SourceViewForm OpenDecrypted(string path, Func<(string? Text, string? Error)> decrypt, Form? near)
    {
        var f = new SourceViewForm(null, null);
        f.ShowNear(near, cascade: false);
        f._basePath = path;
        f.Text = Path.GetFileName(path) + " — bản giải mã";
        f._info.Text = "Bản giải mã (chỉ xem, không lưu) · " + path;
        f._tip.SetToolTip(f._info, path);
        f._view.LoadContent(null, "Đang giải mã " + Path.GetFileName(path) + "...");
        Task.Run(() =>
        {
            (string? Text, string? Error) r;
            try { r = decrypt(); } catch (Exception ex) { r = (null, ex.Message); }
            try
            {
                f.BeginInvoke(new Action(() =>
                {
                    if (f.IsDisposed) return;
                    if (r.Text is { } text)
                    {
                        f._info.Text = "Bản giải mã (chỉ xem, không lưu) · " + path + " · " + text.Split('\n').Length + " dòng";
                        f._view.LoadContent(path, text);
                    }
                    else f._view.LoadContent(null, "Không giải mã được " + Path.GetFileName(path) + ":\r\n" + r.Error);
                }));
            }
            catch (InvalidOperationException) { /* cửa sổ đã đóng / chưa có handle */ }
        });
        return f;
    }

    /// <summary>Mở cửa sổ xem 1 GIÁ TRỊ bất kỳ (vd ô kết quả SQL Query — phím result.viewCell / Ctrl+F3): đủ chuỗi, chỉ đọc, Ctrl+F tìm, chọn copy được.
    /// Tô màu theo nội dung: JSON / HTML / XML nhận ra ở đây, còn lại đuôi .txt (khung xem tự tô SQL / JS / FCode như file include).</summary>
    public static SourceViewForm OpenValue(string title, string info, string value, Form? near)
    {
        var f = new SourceViewForm(null, null);
        f.Text = title;
        f._info.Text = info;
        f._tip.SetToolTip(f._info, info);
        f._view.LoadContent(GuessValueName(value), value);
        return f.ShowNear(near, cascade: false);
    }

    private static readonly System.Text.RegularExpressions.Regex HtmlStart = new(
        @"^<(!doctype\s+html|html|head|body|div|span|table|thead|tbody|tr|td|th|p|a|ul|ol|li|script|style|form|input|button|label|select|br|hr|img|h[1-6]|b|i|u|strong|em|section|header|footer|nav|iframe|font|center)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Tên giả (chỉ để chọn ngôn ngữ tô màu) cho <see cref="OpenValue"/>.</summary>
    private static string GuessValueName(string value)
    {
        var s = value.TrimStart();
        if (s.StartsWith('{') || s.StartsWith('['))
        {
            try { using var _ = System.Text.Json.JsonDocument.Parse(value); return "value.json"; } catch { /* không phải JSON */ }
        }
        if (s.StartsWith('<')) return HtmlStart.IsMatch(s) ? "value.html" : "value.xml";
        return "value.txt";
    }

    private static readonly System.Text.RegularExpressions.Regex SqlWords = new(
        @"\b(select|insert|update|delete|exec|execute|declare|create|alter|drop|set|if|begin|from|where|join)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Ngôn ngữ Monaco cho 1 giá trị (form sửa ô CellEditForm): json / html / xml như <see cref="GuessValueName"/>, chữ có từ khoá SQL → sql, còn lại plaintext.</summary>
    internal static string GuessValueLanguage(string value) => Path.GetExtension(GuessValueName(value)) switch
    {
        ".json" => "json",
        ".html" => "html",
        ".xml" => "xml",
        _ => SqlWords.IsMatch(value.Length > 20000 ? value[..20000] : value) ? "sql" : "plaintext",
    };

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
        if (_sourceVersion is { } ver)
        {
            // Kho source: không lộ đường dẫn ổ mạng — chỉ phiên bản + đường dẫn tính từ thư mục phiên bản.
            var rel = RelativeToVersion(path, ver) ?? Path.GetFileName(path);
            _info.Text = "View source " + ver + (string.IsNullOrEmpty(_note) ? "" : " " + _note) + " · " + rel + modified;
            _tip.SetToolTip(_info, "View source " + ver + " · " + rel);
        }
        else
        {
            _info.Text = (string.IsNullOrEmpty(_note) ? "" : _note + " · ") + path + modified;
            _tip.SetToolTip(_info, path);
        }
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

    /// <summary>Phần đường dẫn sau thư mục phiên bản <paramref name="version"/> (vd "App_Data\Controllers\Filter\X.xml"); null = không nằm dưới thư mục đó.</summary>
    private static string? RelativeToVersion(string path, string version)
    {
        var marker = "\\" + version.Trim('\\', '/') + "\\";
        var i = path.Replace('/', '\\').LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? null : path[(i + marker.Length)..];
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _info.ForeColor = AppColors.TextMuted;
    }
}
