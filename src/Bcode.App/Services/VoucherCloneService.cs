using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>1 chứng từ có sẵn trong source: Dir\{SysId}.xml có &lt;dir type="Voucher" id="{Code}" table="{Table}"&gt;.</summary>
public record VoucherInfo(string SysId, string Code, string Table, string TitleV, string TitleE)
{
    /// <summary>Tiền tố tên file của chứng từ: "CITran" → "CI" (file CITran, CIDetail, CIMasterImport...).</summary>
    public string Prefix => VoucherCloneService.PrefixOf(SysId);
    /// <summary>"m48$000000" → "48": phần số / mã nhóm bảng (m48, d48, c48...).</summary>
    public string TableNo => VoucherCloneService.TableNoOf(Table);
}

/// <summary>Thông số nhân bản: nguồn → đích.</summary>
public class VoucherCloneSpec
{
    public string SrcCode { get; set; } = "";
    public string SrcSysId { get; set; } = "";
    public string SrcTitleV { get; set; } = "";
    public string SrcTitleE { get; set; } = "";
    public string SrcTableNo { get; set; } = "";
    public string DstCode { get; set; } = "";
    public string DstSysId { get; set; } = "";
    public string DstPrefix { get; set; } = "";
    public string DstTitleV { get; set; } = "";
    public string DstTitleE { get; set; } = "";
    /// <summary>true = tạo bộ bảng mới (m/d/c...{DstTableNo}), false = dùng chung bảng của chứng từ nguồn.</summary>
    public bool NewTables { get; set; }
    public string DstTableNo { get; set; } = "";

    public string SrcPrefix => VoucherCloneService.PrefixOf(SrcSysId);

    /// <summary>Tên file (không đuôi) của controller KHÁC trong source có cùng tiền tố nhưng không chép (vd ARInvoiceLookup dùng chung) —
    /// không được đổi tên khi gặp trong nội dung, kẻo trỏ sang controller không tồn tại.</summary>
    public HashSet<string> SharedNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Đổi tên file không theo tiền tố (Main\tpblcthdx.aspx → tpblcthdz.aspx) — áp cả trong nội dung (link menu, dmct9.url...).</summary>
    public List<(string From, string To)> FileRenames { get; set; } = new();
}

/// <summary>
/// "Tạo chứng từ" — nhân bản 1 chứng từ có sẵn sang mã mới, theo quy ước source FastBusiness:
/// <list type="bullet">
/// <item>Chứng từ = nhóm file cùng tiền tố trong App_Data\Controllers (Dir/Grid/Filter/Templates...): CITran, CIDetail, CIMasterImport...;
///   SysID = tiền tố + "Tran". Tiền tố phải đi liền chữ HOA để "SI" không bắt nhầm "SI2Tran".</item>
/// <item>Mã chứng từ nằm ở thuộc tính id="PT2" (Dir, Grid, Import...).</item>
/// <item>Bảng: m48$000000 (master), d48$000000 (chi tiết), c48$000000, $log, và cách gọi m48$$partition$current — có thể dùng chung
///   bảng nguồn (như SITran / SI2Tran cùng m66) hoặc đổi sang bộ bảng mới.</item>
/// </list>
/// Phần SQL (bảng mới, dòng dmct) chỉ SINH SCRIPT để người dùng xem / sửa / tự chạy — không ghi gì vào database.
/// </summary>
public class VoucherCloneService
{
    private readonly DbConnectionService _connections;
    public VoucherCloneService(DbConnectionService connections) => _connections = connections;

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly string[] TextExt = { ".xml", ".txt", ".ent", ".aspx", ".js", ".css", ".sql", ".htm", ".html" };

    public static string PrefixOf(string sysId) =>
        sysId.EndsWith("Tran", StringComparison.Ordinal) && sysId.Length > 4 ? sysId[..^4] : sysId;

