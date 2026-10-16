using System.Data;
using System.IO;
using System.Text.Json;
using Bcode.App.Services.Rpt;

namespace Bcode.App.Forms;

/// <summary>
/// Write Schema → "Cập nhật bảng trong file .rpt": chọn file Crystal Report, mỗi bảng của rpt chọn lấy nguồn từ bảng nào của xsd vừa ghi
/// (mặc định bảng cùng tên), bảng xsd chưa có trong rpt thì thêm mới; báo trước cột thêm / mất — cột mất mà report đang dùng thì Crystal xoá
/// luôn ô đó khỏi layout nên tô đỏ và hỏi lại. Logic ở Services/Rpt/RptSchemaService; giao diện Web/Shell/rptschema.html (App: RptSchemaPage).
/// </summary>
public sealed class RptSchemaForm : WebDialogForm
{
    private readonly string _xsdPath;
    private readonly List<(string Name, List<string> Columns)> _xsd;
    private string _rptPath;
    private RptInfo? _info;
    private bool _busy;

    /// <summary>Dòng trạng thái sau khi cập nhật thành công (hiện ở thanh trạng thái tab SQL Query).</summary>
    public string? ResultStatus { get; private set; }

    public RptSchemaForm(string xsdPath, DataSet schema, string? suggestedRpt)
        : base("Write Schema — cập nhật bảng trong file .rpt", "rptschema.html", 820, 520, 560, 340)
    {
        _xsdPath = xsdPath;
        _xsd = schema.Tables.Cast<DataTable>().Select(t => (t.TableName, t.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList())).ToList();
        _rptPath = suggestedRpt ?? "";
    }

    protected override void OnReady()
    {
        Js($"rptSchema.init({J(new { xsd = _xsdPath, xsdTables = _xsd.Select(t => $"{t.Name} ({t.Columns.Count} cột)"), rpt = _rptPath })})");
        if (_rptPath.Length > 0 && File.Exists(_rptPath)) _ = LoadAsync(_rptPath);
    }

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        switch (action)
        {
            case "pick":
            {
                using var ofd = new OpenFileDialog { Title = "Chọn file Crystal Report", Filter = "Crystal Report (*.rpt)|*.rpt|All files (*.*)|*.*" };
                var dir = _rptPath.Length > 0 ? Path.GetDirectoryName(_rptPath) : Path.GetDirectoryName(_xsdPath);
                if (dir is not null && Directory.Exists(dir)) ofd.InitialDirectory = dir;
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                Js($"rptSchema.setRpt({J(ofd.FileName)})");
                await LoadAsync(ofd.FileName);
                break;
            }
            case "load":
                await LoadAsync(Str(msg, "rpt"));
                break;
            case "apply":
                await ApplyAsync(msg);
                break;
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private async Task LoadAsync(string path)
    {
        path = path.Trim().Trim('"');
        if (_busy) return;
        if (!File.Exists(path)) { Status("Không thấy file " + path, "err"); return; }
        _rptPath = path; _info = null;
        SetBusy(true, "Đang đọc file .rpt bằng Crystal Reports runtime...");
        try
        {
            _info = await RptSchemaService.ReadAsync(path);
            Js($"rptSchema.load({J(BuildView(_info))})");
            Status($"Crystal runtime {_info.Version} · report có {_info.Tables.Count} bảng" +
                   (_info.Subreports.Count > 0 ? $", {_info.Subreports.Count} subreport" : "") + ".", "");
        }
        catch (Exception ex) { Js("rptSchema.load(null)"); Status(ex.Message, "err"); }
        finally { SetBusy(false, null); }
    }

    /// <summary>Mỗi bảng rpt: chọn (giữ nguyên) hoặc 1 bảng xsd, kèm ghi chú khác biệt cột; mỗi bảng xsd chưa có trong rpt: Thêm / Bỏ qua.</summary>
    private object BuildView(RptInfo info)
    {
        var used = new HashSet<string>(info.Used, StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>();
        var mappedByDefault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in info.Tables)
        {
            var options = new List<object> { new { value = "", text = "(giữ nguyên)", note = "", level = "" } };
            options.AddRange(_xsd.Select(x => { var (note, level) = Diff(t, x.Columns, used); return (object)new { value = x.Name, text = "← " + x.Name + " (xsd)", note, level }; }));
            var same = _xsd.FirstOrDefault(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase)).Name
                       ?? _xsd.FirstOrDefault(x => string.Equals(x.Name, t.Alias, StringComparison.OrdinalIgnoreCase)).Name;
            if (same is not null) mappedByDefault.Add(same);
            rows.Add(new
            {
                key = "t:" + t.Alias,
                label = t.Alias + (string.Equals(t.Alias, t.Name, StringComparison.OrdinalIgnoreCase) ? "" : " (" + t.Name + ")"),
                sub = $"{t.Fields.Count} cột · đang trỏ: {(t.Path.Length > 0 ? Path.GetFileName(t.Path) : "?")}",
                tip = t.Path,
                options, selected = same ?? "",
            });
        }
        var aliases = new HashSet<string>(info.Tables.Select(t => t.Alias), StringComparer.OrdinalIgnoreCase);
        foreach (var x in _xsd.Where(x => !aliases.Contains(x.Name)))
        {
            rows.Add(new
            {
                key = "a:" + x.Name,
                label = x.Name,
                sub = "chưa có trong rpt",
                tip = "",
                options = new object[]
                {
                    new { value = "add", text = "Thêm vào rpt", note = $"{x.Columns.Count} cột: {Short(x.Columns)}", level = "ok" },
                    new { value = "", text = "Bỏ qua", note = "", level = "" },
                },
                // rpt đặt tên kiểu d1, d2 (có bảng được ghép mặc định) thì thêm luôn bảng mới; tên khác hẳn thì để người dùng tự chọn.
                selected = mappedByDefault.Count > 0 && !mappedByDefault.Contains(x.Name) ? "add" : "",
            });
        }
        var subNote = info.Subreports.Count == 0 ? "" :
            $"Cập nhật cả subreport ({string.Join(", ", info.Subreports.Select(s => s.Name))}): bảng trùng tên với bảng xsd được trỏ sang file xsd mới";
        return new { rows, subNote };
    }

