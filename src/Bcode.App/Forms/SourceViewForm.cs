using Bcode.App.Controls;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// "View source" của Cấp source / Add Source: xem (chỉ đọc) file source trong kho SourceCollection SẮP được cấp, trước khi copy vào dự án.
/// Dùng chung khung xem của File Lookup (<see cref="MonacoPreviewControl"/>: Monaco ở chế độ Web, Scintilla ở chế độ App, tô FCode như BcodeViewer).
/// F12 trên entity SYSTEM / đường dẫn Include → mở file đó ngay trong cửa sổ này. Không khoá form cha (mở nhiều cửa sổ để so được).
/// </summary>
public sealed class SourceViewForm : ThemedForm
{
    private readonly MonacoPreviewControl _view = new();
    private readonly Label _info = new() { Dock = DockStyle.Top, AutoSize = false, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0), AutoEllipsis = true };
    private readonly ToolTip _tip = new();
    private readonly string? _note;

    /// <param name="note">Ghi chú hiện trước đường dẫn ở dòng trên cùng (vd phiên bản lấy source).</param>
    public SourceViewForm(string path, string? note = null)
    {
        _note = note;
        Width = 1000;
        Height = 700;
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
            else if (e.Control && e.KeyCode == Keys.F) { e.Handled = e.SuppressKeyPress = true; _view.ShowFind(); }
        };
        _view.EntityNavigationRequested += p => { if (File.Exists(p)) LoadFile(p); };
        Controls.Add(_view);
        Controls.Add(_info);
        FormClosed += (_, _) => _tip.Dispose();
        LoadFile(path);
    }

    private void LoadFile(string path)
    {
        Text = "View source — " + Path.GetFileName(path);
        string modified = "";
        try { modified = " · sửa " + File.GetLastWriteTime(path).ToString("dd/MM/yyyy HH:mm"); } catch { }
        _info.Text = (string.IsNullOrEmpty(_note) ? "" : _note + " · ") + path + modified;
        _tip.SetToolTip(_info, path);
        try { _view.LoadContent(path, new ScriptFileService().ReadFile(path)); }
        catch (Exception ex) { _view.LoadContent(null, "Không đọc được file:\r\n" + ex.Message); }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _info.ForeColor = AppColors.TextMuted;
    }
}
