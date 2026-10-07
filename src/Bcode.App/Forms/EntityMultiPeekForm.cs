using System.Text.Json;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// F12 khi bôi đen NHIỀU entity: xem trước nội dung từng entity theo thứ tự đã chọn, 2 cách:
///   • "Một trang"  — nối toàn bộ nội dung vào 1 khung (có dòng phân cách tên entity);
///   • "Từng entity" — mỗi entity 1 trang, chuyển bằng ◀ ▶ / Alt+← Alt+→ / bấm tên.
/// Giao diện là trang WebView2 (Web/Shell/entitypeek.html) dùng Monaco + theme của BcodeViewer, giống khung peek bên BcodeViewer.
/// Lựa chọn cách xem được nhớ trong AppSettings.EntityPeekMode. Cửa sổ không chặn (Show), chỉ đọc.
/// </summary>
public sealed class EntityMultiPeekForm : DpiForm
{
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly List<EntityPreviewItem> _items;
    private readonly AppSettings? _settings;
    private readonly Action<string> _openFile;
    private readonly Action<string, string, string> _peekValue;
    private readonly Action<List<EntityPreviewItem>> _multiPeek;
    private static string ThemeFilePath => Path.Combine(BcodePaths.AppData, "Bcode", "viewer-theme.json");

    public EntityMultiPeekForm(List<EntityPreviewItem> items, AppSettings? settings,
        Action<string> openFile, Action<string, string, string> peekValue, Action<List<EntityPreviewItem>> multiPeek)
    {
        _items = items;
        _settings = settings;
        _openFile = openFile;
        _peekValue = peekValue;
        _multiPeek = multiPeek;

        Text = $"{items.Count} entity đã chọn";
        Width = 1000;
        Height = 720;
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += (_, e) => OnPageMessage(e.TryGetWebMessageAsString());
            _web.CoreWebView2.Navigate(UiOverrides.UrlFor("entitypeek.html"));
        }
        catch (Exception ex)
        {
            Controls.Clear();
            Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(12), ForeColor = AppColors.TextMuted, Text = "Không khởi tạo được khung xem entity (WebView2).\r\n" + ex.Message });
        }
    }

    private void Post(object payload)
    {
        try { _web.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(payload)); }
        catch { /* đã đóng */ }
    }

    private void OnPageMessage(string raw)
    {
        // Đọc hết giá trị cần dùng TRƯỚC khi rời callback (JsonElement không dùng được sau khi tài liệu bị giải phóng).
        string? action; int index = -1, offset = 0, selStart = -1, selEnd = -1; string? text = null, mode = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            action = r.GetProperty("action").GetString();
            if (r.TryGetProperty("index", out var i)) index = i.GetInt32();
            if (r.TryGetProperty("offset", out var o)) offset = o.GetInt32();
            if (r.TryGetProperty("selStart", out var ss)) selStart = ss.GetInt32();
            if (r.TryGetProperty("selEnd", out var se)) selEnd = se.GetInt32();
            if (r.TryGetProperty("text", out var t)) text = t.GetString();
            if (r.TryGetProperty("mode", out var m)) mode = m.GetString();
        }
        catch { return; }

        switch (action)
        {
            case "ready":
                PushTheme();
                Post(new
                {
                    type = "init",
                    mode = string.Equals(_settings?.EntityPeekMode, "each", StringComparison.OrdinalIgnoreCase) ? "each" : "all",
                    items = _items.Select(i => new
                    {
                        name = i.Name, title = i.Title, subtitle = i.Subtitle, missing = i.Missing,
                        text = i.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
                        path = i.FilePath ?? "",
                        canOpen = !i.Missing,
                    }),
                });
                break;
            case "mode":
                if (_settings is not null && mode is "all" or "each" && _settings is not null)
                {
                    _settings.EntityPeekMode = mode!;
                    try { _settings.Save(); } catch { /* không lưu được thì chỉ mất ghi nhớ */ }
                }
                break;
            case "copy":
                if (text is not null) { try { Clipboard.SetText(text.Replace("\n", "\r\n")); } catch { /* clipboard bận */ } }
                break;
            case "close":
                BeginInvoke(new Action(Close));
                break;
            case "open":
                // BeginInvoke: mở cửa sổ khác (có thể có WebView2) sau khi thoát khỏi callback của trang này.
                if (index >= 0 && index < _items.Count)
                {
                    var it = _items[index];
                    BeginInvoke(new Action(() =>
                    {
                        if (it.FilePath is not null) _openFile(it.FilePath);
                        else if (!it.Missing) _peekValue(it.Name, it.Text, it.DeclaringPath ?? "");
                    }));
                }
                break;
            case "f12":
                if (index >= 0 && index < _items.Count) OnF12(_items[index], offset, selStart, selEnd);
                break;
        }
    }

    /// <summary>F12 trong nội dung 1 entity: phân giải đúng như F12 thường, lấy file khai báo của entity đó làm gốc tìm entity lồng bên trong.</summary>
    private void OnF12(EntityPreviewItem it, int offset, int selStart, int selEnd)
    {
        var basePath = it.DeclaringPath ?? it.FilePath;
        var text = it.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (basePath is null) return;
        if (selStart >= 0 && selEnd > selStart
            && ScriptEditorControl.ResolveSelectionEntities(text, selStart, selEnd - selStart, basePath) is { Count: >= 2 } many)
        {
            BeginInvoke(new Action(() => _multiPeek(many)));
            return;
        }
        var result = ScriptEditorControl.ResolveF12(text, offset, basePath);
        BeginInvoke(new Action(() =>
        {
            if (result.NavigatePath is { } target) _openFile(target);
            else if (result.PeekName is { } name) _peekValue(name, result.PeekValue ?? "", result.PeekDeclaringPath!);
            else if (result.Message is { } msg)
                MessageBox.Show(this, msg, "Bcode", MessageBoxButtons.OK, result.IsWarning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }));
    }

    /// <summary>Theme của BcodeViewer (viewer-theme.json) cho Monaco trong trang; không có file thì trang dùng Dark+ mặc định.</summary>
    private void PushTheme()
    {
        try
        {
            if (!File.Exists(ThemeFilePath)) return;
            using var theme = JsonDocument.Parse(File.ReadAllText(ThemeFilePath));
            _web.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "theme", theme = theme.RootElement }));
        }
        catch { /* file đang ghi dở hoặc không đọc được — giữ theme mặc định */ }
    }
}
