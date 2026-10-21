using System.Text.RegularExpressions;

namespace Bcode.App.Controls;

/// <summary>
/// Ctrl+G "Go to" bản WinForms (chế độ App của Bcode, ScriptEditorControl) — GIỐNG HỆT hộp của BcodeViewer (BcodeViewer.App/Web/goto.js,
/// chế độ Web dùng thẳng file đó qua filepreview.html): cùng cách chia TAG (fields, command, script, response, css, ENTITY) / DETAIL,
/// cùng mục được chọn sẵn (tag chứa con trỏ có ●, mục gần nhất phía trước con trỏ), cùng màu mục hay dùng (cam / đỏ) và cùng phím
/// (↑↓ trong cột, ←→ đổi cột, Enter / nhấp đúp = nhảy, Esc = đóng). Sửa luật quét / tô màu ở goto.js thì sửa cả <see cref="Scan"/>.
/// </summary>
internal static class GoToBox
{
    public static readonly string[] Tags = { "fields", "command", "script", "response", "css", "ENTITY" };

    /// <summary>1 mục: <see cref="Offset"/> = chỗ nhảy tới, <see cref="NameOffset"/> = vị trí tên (để tô trong editor), <see cref="Hot"/> = 'a' cam / 'b' đỏ / '\0'.</summary>
    public sealed record Item(string Label, int Offset, int NameOffset, char Hot);

    // Mục hay dùng — như GOTO_HOT / GOTO_HOT_RULES của goto.js (khoá viết thường).
    private static readonly Dictionary<string, Dictionary<string, char>> HotExact = new()
    {
        ["command"] = new() { ["inserted"] = 'a', ["updated"] = 'b' },
        ["script"] = new()
        {
            ["onchange$gridvoucherdetail"] = 'a',
            ["on$voucher$executecommand"] = 'b',
            ["on$voucher$responsecomplete"] = 'a',
        },
    };

    private static readonly (Regex Re, char Hot)[] HotScriptRules =
    {
        (new Regex(@"^on\$\w+\$executecommand$", RegexOptions.CultureInvariant), 'b'),
        (new Regex(@"^on\$\w+\$responsecomplete$", RegexOptions.CultureInvariant), 'a'),
        (new Regex(@"^onchange\$grid\w*\$?$", RegexOptions.CultureInvariant), 'a'),
    };

    public static readonly Color HotA = ColorTranslator.FromHtml("#e5a00d");
    public static readonly Color HotB = ColorTranslator.FromHtml("#f14c4c");

    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex CommandRe = new(@"<command\b[^>]*\bevent\s*=\s*""([^""]*)""", I);
    private static readonly Regex FunctionRe = new(@"function\s+([A-Za-z0-9_$]+)\s*\(", RegexOptions.CultureInvariant);
    private static readonly Regex ActionRe = new(@"<action\b[^>]*\bid\s*=\s*""([^""]*)""", I);
    private static readonly Regex EntityRe = new(@"<!ENTITY\s+%?\s*([A-Za-z0-9_.$-]+)", RegexOptions.CultureInvariant);

    /// <summary>Quét file → { tag: [mục] } — bản C# của goto.js scan().</summary>
    public static Dictionary<string, List<Item>> Scan(string text)
    {
        var o = Tags.ToDictionary(t => t, _ => new List<Item>());
        void Add(string tag, string label, int offset, int nameOffset = -1)
        {
            var key = label.ToLowerInvariant();
            var hot = HotExact.TryGetValue(tag, out var map) && map.TryGetValue(key, out var h) ? h : '\0';
            if (hot == '\0' && tag == "script") hot = HotScriptRules.FirstOrDefault(r => r.Re.IsMatch(key)).Hot;
            o[tag].Add(new Item(label, offset, nameOffset < 0 ? offset : nameOffset, hot));
        }
        int First(string pattern) { var m = Regex.Match(text, pattern, I); return m.Success ? m.Index : -1; }
        (int Start, int End)? Section(string open, string close)
        {
            var s = First(open);
            if (s < 0) return null;
            var m = Regex.Match(text[s..], close, I);
            return (s, m.Success ? s + m.Index + m.Length : text.Length);
        }

        var fStart = First(@"<fields\b");
        if (fStart >= 0) Add("fields", "Start", fStart);
        var fEnd = First(@"</fields>");
        if (fEnd >= 0) Add("fields", "End Fields", fEnd);
        var cat = First(@"<categor(?:y|ies)\b");
        if (cat >= 0) Add("fields", "Categories", cat);
        var vEnd = First(@"</views>");
        if (vEnd >= 0) Add("fields", "End Views", vEnd);

        foreach (Match m in CommandRe.Matches(text)) Add("command", m.Groups[1].Value, m.Index, m.Index + m.Value.LastIndexOf(m.Groups[1].Value, StringComparison.Ordinal));

        if (Section(@"<script\b", @"</script>") is { } scr)
            foreach (Match m in FunctionRe.Matches(text[scr.Start..scr.End])) Add("script", m.Groups[1].Value, scr.Start + m.Index, scr.Start + m.Groups[1].Index);

        if (Section(@"<response\b", @"</response>") is { } resp)
            foreach (Match m in ActionRe.Matches(text[resp.Start..resp.End])) Add("response", m.Groups[1].Value, resp.Start + m.Index);

        var css = First(@"<css\b");
        if (css >= 0)
        {
            Add("css", "Start", css);
            var cEnd = First(@"</css>");
            if (cEnd >= 0) Add("css", "End", cEnd);
        }

        foreach (Match m in EntityRe.Matches(text)) Add("ENTITY", m.Groups[1].Value, m.Index);
        return o;
    }

