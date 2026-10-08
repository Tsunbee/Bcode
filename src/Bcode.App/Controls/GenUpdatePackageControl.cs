using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Gen Update" document tab (WCommand &gt; Gen Update in FCode) — NOT to be confused with
/// the older "Gen Update (dòng đã chọn)" SQL UPDATE-statement generator already in SQL
/// Query/Command's result-grid context menu (<see cref="GenUpdateService"/>,
/// Ctrl+Shift+U). Tab này đóng gói FILE SOURCE (.f/.xml/.xlsx... dưới App_Data) của một hay nhiều
/// menu WCommand thành thư mục "update" gửi cho site khách, giống màn WCommand &gt; Gen Update của FCode.
///
/// Giao diện là trang WebView2 (Web/Shell/genupdate.html, tự co giãn theo cỡ cửa sổ); control này chỉ giữ dữ liệu:
///   • danh sách mục của gói (file thật cần copy, hoặc script sinh ra từ Gen nhanh Store),
///   • cây Source File (dựng từ menu WCommand được double-click — <see cref="LoadForMenuItem"/> — hoặc từ thanh lọc
///     "File [danh mục] [tên/SysId]" mô phỏng Declaration for Generation của FCode),
///   • tạo thư mục gói.
///
/// GIẢ ĐỊNH cần Bee xác nhận lại: tên thư mục thật ứng với mỗi danh mục suy ra bằng cách bỏ tiền tố "Web_"
/// (Dir/Filter/Grid chắc chắn đúng; các cái khác suy đoán theo quy luật đặt tên). Web_Main trỏ tới thư mục "Main"
/// nằm NGANG HÀNG App_Data (không phải trong Controllers).
/// </summary>
public class GenUpdatePackageControl : UserControl
{
    private readonly FileLookupService _fileLookupService;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly WebBarHost _web = new("genupdate.html") { Dock = DockStyle.Fill };
    private readonly Workspace? _workspace;
    private readonly string? _sourceRoot; // gốc site (Workspace.SourcePath): Main\ và App_Data\Controllers\ đều tính từ đây

    /// <summary>1 mục trong gói update — copy nguyên 1 file có sẵn (SourceFilePath) hoặc ghi nội dung sinh ra (GeneratedContent).</summary>
    private sealed class BatchItem
    {
        public required string DisplayName { get; init; }
        public required string RelativeDestPath { get; init; }
        public string? SourceFilePath { get; init; }
        public string? GeneratedContent { get; init; }
        public string Key => RelativeDestPath.ToLowerInvariant();
    }

    private readonly List<BatchItem> _batch = new();

    // Nhãn → tên thư mục con của App_Data\Controllers ("" = quét cả Controllers, "Main" = thư mục ngoài Controllers).
    private static readonly (string Label, string Folder)[] ControllerCategories =
    {
        ("Tất cả (Web_All)", ""),
        ("Web_Dir", "Dir"),
        ("Web_Filter", "Filter"),
        ("Web_Grid", "Grid"),
        ("Web_Lookup", "Lookup"),
        ("Web_Report", "Report"),
        ("Web_Report_Include", "Report_Include"),
        ("Web_Upload", @"Templates\Upload"),                  // Controllers\Templates\Upload
        ("Web_Upload_Include", @"Templates\Upload\Include"),  // Controllers\Templates\Upload\Include
        ("Web_Main", "Main"),
        ("Web_Include", "Include"),
        ("Web_Command", "Command"),
        ("Web_Javascript", "Javascript"),
        ("Web_XML", "XML"),
        ("Web_Excel", "Excel"),
        ("Web_Rpt", "Rpt"),
        ("Web_Image", "Image"),
        ("Web_Bin", "Bin"),
    };

    private string? _lastTreeJson;   // cây hiện tại — gửi lại khi trang nạp xong (LoadForMenuItem có thể gọi trước đó)
    private string? _lastTreeTitle;
    private List<string>? _names;
    private List<string>? _procs;

    private readonly GenAllService? _genAll;

    public GenUpdatePackageControl(FileLookupService fileLookupService, SqlObjectBrowserService sqlObjectService, Workspace? workspace, GenAllService? genAll = null)
    {
        _genAll = genAll;
        _fileLookupService = fileLookupService;
        _sqlObjectService = sqlObjectService;
        _workspace = workspace;
        if (workspace is not null && !string.IsNullOrWhiteSpace(workspace.SourcePath))
            _sourceRoot = workspace.SourcePath;

        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += SendInit;

        // Gợi ý tên (file trong Controllers, stored procedure) nạp nền — lỗi UNC/DB chỉ bỏ qua gợi ý, không chặn việc chính.
        _ = LoadNamesAsync();
        _ = LoadProcsAsync();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);
    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private object BatchView() => _batch.Select(b => new
    {
        key = b.Key,
        name = b.DisplayName,
        rel = b.RelativeDestPath,
        kind = b.GeneratedContent is not null ? "script" : "file",
    });

