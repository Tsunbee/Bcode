using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Tạo nhanh danh mục" — chọn 1 bảng có sẵn trong App Data, khai báo khoá / tiêu đề / cột nào hiện ở Grid, Dir, Lookup,
/// Filter, Import (và tuỳ chọn 1 bảng chi tiết nhúng dạng lưới trong Dir) rồi sinh bộ file danh mục từ source mẫu FCode
/// <c>Templates\fileSource\CreateList</c> (Dir, Grid, Grid chi tiết, Lookup, Filter, Import + Upload, Main.aspx). Phần sinh file nằm trong <see cref="QuickListService"/>;
/// giao diện là trang WebView2 Web/Shell/quicklist.html.
/// </summary>
public class QuickListForm : WebDialogForm
{
    private const string Title = "Tạo nhanh danh mục";

    private readonly SqlObjectBrowserService _sqlObjects;
    private readonly TableDataService _tableData;
    private readonly DbConnectionService _connections;
    private readonly QuickListService _service = new();
    private readonly ColumnHeaderGuesser _headers = new();
    private readonly WCommandService _wcommand;
    private readonly AppSettings _settings;
    private List<WCommandItem>? _menus;

    public QuickListForm(SqlObjectBrowserService sqlObjects, TableDataService tableData, DbConnectionService connections,
        WCommandService wcommand, AppSettings settings)
        : base(Title, "quicklist.html", 1400, 880, 900, 600)
    {
        _sqlObjects = sqlObjects;
        _tableData = tableData;
        _connections = connections;
        _wcommand = wcommand;
        _settings = settings;

        // Mở song song với Bcode (MainForm.OpenQuickList dùng Show, không ShowDialog): là 1 cửa sổ riêng trên taskbar,
        // thu nhỏ được, để vừa làm danh mục vừa quay lại Bcode tra bảng / xem source.
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = true;
        ShowInTaskbar = true;
        if (Bcode.App.UI.AppIcons.AppIcon is { } icon) { Icon = icon; ShowIcon = true; }
    }

    /// <summary>Khoá workspace để tách cấu hình đã lưu (cùng tên bảng ở dự án khác có thể khác hẳn).</summary>
    private string WorkspaceKey => _connections.Current is { } w ? (w.Name.Length > 0 ? w.Name : w.AppDatabase) : "";

