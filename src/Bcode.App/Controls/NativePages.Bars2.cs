using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Thanh trên của Compare Text (comparebar.html): Float · Compare · Sửa lại · ◀ ▶ khác biệt · Tô màu (ngôn ngữ) · File 1 / File 2.</summary>
internal sealed class CompareBarPage : NativePage
{
    private readonly RoundedInput _f1 = NK.Input("Chọn file hoặc gõ trực tiếp bên dưới..."), _f2 = NK.Input("Chọn file hoặc gõ trực tiếp bên dưới...");
    private readonly PillButton _edit, _prev, _next;
    private readonly Label _summary = NK.Muted("");
    private bool _loading;
    private static readonly string[] LangNames = { "Tự nhận", "SQL", "XML / FCode", "C#", "JavaScript", "JSON", "Text (không tô)" };
    private static readonly string[] LangValues = { "auto", "SQL", "XML", "C#", "JavaScript", "JSON", "Text" };

    public CompareBarPage()
    {
        _edit = NK.Button("✎ Sửa lại", () => Post(new { action = "edit" })); _prev = NK.Button("◀ Khác biệt trước", () => Post(new { action = "prev" })); _next = NK.Button("Khác biệt tiếp ▶", () => Post(new { action = "next" }));
        _edit.Visible = _prev.Visible = _next.Visible = false;
        // Ô "Tô màu" (như <select id="lang"> của comparebar.html): Tự nhận = đoán ngôn ngữ theo nội dung dán / gõ / chọn file.
        var langLabel = NK.Muted("Tô màu:"); langLabel.Margin = new Padding(10, 9, 4, 0);
        var lang = new DropDownButton { Width = NativeSkin.S(130), Height = NativeSkin.RowHeight(this, 30), Margin = new Padding(0, 3, 6, 3) };
        lang.SetItems(LangNames, 0);
        lang.SelectionChanged += i => Post(new { action = "lang", value = LangValues[i] });
        NK.Tip(lang, "Ngôn ngữ dùng để tô màu 2 khung — Tự nhận = đoán theo nội dung khi dán / gõ / chọn file");
        var tools = new BarFlow { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 5, 8, 2) };
        tools.Controls.AddRange(new Control[] { NK.Button("⧉ Float window", () => Post(new { action = "float" }), tip: "Mở cửa sổ rời"), NK.Button("📄 Compare", () => Post(new { action = "compare" }), primary: true), _edit, _prev, _next, langLabel, lang, _summary });
        var row = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = NativeSkin.RowHeight(this, 44), Padding = new Padding(8, 4, 8, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // thiếu dòng này thì hàng tự co theo nội dung → nhãn "File 1/2" và nút … bị cắt
        row.Controls.Add(Group("File 1:", _f1, 1)); row.Controls.Add(Group("File 2:", _f2, 2));
        _f1.TextChanged += (_, _) => { if (!_loading) Post(new { action = "update-path", which = "file1", value = _f1.Text }); };
        _f2.TextChanged += (_, _) => { if (!_loading) Post(new { action = "update-path", which = "file2", value = _f2.Text }); };
        Controls.Add(row); Controls.Add(tools);
    }

    private Control Group(string label, RoundedInput input, int n)
    {
        var p = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, n == 1 ? 12 : 0, 0) };
        var lb = NK.Muted(label); lb.Dock = DockStyle.Left; lb.AutoSize = false; lb.Width = NativeSkin.S(58); lb.TextAlign = ContentAlignment.MiddleLeft;
        var b = NK.Button("…", () => Post(new { action = "browse" + n }), tip: "Chọn file " + n); b.AutoWidth = false; b.Width = NativeSkin.S(38);
        var field = NK.InputWithButton(input, b, gap: 6); field.Dock = DockStyle.Fill;
        p.Controls.Add(field); p.Controls.Add(lb);
        return p;
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.setPaths":
                _loading = true;
                if (a.Count > 0 && a[0].ValueKind == JsonValueKind.String) _f1.Text = a[0].GetString() ?? "";
                if (a.Count > 1 && a[1].ValueKind == JsonValueKind.String) _f2.Text = a[1].GetString() ?? "";
                _loading = false;
                break;
            case "window.setMode":
                _edit.Visible = Bool(a, 0);
                _prev.Visible = _next.Visible = Bool(a, 0) && !(a.Count > 2 && a[2].ValueKind == JsonValueKind.False);   // đối số 3 = false: không có chỗ khác → ẩn ◀ ▶
                _summary.Text = Str(a, 1);
                break;
        }
    }
}

