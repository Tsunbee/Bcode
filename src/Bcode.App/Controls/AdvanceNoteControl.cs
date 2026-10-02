using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Note (New)" = Advance Note: danh sách Request (ghi chú y/c đã chỉnh sửa) + khai báo những gì đưa vào gói update của
/// từng y/c — Gen All (controller → mọi file liên quan + procedure trong Filter + sysmenu), File Path, SQL Object,
/// SQL Top Script — rồi Generate Update tạo thư mục gói (cùng cơ chế tab Gen Update).
///
/// Giao diện là trang WebView2 (Web/Shell/advnote.html); control này chỉ giữ dữ liệu, gọi GenAllService và đẩy kết quả
/// lại trang. Lưu danh sách Request theo workspace (AdvanceNoteService).
/// </summary>
public class AdvanceNoteControl : UserControl
{
    private readonly WebBarHost _web = new("advnote.html") { Dock = DockStyle.Fill };
    private readonly AdvanceNoteService _store;
    private readonly GenAllService _genAll;
    private readonly Func<Workspace?> _workspace;
    private List<AdvanceRequest> _requests = new();
    private string _loadedFor = "";
    private readonly Bcode.App.Models.AppSettings? _settings;

    public AdvanceNoteControl(AdvanceNoteService store, GenAllService genAll, Func<Workspace?> workspace, Bcode.App.Models.AppSettings? settings = null)
    {
        _settings = settings;
        _store = store;
        _genAll = genAll;
        _workspace = workspace;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += SendInit;
    }

    private string WorkspaceName => _workspace()?.Name ?? "";

    private void EnsureLoaded()
    {
        // Workspace đổi giữa chừng (File > Choose Server) → nạp lại danh sách của workspace mới.
        if (_loadedFor == WorkspaceName && _requests is not null) return;
        _requests = _store.Load(WorkspaceName);
        _loadedFor = WorkspaceName;
    }

    private static string J(object? o) => JsonSerializer.Serialize(o, AdvanceNoteService.Json);

    private void Js(string script) => _web.Call(script);

