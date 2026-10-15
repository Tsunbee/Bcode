using Bcode.App.Services;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// "String Beauty" ở chế độ App/WinForms: dán SQL / JSON / XML / JavaScript rồi Beautify (SQL có kiểu Expanded / Compact và đổi hoa-thường từ khoá). Cùng logic với
/// <see cref="StringBeautyControl"/> (trang WebView2): dùng <see cref="SqlFormatterService"/>; kết quả tô màu cú pháp bằng Scintilla (chỉ đọc, có số dòng).
/// Ctrl+Enter = Beautify; dán JSON/XML vào lúc đang ở SQL thì tự đổi loại.
/// </summary>
public sealed class NativeStringBeautyControl : UserControl
{
    private readonly SqlFormatterService _formatter = new();
    private readonly SegmentedBar _type, _style, _kw;
    private readonly Label _lblStyle = NK.Text("Kiểu SQL"), _lblKw = NK.Text("Từ khoá");
    private readonly CheckBox _auto = NK.Check("Tự chạy khi dán"), _wrap = NK.Check("Wrap");
    private readonly Scintilla _in = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
    private readonly Scintilla _out = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
    private readonly Label _inMeta = NK.Muted(""), _outMeta = NK.Muted(""), _state = new() { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(8, 8, 0, 0) };
    private readonly Label _err = new() { Dock = DockStyle.Top, Visible = false, Padding = new Padding(8, 6, 8, 6), ForeColor = Color.White, AutoSize = false, Height = NativeSkin.S(30) };
    private readonly PillButton _run, _copy;
    private string _result = "";
    private bool _busy;

    public NativeStringBeautyControl()
    {
        Dock = DockStyle.Fill;

        _type = new SegmentedBar(("SQL", "SQL"), ("JSON", "JSON"), ("XML", "XML"), ("JavaScript", "JavaScript"));
        _style = new SegmentedBar(("Expanded", "Expanded"), ("Compact", "Compact"));
        _kw = new SegmentedBar(("keep", "Giữ nguyên"), ("upper", "VIẾT HOA"), ("lower", "viết thường"));
        NK.Tip(_style, "Expanded: tách dòng theo mệnh đề, thụt lề theo BEGIN/END · Compact: gộp về 1 dòng");
        _type.Changed += v => { SetSqlOnly(); if (_in.TextLength > 0) _ = RunAsync(); };
        _style.Changed += v => { if (_in.TextLength > 0 && _result.Length > 0) _ = RunAsync(); };
        _kw.Changed += v => { if (_in.TextLength > 0 && _result.Length > 0) _ = RunAsync(); };
        _auto.Checked = true;
        _wrap.CheckedChanged += (_, _) => { var m = _wrap.Checked ? WrapMode.Word : WrapMode.None; _in.WrapMode = m; _out.WrapMode = m; };

        var opts = new BarFlow { Dock = DockStyle.Top, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 6) };
        opts.Controls.AddRange(new Control[] { NK.Text("Loại"), _type, _lblStyle, _style, _lblKw, _kw, _auto, _wrap });

        // ---- hai khung ----
        var paneIn = Pane("Nội dung gốc", _in, _inMeta, NK.Button("Dán từ clipboard", PasteFromClipboard), NK.Button("Xoá", () => { _in.Text = ""; _inMeta.Text = ""; _in.Focus(); }));
        _copy = NK.Button("Copy", () => { NK.CopyText(_result); SetState("Đã copy kết quả.", ok: true); }); _copy.Enabled = false;
        var swap = NK.Button("← Dùng làm nội dung gốc", () => { if (_result.Length == 0) return; _in.Text = _result; _inMeta.Text = Stat(_result); SetState("Đã đưa kết quả về khung nội dung gốc.", ok: true); }, tip: "Đưa kết quả về khung bên trái để beautify tiếp");
        _out.Margins[0].Width = 0;
        var outHost = new Panel { Dock = DockStyle.Fill };
        outHost.Controls.Add(_out); outHost.Controls.Add(_err);
        var paneOut = Pane("Kết quả", outHost, _outMeta, swap, _copy);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(paneIn); split.Panel2.Controls.Add(paneOut);

