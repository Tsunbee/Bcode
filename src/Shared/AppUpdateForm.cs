using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.Shared;

/// <summary>
/// Hộp "Cập nhật &lt;app&gt;" cho app KHÔNG có khung WebDialogForm của Bcode (BcodeViewer, Screen Designer): CHÍNH trang update.html của Bcode
/// (link vào Web\update\ cạnh exe lúc build) + cùng xử lý <see cref="UpdateDialogLogic"/>. Form chỉ quyết định; việc cài chạy lúc app đóng
/// (<see cref="UpdateCheckService.StartApply"/>). Bcode dùng Forms/UpdateForm.cs (khung WebDialogForm, ăn theo Template giao diện).
/// </summary>
public sealed class AppUpdateForm : Form
{
    private readonly string _host;
    private WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly CoreWebView2Environment? _env;
    private readonly bool _dark;
    private readonly UpdateDialogLogic _logic;

    public UpdateDialogLogic.Outcome Decision => _logic.Decision;
    public UpdatePlan? Plan => _logic.Plan;
    public BackupInfo? Backup => _logic.Backup;
    public bool OverwriteModified => _logic.OverwriteModified;

    /// <param name="env">Môi trường WebView2 của cửa sổ chính (dùng chung hồ sơ) — null thì tự tạo trong %TEMP%.</param>
    /// <param name="dark">Trang theo nền tối / sáng.</param>
    public AppUpdateForm(CoreWebView2Environment? env, bool dark, Func<string> folder, Func<bool> chooseFolder, bool pendingOnClose)
    {
        _env = env;
        _dark = dark;
        _host = Path.GetFileNameWithoutExtension(UpdateCheckService.ExeName).ToLowerInvariant() + "-update.local";
        Text = "Cập nhật " + UpdateCheckService.AppName;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowIcon = false;
        BackColor = dark ? Color.FromArgb(30, 31, 34) : Color.White;
        Controls.Add(_web);
        _logic = new UpdateDialogLogic(folder, chooseFolder, pendingOnClose, Js, CloseWith, () => IsDisposed, OtherWindowsNote);
        FormClosing += (_, _) => _logic.Cancel();
        Load += async (_, _) =>
        {
            Size = new Size(Px(680), Px(640));                // px thiết kế → px thật theo DPI màn đang mở
            MinimumSize = new Size(Px(480), Px(420));
            if (Owner is { } o) Location = new Point(o.Left + Math.Max(0, (o.Width - Width) / 2), o.Top + Math.Max(0, (o.Height - Height) / 2));
            await InitAsync();
        };
    }

    private int Px(int design) => (int)Math.Round(design * DeviceDpi / 96.0);

    private async Task InitAsync()
    {
        try
        {
            // Thử môi trường của cửa sổ chính trước (dùng chung tiến trình trình duyệt); lỗi thì tạo môi trường riêng (hồ sơ %TEMP%\<exe>.WebView2\<pid>-update)
            // trên 1 control MỚI — control đã gọi EnsureCoreWebView2Async lỗi không dùng lại được.
            Exception? first = null;
            if (_env is not null)
            {
                try { await _web.EnsureCoreWebView2Async(_env); }
                catch (Exception ex) { first = ex; }
            }
            if (_env is null || first is not null)
            {
                if (first is not null)
                {
                    Controls.Remove(_web);
                    try { _web.Dispose(); } catch { /* đã hỏng */ }
                    _web = new WebView2 { Dock = DockStyle.Fill };
                    Controls.Add(_web);
                }
                var own = await CoreWebView2Environment.CreateAsync(userDataFolder:
                    Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(UpdateCheckService.ExeName) + ".WebView2", Environment.ProcessId + "-update"));
                await _web.EnsureCoreWebView2Async(own);
            }
            if (IsDisposed) return;
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(_host, Path.Combine(AppContext.BaseDirectory, "Web", "update"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, e) =>
            {
                JsonElement msg;
                try { using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString()); msg = doc.RootElement.Clone(); }
                catch { return; }
                var action = msg.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
                _ = DispatchAsync(action, msg);
            };
            core.NavigationCompleted += (_, _) =>
            {
                Js($"window.setTheme && window.setTheme({(_dark ? "true" : "false")})");
                _logic.Ready();
            };
            core.Navigate($"https://{_host}/update.html");
        }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show(this, "Không mở được hộp cập nhật (WebView2): " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            CloseWith(DialogResult.Cancel);
        }
    }

    private async Task DispatchAsync(string action, JsonElement msg)
    {
        try
        {
            if (action is "close" or "cancel") { CloseWith(DialogResult.Cancel); return; }
            await _logic.HandleAsync(action, msg);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Còn cửa sổ khác của chính app này (tiến trình khác, cùng exe) — trình cài sẽ chờ chúng đóng hết mới chép.</summary>
    private static string? OtherWindowsNote()
    {
        try
        {
            var me = Environment.ProcessId;
            var exe = Environment.ProcessPath ?? "";
            var others = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Count(p =>
            {
                try { return p.Id != me && string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
                finally { p.Dispose(); }
            });
            return others > 0 ? $"Còn {others} cửa sổ {UpdateCheckService.AppName} khác đang mở — trình cài chờ đóng hết (tối đa 15 phút) rồi mới chép bản mới." : null;
        }
        catch { return null; }
    }

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => Js(script)); } catch { /* đang đóng */ } return; }
        try { _ = _web.CoreWebView2?.ExecuteScriptAsync(script); } catch { /* trang chưa sẵn / đã đóng */ }
    }

    private void CloseWith(DialogResult result)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => CloseWith(result)); } catch { /* đang đóng */ } return; }
        DialogResult = result;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _web.Dispose(); } catch { /* đã huỷ */ } }
        base.Dispose(disposing);
    }

    /// <summary>Hộp nhập 1 dòng (đổi thư mục phát hành). null = Huỷ.</summary>
    public static string? Prompt(IWin32Window owner, string title, string label, string value, bool dark)
    {
        var bg = dark ? Color.FromArgb(43, 45, 49) : SystemColors.Control;
        var fg = dark ? Color.FromArgb(227, 229, 232) : SystemColors.ControlText;
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowIcon = false, ShowInTaskbar = false,
            AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(560, 150), BackColor = bg, ForeColor = fg, Font = new Font("Segoe UI", 9.5f),
        };
        var lbl = new Label { Text = label, Left = 14, Top = 12, Width = 532, Height = 48 };
        var box = new TextBox { Text = value, Left = 14, Top = 66, Width = 532 };
        if (dark) { box.BackColor = Color.FromArgb(30, 31, 34); box.ForeColor = fg; }
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 370, Top = 106, Width = 84, Height = 28 };
        var cancel = new Button { Text = "Huỷ", DialogResult = DialogResult.Cancel, Left = 462, Top = 106, Width = 84, Height = 28 };
        if (dark) foreach (var b in new[] { ok, cancel }) { b.FlatStyle = FlatStyle.Flat; b.BackColor = Color.FromArgb(61, 64, 70); b.ForeColor = fg; }
        f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}
