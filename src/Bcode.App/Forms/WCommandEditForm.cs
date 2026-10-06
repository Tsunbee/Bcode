using System.Globalization;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "WCOMMAND - Edit" — the New/Edit dialog for a single wcommand (+ command) row,
/// opened from the WCommand tree's right-click menu. One form handles both New
/// (pass <paramref name="existing"/> = null) and Edit (pass the selected item).
///
/// The whole dialog is one WebView2 page (Web/Shell/wcommandedit.html) — fields, hints and the
/// Save/Delete/Close bar — instead of a TableLayoutPanel of WinForms TextBoxes plus a separate
/// HTML button bar. C# keeps everything that touches the database; the page only collects
/// values and posts <c>{action, data}</c> messages (ready / suggest-wmenu / suggest-menu /
/// save / delete / close), and C# answers with window.* calls (init, setValue, setHint, ...).
///
/// Deliberately narrower than FCode's real dialog: no "Table Dir"/"Report Form"/"File" tabs
/// (not part of the wcommand row itself).
/// </summary>
public class WCommandEditForm : ThemedForm
{
    private readonly WCommandService _service;
    private WCommandItem? _existing;
    private WCommandItem? _seed;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _ready;
    private readonly string? _sourcePath;
    private int _tableVersion;

    /// <param name="existing">Non-null = Edit mode (Delete enabled, Save deletes this row's own
    /// id before inserting). Null = New mode.</param>
    /// <param name="template">New mode only — the menu the user right-clicked "New" on, if any.
    /// Every field is prefilled from it (same parent, same link/sysid/icon/...), so creating a
    /// menu similar to an existing one is just "New" on it, tweak the name, Save.</param>
    public WCommandEditForm(WCommandService service, WCommandItem? existing, WCommandItem? template = null, string? sourcePath = null)
    {
        _service = service;
        _sourcePath = sourcePath;
        _existing = existing;
        _seed = existing ?? template;

        Text = existing is null ? "WCOMMAND - New" : "WCOMMAND - Edit";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 700;
        Height = 760;
        MinimumSize = new Size(520, 560);
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
            _web.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("wcommandedit.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — WCommand",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                case "suggest-wmenu": await SuggestWMenuIdAsync(Read(data)); break;
                case "suggest-menu": await SuggestMenuIdAsync(Read(data)); break;
                case "save": await SaveAsync(Read(data)); break;
                case "delete": await DeleteAsync(); break;
                case "lookup-tables": await LookupTablesAsync(Get(Read(data), "sysId")); break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
    }

    /// <summary>Hộp thoại mở không chặn (modeless): bấm sang menu khác ở cây thì nạp thông tin menu đó vào đây (existing != null → chế độ Edit),
    /// hoặc làm menu mới từ mẫu (existing == null). Thay đổi chưa Save của menu đang mở sẽ bị thay.</summary>
    public async Task SwitchToAsync(WCommandItem? existing, WCommandItem? template = null)
    {
        if (IsDisposed) return;
        _existing = existing;
        _seed = existing ?? template;
        Text = existing is null ? "WCOMMAND - New" : "WCOMMAND - Edit";
        if (!_ready) return; // trang chưa nạp xong: lúc 'ready' sẽ đọc _existing/_seed mới
        await Js("document.querySelectorAll('.invalid').forEach(function (e) { e.classList.remove('invalid'); })");
        await Js("window.setStatus('')");
        await OnReadyAsync();
    }

    private async Task OnReadyAsync()
    {
        _ready = true;
        PushTheme();

        var s = _seed;
        var state = new
        {
            isNew = _existing is null,
            item = new Dictionary<string, string>
            {
                ["wmenuId"] = s?.WMenuId ?? "",
                ["wmenuId0"] = s?.WMenuId0 ?? "",
                ["menuId"] = s?.MenuId ?? "",
                ["bar"] = s?.Bar ?? "",
                ["bar2"] = s?.Bar2 ?? "",
                ["link"] = s?.Link ?? "",
                ["parameter"] = s?.Parameter ?? "",
                ["iconUrl"] = s?.IconUrl ?? "",
                ["status"] = s?.Status ?? "",
                ["icon"] = s?.Icon ?? "",
                ["sysId"] = s?.SysId ?? "",
                ["type"] = s?.Type ?? "",
                ["sysCode"] = s?.SysCode ?? "",
                ["msys"] = (s?.Msys ?? 0).ToString(CultureInfo.InvariantCulture),
                ["target"] = s?.Target ?? "",
                ["xtype"] = s?.XType ?? "",
                ["edition"] = s?.Edition ?? "",
                ["explIcon"] = (s?.ExplIcon ?? 0).ToString(CultureInfo.InvariantCulture),
            },
        };
        await Js($"window.init({JsonSerializer.Serialize(state)})");
        _ = LookupTablesAsync(s?.SysId ?? "");

        if (_existing is null)
        {
            // New: a free WMenu Id and Menu Id right away — also replaces a cloned template's own
            // (already taken) ids, which are exactly the fields a clone MUST change before Save.
            var item = state.item;
            await SuggestWMenuIdAsync(item, focus: false);
            await SuggestMenuIdAsync(item, focus: false);
            await Js("document.getElementById('bar').focus()");
        }
    }

    /// <summary>Đọc bảng m/d của Sysid từ file Dir/Grid của source (nền, vì đọc qua UNC) rồi đổ vào 2 ô chỉ-đọc.</summary>
    private async Task LookupTablesAsync(string sysId)
    {
        var version = ++_tableVersion;
        if (string.IsNullOrWhiteSpace(_sourcePath))
        {
            await Js("window.setTables('', '', 'Chưa chọn workspace có đường dẫn source.')");
            return;
        }
        await Js("window.setTables('', '', 'Đang đọc file Dir...')");
        var result = await Task.Run(() => Services.CommandTableService.Resolve(_sourcePath, sysId));
        if (version != _tableVersion || IsDisposed) return; // đã gõ Sysid khác trong lúc đọc
        await Js($"window.setTables({Json(result.Master)}, {Json(string.Join(", ", result.Details))}, {Json(result.Note)})");
    }

    // ---------------------------------------------------------------- suggestions

    private async Task SuggestWMenuIdAsync(Dictionary<string, string> f, bool focus = true)
    {
        await Js("window.setBusy(true)");
        try
        {
            var id = await _service.SuggestNextWMenuIdAsync(Get(f, "wmenuId0"));
            await Js(focus ? $"window.setValue('wmenuId', {Json(id)})" : $"document.getElementById('wmenuId').value = {Json(id)}");
            await Js($"window.setHint('w', {Json("Gợi ý: chưa tồn tại trong wcommand")}, 'ok')");
        }
        catch (Exception ex) { await Js($"window.setHint('w', {Json("Không gợi ý được: " + ex.Message)}, 'err')"); }
        finally { await Js("window.setBusy(false)"); }
    }

    private async Task SuggestMenuIdAsync(Dictionary<string, string> f, bool focus = true)
    {
        await Js("window.setBusy(true)");
        try
        {
            var id = await _service.SuggestNextMenuIdAsync(Get(f, "wmenuId0"), Get(f, "menuId"));
            if (id.Length == 0)
            {
                await Js($"window.setHint('m', {Json("Hết Menu Id trống (dạng GG.SS.LL, char(8)).")}, 'err')");
                return;
            }
            await Js(focus ? $"window.setValue('menuId', {Json(id)})" : $"document.getElementById('menuId').value = {Json(id)}");
            await Js($"window.setHint('m', {Json("Gợi ý: chưa tồn tại trong command/wcommand")}, 'ok')");
        }
        catch (Exception ex) { await Js($"window.setHint('m', {Json("Không gợi ý được: " + ex.Message)}, 'err')"); }
        finally { await Js("window.setBusy(false)"); }
    }

    // ---------------------------------------------------------------- save / delete

    private async Task SaveAsync(Dictionary<string, string> f)
    {
        var item = ToItemLoose(f);

        if (item.WMenuId.Length == 0) { await Invalid("wmenuId", "Nhập WMenu Id."); return; }
        if (item.MenuId.Length == 0) { await Invalid("menuId", "Nhập Menu Id."); return; }
        if (item.Bar.Trim().Length == 0) { await Invalid("bar", "Nhập Bar (tên menu)."); return; }

        // Giới hạn cột trong bảng wcommand (char/varchar) — vượt thì SQL cắt hoặc báo lỗi truncate.
        foreach (var (field, label, max) in new[]
        {
            ("wmenuId", "WMenu Id", 8), ("wmenuId0", "WMenu Id0", 8), ("menuId", "Menu Id", 8),
            ("status", "Status", 1), ("type", "Type", 1), ("sysCode", "Syscode", 3), ("edition", "Edition", 1),
            ("sysId", "Sysid", 64), ("target", "Target", 16), ("xtype", "Xtype", 8), ("icon", "Icon", 50),
        })
        {
            if (Get(f, field).Trim().Length > max) { await Invalid(field, $"{label} tối đa {max} ký tự."); return; }
        }
        if (!decimal.TryParse(Get(f, "msys"), NumberStyles.Any, CultureInfo.InvariantCulture, out var msys))
        { await Invalid("msys", "Msys phải là số."); return; }
        item.Msys = msys;
        if (!byte.TryParse(Get(f, "explIcon"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var icon))
        { await Invalid("explIcon", "Expl Icon phải là số từ 0 đến 255."); return; }
        item.ExplIcon = icon;

        await Js("window.setBusy(true)");
        await Js("window.setStatus('Đang lưu...')");
        try
        {
            // SaveAsync DELETE-rồi-INSERT theo id, nên id đã có người dùng sẽ bị ghi đè lặng lẽ —
            // chặn WMenu Id trùng, hỏi lại khi Menu Id đã gắn với menu khác.
            var (wExists, inCommand, inOtherWc) = await _service.CheckIdsAsync(item.WMenuId, item.MenuId, _existing?.WMenuId);
            if (wExists)
            {
                await Invalid("wmenuId", $"WMenu Id '{item.WMenuId}' đã tồn tại — bấm Suggest để lấy id khác.");
                return;
            }

            var menuIdChanged = _existing is null || !string.Equals(_existing.MenuId, item.MenuId, StringComparison.OrdinalIgnoreCase);
            if (menuIdChanged && (inCommand || inOtherWc))
            {
                var ask = MessageBox.Show(this,
                    $"Menu Id '{item.MenuId}' đã tồn tại" + (inCommand ? " trong bảng command" : "") +
                    (inOtherWc ? " và đang gắn với menu khác trong wcommand" : "") +
                    ".\nLưu sẽ GHI ĐÈ dòng command cũ (sysid/syscode/msys).\n\nVẫn lưu?",
                    "Bcode — WCommand", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ask != DialogResult.Yes)
                {
                    await Invalid("menuId", "Menu Id đã tồn tại — bấm Suggest để lấy id khác.");
                    return;
                }
            }

            await _service.SaveAsync(item, _existing?.WMenuId, _existing?.MenuId);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
        finally
        {
            if (!IsDisposed && _web.CoreWebView2 != null) await Js("window.setBusy(false)");
        }
    }

    private async Task DeleteAsync()
    {
        if (_existing is null) return;

        var confirm = MessageBox.Show(this,
            $"Xóa menu '{_existing.Bar}' ({_existing.WMenuId})?",
            "Bcode — WCommand", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        await Js("window.setBusy(true)");
        try
        {
            await _service.DeleteAsync(_existing);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
            await Js("window.setBusy(false)");
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task Invalid(string field, string message)
    {
        await Js($"window.setStatus({Json(message)}, 'err'); window.setInvalid({Json(field)})");
        await Js("window.setBusy(false)");
    }

    private WCommandItem ToItemLoose(Dictionary<string, string>? f = null)
    {
        string V(string k, string fallback) => f is null ? fallback : Get(f, k);
        return new WCommandItem
        {
            WMenuId = V("wmenuId", _seed?.WMenuId ?? "").Trim(),
            WMenuId0 = V("wmenuId0", _seed?.WMenuId0 ?? "").Trim(),
            MenuId = V("menuId", _seed?.MenuId ?? "").Trim(),
            Bar = V("bar", _seed?.Bar ?? ""),
            Bar2 = V("bar2", _seed?.Bar2 ?? ""),
            Link = V("link", _seed?.Link ?? ""),
            Parameter = V("parameter", _seed?.Parameter ?? ""),
            IconUrl = V("iconUrl", _seed?.IconUrl ?? ""),
            Status = V("status", _seed?.Status ?? ""),
            Icon = V("icon", _seed?.Icon ?? ""),
            SysId = V("sysId", _seed?.SysId ?? ""),
            Type = V("type", _seed?.Type ?? ""),
            SysCode = V("sysCode", _seed?.SysCode ?? ""),
            Target = V("target", _seed?.Target ?? ""),
            XType = V("xtype", _seed?.XType ?? ""),
            Edition = V("edition", _seed?.Edition ?? ""),
        };
    }

    /// <summary>Gom <c>data</c> của message thành field→chuỗi; ToItemLoose đọc lại theo tên.</summary>
    private static Dictionary<string, string> Read(JsonElement data)
    {
        var map = new Dictionary<string, string>();
        if (data.ValueKind != JsonValueKind.Object) return map;
        foreach (var p in data.EnumerateObject()) map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        return map;
    }

    private static string Get(Dictionary<string, string> f, string key) => f.TryGetValue(key, out var v) ? v : "";

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private async Task Js(string script)
    {
        if (!_ready && !script.StartsWith("window.setTheme")) return;
        if (IsDisposed || _web.CoreWebView2 is null) return;
        await _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }
}
