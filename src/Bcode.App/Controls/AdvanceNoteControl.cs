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

    private string _loadedStamp = "";

    /// <summary>Nạp lại danh sách khi workspace đổi giữa chừng (File > Choose Server) hoặc file của workspace vừa được tiến trình khác ghi
    /// (BcodeNote.exe / cửa sổ Bcode khác cùng sửa Note (New) của dự án này). Trả true nếu vừa nạp lại.</summary>
    private bool EnsureLoaded()
    {
        if (_viewOwner is not null)
        {
            // Đang xem note của người khác (chỉ đọc): không so / nạp file của mình. Đổi dự án giữa chừng thì quay về note của mình.
            if (_loadedFor == WorkspaceName) return false;
            _viewOwner = null; _viewModified = null;
            _loadedStamp = "\0";
        }
        var stamp = _store.Stamp(_workspace(), _settings);
        if (_loadedFor == WorkspaceName && _requests is not null && stamp == _loadedStamp) return false;
        _requests = _store.Load(_workspace(), _settings);
        _loadedFor = WorkspaceName;
        _loadedStamp = stamp;
        return true;
    }

    // ---- Xem note của người dùng KHÁC cùng dự án (A nghỉ, B mở note của A để gen thay) — CHỈ ĐỌC: file của A không bao giờ bị ghi ----
    /// <summary>null = đang xem note của mình (mặc định); khác null = tên (máy) người có note đang xem, _requests là note của họ (chỉ đọc).</summary>
    private string? _viewOwner;
    private DateTime? _viewModified;
    private int _viewVersion;

    private object? ViewState() => _viewOwner is null ? null : new { owner = _viewOwner, modified = _viewModified, readOnly = true };

    private bool ReadOnlyGuard()
    {
        if (_viewOwner is null) return false;
        Js($"advNote.onError({J($"Đang xem note của {_viewOwner} (chỉ đọc) — không sửa / xoá được. Chọn \"Tôi\" ở nút 👤 để về note của bạn, hoặc 📥 Chép về note của tôi.")})");
        return true;
    }

    /// <summary>Danh sách người khác có note của dự án (đọc thư mục mạng ở luồng nền).</summary>
    private async Task SendOwnersAsync()
    {
        var ws = _workspace();
        var (owners, error) = await Task.Run(() => _store.ListOwners(ws, _settings));
        if (IsDisposed) return;
        Js($"advNote.onOwners({J(new { me = AdvanceNoteService.Owner, viewing = _viewOwner, owners, error, folder = AdvanceNoteService.ResolveFolder(ws, _settings) })})");
    }

    /// <summary>Chuyển sang xem note của <paramref name="owner"/> ("" = note của mình). Đọc file ở luồng nền; lỗi (mất mạng / file hỏng) thì giữ nguyên đang xem và báo.</summary>
    private (string Owner, string? Id)? _pendingView;

    /// <summary>Mở note của <paramref name="owner"/> (chỉ đọc) và chọn sẵn y/c <paramref name="id"/> — từ "📝 Mở ở Note (New)" của tab Cả nhóm.
    /// Trang chưa nạp xong thì làm sau khi trang gửi "ready".</summary>
    public void ShowOwnerRequest(string owner, string? id)
    {
        if (!_web.IsReady) { _pendingView = (owner, id); return; }
        _ = ViewOwnerAsync(owner, id);
    }

    private (string Id, string Message)? _pendingMine;

    /// <summary>Về note của MÌNH, nạp lại file, chọn y/c <paramref name="id"/> (vừa dán từ note người khác — "⚙ Tạo gói update…" ở tab Cả nhóm)
    /// rồi tự bấm 🔍 Xem trước gói để người dùng thấy gói sẽ có gì, bổ sung chỗ thiếu rồi mới Generate. Trang chưa nạp xong thì làm sau "ready".</summary>
    public void OpenMineRequest(string id, string message)
    {
        if (!_web.IsReady) { _pendingMine = (id, message); return; }
        ++_viewVersion;   // bỏ kết quả đọc note người khác còn đang chạy (nếu có)
        _viewOwner = null; _viewModified = null; _loadedStamp = "\0";
        EnsureLoaded();
        _previewItems = new();
        SendList(_requests.Any(r => r.Id == id) ? id : null);
        Js($"advNote.onViewChanged({J(new { ok = true, message })}); window.advNote && advNote.preview && advNote.preview()");
    }

    private async Task ViewOwnerAsync(string owner, string? selectId = null)
    {
        var v = ++_viewVersion;
        if (owner.Length == 0 || string.Equals(owner, AdvanceNoteService.Owner, StringComparison.OrdinalIgnoreCase))
        {
            _viewOwner = null; _viewModified = null; _loadedStamp = "\0";
            EnsureLoaded();
            _previewItems = new();
            SendList(null);
            Js($"advNote.onViewChanged({J(new { ok = true, message = "Đang xem note của bạn." })})");
            return;
        }
        var ws = _workspace();
        var path = AdvanceNoteService.OtherFile(ws, _settings, owner);
        if (path is null) { Js("advNote.setBusy(false); advNote.onError('Không xác định được file note của người này (dự án chưa có Working Path?).')"); return; }
        Js($"advNote.setBusy(true, {J($"Đang đọc note của {owner}...")})");
        try
        {
            var (list, modified) = await Task.Run(() => AdvanceNoteService.ReadOther(path));
            if (v != _viewVersion || IsDisposed) return;
            _viewOwner = owner; _viewModified = modified;
            _requests = list;
            _loadedFor = WorkspaceName;
            _previewItems = new();
            SendList(selectId is not null && list.Any(r => r.Id == selectId) ? selectId : null);
            Js($"advNote.onViewChanged({J(new { ok = true, message = $"Đang xem note của {owner} ({list.Count} y/c, sửa lần cuối {modified:dd/MM/yy HH:mm}) — chỉ đọc." })})");
        }
        catch (Exception ex)
        {
            if (v != _viewVersion || IsDisposed) return;
            Js($"advNote.setBusy(false); advNote.onError({J($"Không đọc được note của {owner}: {ex.Message}")})");
        }
    }

    /// <summary>📥 Chép các y/c (đang xem của người khác) về note của mình — file của người kia không bị đụng tới. Trang đã hỏi xác nhận.</summary>
    private void CopyToMine(JsonElement root)
    {
        if (_viewOwner is null) { Js("advNote.onError('Đang xem note của bạn — không cần chép.')"); return; }
        var ids = root.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
            ? idsEl.EnumerateArray().Select(e => e.GetString()).Where(x => x is not null).ToHashSet() : new HashSet<string?>();
        var picked = _requests.Where(r => ids.Contains(r.Id)).ToList();
        if (picked.Count == 0) { Js("advNote.onError('Chưa chọn y/c nào để chép (tick ở danh sách, hoặc chọn 1 y/c).')"); return; }
        var ws = _workspace();
        var mine = _store.Load(ws, _settings);
        var names = new List<string>();
        foreach (var src in picked)
        {
            var copy = AdvanceNoteService.CloneForMine(src, _viewOwner, mine);
            mine.Insert(0, copy);
            names.Add(copy.Name);
        }
        _store.Save(ws, _settings, mine);
        Js($"advNote.onCopied({J(new { ok = true, message = $"Đã chép {picked.Count} y/c về note của bạn: " + string.Join(", ", names.Take(5)) + (names.Count > 5 ? ", ..." : "") + (_store.LastSaveWarning is { } w ? " " + w : "") })})");
    }

    private void SaveList()
    {
        if (_viewOwner is not null) return;   // note của người khác: KHÔNG ghi (chốt chặn cuối — các thao tác sửa đã bị chặn ở HandleAsync)
        _store.Save(_workspace(), _settings, _requests);
        _loadedStamp = _store.Stamp(_workspace(), _settings);
    }

    private static string J(object? o) => JsonSerializer.Serialize(o, AdvanceNoteService.Json);

    private void Js(string script) => _web.Call(script);

    private void SendInit()
    {
        EnsureLoaded();
        var ws = _workspace();
        var defaultFolder = string.Join("_", new[] { ws?.ProjectId, Environment.MachineName, DateTime.Now.ToString("yyyyMMddHHmmss") }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var opts = GenAllService.LoadPackageOptions();
        Js($"advNote.init({J(new
        {
            workspace = new { name = ws?.Name ?? "", sourcePath = ws?.SourcePath ?? "", savePath = ws?.WorkingPath ?? "", projectId = ws?.ProjectId ?? "" },
            defaultFolder,
            pkg = new { zip = opts.Zip, net = opts.Network, netRoot = opts.NetworkRoot, machine = Environment.MachineName },
            programmer = _settings?.NoteProgrammer ?? "",
            noteHub = NoteHubRequested is not null,   // nút "🗂 Nhiều dự án" chỉ hiện ở Bcode (BcodeNote.exe không nối event này)
            team = TeamRequested is not null,         // nút "👥 Cả nhóm": note người khác + gói update trong thư mục update của dự án
            me = AdvanceNoteService.Owner,
            doneAfterGen = _settings?.NoteDoneAfterGen ?? true,   // ô "Done sau khi gen"
            view = ViewState(),                       // null = note của mình; khác null = đang xem note người khác (chỉ đọc)
            requests = _requests,
        })})");
        _ = PushTableNamesAsync();
        if (_pendingView is { } pv) { _pendingView = null; _ = ViewOwnerAsync(pv.Owner, pv.Id); }
        if (_pendingMine is { } pm) { _pendingMine = null; OpenMineRequest(pm.Id, pm.Message); }
    }

    private List<string>? _tableNames;

    /// <summary>Gợi ý tên table / view cho ô nhập tay ở "Table liên quan" (nạp nền, lỗi DB chỉ mất gợi ý).</summary>
    private async Task PushTableNamesAsync()
    {
        try { _tableNames ??= await _genAll.ListTableNamesAsync(); }
        catch { return; }
        if (!IsDisposed && _tableNames.Count > 0) Js($"advNote.onTableNames({J(_tableNames)})");
    }

    private void SendList(string? selectId) => Js($"advNote.onList({J(_requests)}, {J(selectId)}, {J(ViewState())})");

    private async Task HandleAsync(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();
            var wasViewing = _viewOwner is not null;
            var reloaded = EnsureLoaded();
            if (wasViewing && _viewOwner is null && action is not ("ready" or "refresh" or "flushed"))
            {
                // Dự án vừa đổi trong lúc xem note người khác: đã về note của mình — báo trang, KHÔNG làm thao tác (vd lưu) lên danh sách mới.
                SendList(null);
                Js("advNote.onError('Dự án đã đổi — đã quay về note của bạn.')");
                return;
            }
            // Đang xem note người khác: chặn mọi thao tác ghi vào danh sách / file note.
            if (_viewOwner is not null && action is "newRequest" or "save" or "delete" or "deletePackage" or "sync")
            {
                if (action != "save") ReadOnlyGuard();   // "save" do trang tự gửi — bỏ qua lặng lẽ (trang đã khoá ô sửa)
                return;
            }

            switch (action)
            {
                case "ready":
                    SendInit();
                    break;

                case "owners":
                    await SendOwnersAsync();
                    break;

                case "viewOwner":
                    await ViewOwnerAsync(root.TryGetProperty("owner", out var ow) ? ow.GetString() ?? "" : "");
                    break;

                case "copyToMine":
                    CopyToMine(root);
                    break;

                // Trang vừa được focus lại: file danh sách đổi do tiến trình khác ghi (BcodeNote.exe / Bcode khác) thì đẩy danh sách mới xuống.
                case "refresh":
                    if (reloaded) SendList(null);
                    break;

                // Trả lời FlushAsync: tin "save" (nếu có) đã tới và được xử lý trước tin này.
                case "flushed":
                    _flushed?.TrySetResult();
                    break;

                case "newRequest":
                {
                    var r = new AdvanceRequest { Name = AdvanceNoteService.SuggestName(_requests) };
                    _requests.Add(r);
                    SaveList();
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
                    SaveList();
                    Js($"advNote.onSaved({J(req.Id)})");
                    if (reloaded) SendList(req.Id);   // y/c khác vừa được bên kia sửa → trang thấy luôn
                    break;
                }

                case "delete":
                {
                    // "ids" = xoá 1 loạt (các y/c đã tick); "id" = xoá 1 y/c đang chọn. Chỉ xoá trong danh sách lưu trên máy này, KHÔNG đụng database / quản lý yêu cầu.
                    var ids = root.TryGetProperty("ids", out var idsEl0) && idsEl0.ValueKind == JsonValueKind.Array
                        ? idsEl0.EnumerateArray().Select(e => e.GetString()).Where(x => x is not null).ToHashSet()
                        : new HashSet<string?> { root.GetProperty("id").GetString() };
                    _requests.RemoveAll(x => ids.Contains(x.Id));
                    SaveList();
                    SendList(null);
                    break;
                }

                case "preview":
                {
                    var req = root.GetProperty("request").Deserialize<AdvanceRequest>(AdvanceNoteService.Json);
                    var ws = _workspace();
                    if (req is null || ws is null) { Js("advNote.onError('Chưa chọn workspace.')"); break; }
                    Js("advNote.setBusy(true, 'Đang quét file / procedure...')");
                    // Tick nhiều y/c thì xem trước đủ như lúc Generate (mỗi y/c 1 thư mục con); y/c đang mở lấy bản vừa sửa gửi lên.
                    var pickIds = root.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
                        ? idsEl.EnumerateArray().Select(e => e.GetString()).Where(x => x is not null).ToHashSet() : new HashSet<string?>();
                    var pickList = pickIds.Count > 0
                        ? _requests.Where(r => pickIds.Contains(r.Id)).Select(r => r.Id == req.Id ? req : r).ToList()
                        : new List<AdvanceRequest> { req };
                    var result = await BuildPackageAsync(ws, pickList, !root.TryGetProperty("split", out var spEl) || spEl.ValueKind != JsonValueKind.False);
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

                case "doneAfterGen":
                {
                    var on = root.TryGetProperty("on", out var onEl) && onEl.ValueKind == JsonValueKind.True;
                    if (_settings is not null && _settings.NoteDoneAfterGen != on)
                    {
                        _settings.NoteDoneAfterGen = on;
                        try { _settings.Save(); } catch { /* không lưu được thì lần sau về mặc định */ }
                    }
                    break;
                }

                case "pkgOpts":
                {
                    var o = ReadPackageOptions(root);
                    GenAllService.SavePackageOptions(o.Zip, o.Network, o.NetworkRoot);
                    break;
                }

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

                case "aiCatalog":
                    AiCatalogRequested?.Invoke(root.GetProperty("name").GetString() ?? "", root.GetProperty("content").GetString() ?? "");
                    break;

                case "catalogAssist":
                    CatalogAssistRequested?.Invoke(root.GetProperty("name").GetString() ?? "", root.GetProperty("content").GetString() ?? "",
                        root.TryGetProperty("mode", out var md) ? md.GetString() ?? "" : "");
                    break;

                case "noteHub":
                    NoteHubRequested?.Invoke();
                    break;

                case "team":
                    TeamRequested?.Invoke();
                    break;

                case "browseFiles":
                    BrowseFiles();
                    break;

                case "genPaths":
                    await GeneratePathsAsync(root);
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

                // 🗑 Xoá gói đã gen (kết quả vừa tạo / dòng "Đã gen trước đó") để gen lại — trang đã hỏi xác nhận.
                case "deletePackage":
                {
                    var path = root.GetProperty("path").GetString() ?? "";
                    if (!AdvanceNoteService.DeletePackage(_requests, path)) { Js("advNote.onError('Đường dẫn không có trong lịch sử gen — không xoá.')"); break; }
                    SaveList();
                    SendList(null);
                    Js($"advNote.onPackageDeleted({J(path)})");
                    break;
                }

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

    /// <summary>"🤖 AI tạo danh mục" cho y/c đang chọn (tên, nội dung) — MainForm mở Tạo nhanh danh mục và hỏi AI ở đó.</summary>
    public event Action<string, string>? AiCatalogRequested;

    /// <summary>Nút "🗂 Nhiều dự án": MainForm mở BcodeNote.exe vào dự án đang chọn (trang đã lưu phần đang gõ trước khi gửi).</summary>
    public event Action? NoteHubRequested;

    /// <summary>Nút "👥 Cả nhóm": mở tab xem note của người khác + gói update của dự án (TeamNotesControl).</summary>
    public event Action? TeamRequested;

    /// <summary>Nạp lại danh sách từ file (vd vừa chép 1 y/c của người khác về) — chỉ khi file đã đổi.</summary>
    public void ReloadIfChanged() { if (EnsureLoaded()) SendInit(); }

    /// <summary>Phím tắt note.aiCatalog: bấm nút "AI tạo danh mục" của trang (trang tự lưu + lấy y/c đang chọn).</summary>
    public void RequestAiCatalog() => Js("window.advNote && advNote.aiCatalog && advNote.aiCatalog()");

    /// <summary>"🔎 AI phân tích" (mode "analyze") / "⚙ Theo luật Fast" (mode "rules") cho y/c đang chọn (tên, nội dung, mode)
    /// — MainForm mở Tạo nhanh danh mục ở đúng chế độ.</summary>
    public event Action<string, string, string>? CatalogAssistRequested;

    /// <summary>Phím tắt note.aiAnalyze / note.fastRules: bấm nút tương ứng của trang.</summary>
    public void RequestCatalogAssist(string mode) => Js($"window.advNote && advNote.catalogAssist && advNote.catalogAssist({JsonSerializer.Serialize(mode)})");

    /// <summary>Điền gợi ý (theo menu đã chọn — BcodeNote.exe) vào y/c đang mở: thêm dòng chưa có vào Gen All / File Path / SQL Object App, Sys,
    /// đưa table vào "Table liên quan" (tick Cấu trúc cho table được chọn). Chưa mở y/c nào thì tạo y/c mới rồi điền. Trang tự lưu như khi gõ tay.</summary>
    public void ApplySuggestion(MenuSuggestionPick pick) => Js($"window.advNote && advNote.applySuggest && advNote.applySuggest({J(pick)})");

    private TaskCompletionSource? _flushed;

    /// <summary>Ghi ngay phần đang gõ dở (trang tự lưu sau 0,7 giây) rồi mới trả về — gọi trước khi đổi dự án / đóng cửa sổ (BcodeNote.exe).
    /// Trang chưa nạp xong hoặc không trả lời trong <paramref name="timeoutMs"/> thì thôi.</summary>
    public async Task FlushAsync(int timeoutMs = 1500)
    {
        if (!_web.IsReady || IsDisposed) return;
        var tcs = _flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Js("window.advNote && advNote.flush ? advNote.flush() : window.chrome.webview.postMessage(JSON.stringify({ action: 'flushed' }))");
        await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
    }

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
            rel = GenAllService.PackagePath(i.RelativeDestPath),
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
            var info = $"Bộ phận: {r.BpLt} | Nhân viên: {r.MaNv1} | Lập trình: {r.MaLt1}" + (string.IsNullOrWhiteSpace(r.MaXlyc) ? "" : " | Trạng thái y/c: " + r.MaXlyc.Trim());
            // Thứ tự: Menu (ten_menu) → noi_dung → Ghi chú (ghi_chu) → dòng thông tin bộ phận / nhân viên / lập trình.
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.TenMenu)) parts.Add("Menu: " + r.TenMenu.Trim());
            if (!string.IsNullOrWhiteSpace(r.NoiDung)) parts.Add(r.NoiDung.Trim());
            if (!string.IsNullOrWhiteSpace(r.GhiChu)) parts.Add("Ghi chú: " + r.GhiChu.Trim());
            return parts.Count == 0 ? info : string.Join("\r\n\r\n", parts) + "\r\n\r\n---\r\n" + info;
        }

        // Nội dung chỉ gồm phần tự sinh (Menu: / dòng "Bộ phận: …" / gạch ngang) — chưa có nội dung người dùng viết.
        static bool IsAutoOnly(string c) =>
            c.Length == 0 || c.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != "---")
                .All(l => l.StartsWith("Bộ phận:", StringComparison.Ordinal) || l.StartsWith("Menu:", StringComparison.Ordinal));

        var byName = _requests.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        int added = 0, skipped = 0, filled = 0;
        foreach (var row in rows)
        {
            if (byName.TryGetValue(row.Fcode1, out var existingReq))
            {
                skipped++;
                // y/c đã có: CHỈ cập nhật lại ô nội dung (Content) từ hệ thống; mọi thứ khác (Gen All, đường dẫn file, SQL Object, table, Top/Bottom script,
                // lịch sử Generate, Done...) giữ nguyên. Phần người dùng tự viết thêm SAU dòng "---/Bộ phận: …" cũng được giữ lại; nội dung tự viết không theo
                // khuôn tự sinh (không có dòng "---/Bộ phận") thì không đụng tới.
                var c = (existingReq.Content ?? "").Trim();
                var fresh = BuildContent(row);
                string? updated = null;
                if (IsAutoOnly(c)) updated = fresh;
                else
                {
                    var mInfo = System.Text.RegularExpressions.Regex.Match(c, @"(?:^|\r?\n)---\r?\nBộ phận:[^\r\n]*");
                    if (mInfo.Success)
                    {
                        var tail = c[(mInfo.Index + mInfo.Length)..].Trim();   // phần người dùng tự viết thêm sau dòng thông tin
                        updated = tail.Length > 0 ? fresh + "\r\n\r\n" + tail : fresh;
                    }
                }
                if (updated is not null && updated.Trim() != c)
                {
                    existingReq.Content = updated.TrimEnd();
                    filled++;
                }
                continue;
            }
            var req = new AdvanceRequest { Name = row.Fcode1, Content = BuildContent(row) };
            _requests.Add(req);
            byName[row.Fcode1] = req;
            added++;
        }
        if (added > 0 || filled > 0) SaveList();

        SendList(null);
        var message = rows.Count == 0
            ? $"Không có yêu cầu nào của \"{programmer}\" trong dự án {code}."
            : $"Đã thêm {added} y/c mới" + (skipped > 0 ? $", {skipped} y/c đã có" : "") + (filled > 0 ? $" (cập nhật lại nội dung cho {filled})" : "") + $" — tổng {rows.Count} yêu cầu của {programmer}.";
        Js($"advNote.setBusy(false); advNote.onSynced({J(new { ok = true, message })})");
    }

    /// <summary>2 tuỳ chọn gói trang gửi kèm (zip, net, netRoot) — trang cũ không gửi thì coi như tắt.</summary>
    private static (bool Zip, bool Network, string NetworkRoot) ReadPackageOptions(JsonElement root) =>
        (root.TryGetProperty("zip", out var z) && z.ValueKind == JsonValueKind.True,
         root.TryGetProperty("net", out var n) && n.ValueKind == JsonValueKind.True,
         root.TryGetProperty("netRoot", out var r) ? r.GetString() ?? "" : "");

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
        var merged = await BuildPackageAsync(ws, _requests.Where(r => ids.Contains(r.Id)).ToList(), !root.TryGetProperty("split", out var spEl) || spEl.ValueKind != JsonValueKind.False);

        if (merged.Items.Count == 0)
        {
            Js($"advNote.onGenerated({J(new { ok = false, message = "Không có mục nào để đưa vào gói update.", warnings = merged.Warnings })})");
            return;
        }

        var dest = Path.Combine(savePath, folder);
        var note = AdvancePackageBuilder.NoteText(_requests.Where(r => ids.Contains(r.Id)));
        var count = await Task.Run(() => GenAllService.CreatePackage(dest, merged.Items, description, note));
        _previewItems = OrderForRun(merged.Items);   // để bấm xem nội dung từng mục của gói vừa tạo
        // Chép lên đường dẫn mạng (tick zip = chỉ đưa .zip lên mạng): lỗi chỉ báo, gói ở Save At Path đã tạo xong vẫn giữ.
        var opts = ReadPackageOptions(root);
        GenAllService.SavePackageOptions(opts.Zip, opts.Network, opts.NetworkRoot);
        if (opts.Network) Js("advNote.setBusy(true, " + J("Đang chép gói lên đường dẫn mạng...") + ")");
        var extra = opts.Network ? await GenAllService.DeliverPackageAsync(dest, opts.Zip, opts.NetworkRoot) : new PackageDelivery();

        var now = DateTime.Now;
        if (_viewOwner is not null)
        {
            // Gen từ note của người khác: chỉ tạo gói — KHÔNG ghi lịch sử Generate / cột Updated vào note của họ.
            Js($"advNote.onGenerated({J(new { ok = true, path = dest, count, warnings = merged.Warnings, updated = now, items = ToView(_previewItems, merged.Warnings),
                network = extra.NetworkPath, webLink = extra.WebLink, errors = extra.Errors, readOnly = true })})");
            return;
        }
        // Ô "Done sau khi gen" (trang cũ không gửi → theo settings): gen xong tự đánh dấu Done.
        var markDone = root.TryGetProperty("done", out var dnEl) ? dnEl.ValueKind == JsonValueKind.True : _settings?.NoteDoneAfterGen ?? true;
        foreach (var req in _requests.Where(r => ids.Contains(r.Id)))
        {
            if (markDone) req.Done = true;
            req.Updated = now;
            // Nhớ link gói vừa tạo cho từng y/c đã tick — mở lại y/c sau này vẫn thấy.
            req.Generations.Insert(0, new GenerationRecord { Path = dest, Time = now });
            if (req.Generations.Count > AdvanceRequest.MaxGenerations)
                req.Generations.RemoveRange(AdvanceRequest.MaxGenerations, req.Generations.Count - AdvanceRequest.MaxGenerations);
        }
        SaveList();

        Js($"advNote.onGenerated({J(new { ok = true, path = dest, count, warnings = merged.Warnings, updated = now, items = ToView(_previewItems, merged.Warnings),
            network = extra.NetworkPath, webLink = extra.WebLink, errors = extra.Errors })})");
        SendList(null); // làm mới cột Updated
    }

    /// <summary>
    /// ⚙ Gen update tất cả / dòng đang chọn ở thẻ File Path: gói CHỈ gồm các đường dẫn trang gửi lên (không Gen All, SQL Object, table, script),
    /// cùng Save At Path / Folder Name / tuỳ chọn mạng như Generate Update. Ghi link gói vào lịch sử gen của y/c đang mở nhưng KHÔNG đánh Done / Updated
    /// (gói chưa đủ phần của y/c); đang xem note người khác thì không ghi gì.
    /// </summary>
    private async Task GeneratePathsAsync(JsonElement root)
    {
        var ws = _workspace();
        if (ws is null) { Js("advNote.onError('Chưa chọn workspace.')"); return; }
        var paths = root.TryGetProperty("paths", out var pEl) && pEl.ValueKind == JsonValueKind.Array
            ? pEl.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : new List<string>();
        var savePath = root.GetProperty("savePath").GetString()?.Trim() ?? "";
        var folder = root.GetProperty("folder").GetString()?.Trim() ?? "";
        var description = root.TryGetProperty("description", out var d) ? d.GetString() : "";
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (GenAllService.SplitLines(string.Join("\n", paths)).Count == 0) { Js("advNote.onError('Ô File Path chưa có đường dẫn nào để gen.')"); return; }
        if (savePath.Length == 0) { Js("advNote.onError('Chưa nhập Save At Path.')"); return; }
        if (folder.Length == 0) { Js("advNote.onError('Chưa nhập Folder Name.')"); return; }

        Js("advNote.setBusy(true, 'Đang tạo gói update từ File Path...')");
        var res = await AdvancePackageBuilder.BuildFilePathsAsync(_genAll, ws, paths);
        if (res.Items.Count == 0)
        {
            Js($"advNote.onGenerated({J(new { ok = false, message = "Không có file nào để đưa vào gói update (đường dẫn không tồn tại?).", warnings = res.Warnings })})");
            return;
        }
        var dest = Path.Combine(savePath, folder);
        var count = await Task.Run(() => GenAllService.CreatePackage(dest, res.Items, description, null));
        _previewItems = OrderForRun(res.Items);
        var opts = ReadPackageOptions(root);
        GenAllService.SavePackageOptions(opts.Zip, opts.Network, opts.NetworkRoot);
        if (opts.Network) Js("advNote.setBusy(true, " + J("Đang chép gói lên đường dẫn mạng...") + ")");
        var extra = opts.Network ? await GenAllService.DeliverPackageAsync(dest, opts.Zip, opts.NetworkRoot) : new PackageDelivery();

        var now = DateTime.Now;
        var req = _viewOwner is null ? _requests.FirstOrDefault(r => r.Id == id) : null;
        if (req is not null)
        {
            req.Generations.Insert(0, new GenerationRecord { Path = dest, Time = now });
            if (req.Generations.Count > AdvanceRequest.MaxGenerations)
                req.Generations.RemoveRange(AdvanceRequest.MaxGenerations, req.Generations.Count - AdvanceRequest.MaxGenerations);
            SaveList();
        }
        var n = GenAllService.SplitLines(string.Join("\n", paths)).Count;
        Js($"advNote.onGenerated({J(new { ok = true, path = dest, count, warnings = res.Warnings, updated = now, items = ToView(_previewItems, res.Warnings),
            network = extra.NetworkPath, webLink = extra.WebLink, errors = extra.Errors, readOnly = _viewOwner is not null,
            label = $"Đã tạo gói update từ {n} đường dẫn ở File Path (chỉ file, không script)." })})");
        if (req is not null) SendList(null);   // làm mới lịch sử gen
    }

    /// <summary>Gom script/file của các y/c đã chọn thành 1 gói (1 y/c: phẳng; nhiều y/c: mỗi y/c 1 thư mục con) — dùng chung cho Xem trước gói và Generate Update.</summary>
    private async Task<GenAllResult> BuildPackageAsync(Workspace ws, List<AdvanceRequest> picked, bool split = true)
    {
        var merged = new GenAllResult();
        if (!split && picked.Count > 1)
        {
            // Gộp chung: mọi y/c vào 1 gói phẳng — 1 APP_Script.sql / SYS_Script.sql sắp theo thứ tự table → top → view → stored → bottom, file trùng đường dẫn chỉ lấy 1 lần.
            // Top / Bottom Script của từng y/c khác nhau thì nối hết (không bỏ y/c nào) — cùng nội dung chỉ lấy 1 lần.
            var topBottom = new Dictionary<string, (string Origin, List<string> Parts)>(StringComparer.OrdinalIgnoreCase);
            foreach (var req in picked)
            {
                var one = await _genAll.ResolveAsync(ws, req, combine: false);
                foreach (var item in one.Items)
                {
                    if (item.Origin is "SQL Top Script" or "SQL Bottom Script" && item.GeneratedContent is not null)
                    {
                        if (!topBottom.TryGetValue(item.RelativeDestPath, out var tb)) topBottom[item.RelativeDestPath] = tb = (item.Origin, new List<string>());
                        if (!tb.Parts.Contains(item.GeneratedContent)) tb.Parts.Add(item.GeneratedContent);
                        continue;
                    }
                    merged.Add(item);
                }
                merged.Warnings.AddRange(one.Warnings.Select(w => $"[{req.Name}] {w}"));
            }
            foreach (var (path, tb) in topBottom)
                merged.Add(new PackageItem { Origin = tb.Origin, RelativeDestPath = path, GeneratedContent = string.Join("\r\nGO\r\n", tb.Parts) });
            GenAllService.CombineScripts(merged);
            return merged;
        }
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
        return merged;
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
