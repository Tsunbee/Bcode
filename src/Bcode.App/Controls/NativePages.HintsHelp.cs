using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Tab "Hướng dẫn gợi ý code SQL" (sqlhints-help.html): bảng gõ-gì-ra-gì, dữ liệu từ SqlHintCatalog; ô lọc trên đầu.</summary>
internal sealed class SqlHintsHelpPage : NativePage
{
    private static readonly (string Key, string What, string How)[] Fixed =
    {
        ("exec ten", "Gọi procedure có sẵn", "Gõ \"exec \" rồi vài chữ đầu của tên (vd exec rs_) → danh sách procedure (và các hàm có cách gọi quen thuộc), mỗi mục kèm chữ ký. Chọn bằng Tab / Enter → chèn sẵn tham số bắt buộc, mỗi tham số là một ô nhảy bằng Tab."),
        ("dbo. ten", "Gọi function / procedure", "Gõ \"dbo.\" rồi vài chữ (vd dbo.ff_pad) → mọi function và procedure khớp. Function chèn dạng dbo.ff_PadL(@cpItem, @nLen)."),
        ("ff_pad", "Hàm hay dùng — gõ ngay, không cần dbo.", "Gõ từ 3 chữ trở lên của một hàm / procedure nằm trong danh sách hay dùng (đánh dấu ★ hay dùng) là nó hiện lên, xếp trên cùng. Không nằm trong danh sách thì gõ \"exec \" hoặc \"dbo.\" trước."),
        ("( hoặc ,", "Nhắc tham số đang gõ", "Sau tên hàm gõ ( hoặc , ; hoặc sau \"exec tên \" gõ khoảng trắng / dấu phẩy → khung chữ ký hiện, tham số đang gõ được tô đậm. Gọi lại thủ công: Ctrl+Shift+Space."),
        ("a.", "Cột của bảng", "Sau FROM tên_bảng a hoặc JOIN tên_bảng b, gõ \"a.\" → danh sách cột của bảng đó, cột khoá chính (🔑) lên đầu. Alias không có (chỉ dùng tên bảng) vẫn được. Bảng tạm (#) chưa hỗ trợ."),
        ("name = '", "Tên cấu hình trong bảng options", "Trong câu có options, gõ name = ' → các tên cấu hình đang có trong database (vd m_round_tien), kèm giá trị hiện tại; nhóm m_round_* xếp trên cùng."),
        ("Tên snippet Library", "Snippet của bạn (Library)", "Gõ 2 chữ đầu của tên một snippet trong Library (chỉ hiện snippet chung + của dự án đang chọn) → chọn để chèn nguyên nội dung. Cũng gọi được bằng chuột phải trong editor hoặc Ctrl+P rồi gõ $."),
        ("gạch vàng", "Cảnh báo dùng biến chuẩn chưa khai báo", "Trong CREATE PROCEDURE, nếu thân dùng @Language @UserID @Admin @Unit @DateFrom @DateTo mà không có trong tham số và không DECLARE → gạch vàng dưới biến. Đặt con trỏ vào đó bấm bóng đèn hoặc Ctrl+. → Thêm tham số vào cuối danh sách tham số. (Function không có sửa nhanh.)"),
    };

