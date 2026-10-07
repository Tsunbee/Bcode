using System.Text.Json;
using Bcode.App.Services.Rpt;

namespace Bcode.App.Controls;

/// <summary>
/// Vùng kết quả của SQL Query tách làm 2 tab như FCode: "Grid Result" (các bảng kết quả — <see cref="MultiResultView"/>) và "Message"
/// (PRINT / RAISERROR / lỗi, tô màu từng loại). Cả thanh tab lẫn tab Message là trang WebView2 (Web/Shell/sqlresulttabs.html,
/// sqlmessages.html) nên ăn theo Template giao diện và tự co giãn theo màn hình. Sau mỗi lần chạy, <see cref="SetMessages"/> tự chọn tab:
/// có lỗi hoặc không có bảng nào mà có message → Message; còn lại → Grid Result.
/// </summary>
public sealed class SqlResultTabs : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebBarHost _strip = new("sqlresulttabs.html", 34);
    private readonly WebBarHost _msg = new("sqlmessages.html") { Dock = DockStyle.Fill };
    private readonly WebBarHost _pivot = new("sqlpivot.html") { Dock = DockStyle.Fill };
    private string? _pivotJson;   // payload của tab Pivot (null = không nhận diện được pivot → ẩn tab)

    private string _active = "grid", _kind = "none";
    private int _tables, _lines;
    private string? _error, _printed;

    /// <summary>Bấm "Tạo file Excel pivot…" trong tab Pivot.</summary>
    public event Action? CreateRptRequested;

    /// <summary>Lưới kết quả (tab Grid Result).</summary>
    public MultiResultView Grid { get; }

    public SqlResultTabs(MultiResultView grid)
    {
        Grid = grid;
        Dock = DockStyle.Fill;
        Grid.Dock = DockStyle.Fill;
        // Dock: các control Fill thêm trước, thanh tab (Top) thêm sau cùng.
        Controls.Add(Grid);
        Controls.Add(_msg);
        Controls.Add(_pivot);
        Controls.Add(_strip);
        _msg.Visible = false;
        _pivot.Visible = false;

        _strip.Message += root =>
        {
            if (root.TryGetProperty("action", out var a) && a.GetString() == "tab" && root.TryGetProperty("id", out var id))
                Select(id.GetString() ?? "grid");
        };
        _msg.Message += root =>
        {
            if (root.TryGetProperty("action", out var a) && a.GetString() == "copy" && root.TryGetProperty("text", out var t))
            {
                try { Clipboard.SetText(t.GetString() ?? ""); } catch { /* clipboard đang bị chương trình khác giữ */ }
            }
        };
        _pivot.Message += root =>
        {
            if (root.TryGetProperty("action", out var a) && a.GetString() == "make") CreateRptRequested?.Invoke();
        };
        _pivot.Ready += PushPivot;
        _strip.Ready += PushStrip;
        _msg.Ready += PushMessages;
    }

    /// <summary>Đặt nội dung tab Message. <paramref name="error"/> = lỗi (đỏ), <paramref name="printed"/> = PRINT/RAISERROR (màu nhấn);
    /// <paramref name="tables"/> = số bảng kết quả của lần chạy (để chọn tab hiển thị).</summary>
    public void SetMessages(string? error, string? printed, int tables)
    {
        _error = string.IsNullOrWhiteSpace(error) ? null : error;
        _printed = string.IsNullOrWhiteSpace(printed) ? null : printed;
        _tables = tables;
        _lines = Count(_error) + Count(_printed);
        _kind = _error is not null ? "error" : _printed is not null ? "info" : "none";
        PushMessages();
        Select(_error is not null || (tables == 0 && _lines > 0) ? "msg" : "grid");
    }

    /// <summary>Xoá message (đầu lần chạy mới / debug) và quay về tab Grid Result.</summary>
    public void ClearMessages() => SetMessages(null, null, _tables);

    public void Select(string id)
    {
        _active = id == "msg" ? "msg" : id == "pivot" && _pivotJson is not null ? "pivot" : "grid";
        Grid.Visible = _active == "grid";
        _msg.Visible = _active == "msg";
        _pivot.Visible = _active == "pivot";
        PushStrip();
    }

    /// <summary>Cho tab Pivot: <paramref name="payload"/> = null khi kết quả không phải báo cáo pivot (tab bị ẩn).</summary>
    public void SetPivot(PivotPayload? payload)
    {
        _pivotJson = payload is null ? null : JsonSerializer.Serialize(payload, JsonOpts);
        if (_pivotJson is null && _active == "pivot") Select("grid");
        PushPivot();
        PushStrip();
    }

    private static int Count(string? s) => s is null ? 0 : s.Split('\n').Count(l => l.Trim().Length > 0 && l.Trim() != "---");

    private void PushStrip() =>
        _strip.Call($"window.sqlTabs && sqlTabs.set({JsonSerializer.Serialize(new { tables = _tables, msgs = _lines, kind = _kind, active = _active, pivot = _pivotJson is not null }, JsonOpts)})");

    private void PushPivot()
    {
        if (_pivotJson is not null) _pivot.Call($"window.sqlPivot && sqlPivot.set({_pivotJson})");
    }

    private void PushMessages() =>
        _msg.Call($"window.sqlMsg && sqlMsg.set({JsonSerializer.Serialize(new { error = _error, printed = _printed }, JsonOpts)})");
}
