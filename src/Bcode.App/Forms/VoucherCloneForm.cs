using System.Diagnostics;
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
        MinimizeBox = true;
        ShowInTaskbar = true;
        if (Bcode.App.UI.AppIcons.AppIcon is { } icon) { Icon = icon; ShowIcon = true; }
    }

    private static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    protected override void OnReady()
    {
        var src = _connections.Current?.SourcePath ?? "";
        Js($"vc.init({J(new { source = src, workspaceSource = src, desktop = Path.Combine(Desktop, "VoucherClone") })})");
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
            case "plan": await PlanAsync(S("source"), ReadSpec(msg.GetProperty("spec"))); break;
            case "sql": await SqlAsync(msg); break;
            case "previewFile": Preview(S("source"), S("rel"), S("kind"), ReadSpec(msg.GetProperty("spec"))); break;
            case "menu": await MenuAsync(ReadSpec(msg.GetProperty("spec")), S("mainTarget")); break;
            case "generate": { var copy = msg.Clone(); BeginInvoke(new Action(() => Generate(copy))); break; }
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
            case "openViewer": { var p = S("path"); BeginInvoke(new Action(() => OpenInViewer(p))); break; }
        }
    }

    private static VoucherCloneSpec ReadSpec(JsonElement s)
    {
        string S(string n) => s.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
        var spec = new VoucherCloneSpec
        {
            SrcCode = S("srcCode"), SrcSysId = S("srcSysId"), SrcTitleV = S("srcTitleV"), SrcTitleE = S("srcTitleE"), SrcTableNo = S("srcTableNo"),
            DstCode = S("dstCode"), DstSysId = S("dstSysId"), DstPrefix = S("dstPrefix"), DstTitleV = S("dstTitleV"), DstTitleE = S("dstTitleE"),
            NewTables = s.TryGetProperty("newTables", out var n) && n.ValueKind == JsonValueKind.True, DstTableNo = S("dstTableNo"),
        };
        if (s.TryGetProperty("fileRenames", out var fr) && fr.ValueKind == JsonValueKind.Array)
            spec.FileRenames = fr.EnumerateArray().Select(x => (x.GetProperty("from").GetString() ?? "", x.GetProperty("to").GetString() ?? "")).ToList();
        return spec;
    }

    /// <summary>Danh sách tên đã chép (bảo vệ tên controller dùng chung cùng tiền tố khỏi bị đổi) — tính lại từ source mỗi lần.</summary>
    private static void FillShared(string source, VoucherCloneSpec spec, IEnumerable<string>? copied = null) =>
        spec.SharedNames = VoucherCloneService.SharedNames(source, spec.SrcPrefix, copied ?? VoucherCloneService.SourceFiles(source, spec.SrcPrefix));

    // ---- Bước 1 ---------------------------------------------------------------------------------------

    private async Task ScanAsync(string source)
    {
        if (source.Length == 0) { Js($"vc.onStatus({J("Chưa có thư mục source (workspace chưa khai Source Path) — chọn thư mục source.")}, 'err')"); return; }
        Js($"vc.onStatus({J("Đang quét chứng từ trong source...")}, '')");
        try
        {
            var list = await Task.Run(() => VoucherCloneService.ScanVouchers(source));
            Js($"vc.onVouchers({J(list.Select(v => new { code = v.Code, sysId = v.SysId, table = v.Table, titleV = v.TitleV, titleE = v.TitleE, prefix = v.Prefix, tableNo = v.TableNo }))})");
            Js($"vc.onStatus({J(VoucherCloneService.ControllersDir(source) == null ? "Không thấy App_Data\\Controllers trong thư mục source." : $"{list.Count} chứng từ trong source.")}, {J(list.Count == 0 ? "err" : "")})");
        }
        catch (Exception ex) { Js($"vc.onStatus({J("Không quét được source: " + ex.Message)}, 'err')"); }
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
            var files = await Task.Run(() => VoucherCloneService.SourceFiles(source, spec.SrcPrefix));

            // Main *.aspx: theo link menu của chứng từ nguồn (wcommand) — file trong source\Main.
            var menus = new List<WCommandItem>();
            try { menus = await _wcommand.FindByControllerAsync(spec.SrcSysId); } catch { /* không đọc được wcommand */ }
            foreach (var link in menus.Select(m => (m.Link ?? "").Split('?')[0].Trim()).Where(l => l.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.Combine("Main", link.Replace('/', '\\'));
                if (File.Exists(Path.Combine(source, rel)) && !files.Contains(rel, StringComparer.OrdinalIgnoreCase)) files.Add(rel);
            }
            FillShared(source, spec, files);

            var registry = await Task.Run(() => VoucherCloneService.RegistryEdits(source, spec, files));
            var refs = await Task.Run(() => VoucherCloneService.References(source, files, spec.SrcPrefix, spec.SrcCode)
                .Where(r => !files.Contains(r.Rel, StringComparer.OrdinalIgnoreCase)).ToList());
            var controllers = await Task.Run(() => VoucherCloneService.ReferencedControllers(source, files));
            string? approve = null;
            try { approve = await _sql.SourceApproveTypeAsync(spec.SrcCode); } catch { /* không có DB */ }

            Js($"vc.onPlan({J(new
            {
                files = files.Select(f => new { rel = f, target = VoucherCloneService.TargetPath(f, spec), text = VoucherCloneService.IsText(Path.Combine(source, f)) }),
                registry = registry.Select(r => new { rel = r.Rel, added = r.Added }),
                refs = refs.Select(r => new { rel = r.Rel, exists = r.Exists, specific = r.Specific }),
                controllers,
                menus = menus.Select(m => new { id = m.WMenuId, parent = m.WMenuId0, bar = m.Bar, bar2 = m.Bar2, link = m.Link, menuId = m.MenuId }),
                approve,
            })})");
        }
        catch (Exception ex) { Js($"vc.onStatus({J("Không lập được danh sách file: " + ex.Message)}, 'err')"); }
    }

    private void Preview(string source, string rel, string kind, VoucherCloneSpec spec)
    {
        try
        {
            var full = Path.Combine(source, rel);
            if (!VoucherCloneService.IsText(full)) { Js($"vc.onPreview({J(rel)}, {J("(file nhị phân — chép nguyên)")})"); return; }
            FillShared(source, spec);
            var text = VoucherCloneService.ReadText(full, out _);
            if (kind == "registry") VoucherCloneService.AddRegistryLines(text, spec, out text);
            else if (kind != "asis") text = VoucherCloneService.Transform(text, spec);
            Js($"vc.onPreview({J(rel)}, {J(text)})");
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
            FillShared(S("source"), spec);
            // freecolumns khai theo controller của chứng từ: {SysID}, {tiền tố}Detail / Tax / Charge / Deductible (như gói FCode).
            var controllers = new[] { spec.SrcPrefix + "Detail", spec.SrcSysId, spec.SrcPrefix + "Tax", spec.SrcPrefix + "Charge", spec.SrcPrefix + "Deductible" };
            var blocks = await _sql.ConfigBlocksAsync(spec, controllers, S("srcApprove"), S("dstApprove"));
            var tables = spec.SrcTableNo.Length > 0 ? await _sql.SourceTablesAsync(spec.SrcTableNo) : new List<string>();
            var tableScript = spec.NewTables && spec.DstTableNo.Length > 0 && tables.Count > 0 ? await _sql.NewTablesScriptAsync(tables, spec) : "";

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
                blocks = blocks.Select(b => new { sys = b.Sys, table = b.Table, where = b.Where, rows = b.Rows }),
                app = app.ToString(), sysScript = sys.ToString(),
            })})");
        }
        catch (Exception ex) { Js($"vc.onSql({J(new { error = ex.Message })})"); }
    }

    // ---- Bước 5: menu -----------------------------------------------------------------------------------

    /// <summary>Mở form WCOMMAND (New) chép từ menu của chứng từ nguồn, đổi tên / link / sysid / syscode sang chứng từ mới. Chỉ ghi DB khi bấm Save.</summary>
    private async Task MenuAsync(VoucherCloneSpec spec, string mainTarget)
    {
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
                    var query = link.Contains('?') ? VoucherCloneService.Transform(link[link.IndexOf('?')..], spec) : "";
                    template.Link = (mainTarget.Length > 0 ? Path.GetFileName(mainTarget) : spec.DstSysId + ".aspx") + query;
                    template.Parameter = VoucherCloneService.Transform(template.Parameter ?? "", spec);
                }
                using var form = new WCommandEditForm(_wcommand, edit, template, _connections.Current?.SourcePath);
                form.ShowDialog(this);
                Js("vc.onMenuClosed()");
            }));
        }
        catch (Exception ex) { Js($"vc.onStatus({J("Không mở được menu: " + ex.Message)}, 'err')"); }
    }

    // ---- Bước 6: ghi gói ---------------------------------------------------------------------------------

    private void Generate(JsonElement msg)
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

        // Gói: output\Web\... + output\Script\App|Sys. Ghi thẳng: file vào thư mục source, script vẫn ở output\Script.
        var webRoot = direct ? source : Path.Combine(output, "Web");
        try
        {
            FillShared(source, spec, files.Select(f => f.Rel));
            var targets = files.Select(f => Path.Combine(webRoot, f.Target)).Concat(registry.Select(r => Path.Combine(webRoot, r)))
                .Concat(copies.Select(r => Path.Combine(webRoot, r))).ToList();
            var existing = targets.Where(File.Exists).ToList();
            if (existing.Count > 0)
            {
                var note = direct && registry.Count > 0 ? "\n\n(File đăng ký dùng chung sẽ được GHI ĐÈ bằng bản đã thêm dòng chứng từ mới.)" : "";
                if (MessageBox.Show(this, "Các file sau đã tồn tại, ghi đè?\n\n" + string.Join("\n", existing.Take(30)) + (existing.Count > 30 ? "\n..." : "") + note,
                        "Xác nhận ghi đè", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            var written = VoucherCloneService.WriteFiles(source, webRoot, files, spec);
            written.AddRange(VoucherCloneService.WriteRegistry(source, webRoot, registry, spec));
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