    private readonly RoundedInput _q = NK.Input("Tìm trong hướng dẫn (vd: rsrep, bil, options, a.)…");
    private readonly FlowLayoutPanel _main = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 4, 12, 12) };
    private JsonElement _model;

    public SqlHintsHelpPage()
    {
        var head = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 46), Padding = new Padding(12, 8, 12, 6) };
        var title = new Label { Text = "Hướng dẫn gợi ý code SQL", AutoSize = true, Dock = DockStyle.Left, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 3, FontStyle.Bold), Padding = new Padding(0, 4, 20, 0) };
        _q.Dock = DockStyle.Fill;
        head.Controls.Add(_q); head.Controls.Add(title);
        _q.TextChanged += (_, _) => Render();
        _main.Resize += (_, _) => { foreach (Control c in _main.Controls) c.Width = Math.Max(300, _main.ClientSize.Width - 28); };
        Controls.Add(_main); Controls.Add(head);
    }

    private static string Preview(string body) => Regex.Replace(Regex.Replace(Regex.Replace(body, @"\$\{\d+:([^}|]*)\}", "$1"), @"\$\{\d+\|([^,|]*)[^}]*\}", "$1"), @"\$\{\d+\}|\$\d", "");

    private DataGridView Table(params (string Title, int Width)[] cols)
    {
        var g = new DataGridView
        {
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, BorderStyle = BorderStyle.None, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, ScrollBars = ScrollBars.None, Height = NativeSkin.S(60),
        };
        g.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        foreach (var (t, w) in cols) { var c = new DataGridViewTextBoxColumn { HeaderText = t, SortMode = DataGridViewColumnSortMode.NotSortable }; if (w > 0) c.Width = w; else c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; g.Columns.Add(c); }
        g.RowsAdded += (_, _) => FitHeight(g);
        g.Resize += (_, _) => FitHeight(g);
        return g;
    }

    private static void FitHeight(DataGridView g)
    {
        var h = g.ColumnHeadersHeight; foreach (DataGridViewRow r in g.Rows) h += r.Height;
        var want = Math.Max(h + 4, 40);
        if (g.Height != want) g.Height = want;
    }

    private void Section(string heading, string? intro, DataGridView? grid)
    {
        var h = new Label { Text = heading, AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 1.5f, FontStyle.Bold), Margin = new Padding(0, 14, 0, 4) };
        _main.Controls.Add(h);
        if (intro is not null) { var l = NK.Muted(intro, true); l.MaximumSize = new Size(Math.Max(300, _main.ClientSize.Width - 40), 0); _main.Controls.Add(l); }
        if (grid is not null) _main.Controls.Add(grid);
    }

    private void Render()
    {
        var q = _q.Text.Trim().ToLowerInvariant();
        bool Hit(string s) => q.Length == 0 || s.ToLowerInvariant().Contains(q);
        _main.SuspendLayout();
        foreach (Control c in _main.Controls) c.Dispose();
        _main.Controls.Clear();
        var intro = NK.Muted("Gõ vài ký tự trong editor SQL Query để Bcode gợi ý. Tab hoặc Enter = chấp nhận; trong đoạn vừa chèn, Tab nhảy sang ô kế (chữ mờ là giá trị mặc định, gõ đè được). Esc = tắt gợi ý; Ctrl+Space = mở lại thủ công.\nDữ liệu tên procedure / function / cột / options lấy từ database đang chọn (App Data hoặc Sys Data, theo ô chọn trên thanh Execute) và được lưu cache — lần đầu có thể mất vài giây.", true);
        intro.MaximumSize = new Size(Math.Max(300, _main.ClientSize.Width - 40), 0); _main.Controls.Add(intro);

        IEnumerable<JsonElement> Arr(string n) => _model.ValueKind == JsonValueKind.Object ? PA(_model, n) : Enumerable.Empty<JsonElement>();

        var sn = Arr("snippets").Where(s => Hit(P(s, "prefix") + " " + P(s, "name") + " " + P(s, "desc") + " " + P(s, "category"))).ToList();
        var g1 = Table(("Gõ", 110), ("Ra", 0), ("Nhóm", 120));
        foreach (var s in sn) g1.Rows.Add(P(s, "prefix"), P(s, "name") + "\n" + P(s, "desc") + "\n\n" + Preview(P(s, "body")), P(s, "category"));
        if (sn.Count == 0) g1.Rows.Add("", "Không có mẫu khớp.", "");
        Section("1. Gõ tắt ra cả khối — gõ ≥ 2 chữ đầu của mã", null, g1);

        var ci = Fixed.Where(x => Hit(x.Key + " " + x.What + " " + x.How)).ToList();
        var g2 = Table(("Bạn gõ", 160), ("Xuất hiện", 0));
        foreach (var x in ci) g2.Rows.Add(x.Key, x.What + "\n" + x.How);
        if (ci.Count == 0) g2.Rows.Add("", "Không có mục khớp.");
        Section("2. Gợi ý theo ngữ cảnh", null, g2);

        var sm = Arr("smart").Where(x => Hit(P(x, "usage") + " " + P(x, "example") + " " + P(x, "text"))).ToList();
        var g3 = Table(("Gõ", 180), ("Ví dụ", 260), ("Ra", 0));
        foreach (var x in sm) g3.Rows.Add(P(x, "usage"), P(x, "example"), P(x, "text"));
        if (sm.Count == 0) g3.Rows.Add("", "", "Không có mục khớp.");
        Section("2b. Gõ tên mẫu + tên bảng → điền sẵn cột của bảng", "Gõ tên mẫu, dấu cách, rồi tên bảng (bảng tạm / biến bảng đã khai báo trong script, hoặc bảng trong database). Bcode lấy HẾT cột của bảng đó điền vào mẫu — chọn dòng gợi ý bằng Tab / Enter.", g3);

        var id = Arr("idioms").Where(x => Hit(P(x, "routine") + " " + P(x, "insert") + " " + P(x, "note"))).ToList();
        var g4 = Table(("Hàm", 260), ("Chèn ra", 0), ("Ghi chú", 220));
        foreach (var x in id) g4.Rows.Add(P(x, "routine"), Preview(P(x, "insert")), P(x, "note"));
        if (id.Count == 0) g4.Rows.Add("", "Không có mục khớp.", "");
        Section("3. Cách gọi quen thuộc — chọn hàm trong gợi ý thì chèn đúng dạng này", $"Với các hàm dưới đây Bcode dùng đúng cách gọi hay gặp nhất trong code mẫu ({(_model.ValueKind == JsonValueKind.Object ? PI(_model, "popularCount") : 0)} hàm hay dùng được xếp lên đầu danh sách).", g4);

        var nt = Arr("notes").Where(x => Hit(P(x, "title") + " " + P(x, "prefixes") + " " + P(x, "text"))).ToList();
        var g5 = Table(("Nhóm", 200), ("Gõ", 220), ("Ghi chú", 0));
        foreach (var x in nt) g5.Rows.Add(P(x, "title"), P(x, "prefixes"), P(x, "text"));
        if (nt.Count == 0) g5.Rows.Add("", "", "Không có ghi chú khớp.");
        Section("4. Ghi chú theo nhóm hàm FBO", null, g5);

        foreach (Control c in _main.Controls) c.Width = Math.Max(300, _main.ClientSize.Width - 28);
        _main.ResumeLayout(true);
        foreach (Control c in _main.Controls) if (c is DataGridView g) FitHeight(g);
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "help.init") return;
        _model = a.Count > 0 ? a[0] : default;
        Render();
    }

    public override void FocusPrimary() => _q.Inner.Focus();
}
