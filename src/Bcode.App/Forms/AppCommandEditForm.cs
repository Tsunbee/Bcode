using System.Text.Json;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "COMMAND - Edit" — New/Edit/Delete một dòng bảng <c>command</c> của sản phẩm dạng APP (FBFF...), giống form của FCode:
/// mọi cột hiện dạng "tên (Kiểu)" + ô nhập, Menu_id/Menu_id0/Bar/Bar2 ở trên. Dưới form liệt kê dòng <c>reports</c> (Sys Data)
/// có form = Sysid của menu (mẫu in / khai báo màn hình báo cáo). Mở không chặn: bấm sang menu khác ở cây thì nạp menu đó.
/// </summary>
public class AppCommandEditForm : ThemedForm
{
    private readonly AppCommandService _service;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private string? _menuId;                        // null = New
    private Dictionary<string, string?>? _seed;     // giá trị gợi ý khi New (nhân bản)
    private bool _ready;
    private string? _templateId;                  // New: mượn dữ liệu dòng này (bỏ menu_id) làm giá trị ban đầu

    public AppCommandEditForm(AppCommandService service, string? menuId, string? templateId = null)
    {
        _service = service;
        _templateId = templateId;
        _menuId = menuId;
        Text = menuId is null ? "COMMAND - New" : "COMMAND - Edit";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        Width = 980;
        Height = 760;
        MinimumSize = new Size(640, 480);
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
            _web.CoreWebView2.Navigate(UiOverrides.UrlFor("appcommandedit.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Command", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    /// <summary>Bấm sang menu khác ở cây: nạp dòng đó (menuId != null) hoặc mở trống để làm mới (null).</summary>
    public async Task SwitchToAsync(string? menuId, string? templateId = null)
    {
        if (IsDisposed) return;
        _menuId = menuId;
        _seed = null;
        _templateId = templateId;
        Text = menuId is null ? "COMMAND - New" : "COMMAND - Edit";
        if (_ready) await PushStateAsync();
    }

    private async void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var action = doc.RootElement.GetProperty("action").GetString();
            var values = ReadValues(doc.RootElement.TryGetProperty("data", out var d) ? d : default);
            switch (action)
            {
                case "ready": _ready = true; PushTheme(); await PushStateAsync(); break;
                case "save": await SaveAsync(values); break;
                case "delete": await DeleteAsync(); break;
                case "refresh": await PushStateAsync(); break;
                case "new":
                    _menuId = null;
                    _seed = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase) { ["menu_id"] = "" };
                    Text = "COMMAND - New";
                    await PushStateAsync();
                    break;
                case "reports": await PushReportsAsync(Get(values, "sysid")); break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err'); window.setBusy(false)");
        }
    }

    private async Task PushStateAsync()
    {
        await Js("window.setBusy(true)");
        try
        {
            var cols = await _service.GetColumnsAsync();
            Dictionary<string, string?>? values = _seed;
            if (_menuId is not null) values = await _service.LoadRowAsync(_menuId) ?? values;
            else if (_seed is null && _templateId is not null && await _service.LoadRowAsync(_templateId) is { } tpl) { tpl["menu_id"] = ""; values = tpl; }
            await Js("window.init(" + JsonSerializer.Serialize(new
            {
                isNew = _menuId is null,
                columns = cols.Select(c => new { name = c.Name, kind = c.Kind, nullable = c.Nullable, maxLength = c.Kind == "String" ? c.MaxLength : 0 }),
                values,
            }) + ")");
            await Js("window.setStatus('')");
        }
        catch (Exception ex) { await Js($"window.setStatus({Json(ex.Message)}, 'err')"); }
        finally { await Js("window.setBusy(false)"); }
    }

    private async Task PushReportsAsync(string sysId)
    {
        try
        {
            var r = await _service.LoadReportsAsync(sysId);
            if (IsDisposed) return;
            await Js("window.setReports(" + JsonSerializer.Serialize(new { columns = r.Columns, rows = r.Rows, note = r.Note }) + ")");
        }
        catch (Exception ex) { await Js($"window.setReports({{columns:[],rows:[],note:{Json("Không đọc được reports: " + ex.Message)}}})"); }
    }

    private async Task SaveAsync(Dictionary<string, string?> values)
    {
        var id = Get(values, "menu_id").Trim();
        if (id.Length == 0) { await Invalid("menu_id", "Nhập Menu_id."); return; }
        if (Get(values, "bar").Trim().Length == 0) { await Invalid("bar", "Nhập Bar (tên menu)."); return; }
        var cols = await _service.GetColumnsAsync();
        foreach (var c in cols)
            if (c.Kind == "String" && c.MaxLength > 0 && Get(values, c.Name).Length > c.MaxLength)
            { await Invalid(c.Name, $"{c.Name} tối đa {c.MaxLength} ký tự."); return; }

        await Js("window.setBusy(true)");
        await Js("window.setStatus('Đang lưu...')");
        try
        {
            var idChanged = _menuId is null || !string.Equals(_menuId, id, StringComparison.OrdinalIgnoreCase);
            if (idChanged && await _service.ExistsAsync(id))
            {
                await Invalid("menu_id", $"Menu_id '{id}' đã tồn tại — dùng id khác.");
                return;
            }
            await _service.SaveAsync(_menuId, values);
            _menuId = id;
            Text = "COMMAND - Edit";
            await Js("window.setStatus('Đã lưu.', 'ok')");
            Changed?.Invoke();
        }
        catch (Exception ex) { await Js($"window.setStatus({Json(ex.Message)}, 'err')"); }
        finally { if (!IsDisposed) await Js("window.setBusy(false)"); }
    }

    private async Task DeleteAsync()
    {
        if (_menuId is null) return;
        if (MessageBox.Show(this, $"Xóa menu '{_menuId}' khỏi bảng command?", "Bcode — Command",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await Js("window.setBusy(true)");
        try
        {
            await _service.DeleteAsync(_menuId);
            Changed?.Invoke();
            Close();
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
            await Js("window.setBusy(false)");
        }
    }

    /// <summary>Bắn sau mỗi lần Save/Delete thành công để cây menu nạp lại.</summary>
    public event Action? Changed;

    private async Task Invalid(string col, string message)
    {
        await Js($"window.setStatus({Json(message)}, 'err'); window.setInvalid({Json(col)})");
        await Js("window.setBusy(false)");
    }

    private static Dictionary<string, string?> ReadValues(JsonElement data)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (data.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in data.EnumerateObject())
            map[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return map;
    }

    private static string Get(Dictionary<string, string?> f, string key) => f.TryGetValue(key, out var v) ? v ?? "" : "";
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
