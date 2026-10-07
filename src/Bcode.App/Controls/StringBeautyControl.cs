using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "String Beauty": dán SQL / JSON / XML / JavaScript rồi Beautify (SQL có kiểu Expanded / Compact và đổi hoa-thường từ khoá). Giao diện là trang
/// WebView2 (Web/Shell/stringbeauty.html — ăn theo Template giao diện, tự co giãn theo màn hình, kết quả tô màu cú pháp ngay trong trang); control này
/// chỉ nối trang với <see cref="SqlFormatterService"/> (logic định dạng giữ nguyên như form cũ <c>StringBeautyForm</c>) và clipboard.
/// </summary>
public class StringBeautyControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebBarHost _web = new("stringbeauty.html") { Dock = DockStyle.Fill };
    private readonly SqlFormatterService _formatter = new();

    public StringBeautyControl()
    {
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += () => Js($"stringBeauty.init({J(new { keywords = SqlSyntaxHighlighter.KeywordList })})");
    }

    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private async Task HandleAsync(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            switch (root.GetProperty("action").GetString())
            {
                case "beautify":
                {
                    string Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var type = Str("type"); var style = Str("style"); var kw = Str("kw"); var text = Str("text");
                    try
                    {
                        var result = await Task.Run(() => Beautify(type, style, kw, text));
                        Js($"stringBeauty.onResult({J(new { ok = true, text = result, type })})");
                    }
                    catch (Exception ex)
                    {
                        // Nội dung không hợp lệ với loại đã chọn (vd chọn JSON nhưng dán SQL): báo ngay trong trang, không để trống kết quả.
                        Js($"stringBeauty.onResult({J(new { ok = false, error = ex.Message, type })})");
                    }
                    break;
                }

                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard đang bị chương trình khác giữ */ }
                    break;

                case "paste":
                    try { Js($"stringBeauty.onPaste({J(Clipboard.ContainsText() ? Clipboard.GetText() : "")})"); } catch { /* clipboard bận */ }
                    break;
            }
        }
        catch (Exception ex)
        {
            Js($"stringBeauty.onResult({J(new { ok = false, error = ex.Message })})");
        }
    }

    /// <summary>Cùng logic với form cũ: SQL (Expanded/Compact + đổi hoa-thường từ khoá), JSON, XML, JavaScript.</summary>
    private string Beautify(string type, string style, string kw, string text)
    {
        switch (type)
        {
            case "SQL":
            {
                var result = _formatter.Format(text, compact: style == "Compact");
                return kw switch
                {
                    "upper" => SqlSyntaxHighlighter.TransformKeywordCase(result, toUpper: true),
                    "lower" => SqlSyntaxHighlighter.TransformKeywordCase(result, toUpper: false),
                    _ => result,
                };
            }
            case "JSON": return _formatter.FormatJson(text);
            case "XML": return _formatter.FormatXml(text);
            default: return _formatter.FormatJavaScript(text);
        }
    }
}
