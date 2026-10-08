using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Forms;

/// <summary>
/// "Compare Structure": diffs a table's columns between two workspaces (e.g. a
/// customer DB vs. a reference/master DB) — useful before pushing a schema update.
/// Giao diện là trang WebView2 (Web/Shell/comparestructure.html); form chỉ lấy cột của 2 workspace và so sánh.
/// </summary>
public class CompareStructureForm : WebDialogForm
{
    private readonly AppSettings _settings;
    private readonly SchemaCompareService _schema = new();

    public CompareStructureForm(AppSettings settings) : base("Compare Structure", "comparestructure.html", 980, 640, 640, 380)
    {
        _settings = settings;
    }

    protected override void OnReady() =>
        Js($"cmpStruct.init({J(new { workspaces = _settings.Workspaces.Select((w, i) => new { index = i, name = string.IsNullOrWhiteSpace(w.ProjectId) ? w.Name : w.ProjectId }) })})");

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        if (action != "run") return;
        var l = msg.GetProperty("left").GetInt32();
        var r = msg.GetProperty("right").GetInt32();
        var schema = (msg.GetProperty("schema").GetString() ?? "dbo").Trim();
        var table = (msg.GetProperty("table").GetString() ?? "").Trim();
        if (l < 0 || r < 0 || l >= _settings.Workspaces.Count || r >= _settings.Workspaces.Count)
        { Js($"cmpStruct.onError({J("Chọn Workspace bên trái và bên phải trước.")})"); return; }
        if (table.Length == 0) { Js($"cmpStruct.onError({J("Nhập tên bảng cần so sánh.")})"); return; }

        Js("cmpStruct.onBusy(true)");
        try
        {
            var left = _settings.Workspaces[l];
            var right = _settings.Workspaces[r];
            await using var leftConn = new SqlConnection(left.BuildConnectionString());
            await using var rightConn = new SqlConnection(right.BuildConnectionString());
            await leftConn.OpenAsync();
            await rightConn.OpenAsync();

            var leftCols = await _schema.GetColumnsAsync(leftConn, schema, table);
            var rightCols = await _schema.GetColumnsAsync(rightConn, schema, table);
            var result = _schema.Compare(leftCols, rightCols);

            var rows = new List<object>();
            foreach (var c in result.OnlyInLeft) rows.Add(new { status = "left", text = "Chỉ có ở trái", column = c.ColumnName, left = c.Signature, right = "-" });
            foreach (var c in result.OnlyInRight) rows.Add(new { status = "right", text = "Chỉ có ở phải", column = c.ColumnName, left = "-", right = c.Signature });
            foreach (var (a, b) in result.Changed) rows.Add(new { status = "diff", text = "Khác nhau", column = a.ColumnName, left = a.Signature, right = b.Signature });
            foreach (var c in result.Unchanged) rows.Add(new { status = "same", text = "Giống nhau", column = c.ColumnName, left = c.Signature, right = c.Signature });
            Js($"cmpStruct.onResult({J(rows)})");
        }
        catch (Exception ex)
        {
            Js($"cmpStruct.onError({J(ex.Message)})");
        }
        finally { Js("cmpStruct.onBusy(false)"); }
    }
}
