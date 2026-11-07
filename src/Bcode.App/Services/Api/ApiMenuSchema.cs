using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bcode.App.Models;

namespace Bcode.App.Services.Api;

/// <summary>
/// "Tạo cấu trúc API…" ở chuột phải cây WCommand / file Dir-Grid của File Lookup: suy bảng của menu (<see cref="CommandTableService"/>: Dir → bảng master,
/// Grid chi tiết → bảng detail), đọc cột thật trong database App, chọn cột có trong controller (+ khoá chính, khoá nối stt_rec), tên tiếng Việt = tiêu đề
/// field ở controller (thiếu thì header.xml — <see cref="FieldDictionaryService"/>), tên hàm = <see cref="ApiSchemaStore.FuncName"/> ("Set" + controller).
/// Kết quả dựng thành đoạn XML đúng khuôn ColumnMappings.xml rồi đọc bằng chính <see cref="ApiColumnMappings.Parse"/> → <see cref="Lt3Templates.FormDoc"/>
/// (SyncData / SyncVoucher, body JSON mẫu, bảng trường để Kiểm tra / Tài liệu form) — Khai báo API dùng như form của mapping dự án.
/// Đồng thời ra 1 <see cref="ApiSchemaApi"/> để thêm vào danh sách của "Tạo cấu trúc API" (<see cref="AddToSchemaStore"/>).
/// </summary>
public static class ApiMenuSchema
{
    public sealed record Column(string Name, string Type, bool Nullable, bool IsKey, bool Identity, string Description);
    public sealed record CtlField(string Name, string HeaderV, bool Required);

    /// <summary>1 bảng của cấu trúc: <paramref name="Controller"/> = tên file Dir / Grid khai bảng, <paramref name="Fields"/> = field của controller đó.</summary>
    public sealed record Part(string Table, string Controller, IReadOnlyList<Column> Columns, IReadOnlyDictionary<string, CtlField> Fields);

    public sealed record Result(Lt3Templates.FormDoc Form, string Xml, ApiSchemaApi Schema, List<string> Notes);

    /// <summary>Cột hệ thống Fast không đưa vào API (ngày / người tạo-sửa, cookie).</summary>
    private static readonly HashSet<string> Audit = new(StringComparer.OrdinalIgnoreCase) { "datetime0", "datetime2", "user_id0", "user_id2", "cookie" };
    private const string LinkKey = "stt_rec";

    // ------------------------------------------------------------------------------------------------ đọc source + database

    /// <summary>Cấu trúc API của 1 menu (sysid = controller Dir).</summary>
    public static async Task<Result> ForMenuAsync(Workspace ws, TableDataService tables, SqlObjectBrowserService? objects, string sysId, string title)
    {
        if (string.IsNullOrWhiteSpace(ws.SourcePath)) throw new InvalidOperationException("Project chưa khai Source Path — không đọc được controller của menu.");
        if (string.IsNullOrWhiteSpace(sysId)) throw new InvalidOperationException("Menu không có sysid (controller Dir).");
        var r = await Task.Run(() => CommandTableService.Resolve(ws.SourcePath, sysId.Trim()));
        return await FromDirAsync(tables, objects, r, sysId.Trim(), title);
    }

