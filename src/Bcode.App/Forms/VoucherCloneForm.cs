using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Tạo chứng từ" — nhân bản 1 chứng từ có sẵn sang mã mới, 6 bước như FCode (General, File Source, File Reference, Sql Declaration,
/// Wcommand, File Generation), xuất theo đúng dạng gói FCode: Web\App_Data\..., Web\Main\..., Script\App\AppScript.sql,
/// Script\Sys\SysScript.sql. Đổi tên / nội dung file: <see cref="VoucherCloneService"/>; SQL: <see cref="VoucherSqlScriptService"/>
/// (chỉ sinh script); menu: mở form WCOMMAND có sẵn. Giao diện: Web/Shell/vouchercopy.html.
/// </summary>
public class VoucherCloneForm : WebDialogForm
{
    private const string Title = "Tạo chứng từ";

    private readonly DbConnectionService _connections;
    private readonly WCommandService _wcommand;
    private readonly AppSettings _settings;
    private readonly VoucherCloneService _service;
    private readonly VoucherSqlScriptService _sql;
    private readonly object _loadLock = new();
    private CancellationTokenSource _loadCts = new();
    private Task<SourceData>? _load;
    private string _loadSource = "";
    private VoucherPreviewForm? _preview;

    /// <summary>Nút Open ở bước Sql Declaration: (script, chạy trên Sys, tiêu đề tab) — MainForm mở tab SQL Query mới.</summary>
    public event Action<string, bool, string>? OpenSqlRequested;

    /// <summary>Cây file + chỉ mục của 1 thư mục source, dựng 1 lần ở nền rồi mọi bước dùng chung.</summary>
    private sealed record SourceData(SourceTree Tree, EntityIndex Entities);

    public VoucherCloneForm(DbConnectionService connections, WCommandService wcommand, AppSettings settings)
        : base(Title, "vouchercopy.html", 1300, 860, 900, 600)
    {
        _connections = connections;
        _wcommand = wcommand;
        _settings = settings;
        _service = new VoucherCloneService(connections);
        _sql = new VoucherSqlScriptService(connections);

        // Mở song song với Bcode (MainForm.OpenVoucherClone dùng Show) — cửa sổ riêng trên taskbar, thu nhỏ được.
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        MinimizeBox = true;
        ShowInTaskbar = true;
        if (Bcode.App.UI.AppIcons.AppIcon is { } icon) { Icon = icon; ShowIcon = true; }
        Bcode.App.UI.ThemeManager.Subscribe(this, PushEditorTheme);
    }

    private static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Theme cho các khung Monaco trong trang: tô màu XML / SQL giống BcodeViewer.</summary>
    private void PushEditorTheme()
    {
        try { Js($"vc.onTheme({Bcode.App.Controls.MonacoPreviewControl.ThemeJson(Bcode.App.UI.AppColors.IsDark)})"); }
        catch { /* viewer-theme.json đang ghi dở — giữ theme hiện tại */ }
    }

