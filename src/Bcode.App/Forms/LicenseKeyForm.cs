using System.Text.Json;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// Settings → "Key bản quyền": dán key một lần (mã hoá bcrypt .NET, được nhớ lại) để đối chiếu với key khai trong source của Bcode và mở khoá "Create RPT &amp; XML" và "Excel → FRX". Giao diện là trang WebView2
/// (Web/Shell/licensekey.html) nên tự co giãn theo màn hình và ăn theo Template giao diện; việc kiểm tra key nằm ở <see cref="LicenseService"/>.
/// </summary>
public class LicenseKeyForm : ThemedForm
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string? _reason;

    /// <param name="reason">Lý do mở hộp thoại (vd tính năng đang bị khoá) — hiện ở đầu trang; null = mở từ menu.</param>
    public LicenseKeyForm(string? reason = null)
    {
        _reason = reason;
        Text = "Key bản quyền";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        Width = 640;
        Height = 560;
        MinimumSize = new Size(380, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_web);

        ThemeManager.ThemeChanged += PushTheme;
        FormClosed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
        Load += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate(UiOverrides.UrlFor("licensekey.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Key bản quyền", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void Js(string script) { if (_web.CoreWebView2 is not null) _ = _web.CoreWebView2.ExecuteScriptAsync(script); }
    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    private void PushTheme() => Js($"window.setTheme && window.setTheme({(AppColors.IsDark ? "true" : "false")})");

    private async Task PushStateAsync(string? message = null, bool? ok = null)
    {
        // bcrypt chạy chậm có chủ đích (lần kiểm tra đầu): làm ở luồng nền để hộp thoại không đơ
        var unlocked = await Task.Run(() => LicenseService.IsUnlocked);
        Js($"licenseKey.init({J(new { unlocked, hasKey = LicenseService.HasSavedKey, reason = _reason, message, ok })})");
    }

    private async void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            switch (root.GetProperty("action").GetString())
            {
                case "ready":
                    PushTheme();
                    await PushStateAsync();
                    break;

                case "save":
                {
                    // bcrypt cố tình chạy chậm (~0,1–0,3 s): chạy nền để hộp thoại không đơ
                    var key = root.TryGetProperty("key", out var k) ? k.GetString() : "";
                    var (ok, msg) = await Task.Run(() => LicenseService.Save(key));
                    await PushStateAsync(msg, ok);
                    break;
                }

                case "clear":
                    LicenseService.Clear();
                    await PushStateAsync("Đã xoá key đã lưu. Create RPT & XML và Excel → FRX bị khoá lại.", true);
                    break;

                case "hash":
                {
                    var plain = root.TryGetProperty("plain", out var p) ? p.GetString() ?? "" : "";
                    var hash = plain.Trim().Length == 0 ? "" : await Task.Run(() => LicenseService.CreateHash(plain));
                    Js($"licenseKey.onHash({J(hash)})");
                    break;
                }

                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard đang bị chương trình khác giữ */ }
                    break;

                case "paste":
                    try { Js($"licenseKey.onPaste({J(Clipboard.ContainsText() ? Clipboard.GetText() : "")})"); } catch { /* clipboard bận */ }
                    break;

                case "close":
                    DialogResult = LicenseService.IsUnlocked ? DialogResult.OK : DialogResult.Cancel;
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Js($"licenseKey.onError({J(ex.Message)})");
        }
    }
}
