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

    /// <summary>"📝 Xem ở Note (New)": mở note của người (owner) ở tab Note (New) chỉ đọc, chọn sẵn y/c (id).</summary>
    public event Action<string, string>? OpenInNoteRequested;

    /// <summary>"⚙ Tạo gói update…" (owner, id): MainForm lưu phần đang gõ ở Note (New), gọi <see cref="CopyForEdit"/> rồi mở y/c vừa dán ở Note (New) của mình
    /// (sửa được) + Xem trước gói — người dùng xem / bổ sung chỗ thiếu rồi mới Generate. Không tạo gói ở đây.</summary>
    public event Action<string, string>? EditInNoteRequested;

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
                case "openInNote":
                    OpenInNoteRequested?.Invoke(root.GetProperty("owner").GetString() ?? "", root.GetProperty("id").GetString() ?? "");
                    break;
                // ⚙ Tạo gói update…: KHÔNG gen ngay — dán y/c vào Note (New) của mình (MainForm làm: lưu phần đang gõ ở Note (New), CopyForEdit, mở y/c + Xem trước gói).
                case "editInNote":
                    EditInNoteRequested?.Invoke(root.GetProperty("owner").GetString() ?? "", root.GetProperty("id").GetString() ?? "");
                    break;
            }
        }
        catch (Exception ex) { Js($"team.toast({JsonSerializer.Serialize("Lỗi: " + ex.Message)})"); }
    }

    public void Toast(string message) => Js($"team.toast({JsonSerializer.Serialize(message)})");

    /// <summary>Dán y/c (owner, id) vào note của máy này để sửa rồi gen: đã có bản dán trước (tên "&lt;tên&gt; (từ &lt;owner&gt;)…", chưa Done) thì dùng lại bản đó
    /// — giữ phần bạn đã bổ sung; chưa có thì chép mới (<see cref="AdvanceNoteService.CloneForMine"/>). Trả Id y/c trong note của mình (null = lỗi) + câu báo.
    /// File note của người kia không bị đụng tới.</summary>
    public (string? Id, string Message) CopyForEdit(string owner, string id)
    {
        var src = _others.FirstOrDefault(o => o.Owner == owner)?.Requests.FirstOrDefault(r => r.Id == id);
        if (src is null) return (null, "Không tìm thấy y/c (đã bị sửa / xoá?) — bấm Tải lại.");
        var ws = _workspace();
        var mine = _store.Load(ws, _settings);
        var baseName = $"{src.Name} (từ {owner})";
        var old = mine.Where(r => !r.Done && r.Name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Begin).FirstOrDefault();
        if (old is not null)
            return (old.Id, $"Mở lại \"{old.Name}\" đã dán từ trước ({old.Begin:dd/MM HH:mm}) — xem / bổ sung rồi bấm ⚙ Generate Update. Muốn lấy bản mới nhất của {owner} thì xoá bản này rồi bấm lại.");
        var copy = AdvanceNoteService.CloneForMine(src, owner, mine);
        mine.Insert(0, copy);
        _store.Save(ws, _settings, mine);
        return (copy.Id, $"Đã dán \"{copy.Name}\" vào note của bạn — xem gói bên dưới, bổ sung chỗ còn thiếu rồi bấm ⚙ Generate Update." + (_store.LastSaveWarning is { } w ? " " + w : ""));
    }

    /// <summary>Chép 1 y/c của người khác vào note của máy này (Id mới, chưa xong, bỏ lịch sử gen) — file của người kia không bị đụng tới.</summary>
    private void Clone(string owner, string id)
    {
        var src = _others.FirstOrDefault(o => o.Owner == owner)?.Requests.FirstOrDefault(r => r.Id == id);
        if (src is null) { Js("team.toast('Không tìm thấy y/c (đã bị sửa / xoá?) — bấm Tải lại.')"); return; }
        var ws = _workspace();
        var mine = _store.Load(ws, _settings);
        var copy = AdvanceNoteService.CloneForMine(src, owner, mine);
        var name = copy.Name;
        mine.Insert(0, copy);
        _store.Save(ws, _settings, mine);
        NotesChanged?.Invoke();
        Js($"team.toast({JsonSerializer.Serialize($"Đã chép \"{name}\" vào Note (New) của bạn." + (_store.LastSaveWarning is { } w ? " " + w : ""))})");
    }
}
