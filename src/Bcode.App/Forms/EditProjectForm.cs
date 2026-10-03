using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// Compact single-project editor — matches FCode's own "Edit Project" popup (Server Name /
/// Login User / Password / Test Connection, then Sys Data / App Data / DB Access / ID /
/// Login WLink / Program Path / Source Path / Mobile Path / Working Path / Registry Name,
/// OK/Cancel). Used by the Projects screen (New / Edit) and by MainForm.QuickSelectProjectByCode
/// right after it auto-generates a Workspace from the naming template.
///
/// Cả hộp thoại là MỘT trang WebView2 (Web/Shell/editproject.html) nên tự co giãn theo cỡ cửa sổ — trước đây là
/// TableLayoutPanel + 1 thanh nút WebView2 riêng, nên thanh nút bị cắt/lệch khi mở. C# giữ phần đụng tới dữ liệu
/// (Test Connection, ghi vào Workspace); trang chỉ gom giá trị và gửi <c>{action, data}</c> (ready / test / ok / cancel).
/// </summary>
public class EditProjectForm : ThemedForm
{
    private readonly DbConnectionService _connections;
    private readonly Workspace _original;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    /// <summary>The edited Workspace — only updated when the dialog closes with DialogResult.OK.</summary>
    public Workspace Result { get; private set; }

    public EditProjectForm(Workspace ws, DbConnectionService connections)
    {
        _connections = connections;
        _original = ws;
        Result = ws;

        Text = "Edit Project";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        Width = 720;
        Height = 760;
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
            _web.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("editproject.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Edit Project",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
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
                case "ready": await OnReadyAsync(); break;
                case "test": await TestAsync(FromData(data)); break;
                case "ok": Apply(FromData(data)); break;
                case "cancel": DialogResult = DialogResult.Cancel; Close(); break;
            }
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
    }

    private async Task OnReadyAsync()
    {
        PushTheme();
        var w = _original;
        var state = new
        {
            title = "Edit Project",
            item = new Dictionary<string, string>
            {
                ["server"] = w.Server, ["user"] = w.User, ["pass"] = w.Password,
                ["sysDb"] = w.SysDatabase, ["appDb"] = w.AppDatabase,
                ["id"] = w.ProjectId, ["wlink"] = w.LoginWLink,
                ["programPath"] = w.ProgramPath, ["sourcePath"] = w.SourcePath,
                ["mobilePath"] = w.MobilePath, ["workingPath"] = w.WorkingPath, ["registry"] = w.RegistryName,
            },
        };
        await Js($"window.init({JsonSerializer.Serialize(state)})");
    }

    private async Task TestAsync(Workspace probe)
    {
        var (sysOk, sysMsg) = await _connections.TestConnectionAsync(probe, useSysDatabase: true);
        var (appOk, appMsg) = await _connections.TestConnectionAsync(probe, useSysDatabase: false);
        await Js($"window.testDone({Json($"Sys Data: {sysMsg}   |   App Data: {appMsg}")}, {(sysOk && appOk ? "true" : "false")})");
    }

    private static string Get(JsonElement d, string name) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static Workspace FromData(JsonElement d) => new()
    {
        Name = Get(d, "id").Trim(),
        Server = Get(d, "server").Trim(),
        IntegratedSecurity = false,
        User = Get(d, "user").Trim(),
        Password = Get(d, "pass"),
        SysDatabase = Get(d, "sysDb").Trim(),
        AppDatabase = Get(d, "appDb").Trim(),
        ProjectId = Get(d, "id").Trim(),
        LoginWLink = Get(d, "wlink").Trim(),
        ProgramPath = Get(d, "programPath").Trim(),
        SourcePath = Get(d, "sourcePath").Trim(),
        MobilePath = Get(d, "mobilePath").Trim(),
        WorkingPath = Get(d, "workingPath").Trim(),
        RegistryName = Get(d, "registry").Trim(),
    };

    private void Apply(Workspace edited)
    {
        Result.Name = edited.Name;
        Result.Server = edited.Server;
        Result.IntegratedSecurity = edited.IntegratedSecurity;
        Result.User = edited.User;
        Result.Password = edited.Password;
        Result.SysDatabase = edited.SysDatabase;
        Result.AppDatabase = edited.AppDatabase;
        Result.ProjectId = edited.ProjectId;
        Result.LoginWLink = edited.LoginWLink;
        Result.ProgramPath = edited.ProgramPath;
        Result.SourcePath = edited.SourcePath;
        Result.MobilePath = edited.MobilePath;
        Result.WorkingPath = edited.WorkingPath;
        Result.RegistryName = edited.RegistryName;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private async Task Js(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        await _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }
}
