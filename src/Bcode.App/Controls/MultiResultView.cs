using System.Data;
using System.Globalization;
using System.Text.Json;

namespace Bcode.App.Controls;

/// <summary>
/// Vùng hiển thị kết quả SQL — 1 hoặc nhiều bảng của 1 lần chạy ("Result with N table(s)" như FCode). Trước đây là các
/// DataGridView xếp chồng; giờ là trang WebView2 (Web/Shell/resultview.html) cho đồng bộ giao diện với phần còn lại của
/// Bcode và tự co giãn theo cỡ màn hình:
///   • mỗi bảng 1 thẻ có tiêu đề, số dòng/cột, thu gọn/mở rộng, kéo đổi chiều cao; có chế độ "Từng tab";
///   • lưới ảo hoá (hàng chục nghìn dòng vẫn mượt), sort theo cột, đổi độ rộng cột, chọn ô/dòng, Ctrl+C;
///   • menu chuột phải giữ các mục cũ của ResultGridMenu: Goto Column, Copy Column Name, Filter, Add Index Column Order,
///     Generate Design Fields, Maxlength, Compare Column Content, Set Color Cell.
/// Dữ liệu gửi sang trang dạng chuỗi (null = NULL) qua PostWebMessage; tối đa <see cref="MaxRows"/> dòng mỗi bảng để trang không đơ.
/// </summary>
public class MultiResultView : UserControl
{
    /// <summary>Số dòng tối đa hiển thị mỗi bảng (bảng lớn hơn vẫn báo đủ tổng số dòng, nhưng chỉ vẽ ngần này).</summary>
    public const int MaxRows = 100_000;
    /// <summary>Tổng số ô (dòng × cột) gửi sang trang cho cả lần chạy: báo cáo trả hàng trăm nghìn dòng × hàng trăm cột (hàng chục triệu ô) mà dựng JSON + gửi hết thì
    /// Bcode đứng hình (Not responding). Vượt ngưỡng thì bớt số dòng hiển thị (tổng số dòng vẫn báo đủ). Mỗi ô chữ cũng bị cắt ở <see cref="MaxCellChars"/> ký tự.</summary>
    public const long CellBudget = 2_500_000;
    private const int MaxCellChars = 4000;
    private int _sendVersion;
    private const int BinaryPreviewBytes = 32;

    private readonly IWebPage _web;
    private string? _pending; // dữ liệu gửi trước khi trang nạp xong — gửi lại ở Ready
    private List<DataTable>? _tables; // bảng đang hiện (đủ mọi dòng) — "Save as CSV / Excel" ghi từ đây; null sau Restore (tab ngủ đông dậy)

    /// <summary>Control đang thật sự hiện trang trên cửa sổ — khi trang là 1 khung của <see cref="WebFrameHost"/> thì control này (MultiResultView)
    /// KHÔNG được gắn vào cửa sổ nào (không có handle, FindForm() = null), nên hộp thoại / BeginInvoke / tên tab phải đi qua khung host.</summary>
    private readonly Control? _anchor;
    private Control Anchor => _anchor ?? this;

