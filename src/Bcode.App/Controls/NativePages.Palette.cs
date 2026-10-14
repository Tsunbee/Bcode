using System.Globalization;
using System.Text;
using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Command Palette (commandpalette.html): tìm mờ theo từng ký tự trên danh sách đã nạp sẵn — cùng thuật toán chấm điểm với trang web.</summary>
internal sealed class CommandPalettePage : NativePage
{
    private sealed class Item { public string Id = "", Kind = "", Title = "", Sub = "", Key = "", T = "", S = "", Grp = ""; }

    private static readonly Dictionary<string, string> KindName = new() { ["tool"] = "Tool", ["cmd"] = "Lệnh", ["tab"] = "Tab", ["obj"] = "SQL", ["menu"] = "Menu", ["snip"] = "Snippet" };
    private static readonly string[] Order = { "tab", "tool", "cmd", "snip", "menu", "obj" };
    private static readonly Dictionary<string, string> Group = new() { ["tab"] = "Tab đang mở", ["tool"] = "Công cụ", ["cmd"] = "Lệnh", ["menu"] = "Menu WCommand", ["obj"] = "Object SQL", ["snip"] = "Snippet" };
    private const int MaxShow = 80;

    private readonly TextBox _q = new() { BorderStyle = BorderStyle.None, PlaceholderText = "Gõ để tìm tool, tab, lệnh, menu, object SQL…  (> lệnh · # object · @ menu · $ snippet)" };
    private readonly Panel _qHost = new() { Dock = DockStyle.Top, Padding = new Padding(12, 10, 12, 8) };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawVariable };
    private readonly Label _foot = new() { Dock = DockStyle.Bottom, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 12, 0) };
    private List<Item> _items = new();
    private List<string> _recent = new();
    private List<Item> _shown = new();
    private string _total = "";

    public CommandPalettePage()
    {
        _q.Dock = DockStyle.Top; _qHost.Controls.Add(_q); _qHost.Height = NativeSkin.RowHeight(this, 40);
        _foot.Height = NativeSkin.RowHeight(this, 26);
        _list.MeasureItem += (_, e) => e.ItemHeight = _list.Font.Height + 12;
        _list.DrawItem += Draw;
        _q.TextChanged += (_, _) => Compute();
        _list.MouseMove += (_, e) => { var i = _list.IndexFromPoint(e.Location); if (i >= 0 && i != _list.SelectedIndex) _list.SelectedIndex = i; };
        _list.MouseClick += (_, e) => { var i = _list.IndexFromPoint(e.Location); if (i >= 0) Choose(ModifierKeys.HasFlag(Keys.Shift)); };
        _list.SelectedIndexChanged += (_, _) => UpdateFoot();
        Controls.Add(_list); Controls.Add(_foot); Controls.Add(_qHost);
        HandleCreated += (_, _) => Theme();
        ThemeManager.ThemeChanged += Theme;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= Theme;
        Theme();
    }

    private void Theme()
    {
        BackColor = AppColors.Panel; _qHost.BackColor = AppColors.PanelAlt; _q.BackColor = AppColors.PanelAlt; _q.ForeColor = AppColors.Text;
        _list.BackColor = AppColors.Panel; _foot.BackColor = AppColors.PanelAlt; _foot.ForeColor = AppColors.TextMuted;
        _q.Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 1.5f);
    }

    // ---- tìm mờ (cùng thuật toán với commandpalette.html) ----
    private static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch == 'đ' || ch == 'Đ' ? 'd' : char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static string Norm(string? s) { var f = Fold(s); var sb = new StringBuilder(f.Length); foreach (var c in f) sb.Append("\\/()*>|,".IndexOf(c) >= 0 ? ' ' : c); return sb.ToString(); }

    private static bool WordStart(string hay, int i) => i == 0 || !char.IsLetterOrDigit(hay[i - 1]) || hay[i - 1] > 'z';

    private static int ScoreText(string hay, string needle)
    {
        if (needle.Length == 0) return 0;
        var idx = hay.IndexOf(needle, StringComparison.Ordinal);
        if (idx >= 0) return 100 - Math.Min(idx, 50) + (idx == 0 || !IsAlnum(hay[idx - 1]) ? 40 : 0);
        int i = 0, last = -2, sc = 0;
        for (var c = 0; c < hay.Length && i < needle.Length; c++)
            if (hay[c] == needle[i]) { sc += (c == last + 1 ? 6 : 2) + (c == 0 || !IsAlnum(hay[c - 1]) ? 5 : 0); last = c; i++; }
        return i == needle.Length ? sc : -1;
    }

    private static bool IsAlnum(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

    private static int Match(Item it, string[] tokens)
    {
        var total = 0;
        foreach (var t in tokens)
        {
            var a = ScoreText(it.T, t); var b = it.S.Length > 0 ? ScoreText(it.S, t) - 30 : -1;
            var best = Math.Max(a, b);
            if (best < 0) return -1;
            total += best;
        }
        return total;
    }

    private void Compute()
    {
        var q = _q.Text; string[]? only = null;
        if (q.Length > 0)
        {
            switch (q[0])
            {
                case '>': only = new[] { "tool", "cmd" }; q = q[1..]; break;
                case '#': only = new[] { "obj" }; q = q[1..]; break;
                case '@': only = new[] { "menu" }; q = q[1..]; break;
                case '$': only = new[] { "snip" }; q = q[1..]; break;
            }
        }
        var tokens = Norm(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pool = only is null ? _items : _items.Where(i => only.Contains(i.Kind)).ToList();
        var rank = _recent.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
        List<Item> outl;
        if (tokens.Length == 0)
        {
            var rec = pool.Where(i => rank.ContainsKey(i.Id)).OrderBy(i => rank[i.Id]).ToList();
            foreach (var r in rec) r.Grp = "Gần đây";
            var seen = rec.Select(i => i.Id).ToHashSet();
            var rest = pool.Where(i => !seen.Contains(i.Id) && (only is not null || i.Kind is "tab" or "tool")).ToList();
            foreach (var r in rest) r.Grp = Group.GetValueOrDefault(r.Kind, "");
            outl = rec.Concat(rest).ToList();
        }
        else
        {
            outl = pool.Select(i => (i, sc: Match(i, tokens))).Where(x => x.sc >= 0)
                .Select(x => (x.i, sc: x.sc + (rank.TryGetValue(x.i.Id, out var rk) ? 25 - rk : 0) + (x.i.Kind is "tool" or "tab" ? 8 : 0)))
                .OrderByDescending(x => x.sc).ThenBy(x => Array.IndexOf(Order, x.i.Kind)).Select(x => x.i).ToList();
            foreach (var r in outl) r.Grp = "";
        }
        _shown = outl.Take(MaxShow).ToList();
        _total = outl.Count > _shown.Count ? $"{_shown.Count}/{outl.Count} mục" : $"{outl.Count} mục";
        _list.BeginUpdate(); _list.Items.Clear();
        foreach (var it in _shown) _list.Items.Add(it.Title);
        _list.EndUpdate();
        if (_shown.Count > 0) _list.SelectedIndex = 0;
        UpdateFoot();
        _list.Invalidate();
    }

    private void UpdateFoot()
    {
        if (_shown.Count == 0) { _foot.Text = "Không tìm thấy mục nào khớp."; return; }
        var it = _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count ? _shown[_list.SelectedIndex] : null;
        _foot.Text = "↑↓ chọn   Enter mở" + (it?.Kind == "obj" ? "   Shift+Enter object SQL: ai đang dùng?" : "") + "      " + _total;
    }

    private void Draw(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _shown.Count) return;
        var it = _shown[e.Index]; var sel = (e.State & DrawItemState.Selected) != 0;
        var g = e.Graphics; var b = e.Bounds;
        using (var br = new SolidBrush(sel ? AppColors.Accent : AppColors.Panel)) g.FillRectangle(br, b);
        var fore = sel ? AppColors.OnAccent : AppColors.Text; var muted = sel ? AppColors.OnAccent : AppColors.TextMuted;
        var f = _list.Font; var x = b.X + 12;
        var tag = KindName.GetValueOrDefault(it.Kind, it.Kind);
        var tw = TextRenderer.MeasureText(g, tag, f).Width + 12;
        var tagRect = new Rectangle(x, b.Y + 4, tw, b.Height - 8);
        using (var tb = new SolidBrush(sel ? Color.FromArgb(60, 255, 255, 255) : AppColors.PanelAlt)) g.FillRectangle(tb, tagRect);
        TextRenderer.DrawText(g, tag, f, tagRect, muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        x += tw + 10;
        var keyW = it.Key.Length > 0 ? TextRenderer.MeasureText(g, it.Key, f).Width + 8 : 0;
        var avail = b.Right - x - 12 - keyW;
        var title = new Rectangle(x, b.Y, Math.Max(40, (int)(avail * (it.Sub.Length > 0 ? 0.55 : 1))), b.Height);
        TextRenderer.DrawText(g, it.Title, f, title, fore, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (it.Sub.Length > 0)
        {
            var sub = new Rectangle(title.Right + 8, b.Y, Math.Max(0, avail - title.Width - 8), b.Height);
            TextRenderer.DrawText(g, it.Sub, f, sub, muted, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        if (keyW > 0) TextRenderer.DrawText(g, it.Key, f, new Rectangle(b.Right - keyW - 10, b.Y, keyW, b.Height), muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void Move(int d)
    {
        if (_shown.Count == 0) return;
        _list.SelectedIndex = (_list.SelectedIndex + d + _shown.Count) % _shown.Count;
    }

    private void Choose(bool secondary)
    {
        var i = _list.SelectedIndex;
        if (i >= 0 && i < _shown.Count) Post(new { action = "choose", kind = _shown[i].Kind, id = _shown[i].Id, secondary });
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData & ~Keys.Shift)
        {
            case Keys.Down: Move(1); return true;
            case Keys.Up: Move(-1); return true;
            case Keys.PageDown: Move(8); return true;
            case Keys.PageUp: Move(-8); return true;
            case Keys.Enter: Choose((keyData & Keys.Shift) != 0); return true;
            case Keys.Escape: Post(new { action = "close" }); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "palette.init":
                _items = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>())
                    .Select(e => new Item { Id = P(e, "id"), Kind = P(e, "kind"), Title = P(e, "title"), Sub = P(e, "sub"), Key = P(e, "key"), T = Norm(P(e, "title")), S = Norm(P(e, "sub") + " " + P(e, "code")) }).ToList();
                _recent = (a.Count > 1 && a[1].ValueKind == JsonValueKind.Array ? a[1].EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(x => x.GetString() ?? "").ToList();
                Compute();
                break;
            case "palette.focus": FocusPrimary(); break;
        }
    }

    public override void FocusPrimary() { _q.Focus(); _q.SelectAll(); }
}