    private void SendInit()
    {
        EnsureLoaded();
        var ws = _workspace();
        var defaultFolder = string.Join("_", new[] { ws?.ProjectId, Environment.MachineName, DateTime.Now.ToString("yyyyMMddHHmmss") }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        Js($"advNote.init({J(new
        {
            workspace = new { name = ws?.Name ?? "", sourcePath = ws?.SourcePath ?? "", savePath = ws?.WorkingPath ?? "", projectId = ws?.ProjectId ?? "" },
            defaultFolder,
            programmer = _settings?.NoteProgrammer ?? "",
            requests = _requests,
        })})");
    }

    private void SendList(string? selectId) => Js($"advNote.onList({J(_requests)}, {J(selectId)})");

    private async Task HandleAsync(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();
            EnsureLoaded();

            switch (action)
            {
                case "ready":
                    SendInit();
                    break;

                case "newRequest":
                {
                    var r = new AdvanceRequest { Name = AdvanceNoteService.SuggestName(_requests) };
                    _requests.Add(r);
                    _store.Save(WorkspaceName, _requests);
                    SendList(r.Id);
                    break;
                }

                case "save":
                {
                    var req = root.GetProperty("request").Deserialize<AdvanceRequest>(AdvanceNoteService.Json);
                    if (req is null) break;
                    var i = _requests.FindIndex(x => x.Id == req.Id);
                    // "Updated" là lần Generate Update gần nhất — sửa nội dung không đổi nó.
                    if (i >= 0) { req.Updated = _requests[i].Updated; req.Generations = _requests[i].Generations; _requests[i] = req; } else _requests.Add(req);
                    _store.Save(WorkspaceName, _requests);
                    Js($"advNote.onSaved({J(req.Id)})");
                    break;
                }

                case "delete":
                {
                    // "ids" = xoá 1 loạt (các y/c đã tick); "id" = xoá 1 y/c đang chọn.
                    var ids = root.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
                        ? idsEl.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).ToHashSet()
                        : new HashSet<string?> { root.GetProperty("id").GetString() };
                    _requests.RemoveAll(x => ids.Contains(x.Id));
                    _store.Save(WorkspaceName, _requests);
                    SendList(null);
                    break;
                }

                case "preview":
                {
                    var req = root.GetProperty("request").Deserialize<AdvanceRequest>(AdvanceNoteService.Json);
                    var ws = _workspace();
                    if (req is null || ws is null) { Js("advNote.onError('Chưa chọn workspace.')"); break; }
                    Js("advNote.setBusy(true, 'Đang quét file / procedure...')");
                    var result = await _genAll.ResolveAsync(ws, req);
                    Js($"advNote.onPreview({J(ToView(result))})");
                    break;
                }

                case "generate":
                    await GenerateAsync(root);
                    break;

                case "sync":
                    await SyncRequestsAsync(root.TryGetProperty("programmer", out var pr) ? pr.GetString() ?? "" : "");
                    break;

                case "browseFiles":
                    BrowseFiles();
                    break;

                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard bận */ }
                    break;

                case "open":
                {
                    var path = root.GetProperty("path").GetString();
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Js($"advNote.onError({J(ex.Message)})");
        }
    }

    private static object ToView(GenAllResult r) => new
    {
        items = r.Items.Select(i => new
        {
            origin = i.Origin,
            rel = i.RelativeDestPath,
            kind = i.IsScript ? "script" : "file",
            source = i.SourceFilePath ?? "",
        }),
        warnings = r.Warnings,
    };

    /// <summary>
    /// "Sync yêu cầu": kéo các yêu cầu của lập trình viên (ma_lt1) trong project đang vào từ quản lý yêu cầu (bảng nvphyc) và tạo y/c mới
    /// cho mỗi mã yêu cầu (fcode1) chưa có trong danh sách — y/c đã có (trùng tên, không phân biệt hoa/thường) giữ nguyên, không ghi đè.
    /// Nội dung y/c mới được điền sẵn bộ phận / mã nhân viên / người lập trình lấy về.
    /// </summary>
    private async Task SyncRequestsAsync(string programmer)
    {
        programmer = programmer.Trim();
        var ws = _workspace();
        var code = ws is null ? "" : (string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId).Trim();
        if (code.Length == 0) { Js("advNote.onSynced({ ok: false, message: 'Chưa chọn project.' })"); return; }
        if (programmer.Length == 0) { Js("advNote.onSynced({ ok: false, message: 'Nhập tên lập trình rồi bấm Sync.' })"); return; }

        if (_settings is not null && _settings.NoteProgrammer != programmer)
        {
            _settings.NoteProgrammer = programmer;
            try { _settings.Save(); } catch { /* không lưu được thì chỉ phải nhập lại lần sau */ }
        }

        Js("advNote.setBusy(true, 'Đang đồng bộ yêu cầu...')");
        var (rows, error) = await new FsgProjectLookupService().FetchRequestsAsync(code, programmer);
        if (error is not null)
        {
            Js($"advNote.setBusy(false); advNote.onSynced({J(new { ok = false, message = error })})");
            return;
        }

        // Nội dung y/c = noi_dung kéo về, kèm 1 dòng thông tin bộ phận / nhân viên / lập trình ở cuối.
        static string BuildContent(FsgProjectLookupService.RequestRow r)
        {
            var info = $"Bộ phận: {r.BpLt} | Nhân viên: {r.MaNv1} | Lập trình: {r.MaLt1}";
            return string.IsNullOrWhiteSpace(r.NoiDung) ? info : r.NoiDung.Trim() + "\r\n\r\n---\r\n" + info;
        }

        var byName = _requests.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        int added = 0, skipped = 0, filled = 0;
        foreach (var row in rows)
        {
            if (byName.TryGetValue(row.Fcode1, out var existingReq))
            {
                skipped++;
                // y/c đã có: chỉ điền nội dung khi còn trống hoặc mới là dòng thông tin tự sinh ở lần sync trước (chưa có noi_dung) —
                // tuyệt đối không ghi đè nội dung người dùng đã tự viết/sửa.
                var c = (existingReq.Content ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(row.NoiDung) && (c.Length == 0 || c.StartsWith("Bộ phận:", StringComparison.Ordinal) && !c.Contains('\n')))
                {
                    existingReq.Content = BuildContent(row);
                    filled++;
                }
                continue;
            }
            var req = new AdvanceRequest { Name = row.Fcode1, Content = BuildContent(row) };
            _requests.Add(req);
            byName[row.Fcode1] = req;
            added++;
        }
        if (added > 0 || filled > 0) _store.Save(WorkspaceName, _requests);

        SendList(null);
        var message = rows.Count == 0
            ? $"Không có yêu cầu nào của \"{programmer}\" trong dự án {code}."
            : $"Đã thêm {added} y/c mới" + (skipped > 0 ? $", {skipped} y/c đã có" : "") + (filled > 0 ? $" (điền nội dung cho {filled})" : "") + $" — tổng {rows.Count} yêu cầu của {programmer}.";
        Js($"advNote.setBusy(false); advNote.onSynced({J(new { ok = true, message })})");
    }

    private async Task GenerateAsync(JsonElement root)
    {
        var ws = _workspace();
        if (ws is null) { Js("advNote.onError('Chưa chọn workspace.')"); return; }

        var ids = root.GetProperty("ids").EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).ToHashSet();
        var savePath = root.GetProperty("savePath").GetString()?.Trim() ?? "";
        var folder = root.GetProperty("folder").GetString()?.Trim() ?? "";
        var description = root.TryGetProperty("description", out var d) ? d.GetString() : "";
        if (ids.Count == 0) { Js("advNote.onError('Chưa chọn y/c nào để Generate (tick cột đầu danh sách, hoặc chọn 1 y/c).')"); return; }
        if (savePath.Length == 0) { Js("advNote.onError('Chưa nhập Save At Path.')"); return; }
        if (folder.Length == 0) { Js("advNote.onError('Chưa nhập Folder Name.')"); return; }

        Js("advNote.setBusy(true, 'Đang tạo gói update...')");
        var merged = new GenAllResult();
        foreach (var req in _requests.Where(r => ids.Contains(r.Id)))
        {
            var one = await _genAll.ResolveAsync(ws, req);
            foreach (var item in one.Items) merged.Add(item);
            merged.Warnings.AddRange(one.Warnings.Select(w => $"[{req.Name}] {w}"));
        }

        if (merged.Items.Count == 0)
        {
            Js($"advNote.onGenerated({J(new { ok = false, message = "Không có mục nào để đưa vào gói update.", warnings = merged.Warnings })})");
            return;
        }

        var dest = Path.Combine(savePath, folder);
        var count = await Task.Run(() => GenAllService.CreatePackage(dest, merged.Items, description));

        var now = DateTime.Now;
        foreach (var req in _requests.Where(r => ids.Contains(r.Id)))
        {
            req.Updated = now;
            // Nhớ link gói vừa tạo cho từng y/c đã tick — mở lại y/c sau này vẫn thấy.
            req.Generations.Insert(0, new GenerationRecord { Path = dest, Time = now });
            if (req.Generations.Count > AdvanceRequest.MaxGenerations)
                req.Generations.RemoveRange(AdvanceRequest.MaxGenerations, req.Generations.Count - AdvanceRequest.MaxGenerations);
        }
        _store.Save(WorkspaceName, _requests);

        Js($"advNote.onGenerated({J(new { ok = true, path = dest, count, warnings = merged.Warnings, updated = now })})");
        SendList(null); // làm mới cột Updated
    }

    private void BrowseFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "Chọn file đưa vào gói update", CheckFileExists = true };
        var ws = _workspace();
        if (!string.IsNullOrWhiteSpace(ws?.SourcePath) && Directory.Exists(ws.SourcePath)) dlg.InitialDirectory = ws.SourcePath;
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        Js($"advNote.addPaths({J(dlg.FileNames)})");
    }
}