    /// <summary>Tag chứa con trỏ: mục có điểm bắt đầu gần nhất phía trước (như goto.js tagAt).</summary>
    private static string TagAt(Dictionary<string, List<Item>> data, int offset)
    {
        string? best = null;
        var bestStart = -1;
        foreach (var tag in Tags)
            foreach (var it in data[tag])
                if (it.Offset <= offset && it.Offset > bestStart) { best = tag; bestStart = it.Offset; }
        return best ?? "fields";
    }

    /// <summary>Mở hộp; trả vị trí (ký tự) để nhảy tới, null = đóng không chọn.</summary>
    public static int? Show(IWin32Window? owner, string text, int caret)
    {
        var data = Scan(text);
        var caretTag = TagAt(data, caret);
        var initial = 0;
        for (var i = 0; i < data[caretTag].Count; i++) if (data[caretTag][i].Offset <= caret) initial = i;
        using var box = new Box(data, caretTag, initial);
        return box.ShowDialog(owner) == DialogResult.OK ? box.Result : null;
    }

    /// <summary>Hộp vẽ theo CSS #gotoBox của BcodeViewer (Web/style.css "Ctrl+G"): viền + thanh tiêu đề xanh #0078d7, hàng TAG / DETAIL nền #333,
    /// 2 danh sách nền trắng chữ đen cao 22px, mục chọn nền xanh chữ trắng, mục hay dùng chữ đậm + vạch màu 3px bên trái.</summary>
    private sealed class Box : Bcode.App.UI.DpiForm
    {
        private static readonly Color Blue = ColorTranslator.FromHtml("#0078d7");
        private static readonly Color Dark = ColorTranslator.FromHtml("#333333");
        private static readonly Color TextA = ColorTranslator.FromHtml("#b86e00");
        private static readonly Color TextB = ColorTranslator.FromHtml("#d32f2f");

        private readonly Dictionary<string, List<Item>> _data;
        private readonly string _caretTag;
        private readonly ListBox _tags;
        private readonly ListBox _details;
        private readonly Font _font;
        private readonly Font _bold;
        private readonly Font _dot;
        private string _tag;
        public int? Result { get; private set; }

        public Box(Dictionary<string, List<Item>> data, string caretTag, int initialIndex)
        {
            _data = data;
            _caretTag = caretTag;
            _tag = caretTag;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            ClientSize = new Size(640, 400);
            BackColor = Blue;
            Padding = new Padding(1);
            _font = new Font("Segoe UI", 9f * (float)Bcode.App.UI.UiScale.Factor);
            _bold = new Font(_font, FontStyle.Bold);
            _dot = new Font(_font.FontFamily, _font.Size * 0.75f);
            Font = _font;

            var title = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Blue, Cursor = Cursors.SizeAll };
            var titleText = new Label { Text = "Go to", AutoSize = true, ForeColor = Color.White, Location = new Point(6, 4), BackColor = Color.Transparent };
            var close = new Label { Text = "✕", AutoSize = false, Size = new Size(24, 24), Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Cursor = Cursors.Hand };
            close.MouseEnter += (_, _) => close.BackColor = ColorTranslator.FromHtml("#e81123");
            close.MouseLeave += (_, _) => close.BackColor = Color.Transparent;
            close.Click += (_, _) => DialogResult = DialogResult.Cancel;
            title.Controls.Add(close);
            title.Controls.Add(titleText);
            // Kéo thanh tiêu đề để dời hộp.
            Point? grab = null;
            void Down(object? s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) grab = e.Location; }
            void Move(object? s, MouseEventArgs e) { if (grab is { } g) Location = new Point(Location.X + e.X - g.X, Location.Y + e.Y - g.Y); }
            void Up(object? s, MouseEventArgs e) => grab = null;
            foreach (var c in new Control[] { title, titleText }) { c.MouseDown += Down; c.MouseMove += Move; c.MouseUp += Up; }

