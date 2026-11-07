using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "👥 Note &amp; update cả nhóm" (Web/Shell/teamnotes.html): xem — CHỈ ĐỌC — các gói update trong thư mục update của dự án (do bất kỳ ai gen / kéo về,
/// kèm description.txt / note.txt) và Advance Note của người dùng KHÁC cùng dự án (file &lt;ws&gt;@&lt;máy&gt;.json trong thư mục AdvanceNotes —
/// <see cref="AdvanceNoteService.LoadOthers"/>). Một y/c của người khác có thể "Chép về note của tôi" để sửa / gen lại.
/// Đọc thư mục mạng ở luồng nền; không ghi gì vào file của người khác.
/// </summary>
public sealed class TeamNotesControl : UserControl
{
    private readonly WebBarHost _web = new("teamnotes.html") { Dock = DockStyle.Fill };
    private readonly AdvanceNoteService _store;
    private readonly Func<Workspace?> _workspace;
    private readonly AppSettings _settings;
    private List<AdvanceNoteService.OtherNotes> _others = new();
    private int _version;

    /// <summary>Đã chép 1 y/c về note của máy này — tab Note (New) đang mở nên nạp lại.</summary>
    public event Action? NotesChanged;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TeamNotesControl(AdvanceNoteService store, Func<Workspace?> workspace, AppSettings settings)
    {
        _store = store; _workspace = workspace; _settings = settings;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => Handle(root);
        _web.Ready += () => _ = LoadAsync();
    }

    public void Reload() => _ = LoadAsync();

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    /// <summary>Người tạo gói theo quy ước tên của Gen Update: &lt;dự án&gt;_&lt;máy&gt;_&lt;yyyyMMddHHmmss...&gt;[_tag] (có thể có X trước số).</summary>
    private static string OwnerOf(string name, Workspace? ws)
    {
        var m = Regex.Match(name, @"^(?<p>.+?)_(?<o>[A-Za-z0-9][A-Za-z0-9.\-]*?)_X?\d{12,}");
        return m.Success ? m.Groups["o"].Value : "";
    }

    private async Task LoadAsync()
    {
        var ws = _workspace();
        var v = ++_version;
        var data = await Task.Run(() =>
        {
            string? error = null;
            var folder = AdvanceNoteService.ResolveFolder(ws, _settings);
            var update = AdvanceNoteService.UpdateFolder(ws, _settings);
            var local = string.Equals(folder, AdvanceNoteService.LocalFolder, StringComparison.OrdinalIgnoreCase);
            if (update is not null)
                if (!local && !AdvanceNoteService.IsReachable(folder)) error = "không vào được server — note của bạn đang lưu tạm trên máy, vào lại được sẽ tự đưa lên";
            var others = _store.LoadOthers(ws, _settings);
            var mine = _store.Load(ws, _settings);
            // Gói nào thuộc y/c nào (khớp tên thư mục gói với lịch sử Generate của mọi note, kể cả của mình).
            var usedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void Index(string who, IEnumerable<AdvanceRequest> reqs)
            {
                foreach (var r in reqs)
                    foreach (var g in r.Generations)
                    {
                        var n = Path.GetFileName((g.Path ?? "").TrimEnd('\\', '/'));
                        if (n.Length == 0) continue;
                        if (!usedBy.TryGetValue(n, out var l)) usedBy[n] = l = new();
                        var label = $"{r.Name} ({who})";
                        if (!l.Contains(label)) l.Add(label);
                    }
            }
            Index("tôi", mine);
            foreach (var o in others) Index(o.Owner, o.Requests);
            static string? Cap(string? s) => s is null ? null : s.Length > 4000 ? s[..4000] + "\n…" : s;
            var packages = _store.ListPackages(ws, _settings).Select(p => new
            {
                p.Name, p.Path, p.Modified, p.IsFolder,
                Description = Cap(p.Description), Note = Cap(p.Note),
                Owner = OwnerOf(Path.GetFileNameWithoutExtension(p.Name), ws),
                UsedBy = usedBy.TryGetValue(Path.GetFileNameWithoutExtension(p.Name), out var u) ? string.Join("\n", u) : "",
            }).ToList();
            return (others, payload: new
            {
                project = ws?.Name ?? "", folder, update = update ?? "", local, error,
                packages,
                others = others.Select(o => new { o.Owner, o.Modified, Requests = o.Requests.OrderBy(r => r.Done).ThenByDescending(r => r.Updated ?? r.Begin) }),
            });
        });
        if (v != _version || IsDisposed) return;
        _others = data.others;
        Js($"team.init({JsonSerializer.Serialize(data.payload, Json)})");
    }

    private void Handle(JsonElement root)
    {
        try
        {
            switch (root.GetProperty("action").GetString())
            {
                case "ready": _ = LoadAsync(); break;
                case "refresh": _ = LoadAsync(); break;
                case "open":
                {
                    var p = root.TryGetProperty("path", out var e) ? e.GetString() : null;
                    if (string.IsNullOrWhiteSpace(p)) break;
                    if (Directory.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true });
                    else if (File.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
                    else Js($"team.toast({JsonSerializer.Serialize("Không còn thư mục / file: " + p)})");
                    break;
                }
                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard bận */ }
                    Js("team.toast('Đã copy đường dẫn.')");
                    break;
                case "clone":
                    Clone(root.GetProperty("owner").GetString() ?? "", root.GetProperty("id").GetString() ?? "");
                    break;
            }
        }
        catch (Exception ex) { Js($"team.toast({JsonSerializer.Serialize("Lỗi: " + ex.Message)})"); }
    }

    /// <summary>Chép 1 y/c của người khác vào note của máy này (Id mới, chưa xong, bỏ lịch sử gen) — file của người kia không bị đụng tới.</summary>
    private void Clone(string owner, string id)
    {
        var src = _others.FirstOrDefault(o => o.Owner == owner)?.Requests.FirstOrDefault(r => r.Id == id);
        if (src is null) { Js("team.toast('Không tìm thấy y/c (đã bị sửa / xoá?) — bấm Tải lại.')"); return; }
        var ws = _workspace();
        var copy = JsonSerializer.Deserialize<AdvanceRequest>(JsonSerializer.Serialize(src, AdvanceNoteService.Json), AdvanceNoteService.Json)!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Done = false;
        copy.Generations = new();
        copy.Begin = DateTime.Now;
        copy.Updated = null;
        var mine = _store.Load(ws, _settings);
        var names = new HashSet<string>(mine.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var name = $"{src.Name} (từ {owner})";
        for (var i = 2; names.Contains(name); i++) name = $"{src.Name} (từ {owner}) {i}";
        copy.Name = name;
        mine.Insert(0, copy);
        _store.Save(ws, _settings, mine);
        NotesChanged?.Invoke();
        Js($"team.toast({JsonSerializer.Serialize($"Đã chép \"{name}\" vào Note (New) của bạn." + (_store.LastSaveWarning is { } w ? " " + w : ""))})");
    }
}
