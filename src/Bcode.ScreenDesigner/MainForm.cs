using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.ScreenDesigner;

/// <summary>Cửa sổ duy nhất của Designer: 1 WebView2 chạy Web\designer.html (giao diện + "Xem trước Dir" của BcodeViewer), bridge ở <see cref="DesignerBridge"/>.</summary>
public sealed class MainForm : Form
{
    private const string VirtualHost = "designer.local";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, string> _opts;
    private DesignerBridge? _bridge;

    public MainForm(Dictionary<string, string> opts)
    {
        _opts = opts;
        Text = "Bcode Screen Designer" + (opts.TryGetValue("project", out var p) && p.Length > 0 ? " — " + p : "");
        Width = 1500; Height = 940;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;   // mở là full màn hình
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* không có icon cũng được */ }
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
        FormClosed += (_, _) => { try { _web.Dispose(); } catch { /* đang đóng */ } };
    }

    private async Task InitAsync()
    {
        try
        {
            // Mỗi cửa sổ 1 thư mục profile riêng (có thể mở nhiều Designer cùng lúc, như BcodeViewer).
            var profile = Path.Combine(Path.GetTempPath(), "BcodeScreenDesigner.WebView2", Environment.ProcessId.ToString());
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.Allow);

            _bridge = new DesignerBridge(this, json => { if (!IsDisposed) BeginInvoke(() => { try { core.PostWebMessageAsJson(json); } catch { /* trang đã đóng */ } }); }, _opts);
            core.AddHostObjectToScript("host", _bridge);
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { /* bỏ qua */ }
            };
            core.Navigate($"https://{VirtualHost}/designer.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không khởi tạo được WebView2:\n" + ex.Message + "\n\nCần cài Microsoft Edge WebView2 Runtime.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}
