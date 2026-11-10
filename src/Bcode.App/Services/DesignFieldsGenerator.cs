using System.Data;
using System.Text;
using System.Xml.Linq;

namespace Bcode.App.Services;

/// <summary>
/// "Generate Design Fields" của menu chuột phải lưới kết quả SQL — chép đúng quy tắc của FCode (urQuery, hộp "Generate Design Fields In Grid";
/// giải mã FCode.exe/fsystem.dll 11/2026): sinh khối <c>&lt;fields&gt;</c> + <c>&lt;views&gt;&lt;view id="Grid"&gt;</c> cho file Grid của Fast
/// từ tên + kiểu các cột của bảng kết quả, thụt lề bằng Tab, xuống dòng CRLF.
/// <list type="bullet">
/// <item>width luôn 100 (bỏ qua độ rộng đang hiện); số → type="Decimal" + dataFormatString theo tên cột (+ aggregate="Sum"); ngày → DateTime;
/// bit → Boolean width 60; chữ ten_/dien_giai/ghi_chu/note → width 250; chữ ma_ → @upperCaseFormat.</item>
/// <item>so_ct/trang_thai/status → align="right" width 60; stt/dvt/ty_le → width 60; stt_rec*/sysorder/systotal/sysprint → hidden="true";
/// so_ct khi bảng có cả stt_rec và ma_ct → hyperlink mở chứng từ.</item>
/// <item>header v/e: field đầu tiên cùng tên trong header.xml (giữ nguyên chữ, kể cả dấu cách — vd stt e=" Number"); không có → "???";
/// riêng status cứng "Trạng thái"/"Status".</item>
/// </list>
/// Khác FCode: cột bit FCode quên dấu cách (<c>width="60"type="Boolean"</c> — XML hỏng), ở đây có dấu cách; cột khoá chính của bảng nguồn
/// (truy vấn 1 bảng, ResultSourceKeys) có thêm <c>isPrimaryKey="true"</c> ngay sau name.
/// </summary>
public static class DesignFieldsGenerator
{
    public enum Kind { Other, Numeric, DateTime, Boolean }

    /// <summary>Như Cmd.IsNumericDataType của FCode: byte, Int16, Int32, Int64, Decimal, Double, Single.</summary>
    public static Kind KindOf(Type t) =>
        t == typeof(byte) || t == typeof(short) || t == typeof(int) || t == typeof(long) || t == typeof(decimal) || t == typeof(double) || t == typeof(float) ? Kind.Numeric
        : t == typeof(DateTime) ? Kind.DateTime
        : t == typeof(bool) ? Kind.Boolean
        : Kind.Other;

    /// <summary>Khoá kiểu của trang resultview.html (MultiResultView.TypeKey) → nhóm kiểu.</summary>
    public static Kind KindOf(string typeKey) => typeKey switch
    {
        "int" or "long" or "short" or "byte" or "decimal" or "double" => Kind.Numeric,
        "datetime" => Kind.DateTime,
        "bool" => Kind.Boolean,
        _ => Kind.Other,
    };

    public static string Generate(DataTable table, ISet<string>? keys = null) =>
        Generate(table.Columns.Cast<DataColumn>().Select(c => (c.ColumnName, KindOf(c.DataType))).ToList(), keys);

