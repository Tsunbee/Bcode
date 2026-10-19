using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Check Partition Struct (partitioncheck.html) ở chế độ App: dòng kết luận + lưới Table | Description, nút Copy / OK.</summary>
internal sealed class PartitionCheckPage : DialogPage
{
    private readonly Label _head = new() { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4), Text = "Đang kiểm tra cấu trúc các bảng phân kỳ..." };
    private readonly DataGridView _grid = NK.Grid(("n", "", 40), ("t", "Table", 150), ("d", "Description", 0));

    public PartitionCheckPage()
    {
        _head.Font = new Font(_head.Font, FontStyle.Bold);
        _grid.Columns["d"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        var top = TopBar(); top.Controls.Add(_head);
        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("OK", () => Post(new { action = "close" }), primary: true));
        Bottom.Controls.Add(NK.Button("Copy", () => Post(new { action = "copy" })));
        Layout(top, Pad(_grid));
    }

    protected override bool HandleKey(Keys key) { if (key is Keys.Escape or Keys.Enter) { Post(new { action = "close" }); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "partCheck.show") return;
        var d = Arg(a, 0);
        _head.Text = P(d, "head");
        _head.ForeColor = P(d, "kind") switch { "ok" => AppColors.Success, "warn" or "err" => AppColors.Danger, _ => AppColors.TextMuted };
        _grid.Rows.Clear();
        var i = 0;
        foreach (var r in PA(d, "rows"))
        {
            var isRef = PB(r, "isReference");
            var row = _grid.Rows.Add((++i).ToString(), P(r, "table"), isRef ? "(bảng mẫu)" : P(r, "description"));
            if (isRef) _grid.Rows[row].Cells[1].Style.ForeColor = AppColors.Accent;
            else if (!PB(r, "same")) { _grid.Rows[row].Cells[1].Style.ForeColor = AppColors.Danger; _grid.Rows[row].Cells[1].Style.Font = new Font(_grid.Font, FontStyle.Bold); }
        }
        Say(P(d, "status"));
    }
}
