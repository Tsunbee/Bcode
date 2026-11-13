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
/// Deliberately narrower than FCode's real dialog: no "Report Form"/"File" tabs (not part of the
/// wcommand row itself). Bảng m / d đọc từ file Dir (chỉ xem) kèm nút "Mở" → tab Table (<see cref="OpenTableHandler"/>),
/// như link "Open Table" / "d$ Table" của FCode.
/// </summary>
public class WCommandEditForm : ThemedForm
{
    private readonly WCommandService _service;
    private WCommandItem? _existing;
    private WCommandItem? _seed;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Bcode.App.Controls.WebBarHost? _host;
    private bool _ready;
    private readonly string? _sourcePath;
    private int _tableVersion;
    /// <summary>Bảng m / d đã dò trong database (theo lần đọc file Dir gần nhất) — cho nút "Mở" cạnh 2 ô bảng.</summary>
    private List<(string Table, bool Sys, bool HasMaCt)> _openM = new(), _openD = new();

    /// <summary>Mở bảng sang tab Table của Bcode: (sys, bảng, where). Null = ẩn nút "Mở" (vd hộp mở từ Tạo báo cáo).
    /// Đặt trước khi Show; hộp vẫn mở sau khi bấm (không chặn — tab Table mở ở cửa sổ chính phía sau).</summary>
    public Action<bool, string, string>? OpenTableHandler { get; set; }

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
        if (Bcode.App.UI.UiMode.IsNative) _host = Bcode.App.Controls.NativeForm.Attach(this, "wcommandedit.html", root => _ = HandleAsync(root.GetRawText()), () => _ = HandleAsync("{\"action\":\"ready\"}"));
        else Controls.Add(_web);

        ThemeManager.ThemeChanged += PushTheme;
        FormClosed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
        if (_host is null) Load += async (_, _) => await InitWebAsync();
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

