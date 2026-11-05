using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Bcode.App.Services.Api;

/// <summary>
/// Tài liệu Word ↔ ColumnMappings.xml (mapping của FastAPIService chuẩn LT3 — cấu trúc xem <see cref="ApiColumnMappings"/>).
/// <para><b>Xuất</b> (<see cref="WriteDocx"/>): mỗi form 1 mục — Heading 1 = tên form, dòng "Mô tả / Endpoint / Bảng / Store", bảng
/// Parameter · Field · Data Type · Length · Description · Require · Default · Note (bố cục tài liệu API LT3 mẫu, vd FAST_CRM_API_Docs của KOG),
/// bảng chi tiết (ChildTable) = Heading 2 "Chi tiết: &lt;tên&gt;" + "Bảng / ParentField / ChildField" + bảng cột, cuối mục "JSON Request Example".</para>
/// <para><b>Nhập</b> (<see cref="ReadDocx"/>): đọc lại đúng bố cục đó, và cả tài liệu viết tay cùng kiểu (tiêu đề cột tiếng Anh / Việt: Parameter|Tham số,
/// CRM Value|Field|Cột, Data Type|Kiểu, Length|Độ dài, Description|Mô tả, Require|Bắt buộc, Default|Mặc định, Note|Ghi chú; tên form lấy ở Heading gần nhất
/// hoặc dòng "Hàm setXxx"). <see cref="Diff"/> so với mapping hiện có, <see cref="Merge"/> ghép vào — KHÔNG xoá gì: form / cột / thuộc tính không có trong Word
/// giữ nguyên, ô trống trong Word không xoá giá trị cũ; giữ nguyên chú thích, thứ tự, thụt lề của file.</para>
/// </summary>
public static class ApiMappingWord
{
    // ================================================================ mô hình
    public sealed class MapCol
    {
        public string Param = "", Field = "", SqlType = "", DataType = "", Parent = "", Description = "";
        public string? Default;
        public bool Required;
        /// <summary>Tài liệu có cột "Require" (Y/N) → áp Required; không có cột đó thì giữ Required cũ.</summary>
        public bool RequiredSet;
    }

    public sealed class MapChild
    {
        public string Name = "", Table = "", ParentField = "", ChildField = "";
        public List<MapCol> Columns = new();
    }

    public sealed class MapForm
    {
        public string Name = "", Title = "", Table = "", Store = "";
        public List<MapCol> Columns = new();
        public List<MapChild> Children = new();
        public bool Voucher => Children.Count > 0;
    }

    /// <summary>Kết quả so 1 form của Word với mapping: Status = new | changed | same; Changes = các thay đổi sẽ ghi; Kept = cột chỉ có trong XML (giữ nguyên);
    /// Errors = lỗi chặn ghi (thiếu table / store — service bỏ qua form đó); Warnings = cột bị bỏ qua (thiếu Field…).</summary>
    public sealed record FormDiff(string Name, string Status, string Table, string Store, string Title, bool Voucher, int Columns,
        List<string> Changes, List<string> Kept, List<string> Warnings, List<string> Errors);