    protected override void OnReady()
    {
        PushEditorTheme();
        var src = _connections.Current?.SourcePath ?? "";
        Js($"vc.init({J(new { source = src, workspaceSource = src, desktop = Path.Combine(Desktop, "VoucherClone") })})");
        // Quét ngay cả khi source là đường mạng: Source Path do người dùng tự khai, lần sau chỉ đọc file đã đổi.
        _ = ScanAsync(src);
        _ = SuggestTableNoAsync();
    }

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
        switch (action)
        {
            case "scan": await ScanAsync(S("source")); break;
            case "suggestTableNo": await SuggestTableNoAsync(); break;
            case "approval": await ApprovalAsync(S("source"), S("sysId"), S("code")); break;
            case "plan": await PlanAsync(S("source"), ReadSpec(msg.GetProperty("spec"))); break;
            case "sql": await SqlAsync(msg); break;
            case "previewFile": await PreviewAsync(S("source"), S("rel"), S("kind"), ReadSpec(msg.GetProperty("spec"))); break;
            case "viewVoucher": await ViewVoucherAsync(msg); break;
            case "loadMenus": await LoadMenusAsync(); break;
            case "menu": await MenuAsync(ReadSpec(msg.GetProperty("spec")), S("mainTarget"), msg); break;
            case "generate": { var copy = msg.Clone(); var data = await DataAsync(S("source")); BeginInvoke(new Action(() => Generate(copy, data))); break; }
            case "pickFolder":
            {
                var target = S("target");
                var current = S("path");
                BeginInvoke(new Action(() =>
                {
                    using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(current) ? current : "" };
                    if (dlg.ShowDialog(this) == DialogResult.OK) Js($"vc.onFolder({J(target)}, {J(dlg.SelectedPath)})");
                }));
                break;
            }
            case "openFolder": OpenFolder(S("path")); break;
            case "openSql":
            {
                var script = S("script");
                var sys = msg.TryGetProperty("sys", out var sp) && sp.ValueKind == JsonValueKind.True;
                var title = S("title");
                BeginInvoke(new Action(() => OpenSqlRequested?.Invoke(script, sys, title)));
                break;
            }
            case "openViewer": { var p = S("path"); BeginInvoke(new Action(() => OpenInViewer(p))); break; }
        }
    }

    private static VoucherCloneSpec ReadSpec(JsonElement s)
    {
        string S(string n) => s.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
        var spec = new VoucherCloneSpec
        {
            SrcCode = S("srcCode"), SrcSysId = S("srcSysId"), SrcTitleV = S("srcTitleV"), SrcTitleE = S("srcTitleE"), SrcTableNo = S("srcTableNo"),
            SrcNoPartition = s.TryGetProperty("srcNoPartition", out var np) && np.ValueKind == JsonValueKind.True,
            DstCode = S("dstCode"), DstSysId = S("dstSysId"), DstPrefix = S("dstPrefix"), DstTitleV = S("dstTitleV"), DstTitleE = S("dstTitleE"),
            NewTables = s.TryGetProperty("newTables", out var n) && n.ValueKind == JsonValueKind.True, DstTableNo = S("dstTableNo"),
            SrcApprove = S("srcApprove"), DstApprove = S("dstApprove"), DstApproveController = S("dstApproveController"), SrcNotify = S("srcNotify"), DstNotify = S("dstNotify"),
        };
        if (s.TryGetProperty("fileRenames", out var fr) && fr.ValueKind == JsonValueKind.Array)
            spec.FileRenames = fr.EnumerateArray().Select(x => (x.GetProperty("from").GetString() ?? "", x.GetProperty("to").GetString() ?? "")).ToList();
        if (s.TryGetProperty("copied", out var cp) && cp.ValueKind == JsonValueKind.Array)
            spec.Copied = cp.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
        return spec;
    }


    /// <summary>
    /// Chuẩn bị spec theo đúng các file sẽ chép (tiền tố chứng từ khác, tên dùng chung, entity chép / giữ tên) để xem trước, SQL và ghi file
    /// ra cùng 1 kết quả. Không truyền <paramref name="copied"/> thì lấy file đang tick ở bước 2, chưa có thì mọi file trừ file sao lưu.
    /// </summary>
    private static void Prepare(SourceData data, VoucherCloneSpec spec, IEnumerable<string>? copied = null)
    {
        spec.OtherPrefixes = VoucherCloneService.OtherPrefixes(data.Entities.Vouchers, spec.SrcPrefix);
        var list = copied ?? (spec.Copied.Count > 0 ? spec.Copied
            : VoucherCloneService.SourceFiles(data.Tree, spec.SrcPrefix, spec.OtherPrefixes).Where(f => !VoucherCloneService.IsBackupOf(f, spec.SrcSysId)));
        VoucherCloneService.Prepare(data.Tree, spec, list, data.Entities);
    }

    /// <summary>
    /// Dữ liệu của 1 source: đang dựng hoặc đã xong thì dùng lại, không dựng 2 lần. <paramref name="refresh"/> = duyệt lại cây (bấm Quét);
    /// file không đổi vẫn lấy từ cache trên đĩa nên không đọc lại.
    /// </summary>
    private Task<SourceData> DataAsync(string source, bool refresh = false)
    {
        lock (_loadLock)
        {
            if (!refresh && _load is { IsFaulted: false, IsCanceled: false } && source.Equals(_loadSource, StringComparison.OrdinalIgnoreCase)) return _load;
            _loadCts.Cancel();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;
            _loadSource = source;
            return _load = Task.Run(() => LoadSource(source, ct), ct);
        }
    }

    private SourceData LoadSource(string source, CancellationToken ct)
    {
        // Source của workspace: danh sách theo wcommand, hỏi database song song với duyệt cây. Dự án khác không có kết nối nên theo file Dir.
        var menuTask = IsWorkspace(source) ? MenuVouchersAsync() : Task.FromResult<List<VoucherInfo>?>(null);
        var tree = SourceTree.Load(source, ct);
        List<VoucherInfo>? all = null;
        var entities = EntityIndex.Load(tree, CachePath(source), (done, total) => Js($"vc.onIndex({done}, {total})"),
            dir => all = ShowVouchers(source, dir, menuTask.GetAwaiter().GetResult()), ct);
        entities.Vouchers = all ?? ShowVouchers(source, entities.Vouchers, menuTask.GetAwaiter().GetResult());   // không có Controllers thì EntityIndex không báo danh sách
        Js("vc.onIndex(0, 0)");
        return new SourceData(tree, entities);
    }

    private static string CachePath(string source)
    {
        var key = SHA1.HashData(Encoding.UTF8.GetBytes(source.TrimEnd('\\', '/').ToLowerInvariant()));
        return Path.Combine(BcodePaths.AppData, "cache", "voucher-clone", Convert.ToHexString(key)[..16] + ".json.gz");
    }

    private bool IsWorkspace(string source) => source.Equals(_connections.Current?.SourcePath ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Chứng từ theo wcommand; null = không đọc được hoặc không có dòng nào (sản phẩm không có wcommand), khi đó theo file Dir.</summary>
    private async Task<List<VoucherInfo>?> MenuVouchersAsync()
    {
        try { return await _service.MenuVouchersAsync() is { Count: > 0 } list ? list : null; }
        catch { return null; }
    }

    /// <summary>Đưa danh sách lên trang. Trả về mọi chứng từ đã biết, gồm cả chứng từ có file mà không có menu: vẫn phải tính khi kiểm tra trùng mã và tiền tố.</summary>
    private List<VoucherInfo> ShowVouchers(string source, List<VoucherInfo> dir, List<VoucherInfo>? menu)
    {
        var (list, others) = menu != null ? VoucherCloneService.MergeMenu(menu, dir) : (dir, new List<VoucherInfo>());
        static object Row(VoucherInfo v) => new { code = v.Code, sysId = v.SysId, table = v.Table, titleV = v.TitleV, titleE = v.TitleE, prefix = v.Prefix, tableNo = v.TableNo };
        Js($"vc.onVouchers({J(list.Select(Row))}, {J(IsWorkspace(source))}, {J(others.Select(Row))})");

        var withFile = dir.Select(v => v.SysId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var status = menu != null ? $"{list.Count} chứng từ trong wcommand, {list.Count(v => withFile.Contains(v.SysId))} chứng từ có file Dir trong source."
            : $"{list.Count} chứng từ trong source." + (IsWorkspace(source) ? " Không lấy được từ wcommand nên liệt kê theo file Dir." : "");
        Js($"vc.onStatus({J(status)}, {J(list.Count == 0 ? "err" : "")})");
        return list.Concat(others).ToList();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _loadCts.Cancel();   // đang đọc source: dừng, phần đã đọc vẫn được lưu vào cache
        _preview?.Close();
        base.OnFormClosed(e);
    }

    // ---- Bước 1 ---------------------------------------------------------------------------------------

    private async Task ScanAsync(string source)
    {
        if (source.Length == 0) { Js($"vc.onStatus({J("Chưa có thư mục source (workspace chưa khai Source Path) — chọn thư mục source.")}, 'err')"); return; }
        Js($"vc.onStatus({J("Đang quét chứng từ trong source...")}, '')");
        try
        {
            var data = await DataAsync(source, refresh: true);   // danh sách chứng từ hiện ngay khi đọc xong Dir, phần còn lại đọc tiếp ở nền
            if (data.Tree.Controllers == null) Js($"vc.onStatus({J("Không thấy App_Data\\Controllers trong thư mục source.")}, 'err')");
        }
        catch (OperationCanceledException) { /* bấm Quét lần nữa hoặc đóng form */ }
        catch (Exception ex) { Js($"vc.onStatus({J("Không quét được source: " + ex.Message)}, 'err')"); }
    }

    /// <summary>Khai báo duyệt / thông báo của chứng từ nguồn, hiện ngay khi chọn. Loại duyệt không khai trong file thì hỏi gndmloaiduyet.</summary>
    private async Task ApprovalAsync(string source, string sysId, string code)
    {
        VoucherApproval? info = null;
        try { info = await Task.Run(() => VoucherCloneService.ReadApproval(source, sysId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* không đọc được file: để trống cho người dùng tự nhập */ }
        var type = info?.Type;
        if (type == null) try { type = await _sql.SourceApproveTypeAsync(code); } catch { /* không có DB */ }
        Js($"vc.onApproval({J(sysId)}, {J(new { hasFile = info != null, type, controller = info?.Controller, notify = info?.Notify })})");
    }

    private async Task SuggestTableNoAsync()
    {
        try { Js($"vc.onTableNo({J(await _service.SuggestTableNoAsync())})"); }
        catch { /* chưa có kết nối — người dùng tự nhập */ }
    }

    // ---- Bước 2 + 3 + 5: file, file đăng ký, tham chiếu, menu nguồn ---------------------------------------

    private async Task PlanAsync(string source, VoucherCloneSpec spec)
    {
        try
        {
            // Hỏi database song song với đọc source.
            var menusTask = _wcommand.FindByControllerAsync(spec.SrcSysId);
            var data = await DataAsync(source);
            spec.OtherPrefixes = VoucherCloneService.OtherPrefixes(data.Entities.Vouchers, spec.SrcPrefix);
            var files = await Task.Run(() => VoucherCloneService.SourceFiles(data.Tree, spec.SrcPrefix, spec.OtherPrefixes));

            // Main *.aspx: theo link menu của chứng từ nguồn (wcommand) — file trong source\Main.
            var menus = new List<WCommandItem>();
            try { menus = await menusTask; } catch { /* không đọc được wcommand */ }
            foreach (var link in menus.Select(m => (m.Link ?? "").Split('?')[0].Trim()).Where(l => l.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.Combine("Main", link.Replace('/', '\\'));
                if (File.Exists(Path.Combine(source, rel)) && !files.Contains(rel, StringComparer.OrdinalIgnoreCase)) files.Add(rel);
            }
            // Include riêng của chứng từ nằm ngoài Controllers: tham chiếu sẽ đổi tên nên file cũng phải chép theo.
            await Task.Run(() => data.Tree.Preload(files));   // đọc song song 1 lần, các bước dưới dùng lại
            var specific = await Task.Run(() => VoucherCloneService.References(data.Tree, files, spec.SrcPrefix, spec.SrcCode, spec.OtherPrefixes)
                .Where(r => r.Specific && r.Exists && !r.Rel.StartsWith("..", StringComparison.Ordinal) && !files.Contains(r.Rel, StringComparer.OrdinalIgnoreCase)).Select(r => r.Rel).ToList());
            files.AddRange(specific);
            var copied = files.Where(f => !VoucherCloneService.IsBackupOf(f, spec.SrcSysId)).ToList();
            await Task.Run(() => Prepare(data, spec, copied));

            var registry = await Task.Run(() => VoucherCloneService.RegistryEdits(data.Tree, spec, copied, data.Entities));
            var refs = await Task.Run(() => VoucherCloneService.References(data.Tree, files, spec.SrcPrefix, spec.SrcCode, spec.OtherPrefixes)
                .Where(r => !files.Contains(r.Rel, StringComparer.OrdinalIgnoreCase)).ToList());
            var controllers = await Task.Run(() => VoucherCloneService.ReferencedControllers(data.Tree, files));

            Js($"vc.onPlan({J(new
            {
                files = files.Select(f => new { rel = f, target = VoucherCloneService.TargetPath(f, spec), text = data.Tree.Text(f) != null, backup = VoucherCloneService.IsBackupOf(f, spec.SrcSysId) }),
                kept = spec.KeepEntities.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
                approval = new { src = spec.SrcAction, dst = spec.DstAction, files = VoucherCloneService.ApprovalFiles(data.Tree, spec).Select(a => Path.GetFileName(a.Rel)) },
                registry = registry.Select(r => new { rel = r.Rel, added = r.Added }),
                refs = refs.Select(r => new { rel = r.Rel, exists = r.Exists, specific = r.Specific }),
                controllers,
                menus = menus.Select(m => new { id = m.WMenuId, parent = m.WMenuId0, bar = m.Bar, bar2 = m.Bar2, link = m.Link, menuId = m.MenuId }),
            })})");
        }
        catch (Exception ex) { Js($"vc.onStatus({J("Không lập được danh sách file: " + ex.Message)}, 'err')"); }
    }

    /// <summary>"Xem chứng từ": dựng màn hình Dir của chứng từ mới từ các file đang tick, chưa ghi gì xuống đĩa.</summary>
    private async Task ViewVoucherAsync(JsonElement msg)
    {
        try
        {
            var source = msg.GetProperty("source").GetString() ?? "";
            var spec = ReadSpec(msg.GetProperty("spec"));
            var files = msg.GetProperty("files").EnumerateArray().Select(x => (Rel: x.GetProperty("rel").GetString() ?? "", Target: x.GetProperty("target").GetString() ?? ""))
                .Where(x => x.Rel.Length > 0 && x.Target.Length > 0).ToList();
            var registry = msg.GetProperty("registry").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            var dirs = files.Select(f => f.Target).Where(t => t.Contains(@"\Dir\", StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => Path.GetFileNameWithoutExtension(t).Equals(spec.DstSysId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            if (dirs.Count == 0) { Js($"vc.onStatus({J("Không có file Dir nào đang tick để xem.")}, 'err')"); return; }

            var data = await DataAsync(source);
            await Task.Run(() => Prepare(data, spec, files.Select(f => f.Rel)));
            var overlay = new VoucherOverlay(data.Tree, spec, files, registry);
            Js("vc.onStatus('', '')");
            _preview?.Close();
            _preview = new VoucherPreviewForm(overlay, dirs, $"Xem chứng từ — {spec.DstCode} {spec.DstSysId}");
            _preview.FormClosed += (s, _) => { if (ReferenceEquals(s, _preview)) _preview = null; };
            _preview.Show();
        }
        catch (Exception ex) { Js($"vc.onStatus({J("Không dựng được màn hình: " + ex.Message)}, 'err')"); }
    }

    private async Task PreviewAsync(string source, string rel, string kind, VoucherCloneSpec spec)
    {
        try
        {
            var data = await DataAsync(source);
            // Chạy ngoài luồng giao diện: file chưa đọc lần nào thì phải qua mạng.
            var result = await Task.Run(() =>
            {
                if (data.Tree.Text(rel) is not { } text) return ("(file nhị phân — chép nguyên)", new List<int>());
                Prepare(data, spec);
                if (kind == "registry")
                {
                    VoucherCloneService.AddRegistryLines(source, rel, text, spec, out var added);
                    return (added, VoucherCloneService.ChangedLines(text, added));
                }
                if (kind != "asis") text = VoucherCloneService.Transform(text, spec);
                return (text, new List<int>());
            });
            Js($"vc.onPreview({J(rel)}, {J(result.Item1)}, {J(result.Item2)})");
        }
        catch (Exception ex) { Js($"vc.onPreview({J(rel)}, {J("Không đọc được: " + ex.Message)})"); }
    }

    // ---- Bước 4: SQL ----------------------------------------------------------------------------------

    private async Task SqlAsync(JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
        var spec = ReadSpec(msg.GetProperty("spec"));
        try
        {
            var sourceRoot = S("source");
            var data = await DataAsync(sourceRoot);
            await Task.Run(() => Prepare(data, spec));
            // freecolumns khai theo controller của chứng từ: {SysID}, {tiền tố}Detail / Tax / Charge / Deductible (như gói FCode).
            var controllers = new[] { spec.SrcPrefix + "Detail", spec.SrcSysId, spec.SrcPrefix + "Tax", spec.SrcPrefix + "Charge", spec.SrcPrefix + "Deductible" };
            var blocks = await _sql.ConfigBlocksAsync(spec, controllers, S("srcApprove"), S("dstApprove"));
            var tables = spec.SrcTableNo.Length > 0 ? await _sql.SourceTablesAsync(spec.SrcTableNo, spec.SrcNoPartition) : new List<string>();
            var tableScript = spec.NewTables && spec.DstTableNo.Length > 0 && tables.Count > 0 ? await _sql.NewTablesScriptAsync(tables, spec) : "";
            var taken = spec.NewTables && spec.DstTableNo.Length > 0 ? await _sql.SourceTablesAsync(spec.DstTableNo, spec.SrcNoPartition) : new List<string>();

            // App: tạo bảng → cấu hình → sysgendata + tạo các kỳ (như thứ tự gói FCode). Sys: cấu hình.
            var split = tableScript.IndexOf("DELETE sysgendata", StringComparison.Ordinal);
            var ddl = split >= 0 ? tableScript[..split] : tableScript;
            var gen = split >= 0 ? tableScript[split..] : "";
            var app = new StringBuilder(ddl);
            foreach (var b in blocks.Where(b => !b.Sys)) app.Append(b.Script).Append("\r\n");
            app.Append(gen);
            var sys = new StringBuilder();
            foreach (var b in blocks.Where(b => b.Sys)) sys.Append(b.Script).Append("\r\n");

            Js($"vc.onSql({J(new
            {
                tables,
                newTables = tables.Select(t => VoucherSqlScriptService.NewName(t, spec.SrcTableNo, spec.DstTableNo)),
                taken,
                blocks = blocks.Select(b => new { sys = b.Sys, table = b.Table, where = b.Where, rows = b.Rows }),
                app = app.ToString(), sysScript = sys.ToString(),
            })})");
        }
        catch (Exception ex) { Js($"vc.onSql({J(new { error = ex.Message })})"); }
    }

    // ---- Bước 5: menu -----------------------------------------------------------------------------------

    /// <summary>Mở form WCOMMAND (New) chép từ menu của chứng từ nguồn, đổi tên / link / sysid / syscode sang chứng từ mới. Chỉ ghi DB khi bấm Save.</summary>
    /// <summary>Cây menu web (wcommand) của bước 5 để chọn vị trí menu mới.</summary>
    private async Task LoadMenusAsync()
    {
        try
        {
            var flat = new List<WCommandItem>();
            void Walk(IEnumerable<WCommandItem> items) { foreach (var x in items) { flat.Add(x); Walk(x.Children); } }
            Walk(await _wcommand.LoadTreeAsync());
            Js($"vc.onMenuTree({J(flat.Where(x => !x.IsAppCommand).OrderBy(m => m.WMenuId, StringComparer.Ordinal).Select(m => new
            {
                id = m.WMenuId, parent = m.WMenuId0, bar = m.Bar, bar2 = m.Bar2, link = m.Link, menuId = m.MenuId,
            }))})");
        }
        catch (Exception ex) { Js($"vc.onMenuTree([], {J("Không đọc được wcommand: " + ex.Message)})"); }
    }

    /// <summary>
    /// Mở form WCOMMAND điền sẵn từ menu chứng từ nguồn. Vị trí do trang tính từ cây: parentId = menu cha; seedId / seedMenuId = menu cùng nhánh
    /// để form gợi ý id đúng nhánh mới (không gửi = giữ vị trí menu nguồn). Đọc <paramref name="msg"/> trước await: JsonElement không sống qua lượt xử lý.
    /// </summary>
    private async Task MenuAsync(VoucherCloneSpec spec, string mainTarget, JsonElement msg)
    {
        string? Opt(string n) => msg.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var parentId = Opt("parentId");
        var seedId = Opt("seedId");
        var seedMenuId = Opt("seedMenuId");
        try
        {
            var existing = await _wcommand.FindByControllerAsync(spec.DstSysId);
            var source = (await _wcommand.FindByControllerAsync(spec.SrcSysId)).FirstOrDefault();
            BeginInvoke(new Action(() =>
            {
                WCommandItem? edit = null;
                if (existing.Count > 0)
                {
                    var answer = MessageBox.Show(this, $"Đã có menu cho {spec.DstSysId}:\n\n{string.Join("\n", existing.Select(m => $"{m.WMenuId}  {m.Bar}  ({m.Link})"))}\n\nYes = mở menu đó để sửa\nNo = vẫn tạo menu mới",
                        Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) return;
                    if (answer == DialogResult.Yes) edit = existing[0];
                }
                WCommandItem? template = null;
                if (edit == null)
                {
                    template = source == null ? new WCommandItem { Status = "1" } : WCommandService.CloneItem(source);
                    template.Bar = spec.DstTitleV.Length > 0 ? spec.DstTitleV : template.Bar;
                    template.Bar2 = spec.DstTitleE.Length > 0 ? spec.DstTitleE : template.Bar2;
                    template.SysId = spec.DstSysId;
                    if (template.SysCode.Trim().Equals(spec.SrcCode, StringComparison.OrdinalIgnoreCase) || source == null) template.SysCode = spec.DstCode;
                    // Link: file Main mới (giữ phần ?tham số của menu nguồn, đã đổi mã).
                    var link = source?.Link ?? "";
                    var query = link.Contains('?') ? VoucherCloneService.TransformMenu(link[link.IndexOf('?')..], spec) : "";
                    var page = link.Split('?')[0].Trim();   // không có file Main mới (bỏ tick / Main dùng chung): giữ trang của menu nguồn
                    template.Link = (mainTarget.Length > 0 ? Path.GetFileName(mainTarget) : page.Length > 0 ? page : spec.DstSysId + ".aspx") + query;
                    template.Parameter = VoucherCloneService.TransformMenu(template.Parameter ?? "", spec);
                    if (parentId != null) template.WMenuId0 = parentId;
                    if (seedId != null) template.WMenuId = seedId;
                    if (seedMenuId != null) template.MenuId = seedMenuId;
                }
                using var form = new WCommandEditForm(_wcommand, edit, template, _connections.Current?.SourcePath);
                form.ShowDialog(this);
                Js("vc.onMenuClosed()");
            }));
        }
        catch (Exception ex) { Js($"vc.onStatus({J("Không mở được menu: " + ex.Message)}, 'err')"); }
    }

    // ---- Bước 6: ghi gói ---------------------------------------------------------------------------------

    private void Generate(JsonElement msg, SourceData data)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : "";
        var source = S("source").Trim();
        var output = S("output").Trim();
        var direct = msg.TryGetProperty("direct", out var d) && d.ValueKind == JsonValueKind.True;
        var spec = ReadSpec(msg.GetProperty("spec"));
        if (output.Length == 0) { MessageBox.Show(this, "Chưa chọn thư mục lưu (Save As).", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        List<(string Rel, string Target)> Pairs(string n) => msg.TryGetProperty(n, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => (x.GetProperty("rel").GetString() ?? "", x.GetProperty("target").GetString() ?? ""))
                .Where(x => x.Item1.Length > 0 && x.Item2.Length > 0).ToList() : new();
        List<string> Rels(string n) => msg.TryGetProperty(n, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new();
        var files = Pairs("files");
        var registry = Rels("registry");
        var copies = Rels("copies");
        if (files.Count == 0) { MessageBox.Show(this, "Chưa chọn file nào để tạo.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        // Đích trùng nguồn là ghi đè file của chứng từ nguồn: chặn hẳn, không để hộp thoại ghi đè quyết định.
        var sameName = files.Where(f => f.Rel.Equals(f.Target, StringComparison.OrdinalIgnoreCase)).Select(f => f.Rel).ToList();
        if (sameName.Count > 0)
        {
            MessageBox.Show(this, "File đích trùng file nguồn — đổi tên đích ở bước 2 hoặc bỏ tick:\n\n" + string.Join("\n", sameName.Take(20)), Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Ghi thẳng chỉ dùng cho source của workspace: file đăng ký được đọc từ source nguồn, ghi sang dự án khác sẽ đè mất khai báo của dự án đó.
        if (direct && !source.Equals(_connections.Current?.SourcePath ?? "", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Lấy chứng từ từ dự án khác thì chỉ xuất gói được, không ghi thẳng vào source.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Gói: output\Web\... + output\Script\App|Sys. Ghi thẳng: file vào thư mục source, script vẫn ở output\Script.
        var webRoot = direct ? source : Path.Combine(output, "Web");
        try
        {
            Prepare(data, spec, files.Select(f => f.Rel));
            var targets = files.Select(f => Path.Combine(webRoot, f.Target)).Concat(registry.Select(r => Path.Combine(webRoot, r)))
                .Concat(VoucherCloneService.ApprovalFiles(data.Tree, spec).Select(a => Path.Combine(webRoot, a.Rel)))
                .Concat(direct ? Enumerable.Empty<string>() : copies.Select(r => Path.Combine(webRoot, r))).ToList();
            var existing = targets.Where(File.Exists).ToList();
            if (existing.Count > 0)
            {
                var note = direct && registry.Count > 0 ? "\n\n(File đăng ký dùng chung sẽ được GHI ĐÈ bằng bản đã thêm dòng chứng từ mới.)" : "";
                if (MessageBox.Show(this, "Các file sau đã tồn tại, ghi đè?\n\n" + string.Join("\n", existing.Take(30)) + (existing.Count > 30 ? "\n..." : "") + note,
                        "Xác nhận ghi đè", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            var written = VoucherCloneService.WriteFiles(source, webRoot, files, spec);
            written.AddRange(VoucherCloneService.WriteRegistry(source, webRoot, registry, spec));
            written.AddRange(VoucherCloneService.WriteApprovalFiles(data.Tree, webRoot, spec));
            if (!direct) written.AddRange(VoucherCloneService.CopyAsIs(source, webRoot, copies));

            void Script(string sub, string name, string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                var p = Path.Combine(output, "Script", sub, name);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, text, new UTF8Encoding(true));
                written.Add(p);
            }
            Script("App", "AppScript.sql", S("appScript"));
            Script("Sys", "SysScript.sql", S("sysScript"));
            Js($"vc.onGenerated({J(new { folder = output, files = written.Select(p => new { path = p, name = p.StartsWith(output, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(output, p) : p }) })})");
        }
        catch (Exception ex) { MessageBox.Show(this, "Không tạo được:\n" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Js($"vc.onStatus({J("Không mở được thư mục: " + ex.Message)}, 'err')"); }
    }

    private void OpenInViewer(string path)
    {
        if (!File.Exists(path)) return;
        if (string.IsNullOrWhiteSpace(_settings.ViewerExePath) || !File.Exists(_settings.ViewerExePath))
        {
            using var dialog = new OpenFileDialog { Title = "Chọn BcodeViewer.exe", Filter = "BcodeViewer (BcodeViewer.exe)|BcodeViewer.exe|Tất cả file (*.exe)|*.exe" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _settings.ViewerExePath = dialog.FileName;
            _settings.Save();
        }
        try
        {
            var project = _connections.Current?.Name ?? "";
            Process.Start(new ProcessStartInfo(_settings.ViewerExePath, project.Length == 0 ? $"\"{path}\"" : $"\"{path}\" \"{project}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được BcodeViewer:\n" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
