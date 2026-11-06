using System.Text.Json;
using Bcode.App.Controls;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Ctrl+F3 ở lưới Table (phím table.editCell): sửa ĐỦ giá trị 1 ô (chuỗi dài HTML / XML / JSON / script không thấy hết trên lưới) trong khung soạn
/// có tô màu, rồi Lưu về đúng ô đó — nơi gọi ghi vào DataRow và để luồng tự lưu sẵn có của tab Table (AutoSaveRowAsync) ghi xuống bảng.
/// Khung soạn dùng lại của cửa sổ View Script: Monaco (Web/Shell/scriptpopup.html) ở chế độ Web, Scintilla ở chế độ App.
/// Ctrl+S / nút Lưu = OK, Esc / Huỷ = bỏ. Ô chỉ đọc (cột identity / tính toán, nhị phân…) thì chỉ xem, không có nút Lưu.
/// </summary>
public sealed class CellEditForm : ThemedForm
{
    private readonly Microsoft.Web.WebView2.WinForms.WebView2? _web;
    private readonly ScintillaNET.Scintilla? _sc;
    private TextBox? _fallback;
    private readonly string _original;
    private readonly string _lang;
    private readonly bool _readOnly;
    private bool _ready;

    /// <summary>Giá trị sau khi Lưu (chỉ hợp lệ khi DialogResult.OK).</summary>
    public string Value { get; private set; } = "";

    /// <param name="info">Dòng mô tả trên cùng (cột, kiểu, dòng…).</param>
    public CellEditForm(string title, string info, string value, bool readOnly)
    {
        Text = title;
        _original = value;
        _readOnly = readOnly;
        _lang = SourceViewForm.GuessValueLanguage(value);
        Width = 1000;
        Height = 700;
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
            else if (e.Control && e.KeyCode == Keys.S) { e.Handled = e.SuppressKeyPress = true; _ = SaveAsync(); }
        };

        var top = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0), AutoEllipsis = true, UseMnemonic = false,
            Text = info + (readOnly ? " · CHỈ XEM (ô không sửa được)" : " · Ctrl+S = Lưu, Esc = Huỷ") };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(8, 4, 0, 4) };
        bar.Controls.Add(NK.Button(readOnly ? "Đóng" : "Huỷ", Close));
        if (!readOnly) bar.Controls.Add(NK.Button("Lưu vào ô", () => _ = SaveAsync(), primary: true, tip: "Ghi giá trị vào ô rồi tự lưu dòng như khi sửa trên lưới (Ctrl+S)"));

        if (UiMode.IsNative)
        {
            _sc = new ScintillaNET.Scintilla { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
            SqlScintilla.Configure(_sc); SqlScintilla.ApplyTheme(_sc);
            if (_lang is "json") SqlScintilla.SetLanguage(_sc, "JSON");
            else if (_lang is "html" or "xml") SqlScintilla.SetLanguage(_sc, "XML");
            else if (_lang is "plaintext") _sc.Lexer = ScintillaNET.Lexer.Null;
            _sc.Text = value;
            _sc.EmptyUndoBuffer();
            _sc.ReadOnly = readOnly;
            ThemeManager.Subscribe(this, () => { if (!_sc.IsDisposed) SqlScintilla.ApplyTheme(_sc); });
            Controls.Add(_sc);
        }
        else
        {
            _web = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);
            _ = InitAsync();
        }
        Controls.Add(top);
        Controls.Add(bar);
        Load += (_, _) => top.ForeColor = AppColors.TextMuted;
    }

    private async Task InitAsync()
    {
        var web = _web!;
        try
        {
            await WebViewEnvironment.InitAsync(web);
            web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                string? action;
                try { using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString()); action = doc.RootElement.GetProperty("action").GetString(); }
                catch { return; }
                if (action == "ready")
                {
                    _ready = true;
                    _ = web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
                    Post(new { type = "load", text = _original, lang = _lang, readOnly = _readOnly });
                }
                else if (action == "save-key" && !_readOnly) BeginInvoke(new Action(() => _ = SaveAsync()));
            };
            web.CoreWebView2.Navigate(UiOverrides.UrlFor("scriptpopup.html"));
        }
        catch
        {
            // Không có WebView2 Runtime — sửa trong TextBox thường.
            Controls.Remove(web);
            _fallback = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = ThemeManager.MonoFont, Text = _original, ReadOnly = _readOnly };
            Controls.Add(_fallback);
            _fallback.BringToFront();
        }
    }

    private void Post(object message)
    {
        if (!_ready || _web?.CoreWebView2 is null) return;
        try { _web.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message)); } catch { /* đã đóng */ }
    }

    private async Task<string?> GetTextAsync()
    {
        if (_sc is not null) return _sc.Text;
        if (_fallback is not null) return _fallback.Text;
        if (!_ready || _web?.CoreWebView2 is null) return null;   // trang chưa nạp xong — chưa có gì để lưu
        var json = await _web.CoreWebView2.ExecuteScriptAsync("window.getText()");
        return JsonSerializer.Deserialize<string>(json);
    }

    private async Task SaveAsync()
    {
        if (_readOnly) return;
        var text = await GetTextAsync();
        if (text is null || IsDisposed) return;
        // Monaco đưa xuống dòng lẫn lộn về 1 kiểu: chỉ khác kiểu xuống dòng thì coi như không sửa (khỏi ghi lại cả ô).
        static string Norm(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text == _original || Norm(text) == Norm(_original)) { DialogResult = DialogResult.Cancel; Close(); return; }
        Value = text;
        DialogResult = DialogResult.OK;
        Close();
    }
}