    // ================================================================ đọc ColumnMappings.xml (đủ thuộc tính, để xuất Word)
    public static List<MapForm> ReadXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var list = new List<MapForm>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var form in doc.Descendants("Form"))
        {
            var name = A(form, "name");
            if (name.Length == 0 || !seen.Add(name)) continue;   // như service: trùng tên giữ bản đầu
            var f = new MapForm { Name = name, Title = Title(form), Table = A(form, "table"), Store = A(form, "store"), Columns = Cols(form.Element("Columns")) };
            foreach (var ct in form.Element("ChildTables")?.Elements("ChildTable") ?? Enumerable.Empty<XElement>())
            {
                var cn = A(ct, "name");
                if (cn.Length == 0) continue;
                f.Children.Add(new MapChild
                {
                    Name = cn, Table = A(ct, "table"), ParentField = ((string?)ct.Element("ParentField") ?? "").Trim(),
                    ChildField = ((string?)ct.Element("ChildField") ?? "").Trim(), Columns = Cols(ct.Element("Columns")),
                });
            }
            list.Add(f);
        }
        return list;
    }

    private static string A(XElement e, string n) => ((string?)e.Attribute(n) ?? "").Trim();

    private static List<MapCol> Cols(XElement? cols) =>
        (cols?.Elements("Column") ?? Enumerable.Empty<XElement>()).Select(c => new MapCol
        {
            Param = A(c, "Param"), Field = A(c, "Field"), SqlType = A(c, "SqlDataType"), DataType = A(c, "DataType"), Parent = A(c, "Parent"),
            Description = A(c, "Description"), Default = (string?)c.Attribute("Default"),
            Required = bool.TryParse((string?)c.Attribute("Required"), out var r) && r, RequiredSet = true,
        }).Where(c => c.Param.Length > 0 || c.Field.Length > 0).ToList();

    /// <summary>Chú thích &lt;!-- … --&gt; ngay trước thẻ Form (tiêu đề form trong file của dự án).</summary>
    private static string Title(XElement form)
    {
        for (var n = form.PreviousNode; n is not null; n = n.PreviousNode)
        {
            if (n is XComment cm) return cm.Value.Trim();
            if (n is XElement) return "";
        }
        return "";
    }

    // ================================================================ xuất Word
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly string[] Heads = { "Parameter", "Field", "Data Type", "Length", "Description", "Require", "Default", "Note" };
    private static readonly int[] Widths = { 1550, 1350, 1250, 850, 1850, 850, 900, 1038 };   // tổng 9638 twip = A4 dọc, lề 2 cm

    /// <summary>Thông tin chung đầu tài liệu (lấy từ dự án API đang mở nếu có).</summary>
    public sealed record DocInfo(string Project, string Source, string BaseUrl, string TokenPath);

    public static void WriteDocx(string path, IReadOnlyList<MapForm> forms, DocInfo info)
    {
        var b = new StringBuilder();
        b.Append(Para("TÀI LIỆU API" + (info.Project.Length > 0 ? " — " + info.Project : ""), "Title"));
        b.Append(Para($"Chuẩn LT3 (FastAPIService) · theo {info.Source} · xuất ngày {DateTime.Now:dd/MM/yyyy HH:mm} bằng Bcode", italic: true));

        b.Append(Para("Thông tin chung", "Heading1"));
        var baseUrl = info.BaseUrl.TrimEnd('/');
        var root = baseUrl.Length > 0 ? baseUrl + "/" : "";
        if (baseUrl.Length > 0) b.Append(Para("Địa chỉ gốc: " + baseUrl));
        b.Append(Para($"Lấy token: POST {root}{(info.TokenPath.Length > 0 ? info.TokenPath.TrimStart('/') : "api/getToken")} — gửi tài khoản được cấp, dùng token nhận được ở header Authorization của các lệnh sau."));
        b.Append(Para($"Đồng bộ danh mục: POST {root}api/SyncData · đồng bộ chứng từ (form có bảng chi tiết): POST {root}api/SyncVoucher."));
        b.Append(Para("Body: {\"form\": \"<tên form>\", \"data\": [ {<các trường của form>}, … ]} — mỗi phần tử của data là 1 dòng; chứng từ có thêm mảng chi tiết theo tên bảng chi tiết."));
        b.Append(Para("Require = Y: bắt buộc có giá trị. Default: giá trị dùng khi không gửi trường. Field: cột lưu trong Fast."));

        b.Append(Para("Danh sách form", "Heading1"));
        var sumRows = new List<string> { Row(new[] { "STT", "Form", "Loại", "Endpoint", "Bảng", "Mô tả" }, header: true) };
        var i = 0;
        foreach (var f in forms)
            sumRows.Add(Row(new[] { (++i).ToString(CultureInfo.InvariantCulture), f.Name, f.Voucher ? "SyncVoucher" : "SyncData", "api/" + (f.Voucher ? "SyncVoucher" : "SyncData"), f.Table, f.Title }));
        b.Append(Table(new[] { 550, 2600, 1250, 1600, 1500, 2138 }, sumRows));

        foreach (var f in forms)
        {
            b.Append(Para(f.Name, "Heading1"));
            if (f.Title.Length > 0) b.Append(Para("Mô tả: " + f.Title, keepNext: true));
            b.Append(Para("Endpoint: POST api/" + (f.Voucher ? "SyncVoucher" : "SyncData"), keepNext: true));
            b.Append(Para("Bảng: " + f.Table, keepNext: true));
            b.Append(Para("Store: " + f.Store, keepNext: true));
            b.Append(ColTable(f.Columns));
            foreach (var ct in f.Children)
            {
                b.Append(Para("Chi tiết: " + ct.Name, "Heading2"));
                b.Append(Para("Bảng: " + ct.Table, keepNext: true));
                if (ct.ParentField.Length > 0) b.Append(Para("ParentField: " + ct.ParentField, keepNext: true));
                if (ct.ChildField.Length > 0) b.Append(Para("ChildField: " + ct.ChildField, keepNext: true));
                b.Append(ColTable(ct.Columns));
            }
            b.Append(Para("JSON Request Example", bold: true, keepNext: true));
            var json = Sample(f).Replace("\r\n", "\n").Split('\n');
            b.Append(Table(new[] { 9638 }, new List<string> { "<w:tr><w:tc><w:tcPr><w:tcW w:w=\"9638\" w:type=\"dxa\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F3F4F6\"/></w:tcPr>" +
                string.Concat(json.Select(l => Para(l, mono: true))) + "</w:tc></w:tr>" }));
        }
        WriteZip(path, b.ToString());
    }

    /// <summary>File Word MẪU để người dùng / đối tác điền rồi "Nhập từ Word": phần hướng dẫn + 2 form ví dụ (danh mục, chứng từ có chi tiết) đúng bố cục mà
    /// <see cref="ReadDocx"/> đọc được, mỗi bảng chừa sẵn dòng trống. Tiêu đề "Hướng dẫn…" không phải tên form nên nhập không lấy phần đó.</summary>
    public static void WriteTemplate(string path, DocInfo info)
    {
        var b = new StringBuilder();
        b.Append(Para("TÀI LIỆU API" + (info.Project.Length > 0 ? " — " + info.Project : "") + " (mẫu để điền)", "Title"));
        b.Append(Para($"Chuẩn LT3 (FastAPIService) · mẫu tạo ngày {DateTime.Now:dd/MM/yyyy} bằng Bcode — điền xong dùng Khai báo API → Word ▾ → Nhập từ Word → ColumnMappings.xml.", italic: true));

        b.Append(Para("Hướng dẫn điền", "Heading1"));
        foreach (var line in new[]
        {
            "1. Mỗi form (hàm API) là 1 mục: dòng tiêu đề kiểu Heading 1 = đúng tên form, vd setCustomers (thêm form: chép nguyên 1 mục ví dụ bên dưới rồi sửa).",
            "2. Ngay dưới tiêu đề: \"Mô tả: …\" (tiêu đề form), \"Bảng: <bảng đích trong Fast>\", \"Store: <store đồng bộ, thường fs_sync_<bảng>>\". Thiếu Bảng / Store thì điền lúc nhập.",
            "3. Bảng trường — mỗi dòng 1 trường JSON: Parameter = tên trường JSON (phân biệt hoa / thường); Field = cột lưu trong Fast; Data Type = kiểu SQL (VARCHAR, NVARCHAR, CHAR, NUMERIC, DECIMAL, INT, TINYINT, DATE, DATETIME…); Length = độ dài (vd 64, hoặc 19,4 cho số); Description = mô tả; Require = Y (bắt buộc) / N; Default = giá trị khi không gửi; Note = ghi chú.",
            "4. Chứng từ có chi tiết: thêm tiêu đề Heading 2 \"Chi tiết: <tên mảng JSON>\" (vd Chi tiết: detail), các dòng \"Bảng: <bảng chi tiết>\", \"ParentField: <trường khoá ở phần đầu>\", \"ChildField: <cột khoá ở bảng chi tiết>\", rồi bảng trường như trên. Cột lấy từ phần đầu (không gửi trong JSON): để trống Parameter, Note ghi \"Parent: <trường phần đầu>\".",
            "5. Thêm dòng: đặt con trỏ ở ô cuối bảng rồi bấm Tab. Dòng trống được bỏ qua. Không cần sửa dòng tiêu đề bảng; các mục ví dụ không dùng thì xoá.",
            "6. Khi nhập, Bcode so với ColumnMappings.xml hiện có và cho xem trước: form / cột không có trong Word được giữ nguyên, ô để trống không xoá giá trị cũ.",
        }) b.Append(Para(line));

        var head = new MapForm
        {
            Name = "setDanhMucMau", Title = "Danh mục ví dụ — đổi tên form, bảng, store và các trường", Table = "dmxxx", Store = "fs_sync_dmxxx",
            Columns =
            {
                new MapCol { Param = "YourID", Field = "id_doi_tac", SqlType = "VARCHAR(64)", Required = true, Description = "Khoá chính bên đối tác" },
                new MapCol { Param = "Code", Field = "ma_xxx", SqlType = "VARCHAR(32)", Required = true, Description = "Mã" },
                new MapCol { Param = "Name", Field = "ten_xxx", SqlType = "NVARCHAR(256)", Required = true, Description = "Tên" },
                new MapCol { Param = "Status", Field = "status", SqlType = "CHAR(1)", Required = true, Default = "1", Description = "Trạng thái (1 = dùng)" },
            },
        };
        var voucher = new MapForm
        {
            Name = "setChungTuMau", Title = "Chứng từ ví dụ — phần đầu + chi tiết", Table = "m81", Store = "fs_sync_m81",
            Columns =
            {
                new MapCol { Param = "VoucherID", Field = "id_doi_tac", SqlType = "VARCHAR(64)", Required = true, Description = "Khoá chứng từ bên đối tác" },
                new MapCol { Param = "VoucherDate", Field = "ngay_ct", SqlType = "DATE", Required = true, Description = "Ngày chứng từ" },
                new MapCol { Param = "CustomerCode", Field = "ma_kh", SqlType = "VARCHAR(32)", Required = true, Description = "Mã khách hàng" },
                new MapCol { Param = "TotalAmount", Field = "t_tt_nt", SqlType = "NUMERIC(19,4)", Description = "Tổng tiền" },
            },
            Children =
            {
                new MapChild
                {
                    Name = "detail", Table = "d81", ParentField = "VoucherID", ChildField = "voucher_id",
                    Columns =
                    {
                        new MapCol { Field = "voucher_id", Parent = "VoucherID", SqlType = "VARCHAR(64)", Description = "Khoá chứng từ (tự lấy từ phần đầu)" },
                        new MapCol { Param = "ItemCode", Field = "ma_vt", SqlType = "VARCHAR(32)", Required = true, Description = "Mã vật tư" },
                        new MapCol { Param = "Quantity", Field = "so_luong", SqlType = "NUMERIC(19,4)", Required = true, Description = "Số lượng" },
                        new MapCol { Param = "Price", Field = "gia_nt2", SqlType = "NUMERIC(19,4)", Description = "Đơn giá" },
                    },
                },
            },
        };
        foreach (var f in new[] { head, voucher })
        {
            b.Append(Para(f.Name, "Heading1"));
            b.Append(Para("Mô tả: " + f.Title, keepNext: true));
            b.Append(Para("Endpoint: POST api/" + (f.Voucher ? "SyncVoucher" : "SyncData"), keepNext: true));
            b.Append(Para("Bảng: " + f.Table, keepNext: true));
            b.Append(Para("Store: " + f.Store, keepNext: true));
            b.Append(ColTable(f.Columns, blankRows: 6));
            foreach (var ct in f.Children)
            {
                b.Append(Para("Chi tiết: " + ct.Name, "Heading2"));
                b.Append(Para("Bảng: " + ct.Table, keepNext: true));
                b.Append(Para("ParentField: " + ct.ParentField, keepNext: true));
                b.Append(Para("ChildField: " + ct.ChildField, keepNext: true));
                b.Append(ColTable(ct.Columns, blankRows: 6));
            }
        }
        WriteZip(path, b.ToString());
    }

    private static string ColTable(List<MapCol> cols, int blankRows = 0)
    {
        var rows = new List<string> { Row(Heads, header: true) };
        foreach (var c in cols)
        {
            var (type, len) = SplitType(c.SqlType.Length > 0 ? c.SqlType : c.DataType);
            var note = new List<string>();
            if (c.Parent.Length > 0) note.Add("Parent: " + c.Parent);
            if (c.DataType.Length > 0) note.Add("DataType: " + c.DataType);
            rows.Add(Row(new[] { c.Param, c.Field, type, len, c.Description, c.Parent.Length > 0 ? "" : c.Required ? "Y" : "N", c.Default ?? "", string.Join("; ", note) }));
        }
        for (var i = 0; i < blankRows; i++) rows.Add(Row(Enumerable.Repeat("", Heads.Length).ToList()));   // dòng trống để điền (nhập bỏ qua dòng trống)
        return Table(Widths, rows);
    }

    /// <summary>"VARCHAR(64)" → ("VARCHAR", "64"); "DECIMAL(19,4)" → ("DECIMAL", "19,4"); "TINYINT" → ("TINYINT", "").</summary>
    private static (string Type, string Len) SplitType(string t)
    {
        t = t.Trim();
        var m = Regex.Match(t, @"^\s*([A-Za-z0-9_ ]+?)\s*\(\s*([^)]*)\)\s*$");
        return m.Success ? (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.Replace(" ", "")) : (t.ToUpperInvariant(), "");
    }

    /// <summary>JSON mẫu cho tài liệu (giá trị minh hoạ như tài liệu mẫu: mặc định của mapping, ngày yyyy-MM-dd, số 0, chữ value_&lt;Param&gt;).</summary>
    public static string Sample(MapForm f)
    {
        var row = SampleRow(f.Columns);
        foreach (var ct in f.Children) row[ct.Name] = new JsonArray(SampleRow(ct.Columns));
        return new JsonObject { ["form"] = f.Name, ["data"] = new JsonArray(row) }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static JsonObject SampleRow(List<MapCol> cols)
    {
        var o = new JsonObject();
        foreach (var c in cols.Where(c => c.Parent.Length == 0 && c.Param.Length > 0))
        {
            if (o.ContainsKey(c.Param)) continue;
            var k = Kind(c.SqlType.Length > 0 ? c.SqlType : c.DataType);
            o[c.Param] = c.Default is { Length: > 0 } d ? (k == 'n' && decimal.TryParse(d, NumberStyles.Number, CultureInfo.InvariantCulture, out var dv) ? JsonValue.Create(dv) : JsonValue.Create(d))
                : k == 'd' ? JsonValue.Create(DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : string.Equals(c.Param, "Status", StringComparison.OrdinalIgnoreCase) ? (k == 'n' ? JsonValue.Create(1) : JsonValue.Create("1"))
                : k == 'n' ? JsonValue.Create(0)
                : JsonValue.Create("value_" + c.Param);
        }
        return o;
    }

    /// <summary>'d' ngày, 'n' số, 's' chữ.</summary>
    private static char Kind(string sqlType)
    {
        var s = SplitType(sqlType).Type;
        if (s is "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "DATETIMEOFFSET") return 'd';
        if (s is "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" or "FLOAT" or "REAL" or "INT" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" or "BIT" or "LONG" or "DOUBLE" or "BYTE" or "INT32" or "INT64") return 'n';
        return 's';
    }

    private static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    private static string Para(string text, string style = "", bool bold = false, bool italic = false, bool mono = false, bool keepNext = false)
    {
        var inner = (style.Length > 0 ? $"<w:pStyle w:val=\"{style}\"/>" : "") + (keepNext ? "<w:keepNext/>" : "") + (mono ? "<w:spacing w:before=\"0\" w:after=\"0\"/>" : "");
        var ppr = inner.Length > 0 ? "<w:pPr>" + inner + "</w:pPr>" : "";
        var rpr = (bold ? "<w:b/>" : "") + (italic ? "<w:i/>" : "") + (mono ? "<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"18\"/>" : "");
        return $"<w:p>{ppr}<w:r>{(rpr.Length > 0 ? "<w:rPr>" + rpr + "</w:rPr>" : "")}<w:t xml:space=\"preserve\">{X(text)}</w:t></w:r></w:p>";
    }

    private static string Row(IReadOnlyList<string> cells, bool header = false)
    {
        var sb = new StringBuilder("<w:tr>");
        if (header) sb.Append("<w:trPr><w:tblHeader/></w:trPr>");
        foreach (var c in cells)
        {
            sb.Append("<w:tc>");
            if (header) sb.Append("<w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"D9E2F3\"/></w:tcPr>");
            sb.Append("<w:p><w:pPr><w:spacing w:before=\"20\" w:after=\"20\"/></w:pPr><w:r><w:rPr>").Append(header ? "<w:b/>" : "").Append("<w:sz w:val=\"19\"/></w:rPr><w:t xml:space=\"preserve\">")
              .Append(X(c)).Append("</w:t></w:r></w:p></w:tc>");
        }
        return sb.Append("</w:tr>").ToString();
    }

    private static string Table(int[] widths, List<string> rows)
    {
        const string border = "w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"8EA0B8\"";
        return "<w:tbl><w:tblPr><w:tblW w:w=\"" + widths.Sum() + "\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders>" +
               $"<w:top {border}/><w:left {border}/><w:bottom {border}/><w:right {border}/><w:insideH {border}/><w:insideV {border}/></w:tblBorders>" +
               "<w:tblCellMar><w:left w:w=\"70\" w:type=\"dxa\"/><w:right w:w=\"70\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>" +
               string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>")) + "</w:tblGrid>" + string.Concat(rows) + "</w:tbl>" + Para("");
    }

    private static void WriteZip(string path, string body)
    {
        const string ns = "xmlns:w=\"" + W + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {ns}><w:body>{body}" +
                       "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"709\" w:footer=\"709\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
        static string Heading(string id, string name, int lvl, int size, string color, int before) =>
            $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
            $"<w:pPr><w:keepNext/><w:spacing w:before=\"{before}\" w:after=\"80\"/>{(lvl >= 0 ? $"<w:outlineLvl w:val=\"{lvl}\"/>" : "")}</w:pPr><w:rPr><w:b/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>";
        var styles = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles {ns}><w:docDefaults><w:rPrDefault><w:rPr>" +
                     "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:cs=\"Arial\" w:eastAsia=\"Arial\"/><w:sz w:val=\"21\"/><w:szCs w:val=\"21\"/><w:lang w:val=\"vi-VN\"/></w:rPr></w:rPrDefault>" +
                     "<w:pPrDefault><w:pPr><w:spacing w:after=\"60\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                     "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
                     Heading("Title", "Title", -1, 36, "1F3864", 0) + Heading("Heading1", "heading 1", 0, 28, "1F4E79", 360) + Heading("Heading2", "heading 2", 1, 23, "2E74B5", 200) + "</w:styles>";
        const string types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                             "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                             "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                             "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>";
        const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
        const string docRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                               "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                var e = zip.CreateEntry(name);
                using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
                s.Write(content);
            }
            Add("[Content_Types].xml", types);
            Add("_rels/.rels", rels);
            Add("word/document.xml", document);
            Add("word/styles.xml", styles);
            Add("word/_rels/document.xml.rels", docRels);
        }
        File.Move(tmp, path, overwrite: true);   // file đang mở trong Word → báo lỗi, không để file hỏng nửa chừng
    }

    // ================================================================ nhập Word
    private static readonly XNamespace Wn = W;

    /// <summary>Đọc tài liệu API (.docx) → các form. <paramref name="notes"/>: bảng / form không nhận ra, cột thiếu Field…</summary>
    public static List<MapForm> ReadDocx(string path, List<string> notes)
    {
        XDocument doc, styles = new();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var e = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("File không phải .docx hợp lệ (thiếu word/document.xml). File .doc cũ: mở bằng Word rồi Lưu thành .docx.");
            using (var s = e.Open()) doc = XDocument.Load(s);
            var se = zip.GetEntry("word/styles.xml");
            if (se is not null) using (var s = se.Open()) styles = XDocument.Load(s);
        }

        // styleId → mức tiêu đề (Heading n / outlineLvl); tài liệu tiếng Việt có styleId kiểu "u1" nên tra theo tên + outlineLvl của style.
        var headingOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in styles.Root?.Elements(Wn + "style") ?? Enumerable.Empty<XElement>())
        {
            var id = (string?)st.Attribute(Wn + "styleId") ?? "";
            var name = ((string?)st.Element(Wn + "name")?.Attribute(Wn + "val") ?? "").ToLowerInvariant();
            var lvl = (string?)st.Element(Wn + "pPr")?.Element(Wn + "outlineLvl")?.Attribute(Wn + "val");
            var m = Regex.Match(name, @"^heading\s*(\d)");
            if (m.Success) headingOf[id] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (int.TryParse(lvl, out var l) && l < 9) headingOf[id] = l + 1;
        }
        int HeadingLevel(XElement p)
        {
            var ppr = p.Element(Wn + "pPr");
            if (int.TryParse((string?)ppr?.Element(Wn + "outlineLvl")?.Attribute(Wn + "val"), out var l) && l < 9) return l + 1;
            var sid = (string?)ppr?.Element(Wn + "pStyle")?.Attribute(Wn + "val") ?? "";
            if (headingOf.TryGetValue(sid, out var h)) return h;
            var m = Regex.Match(sid, @"^Heading(\d)$", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        var forms = new List<MapForm>();
        MapForm? cur = null;
        MapChild? child = null;
        var body = doc.Root?.Element(Wn + "body");
        if (body is null) return forms;

        foreach (var el in Blocks(body))
        {
            if (el.Name == Wn + "p")
            {
                var text = ParaText(el).Trim();
                if (text.Length == 0) continue;
                var level = HeadingLevel(el);
                var cm = ChildRx.Match(text);
                if (cm.Success && cur is not null && (level > 0 || text.Length < 80))
                {
                    child = new MapChild { Name = cm.Groups["n"].Value.Trim() };
                    cur.Children.Add(child);
                    continue;
                }
                var formName = level > 0 ? NameFromHeading(text) : NameFromLine(text);
                if (formName is not null)
                {
                    cur = new MapForm { Name = formName };
                    forms.Add(cur); child = null;
                    continue;
                }
                if (level > 0) { cur = null; child = null; continue; }   // tiêu đề khác (Thông tin chung, Danh sách form…) — ngắt form đang đọc
                if (cur is null) continue;
                var kv = KvRx.Match(text);
                if (!kv.Success) continue;
                var key = Norm(kv.Groups["k"].Value);
                var val = kv.Groups["v"].Value.Trim();
                if (key is "bang" or "table" or "bang db" or "bang du lieu") { if (child is not null) child.Table = FirstWord(val); else cur.Table = FirstWord(val); }
                else if (key is "store" or "store procedure" or "stored procedure" && child is null) cur.Store = FirstWord(val);
                else if (key is "parentfield" or "parent field" or "khoa cha" && child is not null) child.ParentField = FirstWord(val);
                else if (key is "childfield" or "child field" or "khoa con" && child is not null) child.ChildField = FirstWord(val);
                else if (key is "mo ta" or "description" or "tieu de" && child is null && cur.Title.Length == 0) cur.Title = val;
            }
            else if (el.Name == Wn + "tbl")
            {
                var rows = TableRows(el);
                if (rows.Count == 0 || rows.Max(r => r.Count) < 2) continue;   // bảng 1 cột = JSON mẫu
                var hi = -1; Dictionary<string, int>? map = null;
                for (var r = 0; r < Math.Min(4, rows.Count) && map is null; r++) { var m = HeaderMap(rows[r]); if (m is not null) { hi = r; map = m; } }
                if (map is null) continue;   // không phải bảng trường (danh sách form, mã lỗi…)
                if (cur is null) { notes.Add("Bỏ qua 1 bảng trường không nằm dưới tiêu đề form nào (dòng đầu: " + string.Join(" | ", rows[hi].Select(c => c.Text)) + ")."); continue; }
                if (!map.ContainsKey("field")) { notes.Add($"{cur.Name}{(child is null ? "" : "." + child.Name)}: bảng không có cột Field / CRM Value (cột Fast) — bỏ qua, mapping cần biết cột lưu trong Fast."); continue; }
                var target = child is not null && child.Columns.Count == 0 ? child.Columns : cur.Columns.Count == 0 && child is null ? cur.Columns : null;
                if (target is null)
                {
                    // bảng thứ 2 dưới cùng 1 form mà không có dòng "Chi tiết: <tên>" → coi là bảng chi tiết, đặt tên tạm
                    child = new MapChild { Name = "detail" + (cur.Children.Count == 0 ? "" : (cur.Children.Count + 1).ToString(CultureInfo.InvariantCulture)) };
                    cur.Children.Add(child); target = child.Columns;
                    notes.Add($"{cur.Name}: có thêm bảng trường không ghi tên chi tiết — tạm đặt \"{child.Name}\" (sửa trong Word: thêm dòng \"Chi tiết: <tên>\" trước bảng).");
                }
                for (var r = hi + 1; r < rows.Count; r++)
                {
                    var c = ReadRow(rows[r], map, out var skip);
                    if (c is not null) target.Add(c);
                    else if (skip is not null) notes.Add($"{cur.Name}{(child is null ? "" : "." + child.Name)}: {skip}");
                }
            }
        }
        // form trùng tên trong Word: gộp vào bản đầu
        var result = new List<MapForm>();
        foreach (var f in forms.Where(f => f.Columns.Count > 0 || f.Children.Any(c => c.Columns.Count > 0)))
        {
            var dup = result.FirstOrDefault(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase));
            if (dup is null) { f.Children.RemoveAll(c => c.Columns.Count == 0); result.Add(f); continue; }
            notes.Add($"Form {f.Name} xuất hiện nhiều lần trong Word — dùng bản đầu.");
        }
        foreach (var f in forms.Where(f => f.Columns.Count == 0 && f.Children.All(c => c.Columns.Count == 0)))
            if (!result.Any(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase))) notes.Add($"Tiêu đề \"{f.Name}\" không có bảng trường — bỏ qua.");
        return result;
    }

    private static readonly Regex ChildRx = new(@"^(?:chi\s*ti[eế]t|detail|child\s*table|b[aả]ng\s*con|b[aả]ng\s*chi\s*ti[eế]t)\s*[:\-–]\s*(?<n>[A-Za-z_][\w\-]*)", RegexOptions.IgnoreCase);
    private static readonly Regex KvRx = new(@"^(?<k>[^:：]{2,30})[:：]\s*(?<v>.*)$");
    private static readonly Regex IdRx = new(@"[A-Za-z_][\w\-]*");

    /// <summary>Tên form từ tiêu đề: "setTradingfloor", "1. setItems", "Hàm setItems", "Form setX — Danh mục…" → token đầu dạng setXxx/getXxx/updateXxx, không thì 1 từ duy nhất.</summary>
    private static string? NameFromHeading(string text)
    {
        text = Regex.Replace(text, @"^[\d.\s)]+", "").Trim();
        var ids = IdRx.Matches(text).Select(m => m.Value).ToList();
        var verb = ids.FirstOrDefault(IsFormName);
        if (verb is not null) return verb;
        return ids.Count == 1 && text == ids[0] && ids[0].Length > 2 ? ids[0] : null;
    }

    /// <summary>Dòng thường chỉ coi là tên form khi đúng dạng "Hàm setXxx" / "Form setXxx" / "API setXxx" (tài liệu không dùng Heading).</summary>
    private static string? NameFromLine(string text)
    {
        var m = Regex.Match(text, @"^(?:h[aà]m|form|api)\s+([A-Za-z_][\w\-]*)\s*$", RegexOptions.IgnoreCase);
        return m.Success && IsFormName(m.Groups[1].Value) ? m.Groups[1].Value : null;
    }

    private static bool IsFormName(string s) => Regex.IsMatch(s, @"^(set|get|update|sync|del|delete|post)[A-Z0-9_\-]", RegexOptions.None) || Regex.IsMatch(s, @"^(Set|Get|Update)[A-Z]");

    private static string FirstWord(string s) { s = s.Trim(); var i = s.IndexOfAny(new[] { ' ', '\t', ',', ';', '(' }); return (i < 0 ? s : s[..i]).Trim(); }

    /// <summary>Các khối thân tài liệu theo thứ tự (đi xuyên w:sdt — mục lục / khối nội dung).</summary>
    private static IEnumerable<XElement> Blocks(XElement parent)
    {
        foreach (var e in parent.Elements())
        {
            if (e.Name == Wn + "p" || e.Name == Wn + "tbl") yield return e;
            else if (e.Name == Wn + "sdt") { var c = e.Element(Wn + "sdtContent"); if (c is not null) foreach (var x in Blocks(c)) yield return x; }
        }
    }

    private static string ParaText(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var n in p.Descendants())
        {
            if (n.Name == Wn + "t") sb.Append(n.Value);
            else if (n.Name == Wn + "tab") sb.Append('\t');
            else if (n.Name == Wn + "br" || n.Name == Wn + "cr") sb.Append('\n');
            else if (n.Name == Wn + "noBreakHyphen") sb.Append('-');
        }
        return sb.ToString().Replace(' ', ' ');
    }

    private sealed record Cell(int Col, string Text);

    /// <summary>Các dòng của bảng; mỗi ô kèm cột lưới bắt đầu (tính gridSpan) để khớp tiêu đề kể cả khi ô bị gộp.</summary>
    private static List<List<Cell>> TableRows(XElement tbl)
    {
        var rows = new List<List<Cell>>();
        foreach (var tr in tbl.Elements(Wn + "tr"))
        {
            var row = new List<Cell>(); var col = 0;
            foreach (var tc in tr.Elements().SelectMany(e => e.Name == Wn + "sdt" ? e.Element(Wn + "sdtContent")?.Elements() ?? Enumerable.Empty<XElement>() : new[] { e }).Where(e => e.Name == Wn + "tc"))
            {
                var span = int.TryParse((string?)tc.Element(Wn + "tcPr")?.Element(Wn + "gridSpan")?.Attribute(Wn + "val"), out var s) && s > 0 ? s : 1;
                var text = string.Join("\n", tc.Elements(Wn + "p").Select(ParaText)).Trim();
                row.Add(new Cell(col, text)); col += span;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Bỏ dấu, thường hoá, gộp khoảng trắng — so tiêu đề cột / khoá dòng.</summary>
    private static string Norm(string s)
    {
        var d = s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return Regex.Replace(sb.ToString().ToLowerInvariant().Replace('_', ' ').Replace('\n', ' '), @"\s+", " ").Trim().TrimEnd('*', ':').Trim();
    }

    private static readonly (string Key, string[] Names)[] HeaderNames =
    {
        ("param", new[] { "parameter", "parameters", "param", "tham so", "ten tham so", "ten truong", "truong api", "ten truong api", "key", "json", "json key", "api field" }),
        ("field", new[] { "crm value", "field", "fast field", "fast value", "cot", "ten cot", "cot fast", "truong fast", "column", "mapping", "field name", "fast", "erp field", "cot du lieu", "truong du lieu" }),
        ("type", new[] { "data type", "datatype", "type", "kieu", "kieu du lieu", "sql type", "sql data type" }),
        ("len", new[] { "length", "len", "do dai", "size", "kich thuoc" }),
        ("desc", new[] { "description", "mo ta", "dien giai", "y nghia", "noi dung" }),
        ("req", new[] { "require", "required", "bat buoc", "mandatory", "req" }),
        ("def", new[] { "default", "default value", "mac dinh", "gia tri mac dinh" }),
        ("note", new[] { "note", "notes", "ghi chu", "remark", "remarks" }),
    };

    private static Dictionary<string, int>? HeaderMap(List<Cell> row)
    {
        var map = new Dictionary<string, int>();
        foreach (var c in row)
        {
            var n = Norm(c.Text);
            foreach (var (key, names) in HeaderNames)
                if (!map.ContainsKey(key) && names.Contains(n)) { map[key] = c.Col; break; }
        }
        return map.ContainsKey("param") && map.Count >= 2 ? map : null;
    }

    private static MapCol? ReadRow(List<Cell> row, Dictionary<string, int> map, out string? skip)
    {
        skip = null;
        string G(string k) => map.TryGetValue(k, out var col) ? row.FirstOrDefault(c => c.Col == col)?.Text.Trim() ?? "" : "";
        var param = G("param"); var field = G("field"); var type = G("type"); var note = G("note");
        if (param.Length == 0 && field.Length == 0) return null;
        if (Norm(param) is "parameter" or "tham so") return null;   // tiêu đề lặp lại (bảng qua trang)
        param = param.Split('\n')[0].Trim(); field = field.Split('\n')[0].Trim();
        if (Regex.IsMatch(type, @"list|array|object|mang|\[\]", RegexOptions.IgnoreCase) && field.Length == 0) return null;   // dòng "detail: List[Object]" — chi tiết mô tả ở bảng riêng
        var c = new MapCol { Param = param, Field = field, Description = G("desc") };
        var pm = Regex.Match(note, @"(?:parent|l[aấ]y\s*t[uừ]\s*cha)\s*[:=]\s*([A-Za-z_]\w*)", RegexOptions.IgnoreCase);
        if (pm.Success) c.Parent = pm.Groups[1].Value;
        var dm = Regex.Match(note, @"datatype\s*[:=]\s*([A-Za-z]+)", RegexOptions.IgnoreCase);
        if (dm.Success) c.DataType = dm.Groups[1].Value;
        if (c.Field.Length == 0) { skip = $"bỏ cột {param} — thiếu Field (cột Fast)."; return null; }
        if (c.Param.Length == 0 && c.Parent.Length == 0) { skip = $"bỏ cột Field {field} — thiếu Parameter."; return null; }
        c.SqlType = SqlType(type, G("len"));
        if (map.ContainsKey("req")) { c.RequiredSet = true; c.Required = Yes(G("req")); }
        if (map.ContainsKey("def")) { var d = G("def"); if (d.Length > 0) c.Default = d; }
        return c;
    }

    private static bool Yes(string s) => Norm(s) is "y" or "yes" or "x" or "co" or "true" or "1" or "✔" or "✓" or "v" or "bat buoc" or "required";

    /// <summary>Kiểu + độ dài trong Word → SqlDataType: "VARCHAR"+"64" → VARCHAR(64); "NUMERIC"+"19,4" → NUMERIC(19,4); "String"+"16" → NVARCHAR(16).</summary>
    private static string SqlType(string type, string len)
    {
        type = type.Trim(); len = len.Trim().Replace(" ", "").Replace('.', ',');
        if (type.Length == 0) return "";
        if (type.Contains('(')) return type.ToUpperInvariant().Replace(" ", "");
        var t = Norm(type) switch
        {
            // chỉ đổi tên kiểu kiểu .NET / tiếng Việt; tên kiểu SQL (VARCHAR, DECIMAL, NUMERIC, MONEY…) giữ nguyên
            "string" or "chuoi" => "NVARCHAR",
            "integer" or "int32" or "so nguyen" => "INT",
            "long" or "int64" => "BIGINT",
            "number" or "double" or "so" => "NUMERIC",
            "date time" => "DATETIME",
            "ngay" => "DATE",
            "bool" or "boolean" => "BIT",
            _ => type.ToUpperInvariant().Replace(" ", ""),
        };
        if (t is "NUMERIC" or "DECIMAL" && len.Length == 0) len = "19,4";
        if (t is "NVARCHAR" or "VARCHAR" or "CHAR" or "NCHAR" && len.Length == 0) len = "512";
        var sized = t is "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" or "NUMERIC" or "DECIMAL" or "VARBINARY" or "BINARY";
        return sized && len.Length > 0 && Regex.IsMatch(len, @"^(\d+(,\d+)?|max)$", RegexOptions.IgnoreCase) ? $"{t}({len.ToUpperInvariant()})" : t;
    }

    // ================================================================ so sánh + ghép vào ColumnMappings.xml
    /// <summary>So các form của Word với mapping hiện có (<paramref name="baseXml"/> rỗng = chưa có file).</summary>
    public static List<FormDiff> Diff(string? baseXml, IEnumerable<MapForm> forms)
    {
        var list = new List<FormDiff>();
        foreach (var f in forms)
        {
            var doc = Load(baseXml);
            var log = new Log();
            var isNew = Apply(doc, f, log);
            var el = FindForm(doc.Root!, f.Name)!;
            var table = A(el, "table"); var store = A(el, "store");
            var errors = new List<string>();
            if (table.Length == 0) errors.Add("thiếu table (bảng đích) — service bỏ qua form không có table");
            if (store.Length == 0) errors.Add("thiếu store (fs_sync_…) — service bỏ qua form không có store");
            list.Add(new FormDiff(f.Name, isNew ? "new" : log.Changes.Count > 0 ? "changed" : "same", table, store, f.Title.Length > 0 ? f.Title : Title(el), f.Voucher || el.Element("ChildTables") is not null,
                f.Columns.Count + f.Children.Sum(c => c.Columns.Count), log.Changes, log.Kept, log.Warnings, errors));
        }
        return list;
    }

    /// <summary>Ghép các form vào mapping, trả về nội dung file mới (giữ BOM / khai báo / thụt lề / chú thích của file gốc).</summary>
    public static string Merge(string? baseXml, IEnumerable<MapForm> forms, out List<string> changes)
    {
        var doc = Load(baseXml);
        var originals = doc.Descendants("Form").ToList();
        var snap = originals.Select(e => e.ToString(SaveOptions.DisableFormatting)).ToList();
        var log = new Log();
        foreach (var f in forms) { log.Prefix = f.Name + ": "; Apply(doc, f, log); }
        changes = log.Changes;
        if (string.IsNullOrWhiteSpace(baseXml)) return Save(doc);
        var nlText = baseXml.Contains("\r\n") ? "\r\n" : "\n";
        try { return Splice(baseXml, doc, originals, snap, nlText) ?? Nl(Save(doc), nlText); }
        catch { return Nl(Save(doc), nlText); }
    }

    /// <summary>Trình đọc XML đổi mọi xuống dòng thành \n — trả lại kiểu xuống dòng của file gốc (Windows: \r\n).</summary>
    private static string Nl(string s, string nl) => nl == "\n" ? s.Replace("\r\n", "\n") : s.Replace("\r\n", "\n").Replace("\n", nl);

    /// <summary>Dấu cho chú thích tiêu đề do Bcode thêm (phân biệt với chú thích sẵn có trong file).</summary>
    private sealed class NewMark { }

    /// <summary>Ghép bằng cách vá văn bản gốc: chỉ thay đúng đoạn &lt;Form&gt;…&lt;/Form&gt; có thay đổi, form mới chèn trước &lt;/Mappings&gt; — mọi chỗ khác của file
    /// (khoảng trắng trong thẻ, dấu nháy, "&gt;" chưa escape, chú thích…) giữ nguyên từng ký tự để so sánh / diff bản cũ – mới dễ đọc. null = không vá được.</summary>
    private static string? Splice(string text, XDocument doc, List<XElement> originals, List<string> snap, string nl)
    {
        var (spans, rootEnd) = FormSpans(text);
        if (spans.Count != originals.Count || rootEnd < 0) return null;
        var unit = Style(doc).Unit;
        var sb = new StringBuilder();
        var pos = 0;
        for (var i = 0; i < originals.Count; i++)
        {
            var now = originals[i].ToString(SaveOptions.DisableFormatting);
            if (now == snap[i]) continue;
            var (start, end) = spans[i];
            if (end < 0 || start < pos) return null;
            sb.Append(text, pos, start - pos).Append(KeepLines(text[start..end], Nl(now, nl), nl));
            pos = end;
        }
        var added = doc.Descendants("Form").Where(e => !originals.Contains(e)).ToList();
        var ins = rootEnd;
        while (ins > pos && char.IsWhiteSpace(text[ins - 1])) ins--;
        sb.Append(text, pos, ins - pos);
        foreach (var f in added)
        {
            sb.Append(nl).Append(nl).Append(unit);
            if (f.PreviousNode is XText { PreviousNode: XComment c } && c.Annotation<NewMark>() is not null) sb.Append(c.ToString()).Append(nl).Append(unit);
            sb.Append(Nl(f.ToString(SaveOptions.DisableFormatting), nl));
        }
        sb.Append(text, ins, text.Length - ins);
        return sb.ToString();
    }

    /// <summary>Dòng của form mới viết lại mà nội dung không đổi (chỉ khác cách viết: "/&gt; hay " /&gt;, &amp;gt; hay &gt;, số khoảng trắng) → dùng lại nguyên văn dòng gốc,
    /// để bản ghi chỉ khác đúng các dòng thật sự sửa / thêm.</summary>
    private static string KeepLines(string old, string rep, string nl)
    {
        static string Key(string l) => Regex.Replace(l.Replace("&gt;", ">").Replace("\" />", "\"/>").Replace("'", "\""), @"\s+", " ").Trim();
        var pool = new Dictionary<string, Queue<string>>();
        foreach (var l in old.Split(nl))
        {
            var k = Key(l);
            if (!pool.TryGetValue(k, out var q)) pool[k] = q = new Queue<string>();
            q.Enqueue(l);
        }
        return string.Join(nl, rep.Split(nl).Select(l => pool.TryGetValue(Key(l), out var q) && q.Count > 0 ? q.Dequeue() : l));
    }

    /// <summary>Vị trí (ký tự) của từng &lt;Form&gt; trong văn bản theo thứ tự tài liệu (End = -1: thẻ rỗng) và của &lt;/Mappings&gt;.</summary>
    private static (List<(int Start, int End)> Forms, int RootEnd) FormSpans(string text)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);
        using var r = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var li = (IXmlLineInfo)r;
        int Off() => lineStarts[li.LineNumber - 1] + li.LinePosition - 1;   // vị trí ký tự đầu của tên thẻ
        var forms = new List<(int, int)>();
        var open = new Stack<int>();
        var rootEnd = -1;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "Form")
            {
                forms.Add((Off() - 1, -1));
                if (!r.IsEmptyElement) open.Push(forms.Count - 1);
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                if (r.LocalName == "Form" && open.Count > 0) { var i = open.Pop(); forms[i] = (forms[i].Item1, text.IndexOf('>', Off()) + 1); }
                else if (r.Depth == 0) rootEnd = Off() - 2;
            }
        }
        return (forms, rootEnd);
    }

    /// <summary>XML của riêng các form (kèm chú thích tiêu đề) lấy từ nội dung đã ghép — để chép dán tay.</summary>
    public static string Fragment(string mergedXml, IEnumerable<string> names)
    {
        var doc = XDocument.Parse(mergedXml, LoadOptions.PreserveWhitespace);
        var sb = new StringBuilder();
        foreach (var n in names)
        {
            var el = FindForm(doc.Root!, n);
            if (el is null) continue;
            var t = Title(el);
            if (t.Length > 0) sb.Append("\t<!-- ").Append(t).Append(" -->\r\n");
            sb.Append('\t').Append(Nl(el.ToString(SaveOptions.DisableFormatting), "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString().TrimEnd() + "\r\n";
    }

    private sealed class Log
    {
        public string Prefix = "";
        public readonly List<string> Changes = new(), Kept = new(), Warnings = new();
        public void C(string s) => Changes.Add(Prefix + s);
    }

    private static XDocument Load(string? xml) =>
        string.IsNullOrWhiteSpace(xml) ? new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Mappings", new XText("\r\n")))
        : XDocument.Parse(xml, LoadOptions.PreserveWhitespace);

    private static string Save(XDocument doc)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { OmitXmlDeclaration = doc.Declaration is null, Indent = false, NewLineHandling = NewLineHandling.None, Encoding = Encoding.UTF8 };
        using (var sw = new Utf8StringWriter(sb))
        using (var w = XmlWriter.Create(sw, settings)) doc.Save(w);
        var s = sb.ToString();
        // XmlWriter bỏ mất xuống dòng giữa khai báo và <Mappings> khi khoảng trắng đó không nằm trong cây
        if (doc.Declaration is not null && s.StartsWith("<?xml", StringComparison.Ordinal))
        {
            var end = s.IndexOf("?>", StringComparison.Ordinal) + 2;
            if (end > 1 && end < s.Length && s[end] == '<') s = s[..end] + "\r\n" + s[end..];
        }
        return s;
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder sb) : base(sb, CultureInfo.InvariantCulture) { }
        public override Encoding Encoding => Encoding.UTF8;
    }

    private static XElement? FindForm(XElement root, string name) =>
        root.Descendants("Form").FirstOrDefault(e => string.Equals(A(e, "name"), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Kiểu xuống dòng + đơn vị thụt lề của file (lấy theo khoảng trắng trước Form / Column đầu tiên).</summary>
    private static (string Nl, string Unit) Style(XDocument doc)
    {
        var ws = doc.Root!.Nodes().OfType<XText>().Select(t => t.Value).FirstOrDefault(v => v.Contains('\n')) ?? "\r\n\t";
        var nl = ws.Contains("\r\n") ? "\r\n" : "\n";
        var unit = ws[(ws.LastIndexOf('\n') + 1)..];
        return (nl, unit.Length == 0 || unit.Trim().Length > 0 ? "\t" : unit);
    }

    private static string IndentOf(XElement e, string unit)
    {
        if (e.PreviousNode is XText t && t.Value.Contains('\n')) return t.Value[(t.Value.LastIndexOf('\n') + 1)..];
        return string.Concat(Enumerable.Repeat(unit, e.Ancestors().Count()));
    }

    /// <summary>Chèn khoảng trắng thụt lề vào phần tử mới tạo (để file vẫn đẹp như viết tay).</summary>
    private static void Format(XElement e, string nl, string indent, string unit)
    {
        var kids = e.Elements().ToList();
        if (kids.Count == 0) return;
        foreach (var k in kids) { k.AddBeforeSelf(new XText(nl + indent + unit)); Format(k, nl, indent + unit, unit); }
        e.Add(new XText(nl + indent));
    }

    /// <summary>Thêm phần tử con mới vào cuối <paramref name="parent"/> (sau phần tử con cuối, đúng thụt lề).</summary>
    private static void Append(XElement parent, XElement child, string nl, string unit)
    {
        var last = parent.Elements().LastOrDefault();
        var indent = last is not null ? IndentOf(last, unit) : IndentOf(parent, unit) + unit;
        Format(child, nl, indent, unit);
        if (last is not null) last.AddAfterSelf(new XText(nl + indent), child);
        else
        {
            foreach (var t in parent.Nodes().OfType<XText>().ToList()) t.Remove();
            parent.Add(new XText(nl + indent), child, new XText(nl + IndentOf(parent, unit)));
        }
    }

    private static XElement NewColumn(MapCol c)
    {
        var e = new XElement("Column");
        if (c.Param.Length > 0) e.SetAttributeValue("Param", c.Param);
        e.SetAttributeValue("Field", c.Field);
        if (c.SqlType.Length > 0) e.SetAttributeValue("SqlDataType", c.SqlType);
        if (c.Required && c.Parent.Length == 0) e.SetAttributeValue("Required", "true");
        if (c.Default is { Length: > 0 }) e.SetAttributeValue("Default", c.Default);
        var dt = c.DataType.Length > 0 ? c.DataType : SplitType(c.SqlType).Type is "DECIMAL" or "NUMERIC" or "MONEY" or "FLOAT" ? "decimal" : "";   // như mapping mẫu: cột số tiền khai DataType="decimal"
        if (dt.Length > 0) e.SetAttributeValue("DataType", dt);
        if (c.Parent.Length > 0) e.SetAttributeValue("Parent", c.Parent);
        e.SetAttributeValue("Description", c.Description);
        return e;
    }

    /// <summary>Áp 1 form vào tài liệu; true = form mới.</summary>
    private static bool Apply(XDocument doc, MapForm f, Log log)
    {
        var root = doc.Root!;
        var (nl, unit) = Style(doc);
        var el = FindForm(root, f.Name);
        if (el is null)
        {
            el = new XElement("Form", new XAttribute("name", f.Name), new XAttribute("table", f.Table), new XAttribute("store", f.Store),
                new XElement("Columns", f.Columns.Select(NewColumn)));
            if (f.Children.Count > 0)
                el.Add(new XElement("ChildTables", f.Children.Select(ct => new XElement("ChildTable", new XAttribute("name", ct.Name), new XAttribute("table", ct.Table),
                    new XElement("ParentField", ct.ParentField), new XElement("ChildField", ct.ChildField), new XElement("Columns", ct.Columns.Select(NewColumn))))));
            Format(el, nl, unit, unit);
            var tail = root.LastNode as XText;
            var nodes = new List<object> { new XText(nl + nl + unit) };
            if (f.Title.Length > 0) { var cm = new XComment(" " + f.Title.Replace("--", "—") + " "); cm.AddAnnotation(new NewMark()); nodes.Add(cm); nodes.Add(new XText(nl + unit)); }
            nodes.Add(el);
            if (tail is not null && tail.Value.Trim().Length == 0) tail.AddBeforeSelf(nodes.ToArray());
            else { root.Add(nodes.ToArray()); root.Add(new XText(nl)); }
            log.C($"thêm form mới ({(f.Voucher ? "chứng từ, " + f.Children.Count + " bảng chi tiết, " : "")}{f.Columns.Count + f.Children.Sum(c => c.Columns.Count)} cột)");
            return true;
        }

        if (f.Table.Length > 0 && !string.Equals(A(el, "table"), f.Table, StringComparison.OrdinalIgnoreCase)) { log.C($"table: {Show(A(el, "table"))} → {f.Table}"); el.SetAttributeValue("table", f.Table); }
        if (f.Store.Length > 0 && !string.Equals(A(el, "store"), f.Store, StringComparison.OrdinalIgnoreCase)) { log.C($"store: {Show(A(el, "store"))} → {f.Store}"); el.SetAttributeValue("store", f.Store); }
        var cols = el.Element("Columns");
        if (cols is null) { cols = new XElement("Columns"); el.AddFirst(cols); Format(cols, nl, IndentOf(el, unit) + unit, unit); }
        ApplyCols(cols, f.Columns, "", log, nl, unit);

        foreach (var ch in f.Children)
        {
            var cts = el.Element("ChildTables");
            var ct = cts?.Elements("ChildTable").FirstOrDefault(x => string.Equals(A(x, "name"), ch.Name, StringComparison.OrdinalIgnoreCase));
            if (ct is null)
            {
                ct = new XElement("ChildTable", new XAttribute("name", ch.Name), new XAttribute("table", ch.Table),
                    new XElement("ParentField", ch.ParentField), new XElement("ChildField", ch.ChildField), new XElement("Columns", ch.Columns.Select(NewColumn)));
                if (cts is null) { cts = new XElement("ChildTables"); Append(el, cts, nl, unit); cts.RemoveNodes(); cts.Add(new XText(nl + IndentOf(cts, unit))); }
                Append(cts, ct, nl, unit);
                log.C($"thêm bảng chi tiết {ch.Name} ({ch.Columns.Count} cột)");
                continue;
            }
            if (ch.Table.Length > 0 && !string.Equals(A(ct, "table"), ch.Table, StringComparison.OrdinalIgnoreCase)) { log.C($"{ch.Name}.table: {Show(A(ct, "table"))} → {ch.Table}"); ct.SetAttributeValue("table", ch.Table); }
            SetChildText(ct, "ParentField", ch.ParentField, ch.Name, log, nl, unit);
            SetChildText(ct, "ChildField", ch.ChildField, ch.Name, log, nl, unit);
            var cc = ct.Element("Columns");
            if (cc is null) { cc = new XElement("Columns"); Append(ct, cc, nl, unit); cc.RemoveNodes(); cc.Add(new XText(nl + IndentOf(cc, unit))); }
            ApplyCols(cc, ch.Columns, ch.Name + ".", log, nl, unit);
        }
        var keptChildren = el.Element("ChildTables")?.Elements("ChildTable").Select(x => A(x, "name"))
            .Where(n => !f.Children.Any(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToList() ?? new();
        if (keptChildren.Count > 0 && f.Children.Count > 0) log.Kept.Add("bảng chi tiết " + string.Join(", ", keptChildren));
        return false;
    }

    private static void SetChildText(XElement ct, string name, string value, string child, Log log, string nl, string unit)
    {
        if (value.Length == 0) return;
        var e = ct.Element(name);
        if (e is null) { e = new XElement(name, value); Append(ct, e, nl, unit); log.C($"{child}.{name}: → {value}"); return; }
        if (!string.Equals(e.Value.Trim(), value, StringComparison.OrdinalIgnoreCase)) { log.C($"{child}.{name}: {Show(e.Value.Trim())} → {value}"); e.Value = value; }
    }

    private static string Show(string s) => s.Length == 0 ? "(trống)" : s;

    private static void ApplyCols(XElement cols, List<MapCol> incoming, string pre, Log log, string nl, string unit)
    {
        var existing = cols.Elements("Column").ToList();
        var matched = new HashSet<XElement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in incoming)
        {
            var key = c.Param.Length > 0 ? "p:" + c.Param : "f:" + c.Field;
            if (!seen.Add(key)) { log.Warnings.Add($"{pre}{(c.Param.Length > 0 ? c.Param : c.Field)} lặp lại trong Word — dùng dòng đầu."); continue; }
            var e = c.Param.Length > 0
                ? existing.FirstOrDefault(x => string.Equals(A(x, "Param"), c.Param, StringComparison.OrdinalIgnoreCase))
                : existing.FirstOrDefault(x => A(x, "Param").Length == 0 && string.Equals(A(x, "Field"), c.Field, StringComparison.OrdinalIgnoreCase));
            if (e is null)
            {
                Append(cols, NewColumn(c), nl, unit);
                log.C($"+ {pre}{(c.Param.Length > 0 ? c.Param : "(cha " + c.Parent + ")")} → {c.Field}{(c.SqlType.Length > 0 ? " " + c.SqlType : "")}{(c.Required ? ", bắt buộc" : "")}");
                continue;
            }
            matched.Add(e);
            var diffs = new List<string>();
            void Set(string attr, string? value, bool ignoreCase = true, Func<string, string>? norm = null)
            {
                if (value is null || value.Length == 0) return;   // ô trống trong Word không xoá giá trị cũ
                var old = (string?)e.Attribute(attr) ?? "";
                var a = norm is null ? old : norm(old); var b = norm is null ? value : norm(value);
                if (string.Equals(a, b, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
                diffs.Add($"{attr} {Show(old)} → {value}");
                e.SetAttributeValue(attr, value);
            }
            if (c.Param.Length > 0) Set("Param", c.Param, ignoreCase: false);   // khác hoa / thường cũng là khác (service đọc JSON phân biệt hoa thường)
            Set("Field", c.Field);
            Set("SqlDataType", c.SqlType, norm: s => s.Replace(" ", "").ToUpperInvariant());
            Set("DataType", c.DataType);
            Set("Default", c.Default, ignoreCase: false);
            Set("Parent", c.Parent);
            Set("Description", c.Description, ignoreCase: false);
            if (c.RequiredSet && c.Parent.Length == 0)
            {
                var old = bool.TryParse((string?)e.Attribute("Required"), out var r) && r;
                if (old != c.Required)
                {
                    diffs.Add(c.Required ? "thêm bắt buộc" : "bỏ bắt buộc");
                    if (c.Required) e.SetAttributeValue("Required", "true"); else e.SetAttributeValue("Required", null);
                }
            }
            if (diffs.Count > 0) log.C($"~ {pre}{(c.Param.Length > 0 ? c.Param : c.Field)}: {string.Join("; ", diffs)}");
        }
        var kept = existing.Where(x => !matched.Contains(x)).Select(x => A(x, "Param") is { Length: > 0 } p ? p : "(cha) " + A(x, "Field")).ToList();
        if (kept.Count > 0 && incoming.Count > 0) log.Kept.Add(pre + string.Join(", ", kept));
    }
}
