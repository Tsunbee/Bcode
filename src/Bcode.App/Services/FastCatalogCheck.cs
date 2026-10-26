using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>1 lưu ý khi soát spec theo chuẩn Fast. Level "warn" = dễ lỗi khi chạy / ghi đè nhầm (hỏi trước khi Tạo file), "info" = lệch quy ước.
/// El = id ô trên trang quicklist.html để nhảy tới (tCols / tDet = tab cột).</summary>
public sealed record FastIssue(string Level, string El, string Message);

/// <summary>
/// Soát spec "Tạo nhanh danh mục" theo cấu trúc Fast trước khi Tạo file — áp cho cả spec AI, luật Fast lẫn tự khai.
/// Không sửa gì: chỉ trả danh sách lưu ý (trang hiện nút "⚡ N lưu ý Fast"; có mục "warn" thì Tạo file hỏi lại).
/// Gồm: cột hệ thống / status mà mẫu Fast ghi vào; quy ước ma_xxx / ten_xxx / ten_xxx2; kiểu cột tra cứu khớp cột mã bảng nguồn;
/// type FastBusiness khớp kiểu SQL; tiêu đề v/e; field lệch với cách dự án đặt (<see cref="SourceFieldIndex"/>) hoặc với lần bạn đã sửa
/// (<see cref="CatalogFieldMemory"/>); controller trùng danh mục khác trong source; khoá / liên kết lưới chi tiết.
/// </summary>
public static class FastCatalogCheck
{
    private static readonly string[] SystemCols = { "datetime0", "datetime2", "user_id0", "user_id2" };
    private const int MaxNames = 6;