    private void SendInit()
    {
        var ws = _workspace;
        var folderPrefix = string.Join("_", new[] { ws?.ProjectId, Environment.MachineName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Js($"genUpdate.init({J(new
        {
            workspace = ws?.Name ?? "",
            program = ws?.ProgramPath ?? "",
            mobile = ws?.MobilePath ?? "",
            save = ws?.WorkingPath ?? "",
            folderPrefix,
            categories = ControllerCategories.Select(c => new { label = c.Label, value = c.Folder }),
            batch = BatchView(),
        })})");
        if (_lastTreeJson is not null) Js($"genUpdate.onTree({_lastTreeJson}, {J(_lastTreeTitle)})");
        if (_names is not null) Js($"genUpdate.onNames({J(_names)})");
        if (_procs is not null) Js($"genUpdate.onProcs({J(_procs)})");
    }

    // ---- gợi ý ---------------------------------------------------------------------------------

    private async Task LoadNamesAsync()
    {
        if (_sourceRoot is null) return;
        var controllersRoot = Path.Combine(_sourceRoot, "App_Data", "Controllers");
        try
        {
            _names = await Task.Run(() =>
            {
                if (!Directory.Exists(controllersRoot)) return new List<string>();
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(controllersRoot, "*", SearchOption.AllDirectories))
                {
                    set.Add(Path.GetFileName(file));
                    set.Add(Path.GetFileNameWithoutExtension(file));
                }
                return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }
        catch { return; }
        if (!IsDisposed && _names.Count > 0) Js($"genUpdate.onNames({J(_names)})");
    }

    private async Task LoadProcsAsync()
    {
        if (_sourceRoot is null) return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var useSys in new[] { false, true })
        {
            try
            {
                foreach (var obj in await _sqlObjectService.ListObjectsAsync(useSys))
                    if (obj.Kind == SqlObjectKind.StoredProcedure) names.Add(obj.Name);
            }
            catch { /* thiếu 1 trong 2 database / mất kết nối tạm — bỏ qua, đây chỉ là gợi ý */ }
        }
        _procs = names.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        if (!IsDisposed && _procs.Count > 0) Js($"genUpdate.onProcs({J(_procs)})");
    }

    // ---- cây source ----------------------------------------------------------------------------

    private static object ToView(FileLookupNode n) => new
    {
        name = n.Name,
        path = n.FullPath,
        dir = n.IsDirectory,
        issues = n.Issues,
        children = n.Children.Select(ToView).ToList(),
    };

    private void PushTree(FileLookupNode root, string title)
    {
        _lastTreeJson = J(ToView(root));
        _lastTreeTitle = title;
        Js($"genUpdate.onTree({_lastTreeJson}, {J(title)})");
    }