    private static (string note, string level) Diff(RptTableInfo t, List<string> xsdCols, HashSet<string> used)
    {
        var have = new HashSet<string>(t.Fields, StringComparer.OrdinalIgnoreCase);
        var cols = new HashSet<string>(xsdCols, StringComparer.OrdinalIgnoreCase);
        var added = xsdCols.Where(c => !have.Contains(c)).ToList();
        var removed = t.Fields.Where(f => !cols.Contains(f)).ToList();
        var usedRemoved = removed.Where(f => used.Contains(t.Alias + "." + f)).ToList();
        var parts = new List<string>();
        if (usedRemoved.Count > 0) parts.Add($"⚠ mất cột report ĐANG DÙNG (ô sẽ bị xoá khỏi layout): {Short(usedRemoved)}");
        var otherRemoved = removed.Except(usedRemoved).ToList();
        if (otherRemoved.Count > 0) parts.Add($"mất {otherRemoved.Count} cột: {Short(otherRemoved)}");
        if (added.Count > 0) parts.Add($"+{added.Count} cột: {Short(added)}");
        if (parts.Count == 0) parts.Add("cột giống hệt");
        return (string.Join(" · ", parts), usedRemoved.Count > 0 ? "err" : removed.Count > 0 ? "warn" : "ok");
    }

    private static string Short(List<string> names) => names.Count <= 8 ? string.Join(", ", names) : string.Join(", ", names.Take(8)) + $"… (+{names.Count - 8})";

    private async Task ApplyAsync(JsonElement msg)
    {
        if (_busy) return;
        if (_info is null) { Status("Chọn file .rpt trước.", "err"); return; }
        var choices = msg.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Object
            ? c.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
            : new Dictionary<string, string>();
        var maps = _info.Tables
            .Select(t => (t, x: choices.TryGetValue("t:" + t.Alias, out var v) ? v : ""))
            .Where(p => p.x.Length > 0).Select(p => new RptTableMap(p.t.Alias, p.x)).ToList();
        var adds = choices.Where(p => p.Key.StartsWith("a:") && p.Value == "add").Select(p => p.Key[2..]).ToList();
        var subreports = msg.TryGetProperty("subreports", out var s) && s.ValueKind == JsonValueKind.True;
        if (maps.Count == 0 && adds.Count == 0) { Status("Chưa chọn bảng nào để cập nhật.", "err"); return; }

        var used = new HashSet<string>(_info.Used, StringComparer.OrdinalIgnoreCase);
        var lost = maps.SelectMany(m =>
        {
            var t = _info.Tables.First(x => x.Alias == m.Alias);
            var cols = new HashSet<string>(_xsd.First(x => x.Name == m.Xsd).Columns, StringComparer.OrdinalIgnoreCase);
            return t.Fields.Where(f => !cols.Contains(f) && used.Contains(t.Alias + "." + f)).Select(f => t.Alias + "." + f);
        }).ToList();
        if (lost.Count > 0 && MessageBox.Show(this,
                "File xsd mới thiếu các cột report đang dùng — Crystal sẽ XOÁ các ô / tham chiếu này khỏi report:\r\n\r\n  " + string.Join("\r\n  ", lost) +
                "\r\n\r\n(Bản gốc vẫn được sao lưu thành .rpt.bak.) Vẫn cập nhật?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        SetBusy(true, "Đang cập nhật file .rpt...");
        try
        {
            var r = await RptSchemaService.UpdateAsync(_rptPath, _xsdPath, maps, adds, subreports);
            var summary = string.Join(", ", maps.Select(m => m.Alias + (m.Alias == m.Xsd ? "" : "←" + m.Xsd)).Concat(adds.Select(a => "+" + a)));
            ResultStatus = $"Đã cập nhật {Path.GetFileName(_rptPath)} ({summary}); bản cũ: {Path.GetFileName(r.BackupPath)}." +
                           (r.Warnings.Count > 0 ? " Lưu ý: " + string.Join(" ", r.Warnings) : "");
            if (r.Warnings.Count > 0)
                MessageBox.Show(this, ResultStatus, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            CloseWith(DialogResult.OK);
        }
        catch (Exception ex) { Status(ex.Message, "err"); }
        finally { SetBusy(false, null); }
    }

    private void SetBusy(bool busy, string? text)
    {
        _busy = busy;
        Js($"rptSchema.busy({(busy ? "true" : "false")})");
        if (text is not null) Status(text, "");
    }

    private void Status(string text, string kind) => Js($"rptSchema.status({J(text)},{J(kind)})");
}
