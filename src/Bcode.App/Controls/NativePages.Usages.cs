using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Tab "Ai đang dùng …" (usages.html): 3 nhóm kết quả (App Data / Sys Data / file controller) dạng cây; bấm tiêu đề một mục để mở nó.</summary>
internal sealed class UsagesPage : NativePage
{
    private static readonly Dictionary<string, string> Kind = new() { ["StoredProcedure"] = "Proc", ["Function"] = "Func", ["View"] = "View", ["Trigger"] = "Trigger", ["Table"] = "Table" };

    private sealed class Line { public int No; public string Text = ""; }
    private sealed class Hit { public string Title = "", Badge = "", Tag = ""; public int Hits; public string Open = ""; public List<(string Id, string Text)> Menus = new(); public List<Line> Lines = new(); public object? Payload; }

    private readonly Label _title = new() { AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 2, FontStyle.Bold), Margin = new Padding(0, 4, 10, 0) };
    private readonly Label _status = NK.Muted("Đang chuẩn bị…");
    private readonly RoundedInput _flt = NK.Input("Lọc theo tên / nội dung…");
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, FullRowSelect = true, ShowNodeToolTips = true };
    private string _name = "", _srcInfo = "";
    private List<Hit>? _app, _sys, _src;
    private string? _appErr, _sysErr, _srcErr;
    private bool _appPending = true, _sysPending = true, _srcPending = true;

    public UsagesPage()
    {
        _flt.Width = NativeSkin.S(260);
        var head = new BarFlow { Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 4), WrapContents = true, AutoSize = true };
        head.Controls.AddRange(new Control[] { _title, _status, _flt, NK.Button("Chép danh sách", Copy, tip: "Chép danh sách tên"), NK.Button("Quét lại", () => Post(new { action = "rerun" }), tip: "Quét lại") });
        var tip = NK.Muted("Bấm tiêu đề một mục để mở nó (procedure / view / function mở trong tab SQL, file mở bằng chương trình mặc định). Tìm theo văn bản, đã lọc đúng ranh giới tên.", true);
        tip.Dock = DockStyle.Bottom; tip.Padding = new Padding(10, 2, 10, 4);
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            switch (e.Node.Tag)
            {
                case ("o", object payload): Post(payload); break;
                case ("f", string path): Post(new { action = "openFile", path }); break;
                case ("m", string id): Post(new { action = "revealMenu", id }); break;
            }
        };
        _flt.TextChanged += (_, _) => Repaint();
        Controls.Add(_tree); Controls.Add(tip); Controls.Add(head);
    }

    private static string Fmt(JsonElement l) => P(l, "line").PadLeft(5) + "   " + P(l, "text").Replace("\t", "    ");

    private static List<Hit> ParseDb(JsonElement arr) => (arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(u => new Hit
    {
        Title = P(u, "schema") + "." + P(u, "name"), Badge = PB(u, "sys") ? "Sys" : "App", Tag = Kind.GetValueOrDefault(P(u, "kind"), P(u, "kind")), Hits = PI(u, "hits"),
        Payload = new { action = "openObject", sys = PB(u, "sys"), schema = P(u, "schema"), name = P(u, "name"), kind = P(u, "kind") },
        Lines = PA(u, "lines").Select(l => new Line { No = PI(l, "line"), Text = P(l, "text") }).ToList(),
    }).ToList();

    private static List<Hit> ParseSrc(JsonElement arr) => (arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(u => new Hit
    {
        Title = P(u, "rel"), Tag = (P(u, "rel").Split('.').LastOrDefault() ?? "").ToLowerInvariant(), Hits = PI(u, "hits"), Open = P(u, "path"),
        Menus = PA(u, "menus").Select(m => (P(m, "id"), P(m, "text"))).ToList(),
        Lines = PA(u, "lines").Select(l => new Line { No = PI(l, "line"), Text = P(l, "text") }).ToList(),
    }).ToList();

    private bool Match(Hit h, string f) => f.Length == 0 || (h.Title + " " + string.Join(" ", h.Lines.Select(l => l.Text))).ToLowerInvariant().Contains(f);

    private void Section(string title, List<Hit>? list, string? err, bool pending, string noSrc = "")
    {
        var f = _flt.Text.Trim().ToLowerInvariant();
        var shown = list?.Where(h => Match(h, f)).ToList();
        var count = pending ? "…" : err is not null ? "!" : list is null ? "–" : list.Count.ToString();
        var root = new TreeNode($"{title}   [{count}]") { NodeFont = new Font(ThemeManager.BaseFont, FontStyle.Bold) };
        if (pending) root.Nodes.Add("Đang quét…");
        else if (err is not null) root.Nodes.Add("Lỗi: " + err);
        else if (list is null) root.Nodes.Add("Workspace chưa khai Source Path — không quét được file.");
        else if (shown!.Count == 0) root.Nodes.Add(list.Count > 0 ? "Không có mục nào khớp bộ lọc." : "Không thấy nơi nào dùng.");
        else
            foreach (var h in shown)
            {
                var n = new TreeNode((h.Badge.Length > 0 ? $"[{h.Badge}] " : "") + (h.Tag.Length > 0 ? $"({h.Tag}) " : "") + h.Title + $"      {h.Hits} dòng") { ToolTipText = h.Title, ForeColor = AppColors.Accent };
                n.Tag = h.Payload is not null ? ("o", h.Payload) : ("f", (object)h.Open);
                foreach (var (id, text) in h.Menus) n.Nodes.Add(new TreeNode("☰ " + text) { Tag = ("m", (object)id), ToolTipText = "Bấm để mở cây menu và chọn menu này" });
                foreach (var l in h.Lines) n.Nodes.Add(new TreeNode(l.No.ToString().PadLeft(5) + "   " + l.Text.Replace("\t", "    ").Trim()) { ForeColor = AppColors.TextMuted });
                root.Nodes.Add(n);
            }
        _tree.Nodes.Add(root);
        root.Expand();
        if (shown is { Count: > 0 } && shown.Count > 40) foreach (TreeNode c in root.Nodes) c.Collapse();
    }

    private void Repaint()
    {
        _tree.BeginUpdate(); _tree.Nodes.Clear();
        Section("Database App Data", _app, _appErr, _appPending);
        Section("Database Sys Data", _sys, _sysErr, _sysPending);
        Section("File controller (App_Data\\Controllers: Dir, Filter, Grid, Include, Options, Report)", _src, _srcErr, _srcPending);
        _tree.EndUpdate();
    }

    private void Copy()
    {
        var t = new List<string>();
        t.AddRange((_app ?? new()).Select(u => u.Title)); t.AddRange((_sys ?? new()).Select(u => u.Title)); t.AddRange((_src ?? new()).Select(u => u.Title));
        Post(new { action = "copy", text = string.Join("\n", t) });
    }

    private void StatusNow(bool done)
    {
        var total = (_app?.Count ?? 0) + (_sys?.Count ?? 0) + (_src?.Count ?? 0);
        _status.Text = done ? $"Xong — {total} nơi dùng{(_srcInfo.Length > 0 ? " · " + _srcInfo : "")}" : "Đang quét… (kết quả hiện dần)";
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "usages.init": _name = P(Arg(a, 0), "name"); _title.Text = "Ai đang dùng " + _name + "?"; Repaint(); break;
            case "usages.onStart":
                _app = _sys = _src = null; _appErr = _sysErr = _srcErr = null; _appPending = _sysPending = _srcPending = true;
                Repaint(); StatusNow(false); break;
            case "usages.onDb":
                if (Bool(a, 0)) { _sys = ParseDb(Arg(a, 1)); _sysPending = false; } else { _app = ParseDb(Arg(a, 1)); _appPending = false; }
                Repaint(); StatusNow(false); break;
            case "usages.onSource":
                _srcPending = false; _srcInfo = Str(a, 1);
                _src = a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? ParseSrc(a[0]) : null;
                Repaint(); StatusNow(false); break;
            case "usages.onProgress": _status.Text = Str(a, 0); break;
            case "usages.onError":
                switch (Str(a, 0)) { case "sys": _sysErr = Str(a, 1); _sysPending = false; break; case "src": _srcErr = Str(a, 1); _srcPending = false; break; default: _appErr = Str(a, 1); _appPending = false; break; }
                Repaint(); break;
            case "usages.onDone": StatusNow(true); break;
        }
    }

    public override void FocusPrimary() => _flt.Inner.Focus();
}