    /// <param name="page">Trang resultview.html đã có sẵn (vd 1 khung trong <see cref="WebFrameHost"/> dùng chung); null = tự tạo 1 WebView2 riêng như trước.</param>
    /// <param name="anchor">Control chứa <paramref name="page"/> trên cửa sổ (vd chính WebFrameHost) — bắt buộc khi truyền page.</param>
    public MultiResultView(IWebPage? page = null, Control? anchor = null)
    {
        _anchor = page is null ? null : anchor;
        Dock = DockStyle.Fill;
        if (page is null)
        {
            var own = new WebBarHost("resultview.html") { Dock = DockStyle.Fill };
            Controls.Add(own);
            page = own;
        }
        _web = page;
        _web.Ready += () => { if (_pending is not null) _web.PostJson(_pending); };
        _web.Message += root =>
        {
            if (!root.TryGetProperty("action", out var a)) return;
            if (a.GetString() == "copy" && root.TryGetProperty("text", out var t))
            {
                try { Clipboard.SetText(t.GetString() ?? ""); } catch { /* clipboard đang bị chương trình khác giữ */ }
            }
            else if (a.GetString() == "save") Save(root);
            else if (a.GetString() == "keys")   // lưới focus / blur: Ctrl+Shift+C = Copy kèm tên cột, không mở Command
            {
                if (root.TryGetProperty("claim", out var c) && c.ValueKind == JsonValueKind.True) Bcode.App.UI.WebViewEnvironment.ClaimKeys(this, "Ctrl+Shift+C");
                else Bcode.App.UI.WebViewEnvironment.ClaimKeys(this);
            }
        };
        Disposed += (_, _) => Bcode.App.UI.WebViewEnvironment.ClaimKeys(this);
    }

    /// <summary>Thay toàn bộ vùng kết quả bằng các bảng này, theo thứ tự. Danh sách rỗng = xoá (như <see cref="Clear"/>).</summary>
    public void SetTables(IReadOnlyList<DataTable> tables)
    {
        if (tables.Count == 0) { Clear(); return; }
        _tables = tables.ToList();
        _ = SendTablesAsync(_tables);
    }

    /// <summary>Dựng JSON ở luồng nền (trước đây chạy ngay trên luồng giao diện nên kết quả lớn làm đứng cả Bcode), xong mới gửi sang trang. Lần gọi mới hơn thay lần cũ.</summary>
    private async Task SendTablesAsync(List<DataTable> tables)
    {
        var version = ++_sendVersion;
        string json;
        try { json = await Task.Run(() => Serialize(tables)); }
        catch { return; }
        if (version != _sendVersion || IsDisposed) return;
        Send(json);
    }

    public void Clear() { _sendVersion++; _tables = null; Send("{\"type\":\"clear\"}"); }