            var head = new Panel { Dock = DockStyle.Top, Height = 20, BackColor = Dark };
            head.Controls.Add(new Label { Text = "DETAIL", Dock = DockStyle.Fill, ForeColor = Color.White, Font = _bold, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(7, 0, 0, 0) });
            head.Controls.Add(new Label { Text = "TAG", Dock = DockStyle.Left, Width = 135, ForeColor = Color.White, Font = _bold, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(2, 0, 0, 0) });

            _tags = MakeList();
            _tags.Dock = DockStyle.Left;
            _tags.Width = 135;
            _details = MakeList();
            _details.Dock = DockStyle.Fill;
            var gap = new Panel { Dock = DockStyle.Left, Width = 3, BackColor = Dark };
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Dark };
            body.Controls.Add(_details);
            body.Controls.Add(gap);
            body.Controls.Add(_tags);

            Controls.Add(body);
            Controls.Add(head);
            Controls.Add(title);

            foreach (var t in Tags) _tags.Items.Add(t);
            _tags.DrawItem += DrawTag;
            _details.DrawItem += DrawDetail;
            _tags.SelectedIndexChanged += (_, _) =>
            {
                if (_tags.SelectedItem is string t && t != _tag) { _tag = t; FillDetails(0); }
            };
            _details.DoubleClick += (_, _) => Go();
            _tags.SelectedIndex = Array.IndexOf(Tags, _tag);
            FillDetails(initialIndex);
            Shown += (_, _) => { Activate(); _details.Focus(); };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var h = (int)Math.Round(22 * DeviceDpi / 96.0 * Bcode.App.UI.UiScale.Factor);
            _tags.ItemHeight = h;
            _details.ItemHeight = h;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _font.Dispose(); _bold.Dispose(); _dot.Dispose(); }
            base.Dispose(disposing);
        }

        private ListBox MakeList() => new()
        {
            DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, BorderStyle = BorderStyle.None,
            BackColor = Color.White, ForeColor = Color.Black, ItemHeight = 22, Font = _font,
        };

        private void FillDetails(int index)
        {
            _details.BeginUpdate();
            _details.Items.Clear();
            var items = _data[_tag];
            if (items.Count == 0) _details.Items.Add("(không có mục nào)");
            else foreach (var it in items) _details.Items.Add(it);
            _details.EndUpdate();
            if (items.Count > 0) _details.SelectedIndex = Math.Clamp(index, 0, items.Count - 1);
        }

        private void Go()
        {
            if (_details.SelectedItem is not Item it) return;
            Result = it.Offset;
            DialogResult = DialogResult.OK;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: DialogResult = DialogResult.Cancel; return true;
                case Keys.Enter: Go(); return true;
                case Keys.Left: _tags.Focus(); return true;
                case Keys.Right: _details.Focus(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void DrawTag(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var tag = Tags[e.Index];
            var sel = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(sel ? Blue : Color.White);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var fg = sel ? Color.White : Color.Black;
            var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, tag, _font, r, fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (tag == _caretTag)
                TextRenderer.DrawText(e.Graphics, "●", _dot, r, fg, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        private void DrawDetail(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var sel = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            if (_details.Items[e.Index] is not Item it)
            {
                g.FillRectangle(Brushes.White, e.Bounds);
                TextRenderer.DrawText(g, _details.Items[e.Index]?.ToString(), _font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width, e.Bounds.Height), Color.Gray, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }
            using (var bg = new SolidBrush(sel ? Blue : Color.White)) g.FillRectangle(bg, e.Bounds);
            var x = e.Bounds.X + 4;
            if (it.Hot != '\0')
            {
                using var bar = new SolidBrush(it.Hot == 'a' ? HotA : HotB);
                g.FillRectangle(bar, e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height);
                x += 3;
            }
            var fg = sel ? Color.White : it.Hot == 'a' ? TextA : it.Hot == 'b' ? TextB : Color.Black;
            TextRenderer.DrawText(g, it.Label, it.Hot != '\0' ? _bold : _font, new Rectangle(x, e.Bounds.Y, e.Bounds.Right - x, e.Bounds.Height), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