        _run = NK.Button("Beautify →", () => _ = RunAsync(), primary: true);
        var bar = new BarFlow { Dock = DockStyle.Bottom, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 6), FlowDirection = FlowDirection.LeftToRight };
        bar.Controls.Add(_state); bar.Controls.Add(NK.Muted("Ctrl+Enter = Beautify")); bar.Controls.Add(_run);

        Controls.Add(split); Controls.Add(opts); Controls.Add(bar);

        foreach (var ed in new[] { _in, _out }) { SqlScintilla.Configure(ed); ed.Margins[0].Type = MarginType.Number; }
        _out.ReadOnly = true; _in.Lexer = Lexer.Null;
        _in.TextChanged += (_, _) => _inMeta.Text = Stat(_in.Text);
        HandleCreated += (_, _) => BeginInvoke(new Action(() => { try { split.SplitterDistance = split.Width / 2; } catch { } ApplyStyles(); }));
        ThemeManager.ThemeChanged += ApplyStyles;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= ApplyStyles;
        SetSqlOnly();
    }

    private Control Pane(string title, Control body, Label meta, params Control[] buttons)
    {
        var host = new Panel { Dock = DockStyle.Fill };
        var head = new BarFlow { Dock = DockStyle.Top, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 5, 8, 5) };
        head.Controls.Add(new Label { Text = title, AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold), Margin = new Padding(0, 7, 8, 0) });
        head.Controls.Add(meta);
        foreach (var b in buttons) head.Controls.Add(b);
        body.Dock = DockStyle.Fill;
        host.Controls.Add(body); host.Controls.Add(head);
        return host;
    }

    private void ApplyStyles()
    {
        foreach (var ed in new[] { _in, _out }) { SqlScintilla.ApplyTheme(ed); ed.Margins[0].Width = ed == _in ? 0 : ed.TextWidth(Style.LineNumber, "99999") + 10; }
        _in.Lexer = Lexer.Null;
        SqlScintilla.SetLanguage(_out, _type.Value);
    }

    private void SetSqlOnly()
    {
        var sql = _type.Value == "SQL";
        _style.Enabled = sql; _kw.Enabled = sql; _lblStyle.Enabled = sql; _lblKw.Enabled = sql;
    }

    private static string Stat(string t) => string.IsNullOrEmpty(t) ? "" : $"{t.Split('\n').Length} dòng · {t.Length:N0} ký tự";

    private void SetState(string text, bool error = false, bool ok = false)
    {
        _state.Text = text;
        _state.ForeColor = error ? AppColors.Danger : ok ? AppColors.Success : AppColors.TextMuted;
    }

    private void PasteFromClipboard()
    {
        string t;
        try { t = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { t = ""; }
        if (t.Length == 0) { SetState("Clipboard không có chữ.", error: true); return; }
        _in.Text = t;
        // đoán loại theo nội dung để đỡ phải đổi tay: bắt đầu bằng { / [ → JSON, < → XML
        var s = t.TrimStart();
        var guess = s.StartsWith('{') || s.StartsWith('[') ? "JSON" : s.StartsWith('<') ? "XML" : null;
        if (guess is not null && guess != _type.Value && _type.Value == "SQL") { _type.Select(guess, raise: true); return; }
        if (_auto.Checked) _ = RunAsync();
    }

    private async Task RunAsync()
    {
        var text = _in.Text ?? "";
        if (text.Trim().Length == 0) { SetState("Chưa có nội dung để beautify.", error: true); return; }
        if (_busy) return;
        _busy = true; _run.Enabled = false; SetState("Đang xử lý...");
        var type = _type.Value; var style = _style.Value; var kw = _kw.Value;
        try
        {
            var result = await Task.Run(() => Beautify(type, style, kw, text));
            _err.Visible = false;
            _result = result;
            _out.ReadOnly = false; _out.Text = result; _out.ReadOnly = true; _out.GotoPosition(0);
            SqlScintilla.SetLanguage(_out, type);
            _outMeta.Text = Stat(result); _copy.Enabled = true;
            SetState("Đã beautify.", ok: true);
        }
        catch (Exception ex)
        {
            // Nội dung không hợp lệ với loại đã chọn (vd chọn JSON nhưng dán SQL): báo ngay trong khung kết quả.
            _err.BackColor = AppColors.Danger; _err.Text = "Không beautify được: " + ex.Message; _err.Visible = true;
            SetState("Không beautify được.", error: true);
        }
        finally { _busy = false; _run.Enabled = true; }
    }

    /// <summary>Cùng logic với trang web: SQL (Expanded/Compact + đổi hoa-thường từ khoá), JSON, XML, JavaScript.</summary>
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter)) { _ = RunAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