    public static string TableNoOf(string table)
    {
        var m = Regex.Match(table, @"^[a-z]+(\w*?)\$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Thư mục Controllers của source: App_Data\Controllers (chuẩn) hoặc AppData\Controllers.</summary>
    public static string? ControllersDir(string sourceRoot)
    {
        foreach (var d in new[] { Path.Combine(sourceRoot, "App_Data", "Controllers"), Path.Combine(sourceRoot, "AppData", "Controllers") })
            if (Directory.Exists(d)) return d;
        return null;
    }

    // ---- Bước 1: danh sách chứng từ có sẵn ----------------------------------------------------------

    private static readonly Regex VoucherRoot = new(@"<dir\s[^>]*\btype=""Voucher""[^>]*>", RegexOptions.IgnoreCase);
    private static string Attr(string tag, string name) =>
        Regex.Match(tag, @"\b" + name + @"=""([^""]*)""", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "";

    public static List<VoucherInfo> ScanVouchers(string sourceRoot)
    {
        var dir = ControllersDir(sourceRoot) is { } c ? Path.Combine(c, "Dir") : null;
        var list = new List<VoucherInfo>();
        if (dir == null || !Directory.Exists(dir)) return list;
        // Dir\{SysID}.xml hoặc .f (dự án lưu XML với đuôi .f) — mỗi SysID lấy 1 file.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".xml" or ".f")
                     .OrderBy(p => Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (!seen.Add(Path.GetFileNameWithoutExtension(f))) continue;
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            var root = VoucherRoot.Match(text);
            if (!root.Success) continue;
            var code = Attr(root.Value, "id");
            if (code.Length == 0) continue;
            var title = Regex.Match(text[root.Index..], @"<title\s+v=""([^""]*)""\s+e=""([^""]*)""");
            list.Add(new VoucherInfo(Path.GetFileNameWithoutExtension(f), code, Attr(root.Value, "table"),
                title.Success ? title.Groups[1].Value : "", title.Success ? title.Groups[2].Value : ""));
        }
        return list.OrderBy(v => v.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- Bước 2: file của chứng từ nguồn -------------------------------------------------------------

    /// <summary>
    /// File thuộc chứng từ (tương đối so với gốc source), ở mọi thư mục con của Controllers kể cả Include: tên có tiền tố đứng đầu 1 từ
    /// — Dir\CITran.f, Grid\CIDetail.f, Include\Extender.CITran, Include\XML\Config\Fields\CIGrid.ent / CIField.txt / CIView.txt.
    /// </summary>
    public static List<string> SourceFiles(string sourceRoot, string prefix)
    {
        var controllers = ControllersDir(sourceRoot);
        var result = new List<string>();
        if (controllers == null || prefix.Length == 0) return result;
        var re = new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(prefix) + "(?=[A-Z_]|\\.)", RegexOptions.None);
        foreach (var f in Directory.EnumerateFiles(controllers, "*", SearchOption.AllDirectories))
            if (re.IsMatch(Path.GetFileName(f))) result.Add(Path.GetRelativePath(sourceRoot, f));
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Controller khác (Dir/Grid/Filter/Lookup) mà file của chứng từ nhắc tới đúng tên — vd showForm('tpblAddCustomer'), controller="tpbldmkhvat".
    /// Gói FCode chép kèm các file này (nguyên trạng) để mang chứng từ sang dự án khác; cùng dự án thì không cần.
    /// </summary>
    public static List<string> ReferencedControllers(string sourceRoot, IEnumerable<string> ownFiles)
    {
        var result = new List<string>();
        if (ControllersDir(sourceRoot) is not { } controllers) return result;
        var own = ownFiles.ToList();
        var ownNames = new HashSet<string>(own.Select(r => Path.GetFileNameWithoutExtension(r)), StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var sub in new[] { "Dir", "Grid", "Filter", "Lookup" })
        {
            var d = Path.Combine(controllers, sub);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (ownNames.Contains(n)) continue;
                if (!byName.TryGetValue(n, out var l)) byName[n] = l = new();
                l.Add(Path.GetRelativePath(sourceRoot, f));
            }
        }
        var hit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in own)
        {
            var full = Path.Combine(sourceRoot, rel);
            if (!File.Exists(full) || !IsText(full)) continue;
            foreach (Match m in Regex.Matches(ReadText(full, out _), @"(?<=['""])[A-Za-z][A-Za-z0-9_]{2,}(?=['""])"))
                if (byName.TryGetValue(m.Value, out var rels)) foreach (var r in rels) hit.Add(r);
        }
        return hit.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- File đăng ký dùng chung (Aggregation.v, Voucher.Controller.003, Filter\Config\Initialize.xml, Include\Extender.ent...) ----

    /// <summary>
    /// File dùng chung có dòng khai báo chứng từ nguồn (chứa SysID nguồn) — FCode thêm 1 dòng tương ứng cho chứng từ mới ngay sau dòng
    /// đó. Chỉ xét file cấu hình: trong thư mục Config (bất kỳ cấp) hoặc file nằm thẳng trong Include — không đụng controller của chứng từ
    /// khác (Dir/Grid/Filter) dù có nhắc tới SysID nguồn. Trả (file, các dòng sẽ thêm).
    /// </summary>
    public static List<(string Rel, List<string> Added)> RegistryEdits(string sourceRoot, VoucherCloneSpec s, IEnumerable<string> ownFiles)
    {
        var result = new List<(string, List<string>)>();
        if (ControllersDir(sourceRoot) is not { } controllers || s.SrcSysId.Length == 0) return result;
        var own = new HashSet<string>(ownFiles, StringComparer.OrdinalIgnoreCase);
        var token = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(s.SrcSysId) + @"(?![A-Za-z0-9_])");
        foreach (var f in Directory.EnumerateFiles(controllers, "*", SearchOption.AllDirectories))
        {
            var relC = Path.GetRelativePath(controllers, f);
            var parts = relC.Split(Path.DirectorySeparatorChar);
            var isConfig = parts.Take(parts.Length - 1).Any(p => p.Equals("Config", StringComparison.OrdinalIgnoreCase))
                           || (parts.Length == 2 && parts[0].Equals("Include", StringComparison.OrdinalIgnoreCase));
            var rel = Path.GetRelativePath(sourceRoot, f);
            if (!isConfig || own.Contains(rel) || new FileInfo(f).Length > 4_000_000 || !IsText(f)) continue;
            var text = ReadText(f, out _);
            if (!token.IsMatch(text)) continue;
            var added = AddRegistryLines(text, s, out _);
            if (added.Count > 0) result.Add((rel, added));
        }
        return result.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Sau mỗi dòng có SysID nguồn, chèn bản đã đổi sang chứng từ mới (nếu file chưa có dòng đó).</summary>
    public static List<string> AddRegistryLines(string text, VoucherCloneSpec s, out string newText)
    {
        var token = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(s.SrcSysId) + @"(?![A-Za-z0-9_])");
        var eol = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(eol);
        var present = new HashSet<string>(lines.Select(l => l.Trim()));
        var output = new List<string>(lines.Length + 8);
        var added = new List<string>();
        foreach (var line in lines)
        {
            output.Add(line);
            if (!token.IsMatch(line)) continue;
            var copy = Transform(line, s);
            if (copy == line || present.Contains(copy.Trim())) continue;
            output.Add(copy); added.Add(copy.Trim()); present.Add(copy.Trim());
        }
        newText = string.Join(eol, output);
        return added;
    }

    /// <summary>Tên file (không đuôi) trong Controllers có cùng tiền tố nhưng không nằm trong danh sách chép — xem <see cref="VoucherCloneSpec.SharedNames"/>.</summary>
    public static HashSet<string> SharedNames(string sourceRoot, string prefix, IEnumerable<string> copiedRel)
    {
        var copied = new HashSet<string>(copiedRel.Select(r => Path.GetFileNameWithoutExtension(r)), StringComparer.OrdinalIgnoreCase);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ControllersDir(sourceRoot) is not { } controllers || prefix.Length == 0) return set;
        foreach (var f in Directory.EnumerateFiles(controllers, prefix + "*", SearchOption.AllDirectories))
        {
            var n = Path.GetFileNameWithoutExtension(f);
            if (!copied.Contains(n)) set.Add(n);
        }
        return set;
    }

    /// <summary>Tên file đích: đổi tiền tố trong TÊN file (giữ thư mục). File Main .aspx không theo tiền tố thì thay tên theo SysID.</summary>
    public static string TargetPath(string rel, VoucherCloneSpec s)
    {
        var dir = Path.GetDirectoryName(rel) ?? "";
        var name = Path.GetFileName(rel);
        string newName;
        if (name.StartsWith(s.SrcSysId, StringComparison.Ordinal)) newName = s.DstSysId + name[s.SrcSysId.Length..];
        else if (dir.Equals("Main", StringComparison.OrdinalIgnoreCase) && s.SrcCode.Length > 0
                 && Path.GetFileNameWithoutExtension(name).EndsWith(s.SrcCode, StringComparison.OrdinalIgnoreCase))
        {
            // Main của dự án kiểu tpblcthdx.aspx (tiền tố dự án + "ct" + mã ct) → tpblcthdz.aspx
            var stem = Path.GetFileNameWithoutExtension(name);
            newName = stem[..^s.SrcCode.Length] + s.DstCode.ToLowerInvariant() + Path.GetExtension(name);
        }
        else
        {
            // Tiền tố đứng đầu 1 từ bất kỳ trong tên (CIDetail.ent, Extra.CIDetail), rồi tới mã ct (…PT2…).
            newName = Regex.Replace(name, @"(?<![A-Za-z0-9])" + Regex.Escape(s.SrcPrefix) + "(?=[A-Z_]|\\.)", s.DstPrefix);
            if (newName == name && s.SrcCode.Length > 0)
                newName = Regex.Replace(name, @"(?<![A-Za-z0-9])" + Regex.Escape(s.SrcCode) + @"(?![A-Za-z0-9])", s.DstCode, RegexOptions.IgnoreCase);
        }
        return Path.Combine(dir, newName);
    }

    /// <summary>File chữ hay nhị phân — theo NỘI DUNG (dự án dùng .f / .v / .003... cho XML), file chưa có thì theo đuôi.</summary>
    public static bool IsText(string path)
    {
        if (!File.Exists(path)) return TextExt.Contains(Path.GetExtension(path).ToLowerInvariant()) || Path.GetExtension(path).Equals(".f", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[8192];
            var n = fs.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true;   // UTF-16 có BOM
            for (var i = 0; i < n; i++) if (buf[i] == 0) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Đọc chữ, nhận đúng bảng mã (UTF-8 có/không BOM, UTF-16) để ghi lại y như cũ.</summary>
    public static string ReadText(string path, out Encoding encoding)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, true); return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = new UnicodeEncoding(true, true); return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = Utf8Bom; return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); }
        encoding = new UTF8Encoding(false);
        return Encoding.UTF8.GetString(bytes);
    }