    private async void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e) => await HandleAsync(e.TryGetWebMessageAsString());

    private async Task HandleAsync(string json0)
    {
        try
        {
            using var doc = JsonDocument.Parse(json0);
            var action = doc.RootElement.GetProperty("action").GetString();
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;

            switch (action)
            {
                case "ready": await OnReadyAsync(); break;
                case "suggest-wmenu": await SuggestWMenuIdAsync(Read(data)); break;
                case "suggest-menu": await SuggestMenuIdAsync(Read(data)); break;
                case "save": await SaveAsync(Read(data)); break;
                case "delete": await DeleteAsync(); break;
                case "translate": await TranslateBarAsync(Get(Read(data), "text"), Get(Read(data), "lang")); break;
                case "lookup-tables": await LookupTablesAsync(Get(Read(data), "sysId")); break;
                case "open-table": await OpenTableAsync(Get(Read(data), "kind"), Get(Read(data), "sysCode")); break;
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
            canOpenTable = OpenTableHandler is not null,
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
        _openM = new(); _openD = new();
        if (string.IsNullOrWhiteSpace(_sourcePath))
        {
            await Js("window.setTables('', '', 'Chưa chọn workspace có đường dẫn source.')");
            return;
        }
        await Js("window.setTables('', '', 'Đang đọc file Dir...')");
        var result = await Task.Run(() => Services.CommandTableService.Resolve(_sourcePath, sysId));
        if (version != _tableVersion || IsDisposed) return; // đã gõ Sysid khác trong lúc đọc
        await Js($"window.setTables({Json(result.Master)}, {Json(string.Join(", ", result.Details))}, {Json(result.Note)})");
        await ProbeTablesAsync(version, result.Master, result.Details);
    }

    /// <summary>Dò bảng m / d có trong database không (App rồi Sys, bảng kỳ $000000 tính cả các kỳ) → bật / mờ nút "Mở" kèm chú thích.</summary>
    private async Task ProbeTablesAsync(int version, string master, List<string> details)
    {
        _openM = new(); _openD = new();
        if (OpenTableHandler is null) return;
        async Task<(List<(string Table, bool Sys, bool HasMaCt)> Found, string Tip)> Probe(IEnumerable<string> names, string kind)
        {
            var list = names.Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return (new(), $"File Dir không khai bảng {kind}.");
            var found = new List<(string Table, bool Sys, bool HasMaCt)>();
            var missing = new List<string>();
            foreach (var n in list)
            {
                var (exists, sys, maCt) = await _service.ProbeDataTableAsync(n);
                if (exists) found.Add((n, sys, maCt)); else missing.Add(n);
            }
            var tip = found.Count == 0
                ? $"Không có bảng {string.Join(", ", missing)} trong database đang chọn."
                : "Mở tab Table: " + string.Join(", ", found.Select(f => f.Table + (f.Sys ? " (Sys)" : "")))
                  + (missing.Count > 0 ? $" — không có: {string.Join(", ", missing)}" : "");
            return (found, tip);
        }
        object m, d;
        try
        {
            var pm = await Probe(new[] { master }, "m");
            var pd = await Probe(details, "d");
            if (version != _tableVersion || IsDisposed) return;
            _openM = pm.Found; _openD = pd.Found;
            m = new { ok = pm.Found.Count > 0, tip = pm.Tip };
            d = new { ok = pd.Found.Count > 0, tip = pd.Tip };
        }
        catch (Exception ex)
        {
            if (version != _tableVersion || IsDisposed) return;
            var tip = "Không kiểm được bảng trong database: " + ex.Message;
            m = new { ok = false, tip }; d = new { ok = false, tip };
        }
        await Js($"window.setTableState({JsonSerializer.Serialize(new { m, d })})");
    }

    /// <summary>Nút "Mở" cạnh ô Bảng m / Bảng d: mở tab Table với bảng đó (nhiều bảng d → chọn trong menu). Bảng có cột ma_ct
    /// (kể cả các bảng kỳ) và ô Syscode có giá trị → điền sẵn Where ma_ct = Syscode như View Relative Tables; xoá ô Where ở tab Table để xem hết.</summary>
    private async Task OpenTableAsync(string kind, string sysCode)
    {
        if (OpenTableHandler is not { } open) return;
        var list = kind == "d" ? _openD : _openM;
        if (list.Count == 0) return;
        sysCode = sysCode.Trim();
        async Task OpenOne((string Table, bool Sys, bool HasMaCt) t)
        {
            var where = t.HasMaCt && sysCode.Length > 0 ? "ma_ct = '" + sysCode.Replace("'", "''") + "'" : "";
            open(t.Sys, t.Table, where);
            await Js($"window.setStatus({Json("Đã mở tab Table: " + t.Table + (where.Length > 0 ? " — lọc sẵn " + where : ""))}, 'ok')");
        }
        if (list.Count == 1) { await OpenOne(list[0]); return; }
        var menu = new Bcode.App.Controls.WebMenu();
        foreach (var t in list) { var x = t; menu.Add(x.Table + (x.Sys ? " (Sys)" : ""), () => _ = OpenOne(x)); }
        menu.Show(this, Cursor.Position);
    }

    // ---------------------------------------------------------------- suggestions

    private async Task SuggestWMenuIdAsync(Dictionary<string, string> f, bool focus = true)
    {
        await Js("window.setBusy(true)");
        try
        {
            // Seed = id đang có trong ô (New: id của menu đang đứng trên cây) → giữ GG.SS., chỉ thay 2 ký tự cuối.
            var id = await _service.SuggestNextWMenuIdAsync(Get(f, "wmenuId0"), Get(f, "wmenuId"));
            await Js(focus ? $"window.setValue('wmenuId', {Json(id)})" : $"window.setFieldValue('wmenuId', {Json(id)})");
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
            await Js(focus ? $"window.setValue('menuId', {Json(id)})" : $"window.setFieldValue('menuId', {Json(id)})");
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
            _service.SyncQlycInBackground();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
        finally
        {
            if (!IsDisposed && (_host is not null || _web.CoreWebView2 != null)) await Js("window.setBusy(false)");
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

    /// <summary>Dịch Bar (tiếng Việt) sang ngôn ngữ chọn (<see cref="GoogleTranslateService"/>) rồi điền vào Bar2.</summary>
    private async Task TranslateBarAsync(string text, string lang)
    {
        var (result, error) = await GoogleTranslateService.TranslateAsync(text, lang);
        await Js($"window.setTranslated({Json(result ?? "")}, {(error is null ? "null" : Json(error))})");
    }

    private async Task Js(string script)
    {
        if (!_ready && !script.StartsWith("window.setTheme")) return;
        if (IsDisposed) return;
        if (_host is not null) { _host.Call(script); return; }
        if (_web.CoreWebView2 is null) return;
        await _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }
}