    /// <summary>Gọi khi double-click 1 menu WCommand lúc tab này đang mở — dựng lại cây Source File theo Link/SysId của menu
    /// (đúng cách File Lookup phân giải: Main\&lt;link&gt; + các file App_Data\Controllers\&lt;sysId&gt; liên quan).</summary>
    public void LoadForMenuItem(WCommandItem item)
    {
        if (_sourceRoot is null)
        {
            Js("genUpdate.onError('Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.')");
            return;
        }
        if (string.IsNullOrWhiteSpace(item.Link) && string.IsNullOrWhiteSpace(item.SysId))
        {
            Js($"genUpdate.onError({J($"Menu \"{item.Bar}\" không có Link/SysId gắn với source (có thể là mục nhóm/menu cha).")})");
            return;
        }

        // onlyFInGridFilterDir: Grid/Filter/Dir đã có bản .f biên dịch nên không đưa .xml nguồn vào gói update.
        var root = _fileLookupService.BuildTreeForMenuItem(_sourceRoot, item.Link, item.SysId, onlyFInGridFilterDir: true);
        PushTree(root, string.IsNullOrWhiteSpace(item.Bar) ? item.SysId : item.Bar);
    }

    private async Task SearchAsync(string category, string name)
    {
        if (_sourceRoot is null)
        {
            Js("genUpdate.onError('Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.')");
            return;
        }

        var controllersRoot = Path.Combine(_sourceRoot, "App_Data", "Controllers");
        string searchRoot;
        if (string.IsNullOrEmpty(category)) searchRoot = controllersRoot;
        else if (string.Equals(category, "Main", StringComparison.OrdinalIgnoreCase)) searchRoot = Path.Combine(_sourceRoot, "Main");
        else searchRoot = Path.Combine(controllersRoot, category);

        if (!Directory.Exists(searchRoot))
        {
            Js($"genUpdate.onError({J($"Không tìm thấy thư mục: {searchRoot} — tên thư mục thật trên site này có thể khác, báo lại để chỉnh bảng danh mục.")})");
            return;
        }

        name = name.Trim();
        var root = await Task.Run(() => _fileLookupService.BuildTree(searchRoot, extensionFilter: "",
            searchText: name.Length > 0 ? name : null, onlyShowFiltered: false));
        PushTree(root, name.Length > 0 ? $"“{name}”" : "tất cả");
        if (root.Children.Count == 0) Js("genUpdate.toast('Không tìm thấy file nào khớp.', 'err')");
    }

    // ---- xử lý tin nhắn từ trang ----------------------------------------------------------------

    private async Task HandleAsync(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            switch (root.GetProperty("action").GetString())
            {
                case "ready": SendInit(); break;

                case "search":
                    await SearchAsync(root.GetProperty("category").GetString() ?? "", root.GetProperty("name").GetString() ?? "");
                    break;

                case "add":
                {
                    var added = 0;
                    foreach (var el in root.GetProperty("paths").EnumerateArray())
                    {
                        var path = el.GetString();
                        if (string.IsNullOrEmpty(path) || _sourceRoot is null || File.Exists(path) == false) continue;
                        if (_batch.Any(b => string.Equals(b.SourceFilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
                        var relative = PackageLayout.Web(Path.GetRelativePath(_sourceRoot, path));
                        _batch.Add(new BatchItem { DisplayName = Path.GetFileName(path), RelativeDestPath = relative, SourceFilePath = path });
                        added++;
                    }
                    Js($"genUpdate.onBatch({J(BatchView())}, {J(added > 0 ? $"Đã thêm {added} file vào gói update." : "Các file đã tick đều đã có trong gói.")})");
                    break;
                }

                case "removeBatch":
                {
                    var keys = root.GetProperty("keys").EnumerateArray().Select(e => e.GetString()).ToHashSet();
                    _batch.RemoveAll(b => keys.Contains(b.Key));
                    Js($"genUpdate.onBatch({J(BatchView())})");
                    break;
                }

                case "clearBatch":
                    _batch.Clear();
                    Js($"genUpdate.onBatch({J(BatchView())})");
                    break;

                case "genStore":
                    await GenStoreScriptAsync(root.GetProperty("name").GetString() ?? "", root.GetProperty("db").GetString() == "sys");
                    break;

                case "loadTables":
                {
                    if (_genAll is null || _workspace is null) { Js("genUpdate.onError('Chưa sẵn sàng để dò table.')"); break; }
                    var names = (root.GetProperty("controllers").GetString() ?? "")
                        .Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (names.Count == 0) { Js("genUpdate.onError('Nhập tên controller / SysId trước.')"); break; }
                    var list = await _genAll.FindTablesAsync(_workspace, names);
                    Js($"genUpdate.onTables({J(list)}, false)");
                    break;
                }

                case "lookupTable":
                {
                    if (_genAll is null) break;
                    var info = await _genAll.LookupTableAsync(root.GetProperty("name").GetString() ?? "");
                    Js($"genUpdate.onTables({J(new[] { info })}, true)");
                    break;
                }

                case "genTables":
                {
                    if (_genAll is null) break;
                    var picks = root.GetProperty("tables").EnumerateArray().Select(e => new TableSelection
                    {
                        Name = e.GetProperty("name").GetString() ?? "",
                        Sys = e.GetProperty("sys").GetBoolean(),
                        Structure = e.GetProperty("structure").GetBoolean(),
                        Data = e.GetProperty("data").GetBoolean(),
                        Where = e.TryGetProperty("where", out var w) ? w.GetString() ?? "" : "",
                    }).ToList();
                    var res = await _genAll.ResolveTablesAsync(picks);
                    foreach (var p in res.Items)
                    {
                        var item = new BatchItem { DisplayName = p.Origin, RelativeDestPath = p.RelativeDestPath, GeneratedContent = p.GeneratedContent };
                        var i = _batch.FindIndex(b => string.Equals(b.RelativeDestPath, item.RelativeDestPath, StringComparison.OrdinalIgnoreCase));
                        if (i >= 0) _batch[i] = item; else _batch.Add(item);
                    }
                    var msg = res.Items.Count > 0 ? $"Đã thêm {res.Items.Count} script table vào gói update." : "Không sinh được script nào.";
                    if (res.Warnings.Count > 0) msg += "\n" + string.Join("\n", res.Warnings);
                    Js($"genUpdate.busy(false); genUpdate.onBatch({J(BatchView())}, {J(msg)})");
                    if (res.Items.Count == 0 && res.Warnings.Count > 0) Js($"genUpdate.onError({J(string.Join(" | ", res.Warnings))})");
                    break;
                }

                case "create":
                    await CreateAsync(root.GetProperty("savePath").GetString() ?? "", root.GetProperty("folder").GetString() ?? "",
                        root.TryGetProperty("description", out var d) ? d.GetString() : "");
                    break;

                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard bận */ }
                    break;

                case "open":
                {
                    var path = root.GetProperty("path").GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                    else Js("genUpdate.toast('Thư mục không tồn tại.', 'err')");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Js($"genUpdate.onError({J(ex.Message)})");
        }
    }

    /// <summary>"Gen Script" của Gen nhanh Store — tìm đúng 1 stored procedure theo tên trong database App/Sys, lấy definition
    /// (đã ALTER, sẵn sàng chạy update) và thêm vào gói tại Script\app\&lt;tên&gt;.sql hoặc Script\sys\&lt;tên&gt;.sql.</summary>
    private async Task GenStoreScriptAsync(string name, bool useSys)
    {
        name = name.Trim();
        if (name.Length == 0) { Js("genUpdate.onError('Nhập tên stored procedure trước.')"); return; }
        try
        {
            var matches = await _sqlObjectService.ListObjectsAsync(useSys, name);
            var target = matches.FirstOrDefault(o => o.Kind == SqlObjectKind.StoredProcedure
                    && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault(o => o.Kind == SqlObjectKind.StoredProcedure);
            if (target is null)
            {
                Js($"genUpdate.onError({J($"Không tìm thấy stored procedure nào khớp \"{name}\" trong {(useSys ? "Sys Data" : "App Data")}.")})");
                return;
            }

            var script = await _sqlObjectService.GetDefinitionAsync(target);
            var relative = PackageLayout.Script(useSys, $"{target.Name}.sql");
            var item = new BatchItem { DisplayName = $"[{(useSys ? "Sys" : "App")}] {target.Name}", RelativeDestPath = relative, GeneratedContent = script };
            var i = _batch.FindIndex(b => string.Equals(b.RelativeDestPath, relative, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _batch[i] = item; else _batch.Add(item);
            Js($"genUpdate.busy(false); genUpdate.onBatch({J(BatchView())}, {J($"Đã thêm script {target.Name} ({(useSys ? "Sys" : "App")}) vào gói update.")})");
        }
        catch (Exception ex)
        {
            Js($"genUpdate.onError({J(ex.Message)})");
        }
    }

    private async Task CreateAsync(string savePath, string folder, string? description)
    {
        savePath = savePath.Trim();
        folder = folder.Trim();
        if (_batch.Count == 0) { Js("genUpdate.onError('Chưa có file nào trong gói update.')"); return; }
        if (savePath.Length == 0) { Js("genUpdate.onError('Chưa nhập Save At Path.')"); return; }
        if (folder.Length == 0) { Js("genUpdate.onError('Chưa nhập Folder Name.')"); return; }

        var destRoot = Path.Combine(savePath, folder);
        var items = _batch.ToList();
        try
        {
            var count = await Task.Run(() =>
            {
                Directory.CreateDirectory(destRoot);
                var copied = 0;
                foreach (var item in items)
                {
                    var destFile = Path.Combine(destRoot, item.RelativeDestPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                    if (item.GeneratedContent is not null) File.WriteAllText(destFile, item.GeneratedContent);
                    else File.Copy(item.SourceFilePath!, destFile, overwrite: true);
                    copied++;
                }
                if (!string.IsNullOrWhiteSpace(description))
                    File.WriteAllText(Path.Combine(destRoot, "description.txt"), description);
                return copied;
            });
            Js($"genUpdate.onCreated({J(new { ok = true, path = destRoot, count })})");
        }
        catch (Exception ex)
        {
            Js($"genUpdate.onCreated({J(new { ok = false, message = ex.Message })})");
        }
    }
}