    // ---- Bước 3: file include / entity tham chiếu ------------------------------------------------------

    /// <summary>Các file mà file nguồn tham chiếu qua &lt;!ENTITY ... SYSTEM "..."&gt; (tương đối so với gốc source) + có tồn tại không.</summary>
    public static List<(string Rel, bool Exists, bool Specific)> References(string sourceRoot, IEnumerable<string> files, string prefix, string code)
    {
        var seen = new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in files)
        {
            var full = Path.Combine(sourceRoot, rel);
            if (!IsText(full) || !File.Exists(full)) continue;
            string text;
            try { text = File.ReadAllText(full); } catch { continue; }
            foreach (Match m in Regex.Matches(text, @"SYSTEM\s+""([^""]+)"""))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, m.Groups[1].Value.Replace('/', '\\')));
                var r = Path.GetRelativePath(sourceRoot, target);
                if (seen.ContainsKey(r)) continue;
                var name = Path.GetFileName(r);
                // File include "riêng" của chứng từ: tên có tiền tố đứng đầu 1 từ (CIGrid.ent, CIDetail.ent, Extra.CIDetail) hoặc mã ct
                // — các tham chiếu này bị đổi sang tên mới nên file cũng phải chép theo; include khác là dùng chung, không chép.
                var specific = Regex.IsMatch(name, @"(?<![A-Za-z0-9])" + Regex.Escape(prefix) + "(?=[A-Z_]|\\.)") ||
                               Regex.IsMatch(name, @"(?<![A-Za-z0-9])" + Regex.Escape(code) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
                seen[r] = (File.Exists(target), specific);
            }
        }
        return seen.Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2)).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- Đổi nội dung --------------------------------------------------------------------------------

    /// <summary>
    /// Đổi nội dung 1 file: SysID → SysID mới; tiền tố (CI + chữ HOA) → tiền tố mới; mã chứng từ ở id="..." và chuỗi '...' / "...";
    /// tiêu đề v/e của chứng từ; và (nếu tạo bảng mới) m48$ / d48$ / c48$ ... → m{số mới}$.
    /// </summary>
    public static string Transform(string text, VoucherCloneSpec s)
    {
        var t = text;
        if (s.SrcSysId != s.DstSysId) t = Regex.Replace(t, @"\b" + Regex.Escape(s.SrcSysId) + @"\b", s.DstSysId);
        if (s.SrcPrefix != s.DstPrefix)
            t = Regex.Replace(t, @"(?<![A-Za-z0-9_])" + Regex.Escape(s.SrcPrefix) + "(?=[A-Z])([A-Za-z0-9_]*)",
                m => s.SharedNames.Contains(m.Value) ? m.Value : s.DstPrefix + m.Groups[1].Value);
        if (s.SrcCode != s.DstCode)
        {
            t = t.Replace($"id=\"{s.SrcCode}\"", $"id=\"{s.DstCode}\"")
                 .Replace($"'{s.SrcCode}'", $"'{s.DstCode}'")
                 .Replace($"\"{s.SrcCode}\"", $"\"{s.DstCode}\"");
        }
        // Tiêu đề v/e của chứng từ (không phân biệt hoa thường: Dir ghi "đơn đặt hàng", Main ghi "Đơn đặt hàng") + description="..." ở file đăng ký.
        if (s.SrcTitleV.Length > 0 && s.DstTitleV.Length > 0)
            t = Regex.Replace(t, "\\bv=\"" + Regex.Escape(s.SrcTitleV) + "\"", "v=\"" + s.DstTitleV.Replace("$", "$$") + "\"", RegexOptions.IgnoreCase);
        if (s.SrcTitleE.Length > 0 && s.DstTitleE.Length > 0)
            t = Regex.Replace(t, "\\b(e|description)=\"" + Regex.Escape(s.SrcTitleE) + "\"", "$1=\"" + s.DstTitleE.Replace("$", "$$") + "\"", RegexOptions.IgnoreCase);
        if (s.NewTables && s.SrcTableNo.Length > 0 && s.DstTableNo.Length > 0 && s.SrcTableNo != s.DstTableNo)
            t = Regex.Replace(t, @"\b([a-z]{1,3})" + Regex.Escape(s.SrcTableNo) + @"\$", "${1}" + s.DstTableNo + "$", RegexOptions.IgnoreCase);
        // Tên file Main (tpblcthdx.aspx → tpblcthdz.aspx) trong link / url
        foreach (var (from, to) in s.FileRenames)
            if (from.Length > 0 && !from.Equals(to, StringComparison.OrdinalIgnoreCase))
                t = Regex.Replace(t, @"(?<![A-Za-z0-9_])" + Regex.Escape(from) + @"(?![A-Za-z0-9_])", to, RegexOptions.IgnoreCase);
        return t;
    }

    /// <summary>Ghi các file đã đổi vào thư mục đích (giữ cấu trúc App_Data\... / Main\...). File không phải chữ thì chép nguyên.</summary>
    public static List<string> WriteFiles(string sourceRoot, string outputRoot, IEnumerable<(string Rel, string TargetRel)> files, VoucherCloneSpec s)
    {
        var written = new List<string>();
        foreach (var (rel, targetRel) in files)
        {
            var src = Path.Combine(sourceRoot, rel);
            var dst = Path.Combine(outputRoot, targetRel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (IsText(src))
            {
                var text = Transform(ReadText(src, out var enc), s);
                File.WriteAllText(dst, text, enc);
            }
            else File.Copy(src, dst, overwrite: true);
            written.Add(dst);
        }
        return written;
    }

    /// <summary>Ghi bản file đăng ký dùng chung đã thêm dòng chứng từ mới (giữ đúng tên + bảng mã).</summary>
    public static List<string> WriteRegistry(string sourceRoot, string outputRoot, IEnumerable<string> rels, VoucherCloneSpec s)
    {
        var written = new List<string>();
        foreach (var rel in rels)
        {
            var src = Path.Combine(sourceRoot, rel);
            var text = ReadText(src, out var enc);
            AddRegistryLines(text, s, out var newText);
            var dst = Path.Combine(outputRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllText(dst, newText, enc);
            written.Add(dst);
        }
        return written;
    }

    /// <summary>Chép nguyên (không đổi gì) — controller / include dự án mà chứng từ gọi tới, khi xuất gói sang dự án khác.</summary>
    public static List<string> CopyAsIs(string sourceRoot, string outputRoot, IEnumerable<string> rels)
    {
        var written = new List<string>();
        foreach (var rel in rels)
        {
            var dst = Path.Combine(outputRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(Path.Combine(sourceRoot, rel), dst, overwrite: true);
            written.Add(dst);
        }
        return written;
    }

    // ---- Bước 4: SQL --------------------------------------------------------------------------------

    /// <summary>
    /// Gợi ý số nhóm bảng còn trống (chưa có bảng [chữ]{no}$000000 nào). Chứng từ tự thêm thường dùng số 3 chữ số (vd m164 trong gói FCode
    /// mẫu): nếu dự án đã có số ≥ 100 thì lấy số lớn nhất + 1; chưa có thì số 2 chữ số còn trống đầu tiên từ 70.
    /// </summary>
    public async Task<string> SuggestTableNoAsync()
    {
        await using var conn = _connections.CreateConnection(false);
        await conn.OpenAsync();
        var used = new HashSet<int>();
        await using (var cmd = new SqlCommand("SELECT name FROM sys.tables WHERE name LIKE '%$000000'", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                if (int.TryParse(TableNoOf(r.GetString(0)), out var no)) used.Add(no);
        var big = used.Where(n => n >= 100).DefaultIfEmpty(0).Max();
        if (big > 0) return (big + 1).ToString();
        for (var n = 70; n <= 99; n++) if (!used.Contains(n)) return n.ToString("D2");
        for (var n = 1; n < 70; n++) if (!used.Contains(n)) return n.ToString("D2");
        return "";
    }
}