    /// <param name="tables">Bảng của App Data (null = chưa nạp, bỏ các mục cần tra bảng).</param>
    /// <param name="types">Cột → kiểu SQL của 1 bảng App Data (rỗng nếu không có bảng).</param>
    /// <param name="output">Thư mục source sẽ ghi file — để biết controller đã có chưa.</param>
    /// <param name="sourcePath">Source Path của workspace — chỉ mục field dự án.</param>
    public static async Task<List<FastIssue>> CheckAsync(QuickListSpec s, ISet<string>? tables,
        Func<string, Task<IReadOnlyDictionary<string, string>>> types, string output, string sourcePath, string catalogPrefix)
    {
        var issues = new List<FastIssue>();
        void Warn(string el, string m) => issues.Add(new("warn", el, m));
        void Info(string el, string m) => issues.Add(new("info", el, m));
        var names = new HashSet<string>(s.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var isCatalog = s.CreateLookup || s.NameField.Length > 0;
        var prefix = string.IsNullOrWhiteSpace(catalogPrefix) ? "zdm" : catalogPrefix.Trim().ToLowerInvariant();

        // ---- cột mẫu Fast tự ghi: thiếu là lỗi lúc lưu / import ----
        if (s.Columns.Count > 0)
        {
            var miss = SystemCols.Where(n => !names.Contains(n)).ToList();
            if (miss.Count > 0)
                Warn("tCols", $"Bảng thiếu {string.Join(", ", miss)} — mẫu Fast gán các cột này khi thêm / sửa{(s.ImportData ? " và khi import" : "")}, lưu sẽ báo lỗi. " +
                    (s.NewTable ? "Bấm “Dùng cột chuẩn” hoặc + Thêm cột." : "Thêm cột vào bảng (tab Script) trước khi dùng."));
            if (!names.Contains("status"))
            {
                if (s.ImportData) Warn("tCols", "Bảng thiếu cột status — mẫu Import ghi status = '1', import sẽ lỗi.");
                else if (isCatalog && s.NewTable) Info("tCols", "Danh mục Fast thường có cột status char(1) (1 = đang dùng) — tra cứu ở màn khác lọc status = '1'.");
            }
        }

        // ---- quy ước mã / tên của danh mục mới ----
        if (s.NewTable && isCatalog && s.Keys.Count == 1)
        {
            var k = s.Keys[0];
            var key = s.Columns.FirstOrDefault(c => c.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (!k.StartsWith("ma_", StringComparison.OrdinalIgnoreCase)) Info("keys", $"Khoá {k}: danh mục Fast đặt mã là ma_xxx.");
            else
            {
                var suffix = k[3..];
                if (s.NameField.Length > 0 && !s.NameField.Equals("ten_" + suffix, StringComparison.OrdinalIgnoreCase))
                    Info("nameField", $"Cột tên nên là ten_{suffix} (cùng đuôi với {k}), đang là {s.NameField}.");
                if (s.Columns.FirstOrDefault(c => c.Name.Equals(s.NameField, StringComparison.OrdinalIgnoreCase)) is { AllowNulls: true } nf)
                    Info("tCols", $"Cột tên {nf.Name} đang cho để trống — danh mục Fast bắt buộc nhập tên (allowNulls=\"false\"); bỏ tick AllowNulls.");
                if (names.Contains("ten_" + suffix) && !names.Contains("ten_" + suffix + "2"))
                    Info("tCols", $"Thiếu ten_{suffix}2 (tên tiếng Anh) — danh mục Fast có cặp ten_{suffix} / ten_{suffix}2.");
            }
            if (key?.SqlType is { } kt && Regex.IsMatch(kt, @"^n(var)?char", RegexOptions.IgnoreCase))
                Info("tCols", $"Khoá {k} kiểu {kt}: mã Fast dùng varchar (không dấu, viết hoa).");
        }
        if (s.NewTable && s.Table.Length > 0)
        {
            var want = isCatalog ? prefix : Regex.Replace(prefix, "dm$", "") is { Length: > 0 } dp ? dp : "z";
            if (!s.Table.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                Info("table", $"Bảng mới {s.Table} không bắt đầu bằng “{want}” — dự án đặt bảng tự thêm theo tiền tố này để không lẫn bảng chuẩn Fast (đổi ở settings NewCatalogTablePrefix).");
        }

        // ---- từng cột (danh mục + chi tiết) ----
        var index = SourceFieldIndex.TryGet(sourcePath);
        var ownKey = s.NewTable && isCatalog && s.Keys.Count == 1 ? s.Keys[0] : "";
        foreach (var (cols, tab, isDetail) in new[] { (s.Columns, "tCols", false), (s.DetailColumns, "tDet", true) })
        {
            var noLookup = new List<string>();
            var badType = new List<string>();
            var noV = new List<string>();
            var noE = new List<string>();
            var upper = new List<string>();
            var project = 0;
            foreach (var c in cols)
            {
                var sys = SystemCols.Contains(c.Name, StringComparer.OrdinalIgnoreCase);
                var keyLike = c.Name.Equals(ownKey, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(s.NameField, StringComparison.OrdinalIgnoreCase)
                    || (isDetail && s.LinkKeys.Contains(c.Name, StringComparer.OrdinalIgnoreCase));

                // Tra cứu: bảng nguồn phải có, cột cùng kiểu cột mã nguồn.
                if (c.Lookup is { } lk && (c.InForm || c.InGrid))
                {
                    var (t, k) = lk.Source;
                    if (t.Length > 0 && tables is not null && !tables.Contains(t))
                        Warn(tab, $"Cột {c.Name} tra cứu {lk.Controller} trên bảng {t} nhưng App Data không có bảng này.");
                    else if (t.Length > 0 && c.Added && c.SqlType.Length > 0 && (await types(t)).TryGetValue(k, out var st) && !SameType(st, c.SqlType))
                        Info(tab, $"Cột {c.Name} {c.SqlType} khác kiểu cột mã {t}.{k} {st} — nên để cùng kiểu.");
                }
                else if (!sys && !keyLike && c.InForm && !c.Hidden && !c.Name.StartsWith("ten_", StringComparison.OrdinalIgnoreCase)
                         && FastCatalogRules.KnownLookup(c.Name, "", sourcePath) is { } std)
                    noLookup.Add($"{c.Name} ({std.Controller})");

                // type FastBusiness theo kiểu SQL (Decimal / DateTime / Boolean) — sai thì form không định dạng / không lọc đúng.
                if (c.SqlType.Length > 0)
                {
                    var t = c.SqlType.ToLowerInvariant();
                    var want = Regex.IsMatch(t, @"^(numeric|decimal|money|smallmoney|float|real)") ? "Decimal"
                        : Regex.IsMatch(t, @"date") ? "DateTime" : t.StartsWith("bit") ? "Boolean" : null;
                    if (want is not null && !string.Equals(c.Type, want, StringComparison.OrdinalIgnoreCase)) badType.Add($"{c.Name} ({c.SqlType} → type {want})");
                    else if (want is null && c.Type.Length > 0 && Regex.IsMatch(t, @"char|text")) badType.Add($"{c.Name} ({c.SqlType} → type trống)");
                }

                if (!sys && !c.Hidden && (c.InForm || c.InGrid))
                {
                    if (c.HeaderV.Trim().Length == 0 || c.HeaderV == c.Name) noV.Add(c.Name);
                    if (c.HeaderE.Trim().Length == 0 || c.HeaderE == c.Name || HasVietnamese(c.HeaderE)) noE.Add(c.Name);
                }
                if (c.Added && c.Name.Any(char.IsUpper)) upper.Add(c.Name);

                // Lệch với lần bạn đã sửa / với cách dự án đặt cho cùng tiêu đề.
                if (c.Added && !sys && !keyLike && c.HeaderV.Length > 0 && project < MaxNames)
                {
                    if (CatalogFieldMemory.Get(c.HeaderV) is { } mem && !mem.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))
                    { Info(tab, $"“{c.HeaderV}”: lần trước bạn đã chốt field {mem.Name}, ở đây là {c.Name}."); project++; }
                    else if (index is not null && index.ByName(c.Name).Count == 0 && FieldDictionaryService.Instance.Header(c.Name) is null
                             && index.ByHeader(c.HeaderV).FirstOrDefault() is { } u && !u.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        Info(tab, $"“{c.HeaderV}”: source dự án dùng field {u.Name}{(u.Lookup is { } ul ? $" ({ul.Controller})" : "")} ở {u.Count} file (vd {string.Join(", ", u.Files)}), ở đây là {c.Name} — field mới chưa có trong dự án.");
                        project++;
                    }
                }
            }
            if (noLookup.Count > 0) Info(tab, $"Có tra cứu chuẩn nhưng đang là ô nhập thường: {Names(noLookup)} — chọn ở cột Lookup nếu cần.");
            if (badType.Count > 0) Warn(tab, $"type chưa khớp kiểu SQL: {Names(badType)} — form sẽ định dạng / lọc sai.");
            if (noV.Count > 0) Info(tab, $"Chưa có tiêu đề tiếng Việt: {Names(noV)}.");
            if (noE.Count > 0) Info(tab, $"Chưa có tiêu đề tiếng Anh (trống / còn tiếng Việt): {Names(noE)} — bấm Gợi ý header hoặc Dịch.");
            if (upper.Count > 0) Info(tab, $"Tên cột Fast viết thường: {Names(upper)}.");
        }

        if (s.TitleV.Trim().Length == 0) Info("titleV", "Chưa có tiêu đề màn (Title v).");
        if (s.TitleE.Trim().Length == 0 || HasVietnamese(s.TitleE)) Info("titleE", "Chưa có tiêu đề tiếng Anh (Title e) — bấm Dịch.");

        // ---- controller trùng danh mục khác trong source ----
        if (output.Length > 0 && s.Controller.Length > 0)
        {
            foreach (var ext in new[] { ".xml", ".f" })
            {
                var path = Path.Combine(output, "App_Data", "Controllers", "Dir", s.Controller + ext);
                if (!File.Exists(path)) continue;
                var table = DirTable(path);
                if (ext == ".f")
                    Warn("controller", $"Source đã có Dir\\{s.Controller}.f{(table.Length > 0 ? $" (bảng {table})" : "")} cùng tên controller — Tạo file sẽ ghi thêm {s.Controller}.xml trùng tên. Đổi Controller.");
                else if (table.Length > 0 && !table.Equals(s.Table, StringComparison.OrdinalIgnoreCase))
                    Warn("controller", $"Source đã có Dir\\{s.Controller}.xml dùng bảng {table} — Tạo file sẽ ghi đè danh mục khác. Đổi Controller.");
            }
        }

        // ---- lưới chi tiết ----
        if (s.HasDetail)
        {
            if (s.DetailKeys.Count > 0 && s.DetailKeys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(s.LinkKeys))
                Warn("detailKeys", "Khoá chi tiết chỉ gồm cột liên kết — mỗi dòng danh mục chỉ lưu được 1 dòng chi tiết. Thường thêm line_nbr.");
            foreach (var k in s.LinkKeys)
            {
                var mt = s.Columns.FirstOrDefault(c => c.Name.Equals(k, StringComparison.OrdinalIgnoreCase))?.SqlType ?? "";
                var dt = s.DetailColumns.FirstOrDefault(c => c.Name.Equals(k, StringComparison.OrdinalIgnoreCase))?.SqlType ?? "";
                if (mt.Length > 0 && dt.Length > 0 && !SameType(mt, dt))
                    Warn("linkKeys", $"Cột liên kết {k}: danh mục {mt}, chi tiết {dt} — nên cùng kiểu.");
            }
            if (s.DetailNew && !s.DetailTable.StartsWith(s.Table, StringComparison.OrdinalIgnoreCase))
                Info("detailTable", $"Bảng chi tiết Fast thường đặt {s.Table}ct.");
        }

        return issues.OrderBy(i => i.Level == "warn" ? 0 : 1).ToList();
    }

    private static string Names(List<string> l) => string.Join(", ", l.Take(MaxNames)) + (l.Count > MaxNames ? $" … (+{l.Count - MaxNames})" : "");

    private static bool SameType(string a, string b) =>
        Regex.Replace(a, @"\s+", "").Equals(Regex.Replace(b, @"\s+", ""), StringComparison.OrdinalIgnoreCase);

    private static bool HasVietnamese(string s) =>
        s.Any(ch => ch is 'đ' or 'Đ') || s.Normalize(NormalizationForm.FormD).Any(ch => ch >= '̀' && ch <= 'ͯ');

    private static string DirTable(string path)
    {
        try
        {
            using var sr = new StreamReader(path, Encoding.UTF8, true);
            var buf = new char[6000];
            var head = new string(buf, 0, sr.ReadBlock(buf, 0, buf.Length));
            // table="&Tag;" (entity) thì coi như không biết bảng.
            return Regex.Match(head, @"<dir\s[^>]*\btable=""(?<t>[A-Za-z0-9_$]+)""") is { Success: true } m ? m.Groups["t"].Value : "";
        }
        catch { return ""; }
    }
}