    protected override void OnReady()
    {
        var src = _connections.Current?.SourcePath ?? "";
        Js($"quick.init({J(new { template = QuickListService.DefaultTemplateDir, output = src, oldKeyFormat = QuickListService.DefaultOldKeyFormat })})");
        _ = LoadTablesAsync();
        // Từ điển field FCode (header.xml ~2MB) nạp sẵn ở nền để lúc "Nạp cột" không phải chờ.
        _ = Task.Run(() =>
        {
            if (FieldDictionaryService.Instance.LoadError is { } err)
                Js($"quick.onStatus({J("Không đọc được từ điển field (header.xml / parameters.xml): " + err)}, 'err')");
        });
    }

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        switch (action)
        {
            case "loadColumns":
                await LoadColumnsAsync((msg.GetProperty("table").GetString() ?? "").Trim(),
                    msg.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.True);
                break;
            case "pickFolder":
            {
                var target = msg.GetProperty("target").GetString() ?? "";
                var current = msg.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                BeginInvoke(new Action(() =>
                {
                    using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(current) ? current : "" };
                    if (dlg.ShowDialog(this) == DialogResult.OK) Js($"quick.onFolder({J(target)}, {J(dlg.SelectedPath)})");
                }));
                break;
            }
            case "preview": Preview(msg); break;
            case "guessHeaders": GuessHeaders(msg); break;
            case "generate": BeginInvoke(new Action(() => Generate(msg))); break;
            case "openFolder": OpenFolder(msg.GetProperty("path").GetString() ?? ""); break;
            case "openViewer":
            {
                var path = msg.GetProperty("path").GetString() ?? "";
                BeginInvoke(new Action(() => OpenInViewer(path)));
                break;
            }
            case "loadMenus": await LoadMenusAsync(msg.TryGetProperty("reload", out var rl) && rl.ValueKind == JsonValueKind.True); break;
            case "findMenus": await FindMenusAsync(msg); break;
            case "editMenu": await EditMenuAsync((msg.GetProperty("id").GetString() ?? "").Trim()); break;
            case "createMenu": await CreateMenuAsync(msg); break;
        }
    }

    private async Task LoadTablesAsync()
    {
        if (_connections.Current is null) { Js($"quick.onStatus({J("Chưa chọn workspace.")}, 'err')"); return; }
        try
        {
            var objs = await _sqlObjects.ListObjectsAsync(useSysDatabase: false);
            var names = objs.Where(o => o.Kind == SqlObjectKind.Table).Select(o => o.Name).OrderBy(n => n).ToArray();
            Js($"quick.onTables({J(names)})");
            Js($"quick.onStatus({J($"{names.Length} bảng trong App Data.")}, '')");
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không nạp được danh sách bảng: " + ex.Message)}, 'err')"); }
    }

    private async Task LoadColumnsAsync(string table, bool detail)
    {
        if (table.Length == 0) { Js($"quick.onStatus({J("Chọn bảng trước.")}, 'err')"); return; }
        try
        {
            var cols = await _sqlObjects.GetColumnsAsync(false, "dbo", table);
            if (cols.Count == 0) { Js($"quick.onStatus({J($"Không thấy cột nào của bảng '{table}'.")}, 'err')"); return; }
            var types = await _tableData.GetColumnTypesAsync(false, "dbo", table);
            var keys = cols.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            if (keys.Count == 0) keys.Add(cols[0].Name);
            var list = cols.Select(c =>
            {
                types.TryGetValue(c.Name, out var sqlType);
                return new
                {
                    name = c.Name, sqlType = sqlType ?? "", type = CatalogCloneService.GuessFieldType(sqlType ?? ""),
                    // Các biến thể tra cứu chuẩn của FCode cho cột này (header.xml) — trang cho chọn, mặc định biến thể đầu.
                    lookups = FieldDictionaryService.Instance.Lookups(c.Name)
                        .Select(l => new { l.Controller, l.Reference, l.Key, l.Check, l.Information }),
                };
            });
            // Bảng danh mục: kèm cấu hình đã lưu lần trước (nếu có) để trang hỏi khôi phục.
            var saved = detail ? null : QuickListConfigStore.Load(WorkspaceKey, table);
            Js($"quick.{(detail ? "onDetailColumns" : "onColumns")}({J(new { table, keys, columns = list, saved })})");
            Js($"quick.onStatus({J($"Đã nạp {cols.Count} cột của '{table}'.")}, 'ok')");
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không nạp được cột: " + ex.Message)}, 'err')"); }
    }

    private static QuickListSpec ReadSpec(JsonElement s)
    {
        string S(string n) => s.TryGetProperty(n, out var p) ? (p.GetString() ?? "").Trim() : "";
        bool B(string n) => s.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
        bool CB(JsonElement c, string n) => c.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
        string CS(JsonElement c, string n) => c.TryGetProperty(n, out var p) ? p.GetString() ?? "" : "";
        List<string> L(string n) => S(n).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        List<QuickListColumn> Cols(string n) => !s.TryGetProperty(n, out var arr) || arr.ValueKind != JsonValueKind.Array ? new() :
            arr.EnumerateArray().Select(c => new QuickListColumn
            {
                Name = CS(c, "name"), SqlType = CS(c, "sqlType"), HeaderV = CS(c, "headerV"), HeaderE = CS(c, "headerE"), Type = CS(c, "type"),
                Width = int.TryParse(CS(c, "width"), out var w) ? w : 150,
                InGrid = CB(c, "inGrid"), InForm = CB(c, "inForm"), InLookup = CB(c, "inLookup"), InFilter = CB(c, "inFilter"), InImport = CB(c, "inImport"),
                AllowNulls = CB(c, "allowNulls"), ReadOnly = CB(c, "readOnly"),
                Hidden = CB(c, "hidden"), HideName = CB(c, "hideName"),
                Lookup = c.TryGetProperty("lookup", out var l) && l.ValueKind == JsonValueKind.Object && CS(l, "controller") != ""
                    ? new FieldLookup(CS(l, "controller"), CS(l, "reference"), CS(l, "key"), CS(l, "check"), CS(l, "information"))
                    : null,
            }).ToList();
        var hasDetail = B("detail");

        return new QuickListSpec
        {
            Controller = S("controller"), MainName = S("main"), Table = S("table"), Order = S("order"), NameField = S("nameField"),
            Keys = L("keys"),
            TitleV = S("titleV"), TitleE = S("titleE"), SubTitleV = S("subV"), SubTitleE = S("subE"),
            FuncNew = B("funcNew"), FuncEdit = B("funcEdit"), FuncDelete = B("funcDelete"), FuncFilter = B("funcFilter"),
            CreateLookup = B("lookup"), CreateFilter = B("filter"), ImportData = B("import"), Import2262 = B("import2262"),
            Comment = B("comment"), Irregular = B("irregular"),
            OldKeyFormat = S("oldKeyFormat") is { Length: > 0 } f ? f : QuickListService.DefaultOldKeyFormat,
            Columns = Cols("columns"),
            DetailTable = hasDetail ? S("detailTable") : "",
            DetailId = S("detailId"), DetailTitleV = S("detailTitleV"), DetailTitleE = S("detailTitleE"), DetailOrder = S("detailOrder"),
            DetailKeys = L("detailKeys"), LinkKeys = L("linkKeys"), DetailImport = B("detailImport"),
            DetailColumns = hasDetail ? Cols("detailColumns") : new(),
        };
    }

    private List<(string Target, string Content)>? Render(QuickListSpec spec, string template, bool showErrors)
    {
        var error = QuickListService.Validate(spec);
        if (error != null)
        {
            if (showErrors) MessageBox.Show(this, error, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else Js($"quick.onStatus({J(error)}, 'err')");
            return null;
        }
        var files = _service.Render(spec, template, out var leftovers);
        Js(leftovers.Count == 0
            ? $"quick.onStatus({J($"{files.Count} file.")}, 'ok')"
            : $"quick.onStatus({J("Mẫu có placeholder chưa biết (đã để trống): " + string.Join(", ", leftovers))}, 'err')");
        return files;
    }

    private void Preview(JsonElement msg)
    {
        var template = (msg.GetProperty("template").GetString() ?? "").Trim();
        try
        {
            var files = Render(ReadSpec(msg.GetProperty("spec")), template, showErrors: false);
            if (files != null) Js($"quick.onPreview({J(files.Select(f => new { target = f.Target, content = f.Content }))})");
        }
        catch (Exception ex) { Js($"quick.onStatus({J(ex.Message)}, 'err')"); }
    }

    /// <summary>Gợi ý header cho cột danh mục + chi tiết (xem <see cref="ColumnHeaderGuesser"/>); trang chỉ áp vào ô người dùng chưa sửa.</summary>
    private void GuessHeaders(JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) ? (p.GetString() ?? "").Trim() : "";
        string[] Names(string n) => msg.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();

        // Chi tiết dùng chung ngữ cảnh danh mục: cột liên kết (vd ma_quay) trong bảng chi tiết vẫn là "Mã quầy".
        var ctx = new ColumnHeaderGuesser.Context(S("table"),
            S("keys").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), S("titleV"), S("titleE"));
        object Map(string[] names) => names
            .Select(n => (n, g: _headers.Guess(n, ctx)))
            .Where(x => x.g != null)
            .Select(x => new { name = x.n, v = x.g!.Value.V, e = x.g!.Value.E });
        Js($"quick.onHeaders({J(new { master = Map(Names("master")), detail = Map(Names("detail")) })})");
    }

    // ---- Sau khi tạo file -----------------------------------------------------------------------

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không mở được thư mục: " + ex.Message)}, 'err')"); }
    }

    /// <summary>Mở 1 file trong BcodeViewer — cùng cách File Lookup "Edit" (hỏi đường dẫn BcodeViewer.exe 1 lần, nhớ vào settings).</summary>
    private void OpenInViewer(string path)
    {
        if (!File.Exists(path)) { Js($"quick.onStatus({J("Không thấy file: " + path)}, 'err')"); return; }
        if (string.IsNullOrWhiteSpace(_settings.ViewerExePath) || !File.Exists(_settings.ViewerExePath))
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Chọn BcodeViewer.exe",
                Filter = "BcodeViewer (BcodeViewer.exe)|BcodeViewer.exe|Tất cả file (*.exe)|*.exe"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _settings.ViewerExePath = dialog.FileName;
            _settings.Save();
        }
        try
        {
            var project = _connections.Current?.Name ?? "";
            var arguments = project.Length == 0 ? $"\"{path}\"" : $"\"{path}\" \"{project}\"";
            Process.Start(new ProcessStartInfo(_settings.ViewerExePath, arguments) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được BcodeViewer:\n" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ---- Bước tạo menu (wcommand) ------------------------------------------------------------

    private async Task<List<WCommandItem>> MenusAsync()
    {
        if (_menus != null) return _menus;
        var flat = new List<WCommandItem>();
        void Walk(IEnumerable<WCommandItem> items) { foreach (var x in items) { flat.Add(x); Walk(x.Children); } }
        Walk(await _wcommand.LoadTreeAsync());
        return _menus = flat.Where(x => !x.IsAppCommand).ToList();
    }

    /// <summary>Danh sách menu web (wcommand) để chọn "menu mẫu": menu mới chép toàn bộ thông tin của nó (menu cha, type, icon...).</summary>
    private async Task LoadMenusAsync(bool reload = false)
    {
        try
        {
            if (reload) _menus = null;
            var menus = await MenusAsync();
            Js($"quick.onMenus({J(menus.OrderBy(m => m.WMenuId, StringComparer.Ordinal).Select(m => new
            {
                id = m.WMenuId, parent = m.WMenuId0, bar = m.Bar, bar2 = m.Bar2, link = m.Link, menuId = m.MenuId,
                type = m.Type, icon = m.Icon, sysId = m.SysId,
            }))})");
            if (menus.Count == 0) Js($"quick.onStatus({J("Sản phẩm này không có menu web (wcommand).")}, 'err')");
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không đọc được wcommand: " + ex.Message)}, 'err')"); }
    }

    /// <summary>
    /// Mở form WCOMMAND có sẵn ở chế độ New, điền trước từ menu mẫu (cùng menu cha, type, icon...) + tên/link/sysid của danh mục
    /// vừa khai báo; form tự gợi ý WMenu Id / Menu Id còn trống. Chưa ghi gì vào database — người dùng bấm Save trong form đó mới ghi.
    /// Đã có menu trỏ tới controller / file Main này thì hỏi mở menu đó để sửa thay vì tạo trùng.
    /// </summary>
    private async Task CreateMenuAsync(JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) ? (p.GetString() ?? "").Trim() : "";
        var controller = S("controller");
        var main = S("main");
        if (controller.Length == 0 || main.Length == 0) { Js($"quick.onStatus({J("Nhập Controller và File Main trước khi tạo menu.")}, 'err')"); return; }

        try
        {
            var found = await ExistingMenusAsync(controller, main);
            var menus = await MenusAsync();
            var templateId = S("templateId");
            var source = menus.FirstOrDefault(m => m.WMenuId.Equals(templateId, StringComparison.OrdinalIgnoreCase));
            if (templateId.Length > 0 && source == null) { Js($"quick.onStatus({J($"Không thấy menu mẫu '{templateId}'.")}, 'err')"); return; }

            BeginInvoke(new Action(() =>
            {
                WCommandItem? existing = null;
                if (found.Count > 0)
                {
                    var list = string.Join("\n", found.Select(m => $"{m.WMenuId}  {m.Bar}  ({m.Link})"));
                    var answer = MessageBox.Show(this, $"Đã có menu trỏ tới danh mục này:\n\n{list}\n\nYes = mở menu đó để sửa\nNo = vẫn tạo menu mới",
                        Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) return;
                    if (answer == DialogResult.Yes) existing = found[0];
                }

                WCommandItem? template = null;
                if (existing == null)
                {
                    template = source == null ? new WCommandItem { Status = "1" } : Clone(source);
                    template.Bar = S("titleV");
                    template.Bar2 = S("titleE");
                    template.Link = main + ".aspx";
                    template.SysId = controller;
                    template.Parameter = "";
                    // Trang đã tính menu cha (chọn 1 nhóm = đặt menu mới bên trong nhóm đó; chọn 1 menu = cùng cha với nó).
                    if (msg.TryGetProperty("parentId", out var pid) && pid.ValueKind == JsonValueKind.String)
                        template.WMenuId0 = pid.GetString() ?? "";
                }
                using var form = new WCommandEditForm(_wcommand, existing, template, _connections.Current?.SourcePath);
                form.ShowDialog(this);
                _menus = null; // có thể vừa thêm/sửa menu — lần sau đọc lại
                Js("quick.onMenuFormClosed()");
            }));
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không tạo được menu: " + ex.Message)}, 'err')"); }
    }

    private async Task<List<WCommandItem>> ExistingMenusAsync(string controller, string main)
    {
        var found = new List<WCommandItem>();
        if (controller.Length > 0) found.AddRange(await _wcommand.FindByControllerAsync(controller));
        if (main.Length > 0 && !main.Equals(controller, StringComparison.OrdinalIgnoreCase)) found.AddRange(await _wcommand.FindByControllerAsync(main));
        return found.GroupBy(m => m.WMenuId).Select(g => g.First()).ToList();
    }

    /// <summary>Menu đã trỏ tới danh mục này (sysid = controller hoặc link = &lt;main&gt;.aspx...) — để tab Menu báo trước, tránh tạo trùng.</summary>
    private async Task FindMenusAsync(JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) ? (p.GetString() ?? "").Trim() : "";
        try
        {
            var found = await ExistingMenusAsync(S("controller"), S("main"));
            Js($"quick.onExistingMenus({J(found.Select(m => new { id = m.WMenuId, parent = m.WMenuId0, bar = m.Bar, link = m.Link, menuId = m.MenuId, sysId = m.SysId }))})");
        }
        catch (Exception ex) { Js($"quick.onExistingMenus([], {J("Không tra được wcommand: " + ex.Message)})"); }
    }

    /// <summary>Mở 1 menu có sẵn trong form WCOMMAND ở chế độ Edit.</summary>
    private async Task EditMenuAsync(string id)
    {
        try
        {
            var menu = (await MenusAsync()).FirstOrDefault(m => m.WMenuId.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (menu == null) { Js($"quick.onStatus({J($"Không thấy menu '{id}'.")}, 'err')"); return; }
            BeginInvoke(new Action(() =>
            {
                using var form = new WCommandEditForm(_wcommand, menu, null, _connections.Current?.SourcePath);
                form.ShowDialog(this);
                _menus = null;
                Js("quick.onMenuFormClosed()");
            }));
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không mở được menu: " + ex.Message)}, 'err')"); }
    }

    private static WCommandItem Clone(WCommandItem m) => new()
    {
        WMenuId = m.WMenuId, WMenuId0 = m.WMenuId0, MenuId = m.MenuId, Bar = m.Bar, Bar2 = m.Bar2, Link = m.Link, Parameter = m.Parameter,
        IconUrl = m.IconUrl, Status = m.Status, Icon = m.Icon, SysId = m.SysId, Type = m.Type, SysCode = m.SysCode, Msys = m.Msys,
        Target = m.Target, XType = m.XType, Edition = m.Edition, ExplIcon = m.ExplIcon,
    };

    private void Generate(JsonElement msg)
    {
        var template = (msg.GetProperty("template").GetString() ?? "").Trim();
        var output = (msg.GetProperty("output").GetString() ?? "").Trim();
        if (output.Length == 0) { MessageBox.Show(this, "Chưa chọn thư mục source để lưu.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        try
        {
            var spec = ReadSpec(msg.GetProperty("spec"));
            var files = Render(spec, template, showErrors: true);
            if (files == null) return;

            var existing = files.Select(f => Path.Combine(output, f.Target)).Where(File.Exists).ToList();
            if (existing.Count > 0 &&
                MessageBox.Show(this, "Các file sau đã tồn tại, ghi đè?\n\n" + string.Join("\n", existing), "Xác nhận ghi đè",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            var written = _service.Write(files, output);
            _headers.Remember(spec.Columns.Concat(spec.DetailColumns));
            QuickListConfigStore.Save(WorkspaceKey, spec.Table, msg.GetProperty("spec"));
            Js($"quick.onStatus({J($"Đã tạo {written.Count} file.")}, 'ok')");
            Js($"quick.onGenerated({J(new { folder = output, files = written.Select(p => new { path = p, name = Path.GetRelativePath(output, p) }) })})");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không tạo được file:\n" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
