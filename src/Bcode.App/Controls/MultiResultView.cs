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
    private const int BinaryPreviewBytes = 32;

    private readonly WebBarHost _web = new("resultview.html") { Dock = DockStyle.Fill };
    private string? _pending; // dữ liệu gửi trước khi trang nạp xong — gửi lại ở Ready

    public MultiResultView()
    {
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Ready += () => { if (_pending is not null) _web.PostJson(_pending); };
        _web.Message += root =>
        {
            if (root.TryGetProperty("action", out var a) && a.GetString() == "copy" && root.TryGetProperty("text", out var t))
            {
                try { Clipboard.SetText(t.GetString() ?? ""); } catch { /* clipboard đang bị chương trình khác giữ */ }
            }
        };
    }

    /// <summary>Thay toàn bộ vùng kết quả bằng các bảng này, theo thứ tự. Danh sách rỗng = xoá (như <see cref="Clear"/>).</summary>
    public void SetTables(IReadOnlyList<DataTable> tables)
    {
        if (tables.Count == 0) { Clear(); return; }
        Send(Serialize(tables));
    }

    public void Clear() => Send("{\"type\":\"clear\"}");

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
                var count = Math.Min(table.Rows.Count, MaxRows);
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
            default: w.WriteStringValue(value.ToString()); break;
        }
    }
}
