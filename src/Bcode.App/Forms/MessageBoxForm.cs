using System.Text;
using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Hộp thông báo / xác nhận theo theme Bcode (thay MessageBox của Windows) — trang Web/Shell/msgbox.html ở chế độ Web, trang native MsgBoxPage
/// (Controls/NativePages.MsgBox.cs) ở chế độ App, cùng giao thức: C# gọi <c>msgbox.init({text, icon, buttons, def})</c>; trang gửi lên
/// <c>answer {r}</c> (nút được bấm), <c>escape</c>, <c>copy</c> (Ctrl+C không bôi đen = chép cả hộp), <c>fit {content, view}</c> (chiều cao nội dung cần → đổi cỡ cửa sổ).
/// Không gọi trực tiếp — dùng <see cref="ThemedMessageBox"/> (hoặc MessageBox.Show trong code Bcode.App, đã trỏ về đó).
/// </summary>
internal sealed class MessageBoxForm : WebDialogForm
{
    private readonly string _text;
    private readonly MessageBoxButtons _buttons;
    private readonly MessageBoxIcon _icon;
    private readonly int _default;
    private readonly (DialogResult Result, string Label, string Key)[] _set;
    private readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 6000 };
    private bool _answered, _fitted;

    /// <summary>Trang không nạp được (WebView2 hỏng / thiếu) trong thời gian chờ — người gọi hiện lại bằng MessageBox của Windows.</summary>
    public bool Fallback { get; private set; }

    public MessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, bool inTaskbar)
        : base(caption, "msgbox.html", 440, 200, minWidth: 320, minHeight: 140, resizable: false)
    {
        _text = text;
        _buttons = buttons;
        _icon = icon;
        _set = ButtonSet(buttons);
        _default = Math.Clamp(defaultButton switch { MessageBoxDefaultButton.Button2 => 1, MessageBoxDefaultButton.Button3 => 2, MessageBoxDefaultButton.Button4 => 3, _ => 0 }, 0, _set.Length - 1);
        ShowInTaskbar = inTaskbar;
        StartPosition = inTaskbar ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        // Như MessageBox: hộp Yes/No hay Abort/Retry/Ignore (không có nút "thôi") không đóng được bằng nút ✕ / Alt+F4 / Esc.
        ControlBox = CancelResult() is not null;
        var (w, h) = EstimateSize(text, icon != MessageBoxIcon.None, _set.Length);
        Width = w;
        Height = h;
        _watchdog.Tick += (_, _) =>
        {
            _watchdog.Stop();
            if (Web.IsReady || IsDisposed) return;
            Fallback = true;
            _answered = true;
            CloseWith(DialogResult.None);
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!Web.IsReady) _watchdog.Start();
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _watchdog.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnReady()
    {
        _watchdog.Stop();
        var icon = _icon switch
        {
            MessageBoxIcon.Error => "error",            // = Hand = Stop
            MessageBoxIcon.Warning => "warning",        // = Exclamation
            MessageBoxIcon.Question => "question",
            MessageBoxIcon.Information => "info",       // = Asterisk
            _ => "",
        };
        Js("msgbox.init(" + J(new
        {
            text = _text,
            icon,
            buttons = _set.Select(b => new { id = b.Result.ToString(), label = b.Label, key = b.Key }).ToArray(),
            def = _default,
        }) + ")");
        Web.FocusWeb();
    }

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        switch (action)
        {
            case "answer":
            {
                var id = msg.TryGetProperty("r", out var r) ? r.GetString() ?? "" : "";
                if (Enum.TryParse<DialogResult>(id, out var res) && _set.Any(b => b.Result == res)) Answer(res);
                break;
            }
            case "escape":
                if (CancelResult() is { } c) Answer(c);
                break;
            case "copy":
                CopyAll();
                break;
            case "fit":
                Fit(msg);
                break;
        }
        return Task.CompletedTask;
    }

    private void Answer(DialogResult r) { _answered = true; CloseWith(r); }

    /// <summary>Kết quả khi đóng không qua nút (✕, Esc): như MessageBox — có Cancel thì Cancel, chỉ có OK thì OK, còn lại (Yes/No...) không đóng được.</summary>
    private DialogResult? CancelResult()
    {
        if (_set.Any(b => b.Result == DialogResult.Cancel)) return DialogResult.Cancel;
        if (_buttons == MessageBoxButtons.OK) return DialogResult.OK;
        return null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_answered || e.Cancel) return;
        // ✕ / Alt+F4 / "close" của trang (WebDialogForm đặt Cancel trước khi đóng): đổi về đúng kết quả MessageBox trả, hoặc chặn đóng nếu hộp không có lối thoát.
        if (CancelResult() is { } c) { DialogResult = c; return; }
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.None) e.Cancel = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Phím rơi vào vỏ WinForms (trang chưa nạp xong / focus chưa vào trang)
        if (keyData == Keys.Escape) { if (CancelResult() is { } c) Answer(c); return true; }
        if (keyData is (Keys.Control | Keys.C) or (Keys.Control | Keys.Insert) && !Web.ContainsFocus) { CopyAll(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Chép cả hộp theo đúng khuôn MessageBox của Windows (tiêu đề / nội dung / các nút) — dán vào báo lỗi, chat.</summary>
    private void CopyAll()
    {
        const string line = "---------------------------";
        var sb = new StringBuilder();
        sb.AppendLine(line).AppendLine(Text).AppendLine(line).AppendLine(_text).AppendLine(line)
          .AppendLine(string.Join("   ", _set.Select(b => b.Label))).AppendLine(line);
        try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard đang bị chương trình khác giữ */ }
    }

    private void Fit(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var c) || !msg.TryGetProperty("view", out var v)) return;
        var content = c.GetDouble(); var view = v.GetDouble();
        if (view <= 0 || Web.ClientSize.Height <= 0) return;
        var delta = (int)Math.Round((content - view) * Web.ClientSize.Height / view);
        if (Math.Abs(delta) <= 2) { _fitted = true; return; }
        var area = Screen.FromControl(this).WorkingArea;
        var newH = Math.Clamp(Height + delta, MinimumSize.Height, area.Height * 70 / 100);   // dài hơn thì vùng chữ tự cuộn
        if (newH == Height) return;
        var top = Top - (newH - Height) / 2;
        Height = newH;
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - newH));
        if (!_fitted) { _fitted = true; Web.FocusWeb(); }
    }

    private static (DialogResult, string, string)[] ButtonSet(MessageBoxButtons b) => b switch
    {
        // Nhãn tiếng Anh như MessageBox đang hiện — nhiều thông báo ghi "Yes = ... · No = ... · Cancel = ...".
        MessageBoxButtons.OKCancel => new[] { (DialogResult.OK, "OK", "o"), (DialogResult.Cancel, "Cancel", "c") },
        MessageBoxButtons.YesNo => new[] { (DialogResult.Yes, "Yes", "y"), (DialogResult.No, "No", "n") },
        MessageBoxButtons.YesNoCancel => new[] { (DialogResult.Yes, "Yes", "y"), (DialogResult.No, "No", "n"), (DialogResult.Cancel, "Cancel", "c") },
        MessageBoxButtons.RetryCancel => new[] { (DialogResult.Retry, "Retry", "r"), (DialogResult.Cancel, "Cancel", "c") },
        MessageBoxButtons.AbortRetryIgnore => new[] { (DialogResult.Abort, "Abort", "a"), (DialogResult.Retry, "Retry", "r"), (DialogResult.Ignore, "Ignore", "i") },
        MessageBoxButtons.CancelTryContinue => new[] { (DialogResult.Cancel, "Cancel", "c"), (DialogResult.TryAgain, "Try Again", "t"), (DialogResult.Continue, "Continue", "o") },
        _ => new[] { (DialogResult.OK, "OK", "o") },
    };

    /// <summary>Cỡ cửa sổ ban đầu (px thiết kế — DpiForm tự nhân DPI × Tỉ lệ): rộng theo dòng dài nhất (320–620 cho phần chữ), cao theo số dòng sau khi xuống dòng.
    /// Trang báo lại chiều cao thật bằng "fit" ngay sau khi vẽ nên ở đây chỉ cần gần đúng.</summary>
    private static (int W, int H) EstimateSize(string text, bool hasIcon, int buttonCount)
    {
        using var font = new Font("Segoe UI", 9.75f);   // ≈ 13px của trang
        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.ExpandTabs;
        var dpi = 96f;
        try { using var g = Graphics.FromHwnd(IntPtr.Zero); dpi = g.DpiX; } catch { }
        double Design(int px) => px * 96.0 / dpi;
        var body = text.Length > 20_000 ? text[..20_000] : text;
        var longest = 0;
        foreach (var ln in body.Split('\n')) longest = Math.Max(longest, TextRenderer.MeasureText(ln.TrimEnd('\r'), font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.ExpandTabs).Width);
        var buttonsW = 40 + buttonCount * 94 + 165;   // nút + chữ "Ctrl+C: chép nội dung" (chế độ App chữ to hơn trang một chút)
        var chrome = 34 + (hasIcon ? 44 : 0) + 18;     // lề trái/phải + cột biểu tượng + viền cửa sổ
        var textW = (int)Math.Clamp(Design(longest) + 8, 300, 620);
        textW = Math.Max(textW, buttonsW - chrome);
        var measured = TextRenderer.MeasureText(body.Length == 0 ? " " : body, font, new Size((int)(textW * dpi / 96.0), int.MaxValue), flags);
        var textH = (int)Math.Max(Design(measured.Height) * 1.12, hasIcon ? 32 : 18);   // trang dùng line-height 1.5 > dòng GDI
        var h = textH + 36 /* lề trên/dưới */ + 46 /* thanh nút */ + 40 /* thanh tiêu đề + viền */;
        return (textW + chrome, Math.Min(h, 640));
    }
}
