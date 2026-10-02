using System.Text.Json;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Change Owner" tool — moves a SQL object to a different schema (the modern SQL Server equivalent of the old "change owner"
/// concept; see ChangeOwnerService for why this runs ALTER SCHEMA ... TRANSFER).
///
/// Giao diện là 1 trang WebView2 (Web/Shell/changeowner.html) tự co giãn theo cỡ cửa sổ: chọn database, gõ object (có gợi ý tên từ cả
/// App Data và Sys Data), nhập schema mới. C# giữ phần chạy lệnh và hộp xác nhận — thao tác này không hoàn tác được nên luôn hỏi trước.
/// </summary>
public class ChangeOwnerForm : ThemedForm
{
    private readonly ChangeOwnerService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly string _preselected;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public ChangeOwnerForm(ChangeOwnerService service, SqlObjectBrowserService sqlObjectService, string? preselectedObject = null)
    {
        _service = service;
        _sqlObjectService = sqlObjectService;
        _preselected = preselectedObject ?? "";

        Text = "Change Owner";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        Width = 620;
        Height = 380;
        MinimumSize = new Size(380, 320);
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
            _web.CoreWebView2.Navigate($"https://{WebViewEnvironment.Host}/changeowner.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Change Owner", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var action = doc.RootElement.GetProperty("action").GetString();
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;

            switch (action)
            {
                case "ready":
                    PushTheme();
                    await Js($"window.init({JsonSerializer.Serialize(new { @object = _preselected })})");
                    _ = LoadSuggestionsAsync();
                    break;
                case "run":
                    await RunAsync(Bool(data, "sys"), Str(data, "object"), Str(data, "schema"));
                    break;
                case "close":
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
    }

    private async Task LoadSuggestionsAsync()
    {
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys);
                names.AddRange(objs.Select(o => o.QualifiedName));
            }
            await Js($"window.setSuggestions({JsonSerializer.Serialize(names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n))})");
        }
        catch { /* chưa kết nối — không có gợi ý */ }
    }

    private async Task RunAsync(bool useSys, string objectText, string newSchema)
    {
        objectText = objectText.Trim();
        newSchema = newSchema.Trim();
        if (objectText.Length == 0 || newSchema.Length == 0)
        {
            await Js("window.setStatus('Nhập Object và Schema mới.', 'err')");
            return;
        }

        var raw = objectText.Replace("[", "").Replace("]", "");
        var parts = raw.Split('.', 2);
        var (schema, name) = parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);

        var confirm = MessageBox.Show(this,
            $"Chuyển [{schema}].[{name}] sang schema [{newSchema}]?\nMọi code/view/proc tham chiếu object này bằng tên schema cũ sẽ phải sửa lại.",
            "Bcode — Change Owner", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        await Js("window.setBusy(true); window.setStatus('Đang chạy...', '')");
        try
        {
            await _service.ChangeOwnerAsync(useSys, schema, name, newSchema);
            await Js("window.setStatus('Đã đổi owner (schema) thành công.', 'ok')");
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
        finally
        {
            await Js("window.setBusy(false)");
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private Task Js(string script) =>
        IsDisposed || _web.CoreWebView2 is null ? Task.CompletedTask : _web.CoreWebView2.ExecuteScriptAsync(script);

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }
}