    /// <summary>Cấu trúc API từ 1 file controller: file Dir = cả menu (master + Grid chi tiết), file Grid = riêng bảng của Grid đó — tên hàm theo sysid
    /// của menu dùng bảng (<see cref="ApiSchemaStore.MenuForTableAsync"/>, gọi trên luồng UI), không thấy menu thì theo tên Grid.</summary>
    public static async Task<Result> ForFileAsync(TableDataService tables, SqlObjectBrowserService? objects, string path, TableMenuService? menus = null)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        var controllers = Path.GetDirectoryName(Path.GetDirectoryName(path) ?? "") ?? "";
        if (folder.Equals("Dir", StringComparison.OrdinalIgnoreCase))
        {
            var r = await Task.Run(() => CommandTableService.ResolveIn(controllers, name));
            return await FromDirAsync(tables, objects, r, name, name);
        }
        var text = await Task.Run(() => ReadText(path)) ?? throw new InvalidOperationException("Không đọc được file " + path);
        var notes = new List<string>();
        var table = ResolveEntity(CommandTableService.TableOf(text), text, notes);
        if (table.Length == 0) throw new InvalidOperationException($"File {Path.GetFileName(path)} không khai table=\"…\" ở thẻ gốc.");
        var part = await PartAsync(tables, objects, table, name, text, notes);
        ApiSchemaStore.MenuHit? menu = null;
        try { menu = await ApiSchemaStore.MenuForTableAsync(menus, table); } catch { /* không tra được menu */ }
        if (menu is null) notes.Add($"Không thấy menu (sysid) nào dùng bảng {table} — tên hàm theo tên Grid {name}.");
        var res = Build(ApiSchemaStore.FuncName(menu?.SysId ?? name), menu is null ? name : menu.Label + " — " + name, part, Array.Empty<Part>(), notes);
        if (menu is not null) res.Schema.Tables[0].Menu = menu.Label;
        return res;
    }

    private static async Task<Result> FromDirAsync(TableDataService tables, SqlObjectBrowserService? objects, CommandTableService.Result r, string sysId, string title)
    {
        if (r.DirFile is null)
            throw new InvalidOperationException($"Menu {sysId} không có controller Dir (Dir/{sysId}.f / .xml) — thường là menu báo cáo / trang riêng. "
                + "Tạo cấu trúc API chỉ dùng cho menu danh mục / chứng từ (có Dir khai table=\"…\").");
        var notes = new List<string>();
        var dirText = await Task.Run(() => ReadText(r.DirFile)) ?? "";
        var master = ResolveEntity(r.Master, dirText, notes);
        if (master.Length == 0) throw new InvalidOperationException($"Dir/{Path.GetFileName(r.DirFile)} không khai table=\"…\" — không biết bảng của menu.");
        var head = await PartAsync(tables, objects, master, sysId, dirText, notes);
        var children = new List<Part>();
        foreach (var g in r.Grids)
        {
            var gText = await Task.Run(() => ReadText(g.File)) ?? "";
            var t = ResolveEntity(g.Table, gText, notes);
            if (t.Length == 0 || t.Equals(master, StringComparison.OrdinalIgnoreCase)) continue;
            children.Add(await PartAsync(tables, objects, t, g.Controller, gText, notes));
        }
        return Build(ApiSchemaStore.FuncName(sysId), string.IsNullOrWhiteSpace(title) ? sysId : title.Trim(), head, children, notes);
    }

    private static async Task<Part> PartAsync(TableDataService tables, SqlObjectBrowserService? objects, string table, string controller, string ctlText, List<string> notes)
    {
        var (schema, name) = Split(table);
        var cols = await ColumnsAsync(tables, schema, name);
        if (cols.Count == 0 && objects is not null && name.IndexOf('$') is var d and > 0)
        {
            // Bảng theo kỳ (m81$000000 không có thật trong database): lấy cấu trúc của 1 bảng kỳ cùng gốc.
            var alt = (await objects.ListObjectsAsync(false, name[..(d + 1)] + "%"))
                .Where(o => o.Kind == SqlObjectKind.Table).Select(o => o.Name).OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (alt is not null)
            {
                cols = await ColumnsAsync(tables, schema, alt);
                if (cols.Count > 0) notes.Add($"Bảng {name} không có trong database — lấy cột theo {alt}.");
            }
        }
        if (cols.Count == 0) notes.Add($"Không đọc được cột của bảng {table} trong database App (sai kết nối / bảng không có).");
        return new Part(name, controller, cols, ParseFields(ctlText));
    }

    private static async Task<List<Column>> ColumnsAsync(TableDataService tables, string schema, string name)
    {
        try { return (await tables.GetColumnDescriptionsAsync(false, schema, name)).Select(c => new Column(c.Name, c.Type, c.Nullable, c.IsKey, c.Identity, c.Description)).ToList(); }
        catch { return new(); }
    }

    private static (string Schema, string Name) Split(string table)
    {
        var t = table.Replace("[", "").Replace("]", "").Trim();
        var dot = t.LastIndexOf('.');
        return dot > 0 ? (t[..dot], t[(dot + 1)..]) : ("dbo", t);
    }

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); } catch { return null; }
    }

    /// <summary>table="&amp;Tag;" → giá trị &lt;!ENTITY Tag "…"&gt; khai trong chính file (không thấy thì ghi chú, trả "").</summary>
    internal static string ResolveEntity(string table, string text, List<string> notes)
    {
        var m = Regex.Match(table ?? "", @"^&([\w.\-]+);$");
        if (!m.Success) return (table ?? "").Trim();
        var e = Regex.Match(text, @"<!ENTITY\s+" + Regex.Escape(m.Groups[1].Value) + @"\s+""([^""]*)""");
        if (e.Success && e.Groups[1].Value.Trim().Length > 0) return e.Groups[1].Value.Trim();
        notes.Add($"table=\"{table}\" là entity khai ở file khác — chưa tra được tên bảng.");
        return "";
    }

    // ------------------------------------------------------------------------------------------------ phần thuần (thử được không cần database)

    private static readonly Regex FieldRx = new(@"<field\b(?<a>[^>]*?)(?:/>|>(?<b>.*?)</field>)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NameRx = new(@"\bname\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex AllowNullsRx = new(@"\ballowNulls\s*=\s*""false""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeaderVRx = new(@"<header\b[^>]*?\bv\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled);

    /// <summary>Field trong &lt;fields&gt; của controller: tên (bỏ field ảo kiểu ten_vt%l), tiêu đề Việt, allowNulls="false" = bắt buộc.</summary>
    public static Dictionary<string, CtlField> ParseFields(string text)
    {
        var map = new Dictionary<string, CtlField>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FieldRx.Matches(text ?? ""))
        {
            var a = m.Groups["a"].Value;
            var n = NameRx.Match(a) is { Success: true } nm ? nm.Groups["v"].Value.Trim() : "";
            if (n.Length == 0 || !Regex.IsMatch(n, @"^\w+$")) continue;
            var v = HeaderVRx.Match(m.Groups["b"].Value) is { Success: true } h ? System.Net.WebUtility.HtmlDecode(h.Groups["v"].Value).Trim() : "";
            var req = AllowNullsRx.IsMatch(a);
            if (map.TryGetValue(n, out var old)) map[n] = old with { HeaderV = old.HeaderV.Length > 0 ? old.HeaderV : v, Required = old.Required || req };
            else map[n] = new CtlField(n, v, req);
        }
        return map;
    }

    /// <summary>Cột đưa vào API: bỏ identity + cột hệ thống; controller có field khớp cột thì chỉ giữ cột có trên màn hình + khoá chính (+ stt_rec nếu là chứng từ).</summary>
    internal static List<Column> Pick(Part p, bool keepLink)
    {
        var cols = p.Columns.Where(c => !c.Identity && !Audit.Contains(c.Name)).ToList();
        if (cols.Any(c => p.Fields.ContainsKey(c.Name)))
            cols = cols.Where(c => c.IsKey || p.Fields.ContainsKey(c.Name) || (keepLink && c.Name.Equals(LinkKey, StringComparison.OrdinalIgnoreCase))).ToList();
        return cols;
    }

    /// <summary>Tên mảng con trong body: Grid đầu = "detail" (chuẩn LT3), Grid có tên …Tax = "tax", còn lại = tên controller viết thường.</summary>
    internal static List<string> ChildNames(IReadOnlyList<Part> children)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        for (var i = 0; i < children.Count; i++)
        {
            var c = children[i].Controller;
            var n = i == 0 ? "detail" : c.EndsWith("Tax", StringComparison.OrdinalIgnoreCase) && !used.Contains("tax") ? "tax" : c.ToLowerInvariant();
            var u = n; for (var k = 2; !used.Add(u); k++) u = n + k;
            names.Add(u);
        }
        return names;
    }

    /// <param name="dictionary">Tiêu đề Việt theo tên field khi controller không có (mặc định header.xml của FCode).</param>
    public static Result Build(string func, string title, Part master, IReadOnlyList<Part> children, List<string>? notes = null, Func<string, string?>? dictionary = null)
    {
        notes ??= new();
        dictionary ??= n => FieldDictionaryService.Instance.Header(n) is { V.Length: > 0 } h ? h.V : null;
        if (func.Length == 0) func = ApiSchemaStore.FuncName(master.Table);
        string Vi(Part p, Column c) => p.Fields.TryGetValue(c.Name, out var f) && f.HeaderV.Length > 0 ? f.HeaderV : dictionary(c.Name) ?? c.Description ?? "";
        bool Req(Part p, Column c) => c.IsKey || (p.Fields.TryGetValue(c.Name, out var f) && f.Required);

        var voucher = children.Count > 0;
        var headCols = Pick(master, voucher);
        XElement ColumnsEl(Part p, IEnumerable<Column> cols, string? parentKey) => new("Columns", cols.Select(c =>
        {
            var e = new XElement("Column", new XAttribute("Param", c.Name), new XAttribute("Field", c.Name), new XAttribute("SqlDataType", c.Type));
            if (parentKey is not null && c.Name.Equals(parentKey, StringComparison.OrdinalIgnoreCase)) e.Add(new XAttribute("Parent", parentKey));
            else if (Req(p, c)) e.Add(new XAttribute("Required", "true"));
            var vi = Vi(p, c);
            if (vi.Length > 0) e.Add(new XAttribute("Description", vi));
            return e;
        }));

        var form = new XElement("Form", new XAttribute("name", func), new XAttribute("table", master.Table), ColumnsEl(master, headCols, null));
        var childNames = ChildNames(children);
        var childCols = new List<List<Column>>();
        if (voucher)
        {
            var hasLink = master.Columns.Any(c => c.Name.Equals(LinkKey, StringComparison.OrdinalIgnoreCase));
            var cts = new XElement("ChildTables");
            for (var i = 0; i < children.Count; i++)
            {
                var ch = children[i];
                var link = hasLink && ch.Columns.Any(c => c.Name.Equals(LinkKey, StringComparison.OrdinalIgnoreCase)) ? LinkKey : null;
                if (link is null) notes.Add($"Bảng {ch.Table} không có cột {LinkKey} nối với {master.Table} — khai khoá nối tay.");
                var cols = Pick(ch, link is not null);
                childCols.Add(cols);
                var ct = new XElement("ChildTable", new XAttribute("name", childNames[i]), new XAttribute("table", ch.Table));
                if (link is not null) { ct.Add(new XElement("ParentField", link)); ct.Add(new XElement("ChildField", link)); }
                ct.Add(ColumnsEl(ch, cols, link));
                cts.Add(ct);
            }
            form.Add(cts);
        }
        var root = new XElement("ColumnMappings", new XComment(" " + title + " "), form);
        var xml = root.ToString();
        var doc = ApiColumnMappings.Parse(xml).First();
        doc = doc with
        {
            Title = title,
            Description = "Dựng từ menu / controller " + master.Controller + " — bảng " + string.Join(", ", new[] { master.Table }.Concat(children.Select(c => c.Table)))
                          + ". Tên trường = cột database, kiểm lại trước khi gửi khách.",
        };

        // Bản cho "Tạo cấu trúc API" (apischema.html): cùng cột, tên trường trùng giữa các bảng thì thêm tiền tố tên bảng như trang làm.
        var api = new ApiSchemaApi { Name = title, Desc = doc.Description, Func = func, FuncAuto = true };
        var usedField = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddTable(Part p, List<Column> cols)
        {
            var tname = "dbo." + p.Table;
            var key = "0|" + tname;
            api.Tables.Add(new ApiSchemaTable { Db = "0", Table = tname, Controller = p.Controller });
            foreach (var c in cols)
            {
                var field = c.Name.ToLowerInvariant();
                if (!usedField.Add(field)) { field = p.Table.ToLowerInvariant() + "_" + field; usedField.Add(field); }
                var vi = Vi(p, c);
                api.Columns.Add(new ApiSchemaColumn
                {
                    ColName = c.Name, DataType = c.Type, IsPk = c.IsKey, Nullable = c.Nullable, Identity = c.Identity, Required = Req(p, c),
                    FieldDesc = field, LogicDesc = c.Description ?? "", ViName = vi, Tkey = key, Tname = tname, Tdb = "0",
                });
            }
        }
        AddTable(master, headCols);
        for (var i = 0; i < children.Count; i++) AddTable(children[i], childCols[i]);
        return new Result(doc, xml, api, notes);
    }

    // ------------------------------------------------------------------------------------------------ "Tạo cấu trúc API"

    /// <summary>Khoá file lưu của "Tạo cấu trúc API" — như ApiSchemaBuilderForm.</summary>
    public static string SchemaKey(Workspace? ws) => ws is null ? "" : ws.Name.Length > 0 ? ws.Name : ws.AppDatabase ?? "";

    /// <summary>Thêm API vào danh sách đã lưu của dự án và chọn nó làm API đang mở (mở "Tạo cấu trúc API" là thấy ngay). Không đè: đã có API cùng
    /// tên hàm thì giữ bản cũ (có thể đã sửa tay), chỉ chọn nó — trả false.</summary>
    public static bool AddToSchemaStore(string wsKey, ApiSchemaApi api)
    {
        var st = ApiSchemaStore.Load(wsKey) ?? new ApiSchemaState();
        var at = st.Apis.FindIndex(a => string.Equals(a.Func, api.Func, StringComparison.OrdinalIgnoreCase));
        var added = at < 0;
        if (added) { st.Apis.Add(api); at = st.Apis.Count - 1; }
        st.Active = at;
        ApiSchemaStore.Save(wsKey, st);
        return added;
    }
}
