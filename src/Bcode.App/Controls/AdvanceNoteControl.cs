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
        _ = PushTableNamesAsync();
    }

    private List<string>? _tableNames;

    /// <summary>Gợi ý tên table / view cho ô nhập tay ở "Table liên quan" (nạp nền, lỗi DB chỉ mất gợi ý).</summary>
    private async Task PushTableNamesAsync()
    {
        try { _tableNames ??= await _genAll.ListTableNamesAsync(); }
        catch { return; }
        if (!IsDisposed && _tableNames.Count > 0) Js($"advNote.onTableNames({J(_tableNames)})");
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
                    _previewItems = OrderForRun(result.Items);
                    Js($"advNote.onPreview({J(ToView(_previewItems, result.Warnings))})");
                    break;
                }

                // Mở script của gói (xem trước) vào các tab SQL Query theo đúng thứ tự chạy: index = 1 script, thiếu index = tất cả.
                // Bấm 1 dòng trong danh sách gói: bung nội dung script (hoặc file văn bản nhỏ) ngay dưới dòng đó.
                case "viewItem":
                {
                    var idx = root.GetProperty("index").GetInt32();
                    string text;
                    if (idx < 0 || idx >= _previewItems.Count) text = "(không còn dữ liệu — bấm Xem trước gói lại)";
                    else
                    {
                        var it = _previewItems[idx];
                        if (it.GeneratedContent is not null) text = it.GeneratedContent;
                        else
                        {
                            try
                            {
                                var fi = new FileInfo(it.SourceFilePath ?? "");
                                text = !fi.Exists ? "(file không tồn tại)" : fi.Length > 400_000 ? $"(file {fi.Length / 1024:N0} KB — quá lớn để xem nhanh)" : File.ReadAllText(fi.FullName);
                            }
                            catch (Exception ex) { text = "(không đọc được: " + ex.Message + ")"; }
                        }
                    }
                    Js($"advNote.onItemText({idx}, {J(text)})");
                    break;
                }

                case "openScripts":
                {
                    var scripts = new List<(string Title, bool Sys, string Text)>();
                    var only = root.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : -1;
                    for (var i = 0; i < _previewItems.Count; i++)
                    {
                        var it = _previewItems[i];
                        if (it.GeneratedContent is null || (only >= 0 && i != only)) continue;
                        var sys = it.RelativeDestPath.Replace('/', '\\').Contains("\\sys\\", StringComparison.OrdinalIgnoreCase);
                        scripts.Add((Path.GetFileNameWithoutExtension(it.RelativeDestPath) + (sys ? " [Sys]" : ""), sys, it.GeneratedContent));
                    }
                    if (scripts.Count == 0) Js("advNote.onError('Chưa có script nào — bấm Xem trước gói trước.')");
                    else OpenScriptsRequested?.Invoke(scripts);
                    break;
                }

                case "generate":
                    await GenerateAsync(root);
                    break;

                case "loadTables":
                {
                    var ws = _workspace();
                    var names = GenAllService.SplitNames(root.TryGetProperty("controllers", out var cEl) ? cEl.GetString() : "");
                    if (ws is null) { Js("advNote.onError('Chưa chọn workspace.')"); break; }
                    if (names.Count == 0) { Js("advNote.onTables([])"); break; }
                    Js("advNote.setBusy(true, 'Đang đọc table liên quan...')");
                    var list = await _genAll.FindTablesAsync(ws, names);
                    Js($"advNote.onTables({J(list)})");
                    break;
                }

                case "lookupTable":
                {
                    var info = await _genAll.LookupTableAsync(root.GetProperty("name").GetString() ?? "");
                    Js($"advNote.onTableAdded({J(info)})");
                    break;
                }

                case "sync":
                    await SyncRequestsAsync(root.TryGetProperty("programmer", out var pr) ? pr.GetString() ?? "" : "");
                    break;

                case "browseFiles":
                    BrowseFiles();
                    break;

                case "pasteFiles":
                    // File đã Ctrl+C ở Explorer: trên clipboard là danh sách file (CF_HDROP), không có chữ → đọc đường dẫn ở đây rồi chèn vào ô File Path.
                    try
                    {
                        if (Clipboard.ContainsFileDropList())
                        {
                            var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
                            if (paths.Length > 0) Js($"advNote.addPaths({J(paths)})");
                        }
                    }
                    catch { /* clipboard đang bị chương trình khác giữ */ }
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

    /// <summary>Yêu cầu mở script (theo thứ tự chạy) vào các tab SQL Query — MainForm tạo tab.</summary>
    public event Action<IReadOnlyList<(string Title, bool Sys, string Text)>>? OpenScriptsRequested;

    private List<PackageItem> _previewItems = new();

    /// <summary>File đứng trước, script sau và xếp theo database rồi tên file (00_Table → 01_script_top → 01_view → 02_* → 03_script_bottom) = thứ tự nên chạy.</summary>
    private static List<PackageItem> OrderForRun(IEnumerable<PackageItem> items) =>
        items.Where(i => !i.IsScript).Concat(items.Where(i => i.IsScript)
            .OrderBy(i => i.RelativeDestPath.Replace('/', '\\').Contains("\\sys\\", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(i => Path.GetFileName(i.RelativeDestPath), StringComparer.OrdinalIgnoreCase)).ToList();

    private static object ToView(List<PackageItem> items, List<string> warnings) => new
    {
        items = items.Select(i => new
        {
            origin = i.Origin,
            rel = i.RelativeDestPath,
            kind = i.IsScript ? "script" : "file",
            source = i.SourceFilePath ?? "",
        }),
        warnings,
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
        var picked = _requests.Where(r => ids.Contains(r.Id)).ToList();
        var multi = picked.Count > 1;
        // Nhiều y/c: mỗi y/c 1 thư mục con riêng (<tên y/c>\...). Top/Bottom Script giống hệt nhau giữa các y/c thì dùng chung ở gốc gói.
        var perReq = new List<(string Sub, GenAllResult Res)>();
        foreach (var req in picked)
        {
            var one = await _genAll.ResolveAsync(ws, req);
            perReq.Add((SafeFolder(req.Name), one));
            merged.Warnings.AddRange(one.Warnings.Select(w => $"[{req.Name}] {w}"));
        }
        if (!multi) { foreach (var item in perReq[0].Res.Items) merged.Add(item); }
        else
        {
            static bool IsShared(PackageItem i) => i.Origin is "SQL Top Script" or "SQL Bottom Script";
            var sharedOk = perReq.SelectMany(p => p.Res.Items).Where(IsShared).GroupBy(i => i.RelativeDestPath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(i => i.GeneratedContent).Distinct().Count() == 1)
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (sub, res) in perReq)
                foreach (var item in res.Items)
                {
                    if (IsShared(item) && sharedOk.Contains(item.RelativeDestPath)) { merged.Add(item); continue; }
                    merged.Add(new PackageItem
                    {
                        Origin = item.Origin,
                        RelativeDestPath = Path.Combine(sub, item.RelativeDestPath),
                        SourceFilePath = item.SourceFilePath,
                        GeneratedContent = item.GeneratedContent,
                    });
                }
        }

        if (merged.Items.Count == 0)
        {
            Js($"advNote.onGenerated({J(new { ok = false, message = "Không có mục nào để đưa vào gói update.", warnings = merged.Warnings })})");
            return;
        }

        var dest = Path.Combine(savePath, folder);
        var count = await Task.Run(() => GenAllService.CreatePackage(dest, merged.Items, description));
        _previewItems = OrderForRun(merged.Items);   // để bấm xem nội dung từng mục của gói vừa tạo

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

        Js($"advNote.onGenerated({J(new { ok = true, path = dest, count, warnings = merged.Warnings, updated = now, items = ToView(_previewItems, merged.Warnings) })})");
        SendList(null); // làm mới cột Updated
    }

    private static string SafeFolder(string? name)
    {
        var s = string.Concat((name ?? "").Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return s.Length == 0 ? "request" : s;
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