/// <summary>Thanh chế độ của cửa sổ "Tạo báo cáo" (reportmodes.html): ① Từ procedure có sẵn / ② Thiết kế từ bảng + thông báo "Dùng procedure này →".</summary>
internal sealed class ReportModesPage : NativePage
{
    private static readonly Dictionary<string, string> Hints = new()
    {
        ["design"] = "Chưa có procedure: chọn bảng & trường → “Mở trong tab SQL” để tạo procedure → “Dùng procedure này” để làm Filter / Grid từ source mẫu.",
        ["proc"] = "Đã có procedure: tham số thành ô lọc, kết quả chạy thành cột lưới, file lấy từ source mẫu CreateReport.",
    };

    private readonly PillButton _bProc, _bDesign;
    private readonly Label _hint = NK.Muted(""), _built = new() { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 8, 0) };
    private readonly PillButton _use, _dismiss;

    public ReportModesPage()
    {
        _bProc = NK.Button("① Từ procedure có sẵn", () => Post(new { action = "mode", mode = "proc" }), tip: "Đã có procedure: tham số → form lọc, kết quả chạy → lưới, file lấy từ source mẫu CreateReport");
        _bDesign = NK.Button("② Thiết kế từ bảng", () => Post(new { action = "mode", mode = "design" }), tip: "Chọn bảng / trường / cách thể hiện như Power BI — Bcode sinh procedure zrs_*");
        _use = NK.Button("Dùng procedure này →", () => { SetBuilt(null); Post(new { action = "useProc" }); }, primary: true);
        _dismiss = NK.Button("✕", () => { SetBuilt(null); Post(new { action = "dismiss" }); }, tip: "Ẩn"); _dismiss.AutoWidth = false; _dismiss.Width = NativeSkin.S(32);
        _use.Visible = _dismiss.Visible = _built.Visible = false;
        var bar = new BarFlow { Dock = DockStyle.Fill, Padding = new Padding(10, 5, 10, 5), WrapContents = true };
        bar.Controls.AddRange(new Control[] { new Label { Text = "▦ Tạo báo cáo", AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold), Margin = new Padding(0, 8, 12, 0) }, _bProc, _bDesign, _hint, _built, _use, _dismiss });
        Controls.Add(bar);
    }

    private void SetMode(string m)
    {
        _bProc.IsPrimary = m == "proc"; _bDesign.IsPrimary = m == "design"; _bProc.Invalidate(); _bDesign.Invalidate();
        _hint.Text = Hints.GetValueOrDefault(m, "");
    }

    private void SetBuilt(string? name)
    {
        var has = !string.IsNullOrEmpty(name);
        _built.Visible = _use.Visible = _dismiss.Visible = has; _hint.Visible = !has;
        if (has) _built.Text = $"Procedure {name} đã mở trong tab SQL — chạy (F5) để tạo trong database, rồi";
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "bar.init": { var d = Arg(a, 0); SetMode(P(d, "mode")); SetBuilt(P(d, "proc")); break; }
            case "bar.setMode": SetMode(Str(a, 0)); break;
            case "bar.onProc": SetBuilt(a.Count > 0 && a[0].ValueKind == JsonValueKind.String ? a[0].GetString() : null); break;
        }
    }
}
