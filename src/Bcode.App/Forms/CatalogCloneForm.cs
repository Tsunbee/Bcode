using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Clone danh mục" — khai báo 1 danh mục mới (bảng, khoá, tiêu đề, cột nào hiện ở Grid / form)
/// rồi sinh bộ file Dir / Grid / (Lookup) / Main.aspx từ thư mục mẫu. Danh sách cột lấy từ bảng
/// thật của workspace (cùng cách "Tạo cấu trúc API" liệt kê bảng/cột). Phần sinh file nằm hết
/// trong <see cref="CatalogCloneService"/> — form này chỉ thu thập dữ liệu; giao diện là trang WebView2 (Web/Shell/catalogclone.html).
/// </summary>
public class CatalogCloneForm : WebDialogForm
{
    private static readonly string[] ToolbarOptions = { "New", "Edit", "Delete", "Clone", "Search", "View", "Export", "Freeze" };

    private readonly SqlObjectBrowserService _sqlObjects;
    private readonly TableDataService _tableData;
    private readonly DbConnectionService _connections;
    private readonly CatalogCloneService _service = new();

    public CatalogCloneForm(SqlObjectBrowserService sqlObjects, TableDataService tableData, DbConnectionService connections)
        : base("Clone danh mục", "catalogclone.html", 1200, 800, 760, 520)
    {
        _sqlObjects = sqlObjects;
        _tableData = tableData;
        _connections = connections;
    }

    private string DefaultOutputRoot()
    {
        var src = _connections.Current?.SourcePath;
        return string.IsNullOrWhiteSpace(src)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Path.Combine(src, "App_Data", "Controllers");
    }

    protected override void OnReady()
    {
        Js($"catalog.init({J(new { template = CatalogCloneService.DefaultTemplateDir, output = DefaultOutputRoot(), toolbar = ToolbarOptions })})");
        _ = LoadTablesAsync();
    }

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        switch (action)
        {
            case "loadColumns": await LoadColumnsAsync((msg.GetProperty("table").GetString() ?? "").Trim()); break;
            case "pickFolder":
            {
                var target = msg.GetProperty("target").GetString() ?? "";
                var current = msg.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                BeginInvoke(new Action(() =>
                {
                    using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(current) ? current : "" };
                    if (dlg.ShowDialog(this) == DialogResult.OK) Js($"catalog.onFolder({J(target)}, {J(dlg.SelectedPath)})");
                }));
                break;
            }
            case "generate": Generate(msg.GetProperty("spec"), (msg.GetProperty("template").GetString() ?? "").Trim(), (msg.GetProperty("output").GetString() ?? "").Trim()); break;
        }
    }

    private async Task LoadTablesAsync()
    {
        if (_connections.Current is null) { Js($"catalog.onStatus({J("Chưa chọn workspace.")}, 'err')"); return; }
        try
        {
            var objs = await _sqlObjects.ListObjectsAsync(useSysDatabase: false);
            var names = objs.Where(o => o.Kind is SqlObjectKind.Table or SqlObjectKind.View).Select(o => o.Name).OrderBy(n => n).ToArray();
            Js($"catalog.onTables({J(names)})");
            Js($"catalog.onStatus({J($"{names.Length} bảng/view trong App Data.")}, '')");
        }
        catch (Exception ex) { Js($"catalog.onStatus({J("Không nạp được danh sách bảng: " + ex.Message)}, 'err')"); }
    }

    private async Task LoadColumnsAsync(string table)
    {
        if (table.Length == 0) { Js($"catalog.onStatus({J("Chọn bảng trước.")}, 'err')"); return; }
        try
        {
            var cols = await _sqlObjects.GetColumnsAsync(false, "dbo", table);
            if (cols.Count == 0) { Js($"catalog.onStatus({J($"Không thấy cột nào của bảng '{table}'.")}, 'err')"); return; }
            var types = await _tableData.GetColumnTypesAsync(false, "dbo", table);
            var list = cols.Select(c =>
            {
                types.TryGetValue(c.Name, out var sqlType);
                return new { name = c.Name, headerV = c.Name, headerE = c.Name, isKey = c.IsPrimaryKey, type = CatalogCloneService.GuessFieldType(sqlType ?? "") };
            });
            Js($"catalog.onColumns({J(new { table, key = cols.FirstOrDefault(c => c.IsPrimaryKey).Name ?? cols[0].Name, columns = list })})");
            Js($"catalog.onStatus({J($"Đã nạp {cols.Count} cột của '{table}'.")}, 'ok')");
        }
        catch (Exception ex) { Js($"catalog.onStatus({J("Không nạp được cột: " + ex.Message)}, 'err')"); }
    }

    private void Generate(JsonElement s, string template, string output)
    {
        string S(string n) => (s.GetProperty(n).GetString() ?? "").Trim();
        var spec = new CatalogSpec
        {
            Id = S("id"), Table = S("table"), Key = S("key"), Order = S("order"),
            TitleV = S("titleV"), TitleE = S("titleE"), SubTitleV = S("subV"), SubTitleE = S("subE"), MainName = S("main"),
            CreateLookup = s.GetProperty("lookup").GetBoolean(), LookupTable = S("lookupTable"),
            ToolbarCommands = s.GetProperty("toolbar").EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToList(),
            Columns = s.GetProperty("columns").EnumerateArray().Select(c => new CatalogColumn
            {
                InGrid = c.GetProperty("inGrid").GetBoolean(), InForm = c.GetProperty("inForm").GetBoolean(),
                Name = c.GetProperty("name").GetString() ?? "", HeaderV = c.GetProperty("headerV").GetString() ?? "", HeaderE = c.GetProperty("headerE").GetString() ?? "",
                Width = int.TryParse(c.GetProperty("width").GetString(), out var w) ? w : 150, Type = c.GetProperty("type").GetString() ?? "",
                AllowNulls = c.GetProperty("allowNulls").GetBoolean(), ReadOnly = c.GetProperty("readOnly").GetBoolean(),
            }).ToList(),
        };
        foreach (var c in spec.Columns) c.IsKey = c.Name.Equals(spec.Key, StringComparison.OrdinalIgnoreCase);

        var error = CatalogCloneService.Validate(spec);
        if (error != null) { MessageBox.Show(this, error, "Clone danh mục", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (output.Length == 0) { MessageBox.Show(this, "Chưa chọn thư mục lưu.", "Clone danh mục", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        var existing = _service.PlannedFiles(spec, output).Where(f => File.Exists(f.Path)).Select(f => f.Path).ToList();
        if (existing.Count > 0 &&
            MessageBox.Show(this, "Các file sau đã tồn tại, ghi đè?\n\n" + string.Join("\n", existing), "Xác nhận ghi đè", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        try
        {
            var written = _service.Generate(spec, template, output);
            Js($"catalog.onStatus({J($"Đã tạo {written.Count} file.")}, 'ok')");
            MessageBox.Show(this, "Đã tạo:\n\n" + string.Join("\n", written), "Clone danh mục", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không tạo được file:\n" + ex.Message, "Clone danh mục", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