    /// <param name="keys">Tên cột kết quả là khoá chính bảng nguồn (ResultSourceKeys) → <c>isPrimaryKey="true"</c> ngay sau name, đúng thứ tự file
    /// Grid của Fast. null / rỗng = không đánh dấu (như FCode).</param>
    public static string Generate(IReadOnlyList<(string Name, Kind Kind)> columns, ISet<string>? keys = null)
    {
        bool Has(string n) => columns.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
        var hasVoucherLink = Has("stt_rec") && Has("ma_ct");

        var fields = new StringBuilder("<fields>");
        var view = new StringBuilder("<views>\r\n\t<view id=\"Grid\">");
        foreach (var (rawName, kind) in columns)
        {
            var n = rawName.ToLowerInvariant();
            bool In(string s) => n.Contains(s, StringComparison.Ordinal);
            bool At0(string s) => n.StartsWith(s, StringComparison.Ordinal);

            var (v, e) = Header(n);
            var width = "100";
            var attrs = "";
            if (kind == Kind.Numeric)
            {
                var format = "### ### ### ##0.00";
                if (In("tien") || In("cp") || In("dt") || In("du"))
                    format = n.IndexOf("_nt", StringComparison.Ordinal) > 0 ? "@foreignCurrencyAmountViewFormat" : "@baseCurrencyAmountViewFormat";
                else if (In("stt")) format = "####";
                else if (In("ty_gia")) format = "@exchangeRateViewFormat";
                else if (In("gia"))
                    format = n.IndexOf("_nt", StringComparison.Ordinal) > 0 ? "@foreignCurrencyPriceViewFormat" : "@baseCurrencyPriceViewFormat";
                else if (In("he_so") || In("hs")) format = "@coefficientViewFormat";
                else if (In("so_luong") || In("sl") || In("ton")) format = "@quantityViewFormat";
                else if (In("ty_le") || In("tl_")) format = "##0.00";
                var sum = In("tien") || In("so_luong") || In("sl") || At0("t_") || At0("tt") || In("chenh") || In("cl_") || In("ton") || In("dk") || In("ck");
                attrs = " type=\"Decimal\" dataFormatString=\"" + format + "\"" + (sum ? " aggregate=\"Sum\"" : "");
            }
            else if (kind == Kind.DateTime) attrs = " type=\"DateTime\" dataFormatString=\"@datetimeFormat\"";
            else if (kind == Kind.Boolean) { width = "60"; attrs = " type=\"Boolean\""; }
            else if (At0("ten_") || In("dien_giai") || In("ghi_chu") || In("note")) width = "250";
            else if (In("ma_")) attrs = " dataFormatString=\"@upperCaseFormat\"";

            var align = "";
            if (In("so_ct") || In("trang_thai") || In("status")) { align = " align=\"right\""; width = "60"; }
            else if (In("stt") || In("dvt") || In("ty_le")) width = "60";

            var hidden = In("stt_rec") || n is "sysorder" or "systotal" or "sysprint" ? " hidden=\"true\"" : "";
            var link = n == "so_ct" && hasVoucherLink
                ? " hyperlinkFormatString=\"~/AppHandler/Voucher.ashx Query: {Name: '[ma_ct]', Value: '[stt_rec]'}, Script: 'beforeDrillDown(this);'\""
                : "";

            var pk = keys is not null && keys.Contains(rawName) ? " isPrimaryKey=\"true\"" : "";
            fields.Append("\r\n\t<field name=\"").Append(n).Append('"').Append(pk).Append(" width=\"").Append(width).Append('"').Append(attrs)
                  .Append(" allowSorting=\"true\" allowFilter=\"true\"").Append(align).Append(hidden).Append(link).Append('>');
            fields.Append("\r\n\t\t<header v=\"").Append(v).Append("\" e=\"").Append(e).Append("\"></header>");
            fields.Append("\r\n\t</field>");
            view.Append("\r\n\t\t<field name=\"").Append(n).Append("\"/>");
        }
        fields.Append("\r\n</fields>");
        view.Append("\r\n\t</view>\r\n</views>");
        return fields + "\r\n\r\n" + view;
    }

    /// <summary>header v/e như Cmd.GetXMLHeader của FCode: field ĐẦU TIÊN có đúng tên (phân biệt hoa thường) trong header.xml, lấy nguyên văn
    /// &lt;header&gt; / &lt;header2&gt; (thiếu thẻ nào thì "???" cho ô đó).</summary>
    private static (string V, string E) Header(string name)
    {
        if (name == "status") return ("Trạng thái", "Status");
        return _headers.Value.TryGetValue(name, out var h) ? (h.V ?? "???", h.E ?? "???") : ("???", "???");
    }

    private static readonly Lazy<Dictionary<string, (string? V, string? E)>> _headers = new(() =>
    {
        var map = new Dictionary<string, (string? V, string? E)>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", "fileSource", "header.xml");
            if (!File.Exists(path)) return map;
            foreach (var f in XDocument.Load(path).Descendants("field"))
            {
                var name = (string?)f.Attribute("name");
                if (name is null || map.ContainsKey(name)) continue;
                map[name] = (f.Element("header")?.Value, f.Element("header2")?.Value);
            }
        }
        catch { /* header.xml hỏng → mọi cột "???" như FCode khi không đọc được */ }
        return map;
    });
}
