using System.Text;
using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Check Partitions Struct" (chuột phải ô Table của tab Table khi đang là bảng phân kỳ xxx$yyyymm / xxx$000000): so cấu trúc mọi bảng kỳ cùng
/// tiền tố với bảng mẫu xxx$000000 (<see cref="TableDataService.CheckPartitionStructureAsync"/>) rồi liệt kê Table | Description ("OK" = giống).
/// Giao diện là trang WebView2 Web/Shell/partitioncheck.html (chế độ App: PartitionCheckPage).
/// </summary>
public sealed class PartitionCheckForm : WebDialogForm
{
    private readonly TableDataService _service;
    private readonly bool _useSys;
    private readonly string _schema, _prefix;
    private List<TableDataService.PartitionCheckRow> _rows = new();

    public PartitionCheckForm(TableDataService service, bool useSys, string schema, string prefix)
        : base($"Check Partition Struct — {prefix}$", "partitioncheck.html", 760, 560, 480, 300)
    {
        _service = service;
        _useSys = useSys;
        _schema = schema;
        _prefix = prefix;
    }

    protected override async void OnReady()
    {
        try
        {
            _rows = await _service.CheckPartitionStructureAsync(_useSys, _schema, _prefix);
            var others = _rows.Where(r => !r.IsReference).ToList();
            var diff = others.Count(r => !r.Same);
            var head = _rows.Count == 0 ? $"Không thấy bảng {_prefix}$yyyymm nào trong {(_useSys ? "Sys Data" : "App Data")}."
                : others.Count == 0 ? $"Chỉ có 1 bảng ({_rows[0].Table}) — không có bảng kỳ nào để so."
                : diff == 0 ? "Tables are same structure"
                : $"{diff}/{others.Count} bảng kỳ khác cấu trúc so với {_rows[0].Table}";
            var kind = _rows.Count == 0 ? "err" : diff == 0 ? "ok" : "warn";
            Js($"partCheck.show({J(new { head, kind, rows = _rows, status = _rows.Count > 0 ? $"{_rows.Count} bảng — so với {_rows[0].Table}" : "" })})");
        }
        catch (Exception ex)
        {
            Js($"partCheck.show({J(new { head = "Lỗi: " + ex.Message, kind = "err", rows = Array.Empty<object>() })})");
        }
    }

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action == "copy" && _rows.Count > 0)
        {
            var sb = new StringBuilder("Table\tDescription\r\n");
            foreach (var r in _rows) sb.Append(r.Table).Append('\t').Append(r.IsReference ? "(bảng mẫu)" : r.Description).Append("\r\n");
            try { Clipboard.SetText(sb.ToString()); } catch { /* clipboard bận */ }
        }
        return Task.CompletedTask;
    }
}