    /// <summary>Menu chuột phải "Save as CSV… / Save as Excel (.xlsx)… / Xuất nhanh ra Excel" của trang: {table, format: "csv"|"xlsx", rows: null | [chỉ số dòng gốc], quick}.</summary>
    private void Save(JsonElement root)
    {
        var index = root.TryGetProperty("table", out var ti) && ti.TryGetInt32(out var n) ? n : 0;
        if (_tables is null || index < 0 || index >= _tables.Count)
        {
            MessageBox.Show(Anchor.FindForm(), "Kết quả này được khôi phục từ lần mở trước (tab ngủ đông / khôi phục phiên) nên không còn dữ liệu gốc đầy đủ.\nChạy lại truy vấn rồi lưu.",
                "Bcode — Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var tables = _tables;
        var table = tables[index];
        List<DataRow>? rows = null;
        if (root.TryGetProperty("rows", out var rs) && rs.ValueKind == JsonValueKind.Array)
        {
            rows = new List<DataRow>(rs.GetArrayLength());
            foreach (var r in rs.EnumerateArray())
                if (r.TryGetInt32(out var i) && i >= 0 && i < table.Rows.Count) rows.Add(table.Rows[i]);
            if (rows.Count == 0) return;
        }
        var xlsx = !(root.TryGetProperty("format", out var f) && f.GetString() == "csv");
        var quick = root.TryGetProperty("quick", out var q) && q.ValueKind == JsonValueKind.True;
        // Mở SaveFileDialog sau khi xử lý xong tin nhắn của WebView2 (hộp thoại modal trong handler WebMessageReceived dễ treo trang).
        var anchor = Anchor;
        Action run = () => _ = ResultExport.SaveAsync(anchor, table, xlsx, rows, tables.Count > 1 ? $"Bảng {index + 1}" : null, quick);
        if (anchor.IsHandleCreated) anchor.BeginInvoke(run);
        else SynchronizationContext.Current?.Post(_ => run(), null);
    }

    /// <summary>JSON lần gửi gần nhất (bảng đang hiện) — để tab SQL "ngủ đông" rồi dựng lại y như cũ (<see cref="Restore"/>).</summary>
    public string? Snapshot => _pending;

    /// <summary>Hiện lại đúng dữ liệu đã chụp bằng <see cref="Snapshot"/> (gửi khi trang nạp xong).</summary>
    public void Restore(string? json)
    {
        if (json is null) return;
        _sendVersion++;
        _tables = null;
        Send(json);
    }

    private void Send(string json)
    {
        _pending = json;
        _web.PostJson(json);
    }

    private static string Serialize(IReadOnlyList<DataTable> tables)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "tables");
            w.WriteStartArray("tables");
            for (var i = 0; i < tables.Count; i++)
            {
                var table = tables[i];
                w.WriteStartObject();
                w.WriteString("name", string.IsNullOrWhiteSpace(table.TableName) || table.TableName.StartsWith("Table", StringComparison.OrdinalIgnoreCase)
                    ? $"Table {i + 1}" : table.TableName);
                w.WriteNumber("total", table.Rows.Count);
                if (table.ExtendedProperties["ms"] is long runMs) w.WriteNumber("ms", runMs);   // thời gian chạy SELECT này (RawSqlService)

                w.WriteStartArray("cols");
                foreach (DataColumn c in table.Columns)
                {
                    w.WriteStartObject();
                    w.WriteString("n", c.ColumnName);
                    w.WriteString("t", TypeKey(c.DataType));
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("rows");
                var perTable = Math.Max(1000L, CellBudget / Math.Max(1, tables.Count) / Math.Max(1, table.Columns.Count));
                var count = (int)Math.Min(Math.Min(table.Rows.Count, MaxRows), perTable);
                for (var r = 0; r < count; r++)
                {
                    var row = table.Rows[r];
                    w.WriteStartArray();
                    for (var c = 0; c < table.Columns.Count; c++) WriteValue(w, row[c]);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static string TypeKey(Type t) =>
        t == typeof(int) ? "int" : t == typeof(long) ? "long" : t == typeof(short) ? "short" : t == typeof(byte) ? "byte"
        : t == typeof(decimal) ? "decimal" : t == typeof(double) || t == typeof(float) ? "double" : t == typeof(bool) ? "bool"
        : t == typeof(DateTime) ? "datetime" : t == typeof(Guid) ? "guid" : t == typeof(byte[]) ? "bytes" : "string";

    /// <summary>Giá trị → chuỗi hiển thị như lưới cũ: ngày không có giờ → dd/MM/yyyy, có giờ → thêm HH:mm:ss; số theo culture hiện tại;
    /// nhị phân → hex rút gọn ("0x4D5A… (12.345 bytes)").</summary>
    private static void WriteValue(Utf8JsonWriter w, object value)
    {
        switch (value)
        {
            case DBNull or null: w.WriteNullValue(); break;
            case DateTime d: w.WriteStringValue(d.TimeOfDay == TimeSpan.Zero ? d.ToString("dd/MM/yyyy") : d.ToString("dd/MM/yyyy HH:mm:ss")); break;
            case byte[] b:
            {
                var hex = Convert.ToHexString(b, 0, Math.Min(b.Length, BinaryPreviewBytes));
                w.WriteStringValue(b.Length > BinaryPreviewBytes ? $"0x{hex}… ({b.Length:N0} bytes)" : "0x" + hex);
                break;
            }
            case IFormattable f: w.WriteStringValue(f.ToString(null, CultureInfo.CurrentCulture)); break;
            default:
            {
                var str = value.ToString() ?? "";
                w.WriteStringValue(str.Length > MaxCellChars ? str[..MaxCellChars] + "…" : str);
                break;
            }
        }
    }
}
