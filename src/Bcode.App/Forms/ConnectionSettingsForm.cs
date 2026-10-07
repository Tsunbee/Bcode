using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// File > Choose Server / Workspaces (Edit Project): quản lý danh sách các Workspace (WS).
/// Cả hộp thoại là MỘT trang WebView2 (Web/Shell/connections.html — danh sách có ô lọc bên trái, các thẻ Kết nối / Database / Project /
/// FSG bên phải, hộp thoại Synchronize nằm ngay trong trang) nên tự co giãn theo cỡ cửa sổ và theo UiScale/DPI của mọi màn hình.
/// C# giữ phần đụng tới dữ liệu (ghi vào Workspace, Test Connection, Synchronize, Sync menu); trang chỉ gom giá trị và gửi
/// <c>{action, data}</c>: ready / select / new / delete / test / apply / save / close / sync / sync-menu.
/// Như trước: sửa tay áp thẳng lên các Workspace trong AppSettings; Apply và Save &amp; Close ghi xuống file.
/// </summary>
public class ConnectionSettingsForm : ThemedForm
{
    private readonly AppSettings _settings;
    private readonly DbConnectionService _connections;
    private readonly bool _allowSync;
    private readonly bool _autoSync;
    private readonly bool _startNew;
    private int _initialIndex;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    /// <param name="allowSync">true CHỈ khi mở từ màn hình Projects lúc mới mở Bcode: hiện khối "FSG — Đồng bộ dự án" và cho
    /// Ctrl+F5 — thêm/ghi đè project theo mã từ danh mục dự án FSG. Mở từ File &gt; Choose Server thì không có.</param>
    /// <param name="select">Project cần chọn sẵn ở danh sách bên trái (nút Edit của màn hình Projects).</param>
    /// <param name="startNew">Tạo ngay 1 project mới và đứng ở đó (nút New của màn hình Projects).</param>
    /// <param name="autoSync">Mở xong chạy luôn Synchronize (Ctrl+F5 bấm ngay từ màn hình Projects).</param>
    public ConnectionSettingsForm(AppSettings settings, DbConnectionService connections,
        bool allowSync = false, Workspace? select = null, bool startNew = false, bool autoSync = false)
    {
        _settings = settings;
        _connections = connections;
        _allowSync = allowSync;
        _autoSync = autoSync;
        _startNew = startNew;
        KeyPreview = true;

        Text = "Edit Project (Workspaces)";
        Width = 1120;
        Height = 760;
        MinimumSize = new Size(560, 480);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowIcon = false;

        _initialIndex = select is not null ? Math.Max(0, _settings.Workspaces.IndexOf(select)) : (_settings.Workspaces.Count > 0 ? 0 : -1);
        if (startNew)
        {
            var ws = new Workspace { Name = $"WS{_settings.Workspaces.Count + 1}", Server = _settings.DefaultProjectServer, IntegratedSecurity = false };
            _settings.Workspaces.Add(ws);
            _initialIndex = _settings.Workspaces.Count - 1;
        }

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
            _web.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("connections.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Edit Project",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F5) && _allowSync)
        {
            _ = Js("window.openSync && window.openSync()");
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------------------------------------------------------- web <-> C#

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
                case "select":
                    Commit(Int(data, "from"), Item(data));
                    await PushListAsync(Int(data, "to"));
                    break;
                case "new":
                    Commit(Int(data, "from"), Item(data));
                    var ws = new Workspace { Name = $"WS{_settings.Workspaces.Count + 1}" };
                    _settings.Workspaces.Add(ws);
                    await PushListAsync(_settings.Workspaces.Count - 1);
                    await Js("document.getElementById('name').focus(); document.getElementById('name').select()");
                    break;
                case "delete": await DeleteAsync(Int(data, "index")); break;
                case "test": await TestAsync(Int(data, "index"), Item(data)); break;
                case "apply":
                    Commit(Int(data, "index"), Item(data));
                    _settings.Save();
                    await Js($"window.setStatus({Json("Đã lưu (Apply).")}, 'ok')");
                    break;
                case "save":
                    Commit(Int(data, "index"), Item(data));
                    _settings.Save();
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
                case "close": DialogResult = DialogResult.Cancel; Close(); break;
                case "sync": await SynchronizeAsync(data); break;
                case "sync-menu": await CreateMenuAsync(data); break;
            }
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err'); window.setBusy(false)");
        }
    }

    private async Task OnReadyAsync()
    {
        PushTheme();
        var state = new
        {
            allowSync = _allowSync,
            names = _settings.Workspaces.Select(w => w.Name).ToArray(),
            sel = _initialIndex,
            item = ItemOf(_initialIndex),
            focusId = _startNew,
        };
        await Js($"window.init({JsonSerializer.Serialize(state)})");
        if (_autoSync && _allowSync) await Js("window.openSync && window.openSync()");
    }

    private async Task PushListAsync(int sel)
    {
        sel = _settings.Workspaces.Count == 0 ? -1 : Math.Clamp(sel, 0, _settings.Workspaces.Count - 1);
        var names = JsonSerializer.Serialize(_settings.Workspaces.Select(w => w.Name).ToArray());
        await Js($"window.setList({names}, {sel}, {JsonSerializer.Serialize(ItemOf(sel))})");
    }

    private object? ItemOf(int index)
    {
        if (index < 0 || index >= _settings.Workspaces.Count) return null;
        var w = _settings.Workspaces[index];
        return new
        {
            name = w.Name, server = w.Server, integrated = w.IntegratedSecurity, user = w.User, pass = w.Password,
            sysDb = w.SysDatabase, appDb = w.AppDatabase, id = w.ProjectId, wlink = w.LoginWLink,
            programPath = w.ProgramPath, sourcePath = w.SourcePath, mobilePath = w.MobilePath,
            workingPath = w.WorkingPath, registry = w.RegistryName, versionCode = w.VersionCode, dbAccess = w.DbAccess,
        };
    }

    /// <summary>Ghi giá trị trang gửi lên vào Workspace thứ <paramref name="index"/> (sửa tại chỗ như trước).</summary>
    private void Commit(int index, JsonElement item)
    {
        if (index < 0 || index >= _settings.Workspaces.Count || item.ValueKind != JsonValueKind.Object) return;
        var ws = _settings.Workspaces[index];
        string S(string k, bool trim = true) => item.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? (trim ? v.GetString()?.Trim() : v.GetString()) ?? "" : "";
        ws.Name = S("name");
        ws.Server = S("server");
        ws.IntegratedSecurity = !item.TryGetProperty("integrated", out var integ) || integ.ValueKind != JsonValueKind.False;
        ws.User = S("user");
        ws.Password = S("pass", trim: false);
        ws.SysDatabase = S("sysDb");
        ws.AppDatabase = S("appDb");
        ws.ProjectId = S("id");
        ws.LoginWLink = S("wlink");
        ws.ProgramPath = S("programPath");
        ws.SourcePath = S("sourcePath");
        ws.MobilePath = S("mobilePath");
        ws.WorkingPath = S("workingPath");
        ws.RegistryName = S("registry");
        ws.VersionCode = S("versionCode");
        ws.DbAccess = S("dbAccess");
    }

    private static int Int(JsonElement d, string name) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : -1;

    private static JsonElement Item(JsonElement d) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty("item", out var v) ? v : default;

    private async Task DeleteAsync(int index)
    {
        if (index < 0 || index >= _settings.Workspaces.Count) return;
        _settings.Workspaces.RemoveAt(index);
        await PushListAsync(index);
    }

    private async Task TestAsync(int index, JsonElement item)
    {
        Commit(index, item);
        if (index < 0 || index >= _settings.Workspaces.Count)
        {
            await Js($"window.testDone({Json("Chưa chọn Workspace nào.")}, false)");
            return;
        }
        var ws = _settings.Workspaces[index];
        try
        {
            var (sysOk, sysMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: true);
            var (appOk, appMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: false);
            await Js($"window.testDone({Json($"Sys Data: {sysMsg}   |   App Data: {appMsg}")}, {(sysOk && appOk ? "true" : "false")})");
        }
        catch (Exception ex)
        {
            await Js($"window.testDone({Json($"Lỗi: {ex.Message}")}, false)");
        }
    }

    /// <summary>
    /// Synchronize (Ctrl+F5): đồng bộ project từ database FSG_A (xem FsgProjectLookupService) với 3 chế độ — thêm 1 mã dự án /
    /// chỉ thêm dự án mới / ghi đè tất cả (đã có thì cập nhật, chưa có thì thêm). Kết quả nằm trong danh sách bên trái, còn phải bấm
    /// Apply / Save &amp; Close để lưu xuống file.
    /// </summary>
    private async Task SynchronizeAsync(JsonElement data)
    {
        if (!_allowSync) return;
        var modeText = data.TryGetProperty("mode", out var m) ? m.GetString() : "AddOne";
        var mode = Enum.TryParse<FsgProjectLookupService.SyncMode>(modeText, out var parsed) ? parsed : FsgProjectLookupService.SyncMode.AddOne;
        var code = data.TryGetProperty("code", out var c) ? c.GetString()?.Trim() ?? "" : "";
        var index = Int(data, "index");

        if (mode == FsgProjectLookupService.SyncMode.AddOne && code.Length == 0)
        {
            await Js($"window.setStatus({Json("Chưa nhập mã dự án.")}, 'err')");
            return;
        }
        if (mode == FsgProjectLookupService.SyncMode.OverwriteAll &&
            MessageBox.Show(this,
                "Ghi đè TẤT CẢ dự án đã có bằng thông tin đồng bộ?\n\n" +
                "Chỉ ghi đè những giá trị FSG có; mật khẩu web, Mobile Path và cấu hình Profiler giữ nguyên.",
                "Synchronize — FSG", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        Commit(index, Item(data)); // đang sửa dở thì giữ lại trước khi danh sách bị nạp lại
        var keepName = index >= 0 && index < _settings.Workspaces.Count ? _settings.Workspaces[index].Name : null;
        await Js("window.setBusy(true); window.setStatus('Đang đồng bộ...')");
        try
        {
            var result = await new FsgProjectLookupService().SyncAsync(mode, code, _settings.Workspaces);
            if (!result.Success)
            {
                await Js($"window.setStatus({Json(result.Error!)}, 'err')");
                return;
            }

            var select = mode == FsgProjectLookupService.SyncMode.AddOne
                ? _settings.Workspaces.FindIndex(w => w.ProjectId.Equals(code, StringComparison.OrdinalIgnoreCase))
                : _settings.Workspaces.FindIndex(w => w.Name == keepName);
            await PushListAsync(select >= 0 ? select : 0);
            await Js($"window.setStatus({Json(result.Summary + (result.Notes.Count > 0 ? "  " + string.Join(" ", result.Notes) : "") + "  Bấm Apply/Save để lưu.")}, 'ok')");
        }
        finally
        {
            await Js("window.setBusy(false)");
        }
    }

    /// <summary>Sync menu: chạy <c>exec ns_createCommand N'mã dự án'</c> trên FSG_A cho project đang chọn. Thành công thì không
    /// thông báo gì (chỉ xoá dòng "Đang tạo menu..."); lỗi mới hiện.</summary>
    private async Task CreateMenuAsync(JsonElement data)
    {
        var index = Int(data, "index");
        Commit(index, Item(data));
        var sel = index >= 0 && index < _settings.Workspaces.Count ? _settings.Workspaces[index] : null;
        var code = sel is null ? "" : (string.IsNullOrWhiteSpace(sel.ProjectId) ? sel.Name : sel.ProjectId).Trim();
        if (code.Length == 0) { await Js($"window.setStatus({Json("Chưa chọn project nào.")}, 'err')"); return; }

        await Js("window.setBusy(true); window.setStatus('Đang tạo menu...')");
        try
        {
            var (ok, message) = await new FsgProjectLookupService().CreateMenuAsync(code);
            await Js(ok ? "window.setStatus('')" : $"window.setStatus({Json(message)}, 'err')");
        }
        finally
        {
            await Js("window.setBusy(false)");
        }
    }

    // ---------------------------------------------------------------- helpers

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
