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

    /// <summary>Từ trong tên file của file CÓ chép (CIDetail, CI_Field, CI...) — luôn đổi khi gặp trong nội dung.</summary>
    public HashSet<string> OwnWords { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Từ trong tên file của file KHÔNG chép nhưng cùng tiền tố — không được đổi khi gặp trong nội dung, kẻo trỏ sang file không tồn tại.</summary>
    public HashSet<string> SharedNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tiền tố dài hơn của chứng từ khác bắt đầu bằng tiền tố nguồn (HD → HDC, AS → ASA01, ASAsset) — file / từ của chúng không thuộc chứng từ nguồn.</summary>
    public List<string> OtherPrefixes { get; set; } = new();

    /// <summary>Entity giữ nguyên tên nguồn: định nghĩa dùng chung với controller khác, hoặc trải nhiều dòng nên không chép được.</summary>
    public HashSet<string> KeepEntities { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Entity định nghĩa ở file dùng chung, chỉ chứng từ nguồn dùng → thêm dòng định nghĩa cho tên mới ngay sau dòng gốc.</summary>
    public HashSet<string> CopyEntities { get; set; } = new(StringComparer.Ordinal);

    /// <summary>File dùng chung (tương đối so với gốc source) chứa định nghĩa trong <see cref="CopyEntities"/>.</summary>
    public HashSet<string> EntityFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File đang tick ở bước 2 (tương đối so với gốc source); rỗng = chưa lập danh sách.</summary>
    public List<string> Copied { get; set; } = new();

    /// <summary>Đổi tên file không theo tiền tố (Main\tpblcthdx.aspx → tpblcthdz.aspx) — áp cả trong nội dung (link menu, dmct9.url...).</summary>
    public List<(string From, string To)> FileRenames { get; set; } = new();

    private Regex? _tokens;
    private int _tokensKept = -1;

    /// <summary>Regex 1 lượt của nội dung: vùng được bảo vệ (group "keep") hoặc 1 từ bắt đầu bằng tiền tố nguồn.</summary>
    internal Regex Tokens()
    {
        if (_tokens != null && _tokensKept == KeepEntities.Count) return _tokens;
        var keep = new List<string> { @"<Encrypted>[^<\]]*" };   // base64 của khối mã hoá: không được đụng tới
        if (KeepEntities.Count > 0)
        {
            var names = string.Join("|", KeepEntities.OrderByDescending(n => n.Length).Select(Regex.Escape));
            keep.Add(@"[&%](?:" + names + @")(?=;)");
            keep.Add(@"<!ENTITY\s+(?:%\s+)?(?:" + names + @")(?=\s)");
        }
        _tokens = new Regex("(?<keep>" + string.Join("|", keep) + ")|" + VoucherCloneService.WordPattern(SrcPrefix), RegexOptions.CultureInvariant);
        _tokensKept = KeepEntities.Count;
        return _tokens;
    }
}

/// <summary>1 dòng định nghĩa entity; <see cref="Line"/> = null khi không chép được theo dòng (nhiều dòng / nhiều định nghĩa trên 1 dòng).</summary>
public record EntityDef(string Rel, string? Line);

/// <summary>Toàn bộ entity trong Controllers: định nghĩa ở đâu, được file nào gọi. Dựng 1 lần cho mỗi thư mục source.</summary>
public class EntityIndex
{
    private static readonly Regex DefName = new(@"<!ENTITY\s+(?:%\s+)?([^\s""'%]+)\s", RegexOptions.CultureInvariant);
    private static readonly Regex WholeDef = new(@"^\s*<!ENTITY\s+(?:%\s+)?[^\s""'%]+\s+(?:SYSTEM\s+)?(?:""[^""]*""|'[^']*')\s*>\s*$", RegexOptions.CultureInvariant);
    internal static readonly Regex Use = new(@"[&%]([A-Za-z_][\w.\-]*);", RegexOptions.CultureInvariant);
    internal static readonly Regex SystemPath = new(@"\sSYSTEM\s+""([^""]+)""", RegexOptions.CultureInvariant);

    public Dictionary<string, List<EntityDef>> Defs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Uses { get; } = new(StringComparer.Ordinal);

    /// <summary>Định nghĩa SYSTEM: (entity, file định nghĩa, tên file được trỏ tới).</summary>
    public List<(string Name, string DefRel, string Target)> SystemRefs { get; } = new();

    /// <summary>Tên entity mà dòng này định nghĩa, nếu có.</summary>
    public static string? DefinedBy(string line) => DefName.Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    public static EntityIndex Build(string sourceRoot)
    {
        var index = new EntityIndex();
        if (VoucherCloneService.ControllersDir(sourceRoot) is not { } controllers) return index;
        var templates = Path.Combine(controllers, "Templates") + Path.DirectorySeparatorChar;
        foreach (var f in Directory.EnumerateFiles(controllers, "*", SearchOption.AllDirectories))
        {
            if (f.StartsWith(templates, StringComparison.OrdinalIgnoreCase) || new FileInfo(f).Length > 8_000_000 || !VoucherCloneService.IsText(f)) continue;
            string text;
            try { text = VoucherCloneService.ReadText(f, out _); } catch { continue; }
            var rel = Path.GetRelativePath(sourceRoot, f);
            foreach (Match m in Use.Matches(text))
            {
                if (!index.Uses.TryGetValue(m.Groups[1].Value, out var users)) index.Uses[m.Groups[1].Value] = users = new(StringComparer.OrdinalIgnoreCase);
                users.Add(rel);
            }
            if (!text.Contains("<!ENTITY", StringComparison.Ordinal)) continue;
            foreach (var line in text.Split('\n'))
            {
                var count = DefName.Matches(line).Count;
                if (count == 0) continue;
                var trimmed = line.TrimEnd('\r');
                foreach (Match m in DefName.Matches(line))
                {
                    if (!index.Defs.TryGetValue(m.Groups[1].Value, out var defs)) index.Defs[m.Groups[1].Value] = defs = new();
                    defs.Add(new EntityDef(rel, count == 1 && WholeDef.IsMatch(trimmed) ? trimmed : null));
                    if (count == 1 && SystemPath.Match(trimmed) is { Success: true } sys)
                        index.SystemRefs.Add((m.Groups[1].Value, rel, Path.GetFileName(sys.Groups[1].Value.Replace('/', '\\'))));
                }
            }
        }
        return index;
    }
}

/// <summary>
/// "Tạo chứng từ" — nhân bản 1 chứng từ có sẵn sang mã mới, theo quy ước source FastBusiness:
/// <list type="bullet">
/// <item>Chứng từ = nhóm file cùng tiền tố trong App_Data\Controllers (Dir/Grid/Filter/Templates...): CITran, CIDetail, CIMasterImport...;
///   SysID = tiền tố + "Tran". Tiền tố phải đi liền chữ HOA để "SI" không bắt nhầm "SI2Tran"; tiền tố dài hơn của chứng từ khác
///   (HD / HDC) được loại riêng, xem <see cref="VoucherCloneSpec.OtherPrefixes"/>.</item>
/// <item>Tên file và nội dung đổi qua cùng 1 hàm (<see cref="MapWord"/>), 1 lượt duy nhất — tham chiếu luôn khớp file được tạo.</item>
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
    private static readonly UTF8Encoding Utf8Strict = new(false, throwOnInvalidBytes: true);
    private static readonly string[] TextExt = { ".xml", ".txt", ".ent", ".aspx", ".js", ".css", ".sql", ".htm", ".html" };
    private static readonly Regex Backup = new(@"( - Copy|_bk|\.bak$|\.old$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string PrefixOf(string sysId) =>
        sysId.EndsWith("Tran", StringComparison.Ordinal) && sysId.Length > 4 ? sysId[..^4] : sysId;

    public static string TableNoOf(string table)
    {
        var m = Regex.Match(table, @"^[a-z]+(\w*?)\$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>File sao lưu (SVTran - Copy.xml, SOTran_bk_260625V.xml): liệt kê nhưng mặc định không chép.</summary>
    public static bool IsBackupName(string fileName) => Backup.IsMatch(fileName);

    /// <summary>Đường mạng (UNC hoặc ổ map) — không tự đọc khi mở form, chờ người dùng bấm Quét.</summary>
    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try { return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network; }
        catch { return false; }
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
        // Dir\{SysID}.xml hoặc .f (dự án lưu XML với đuôi .f) — mỗi SysID lấy 1 file; bỏ file sao lưu.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".xml" or ".f" && !IsBackupName(Path.GetFileName(p)))
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

    /// <summary>Tiền tố dài hơn của chứng từ khác bắt đầu bằng tiền tố này: HD → HDC; AS → ASA01, ASAsset.</summary>
    public static List<string> OtherPrefixes(IEnumerable<VoucherInfo> vouchers, string prefix) =>
        vouchers.Select(v => v.Prefix).Where(p => p.Length > prefix.Length && p.StartsWith(prefix, StringComparison.Ordinal)).Distinct().ToList();

    // ---- Đổi tên: 1 hàm duy nhất cho cả tên file lẫn nội dung ------------------------------------------

    /// <summary>Mẫu 1 "từ" bắt đầu bằng tiền tố: không dính ký tự chữ / số / _ ở trước, lấy trọn phần chữ / số / _ ở sau.</summary>
    internal static string WordPattern(string prefix) => @"(?<![A-Za-z0-9_])" + Regex.Escape(prefix) + "[A-Za-z0-9_]*";

    /// <summary>Từ có đúng là tiền tố + phần sau hợp lệ (rỗng, chữ HOA, _) và không thuộc chứng từ có tiền tố dài hơn.</summary>
    private static bool IsPrefixWord(string word, string prefix, IReadOnlyCollection<string> others)
    {
        if (!word.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (word.Length > prefix.Length && !(char.IsAsciiLetterUpper(word[prefix.Length]) || word[prefix.Length] == '_')) return false;
        return !others.Any(o => word.StartsWith(o, StringComparison.Ordinal)
                                && (word.Length == o.Length || char.IsAsciiLetterUpper(word[o.Length]) || word[o.Length] == '_'));
    }

    private static IEnumerable<string> WordsOf(string fileName, string prefix, IReadOnlyCollection<string> others) =>
        Regex.Matches(fileName, WordPattern(prefix)).Select(m => m.Value).Where(w => IsPrefixWord(w, prefix, others));

    /// <summary>Tên file có thuộc chứng từ không: có 1 từ theo tiền tố — CITran.f, Extra.CIDetail, CI_Field.txt, ImportOverWriteVoucher.Bill.</summary>
    public static bool IsOwnFileName(string fileName, string prefix, IReadOnlyCollection<string>? others = null) =>
        prefix.Length > 0 && WordsOf(fileName, prefix, others ?? Array.Empty<string>()).Any();

    /// <summary>
    /// Đổi 1 từ. Trong tên file: mọi từ theo tiền tố. Trong nội dung: thêm điều kiện để không đụng từ tình cờ cùng đầu (CDATA, ASSET, CITY):
    /// từ phải là tên file có chép, hoặc tên kiểu CamelCase (entity, hàm JS); tên file KHÔNG chép thì giữ nguyên.
    /// </summary>
    private static string MapWord(string word, VoucherCloneSpec s, bool fileName)
    {
        var prefix = s.SrcPrefix;
        if (!IsPrefixWord(word, prefix, s.OtherPrefixes)) return word;
        var rest = word[prefix.Length..];
        // So SysID không phân biệt hoa thường: source có file BI.PQTRan.Fields.txt mà nội dung gọi BI.PQTran.Fields.txt (Windows coi là 1).
        var sysId = word.Equals(s.SrcSysId, StringComparison.OrdinalIgnoreCase);
        if (!fileName && !sysId)
        {
            if (s.SharedNames.Contains(word)) return word;
            if (!s.OwnWords.Contains(word) && (rest.Length == 0 || rest[0] == '_' || !rest.Any(char.IsLower))) return word;
        }
        return sysId ? s.DstSysId : s.DstPrefix + rest;
    }

    /// <summary>Tên entity sau khi đổi (không xét danh sách giữ nguyên).</summary>
    private static string MapEntity(string name, VoucherCloneSpec s) =>
        Regex.Replace(name, WordPattern(s.SrcPrefix), m => MapWord(m.Value, s, false));

    // ---- Bước 2: file của chứng từ nguồn -------------------------------------------------------------

    /// <summary>
    /// File thuộc chứng từ (tương đối so với gốc source), ở mọi thư mục con của Controllers kể cả Include: tên có tiền tố đứng đầu 1 từ
    /// — Dir\CITran.f, Grid\CIDetail.f, Include\Extender.CITran, Include\XML\Config\Fields\CIGrid.ent / CIField.txt / CIView.txt.
    /// </summary>
    public static List<string> SourceFiles(string sourceRoot, string prefix, IReadOnlyCollection<string>? others = null)
    {
        var controllers = ControllersDir(sourceRoot);
        var result = new List<string>();
        if (controllers == null || prefix.Length == 0) return result;
        foreach (var f in Directory.EnumerateFiles(controllers, "*", SearchOption.AllDirectories))
            if (IsOwnFileName(Path.GetFileName(f), prefix, others)) result.Add(Path.GetRelativePath(sourceRoot, f));
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Chuẩn bị spec theo danh sách file sẽ chép: <see cref="VoucherCloneSpec.OwnWords"/>, <see cref="VoucherCloneSpec.SharedNames"/>, rồi quyết định
    /// từng entity bị đổi tên (xem <see cref="PlanEntities"/>). Gọi trước mọi lần đổi nội dung để xem trước / SQL / ghi file ra cùng 1 kết quả.
    /// </summary>
    public static void Prepare(string sourceRoot, VoucherCloneSpec s, IEnumerable<string> copiedRel, EntityIndex index)
    {
        var copied = copiedRel.ToList();
        s.OwnWords = new(copied.SelectMany(r => WordsOf(Path.GetFileName(r), s.SrcPrefix, s.OtherPrefixes)), StringComparer.OrdinalIgnoreCase);
        s.SharedNames = new(StringComparer.OrdinalIgnoreCase);
        if (ControllersDir(sourceRoot) is { } controllers && s.SrcPrefix.Length > 0)
            foreach (var f in Directory.EnumerateFiles(controllers, "*", SearchOption.AllDirectories))
                foreach (var w in WordsOf(Path.GetFileName(f), s.SrcPrefix, s.OtherPrefixes))
                    if (!s.OwnWords.Contains(w)) s.SharedNames.Add(w);
        PlanEntities(sourceRoot, s, copied, index);
    }

    /// <summary>
    /// Quyết định từng entity sẽ đổi tên của chứng từ:
    /// <list type="bullet">
    /// <item>Định nghĩa trong file có chép, không file ngoài nào gọi → đổi theo file.</item>
    /// <item>Định nghĩa ở file dùng chung, chỉ chứng từ này dùng, 1 dòng → thêm dòng cho tên mới ngay sau dòng gốc (<see cref="VoucherCloneSpec.CopyEntities"/>).</item>
    /// <item>Còn lại (controller khác cùng dùng, định nghĩa nhiều dòng, định nghĩa nằm ở controller khác) → giữ tên nguồn (<see cref="VoucherCloneSpec.KeepEntities"/>).</item>
    /// </list>
    /// Dòng được chép có thể gọi entity khác nên duyệt tiếp các tên trong dòng đó.
    /// </summary>
    private static void PlanEntities(string sourceRoot, VoucherCloneSpec s, IReadOnlyCollection<string> copied, EntityIndex index)
    {
        s.KeepEntities = new(StringComparer.Ordinal);
        s.CopyEntities = new(StringComparer.Ordinal);
        s.EntityFiles = new(StringComparer.OrdinalIgnoreCase);
        var own = new HashSet<string>(copied, StringComparer.OrdinalIgnoreCase);

        // File có chép nhưng bản GỐC vẫn được nạp: file dùng chung trỏ tới nó qua entity không đổi tên (CompactMode.ent: XMLGridToolbarPSDetail →
        // Grid.Toolbar.PSDetail.f). Entity gọi trong file đó phải tính là "file khác đang dùng", nếu đổi tên thì bản gốc gọi tên không còn tồn tại.
        var byName = own.ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entity, defRel, target) in index.SystemRefs)
            if (!own.Contains(defRel) && byName.Contains(target) && MapEntity(entity, s) == entity)
                foreach (var rel in byName[target]) loaded.Add(rel);
        bool Private(string rel) => own.Contains(rel) && !loaded.Contains(rel);

        var queue = new Queue<string>();
        foreach (var (name, users) in index.Uses) if (users.Any(own.Contains)) queue.Enqueue(name);
        foreach (var (name, defs) in index.Defs) if (defs.Any(d => own.Contains(d.Rel))) queue.Enqueue(name);

        var token = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(s.SrcSysId) + @"(?![A-Za-z0-9_])");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name) || MapEntity(name, s) == name || !index.Defs.TryGetValue(name, out var defs)) continue;
            var defFiles = new HashSet<string>(defs.Select(d => d.Rel), StringComparer.OrdinalIgnoreCase);
            var usedElsewhere = index.Uses.TryGetValue(name, out var users) && users.Any(u => !Private(u) && !defFiles.Contains(u));

            // Định nghĩa dùng chung (cùng tên có thể có cả ở file riêng: FormulaExpression.CPCharge ở Clipboard.CPDetail và Clipboard.BankCharges).
            var shared = defs.Where(d => !own.Contains(d.Rel) && !IsVoucherController(d.Rel)).ToList();
            if (shared.Count == 0 && defs.Any(d => own.Contains(d.Rel)))
            {
                if (usedElsewhere) s.KeepEntities.Add(name);
                continue;
            }
            // Trỏ tới file có chép (SYSTEM "...Grid.Toolbar.PSDetail.f"): phải có tên mới, nếu giữ tên thì bản sao đó không được nạp mà bản gốc lại gọi entity đã đổi.
            var pointsToOwn = shared.Count > 0 && shared.All(d => d.Line != null && PointsToOwnFile(d.Line, s));
            if (shared.Count == 0 || shared.Any(d => d.Line == null) || (usedElsewhere && !token.IsMatch(name) && !pointsToOwn))
            {
                s.KeepEntities.Add(name);
                continue;
            }
            s.CopyEntities.Add(name);
            foreach (var d in shared)
            {
                s.EntityFiles.Add(d.Rel);
                foreach (Match m in EntityIndex.Use.Matches(d.Line!)) queue.Enqueue(m.Groups[1].Value);
            }
        }
    }

    /// <summary>Dòng định nghĩa SYSTEM trỏ tới 1 file trong danh sách chép.</summary>
    private static bool PointsToOwnFile(string line, VoucherCloneSpec s)
    {
        var m = EntityIndex.SystemPath.Match(line);
        return m.Success && WordsOf(Path.GetFileName(m.Groups[1].Value.Replace('/', '\\')), s.SrcPrefix, s.OtherPrefixes).Any(s.OwnWords.Contains);
    }

    /// <summary>File controller của 1 màn hình (Dir\x.f, Grid\x.f...) — định nghĩa trong đó là của riêng màn hình đó, không phải file dùng chung.</summary>
    private static bool IsVoucherController(string rel)
    {
        var parts = rel.Split(Path.DirectorySeparatorChar);
        return parts.Length == 4 && parts[2] is "Dir" or "Grid" or "Filter" or "Lookup" or "Report";
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

    /// <summary>File cấu hình đăng ký chứng từ: trong thư mục Config (bất kỳ cấp) hoặc nằm thẳng trong Include — không đụng controller của chứng từ khác.</summary>
    private static bool IsRegistryFile(string relToControllers)
    {
        var parts = relToControllers.Split(Path.DirectorySeparatorChar);
        return parts.Take(parts.Length - 1).Any(p => p.Equals("Config", StringComparison.OrdinalIgnoreCase))
               || (parts.Length == 2 && parts[0].Equals("Include", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// File dùng chung sẽ thêm dòng cho chứng từ mới ngay sau dòng gốc: file đăng ký có SysID nguồn, và file định nghĩa entity trong
    /// <see cref="VoucherCloneSpec.CopyEntities"/>. Trả (file, các dòng sẽ thêm).
    /// </summary>
    public static List<(string Rel, List<string> Added)> RegistryEdits(string sourceRoot, VoucherCloneSpec s, IEnumerable<string> ownFiles)
    {
        var result = new List<(string, List<string>)>();
        if (ControllersDir(sourceRoot) is not { } controllers || s.SrcSysId.Length == 0) return result;
        var own = new HashSet<string>(ownFiles, StringComparer.OrdinalIgnoreCase);
        var token = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(s.SrcSysId) + @"(?![A-Za-z0-9_])");
        foreach (var f in Directory.EnumerateFiles(controllers, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, f);
            var registry = IsRegistryFile(Path.GetRelativePath(controllers, f));
            if ((!registry && !s.EntityFiles.Contains(rel)) || own.Contains(rel) || new FileInfo(f).Length > 4_000_000 || !IsText(f)) continue;
            var text = ReadText(f, out _);
            if (!s.EntityFiles.Contains(rel) && !token.IsMatch(text)) continue;
            var added = AddRegistryLines(text, s, registry, out _);
            if (added.Count > 0) result.Add((rel, added));
        }
        return result.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Sau mỗi dòng cần nhân bản, chèn bản đã đổi sang chứng từ mới (nếu file chưa có dòng đó). Dòng cần nhân bản: có SysID nguồn
    /// (khi <paramref name="registry"/> = file đăng ký), hoặc định nghĩa 1 entity trong <see cref="VoucherCloneSpec.CopyEntities"/>.
    /// </summary>
    public static List<string> AddRegistryLines(string text, VoucherCloneSpec s, bool registry, out string newText)
    {
        var token = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(s.SrcSysId) + @"(?![A-Za-z0-9_])");
        var eol = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(eol);
        var present = new HashSet<string>(lines.Select(l => l.Trim()));
        var output = new List<string>(lines.Length + 8);
        var added = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var hit = registry && token.IsMatch(line);
            // Dòng mở khối điều kiện <![%Conditional.FATran.Nested;[: nhân bản nguyên khối tới ]]> tương ứng, chép riêng dòng mở sẽ làm hỏng DTD.
            if (hit && line.TrimStart().StartsWith("<![%", StringComparison.Ordinal))
            {
                var end = ConditionalEnd(lines, i);
                if (end < 0) { output.Add(line); continue; }
                var block = lines[i..(end + 1)];
                output.AddRange(block);
                i = end;
                var copies = block.Select(l => Transform(l, s)).ToList();
                if (copies[0] == line || present.Contains(copies[0].Trim())) continue;
                output.AddRange(copies);
                added.AddRange(copies.Select(c => c.Trim()).Where(c => c.Length > 0));
                present.Add(copies[0].Trim());
                continue;
            }
            output.Add(line);
            if (!hit && EntityIndex.DefinedBy(line) is { } name) hit = s.CopyEntities.Contains(name);
            if (!hit) continue;
            var copy = Transform(line, s);
            if (copy == line || present.Contains(copy.Trim())) continue;
            output.Add(copy); added.Add(copy.Trim()); present.Add(copy.Trim());
        }
        newText = string.Join(eol, output);
        return added;
    }

    /// <summary>Dòng đóng ]]> của khối điều kiện mở ở dòng <paramref name="start"/> (tính cả khối lồng nhau), -1 nếu không thấy.</summary>
    private static int ConditionalEnd(string[] lines, int start)
    {
        var depth = 0;
        for (var i = start; i < lines.Length; i++)
        {
            depth += Regex.Matches(lines[i], @"<!\[%").Count - Regex.Matches(lines[i], @"\]\]>").Count;
            if (depth <= 0) return i;
        }
        return -1;
    }

    /// <summary>Thêm dòng cho 1 file theo đường dẫn tương đối so với gốc source — tự xác định có phải file đăng ký không.</summary>
    public static List<string> AddRegistryLines(string sourceRoot, string rel, string text, VoucherCloneSpec s, out string newText)
    {
        var registry = ControllersDir(sourceRoot) is { } c && IsRegistryFile(Path.GetRelativePath(c, Path.Combine(sourceRoot, rel)));
        return AddRegistryLines(text, s, registry, out newText);
    }

    /// <summary>Tên file đích: đổi các từ theo tiền tố trong TÊN file (giữ thư mục), cùng hàm với nội dung. File Main không theo tiền tố thì đổi theo mã ct.</summary>
    public static string TargetPath(string rel, VoucherCloneSpec s)
    {
        var dir = Path.GetDirectoryName(rel) ?? "";
        var name = Path.GetFileName(rel);
        var main = dir.Split(Path.DirectorySeparatorChar)[0].Equals("Main", StringComparison.OrdinalIgnoreCase);
        var newName = Regex.Replace(name, WordPattern(s.SrcPrefix), m => MapWord(m.Value, s, true));
        if (newName == name && s.SrcCode.Length > 0)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            // Main của dự án kiểu tpblcthdx.aspx (tiền tố dự án + "ct" + mã ct) → tpblcthdz.aspx
            if (main && stem.EndsWith(s.SrcCode, StringComparison.OrdinalIgnoreCase))
                newName = stem[..^s.SrcCode.Length] + s.DstCode.ToLowerInvariant() + Path.GetExtension(name);
            else
                newName = Regex.Replace(name, @"(?<![A-Za-z0-9])" + Regex.Escape(s.SrcCode) + @"(?![A-Za-z0-9])", s.DstCode, RegexOptions.IgnoreCase);
        }
        // Main không có mã trong tên (phieuthu.aspx): vẫn phải ra file mới, không được đè file của chứng từ nguồn.
        if (newName == name && main)
            newName = Path.GetFileNameWithoutExtension(name) + s.DstCode.ToLowerInvariant() + Path.GetExtension(name);
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

    /// <summary>Đọc chữ, nhận đúng bảng mã (UTF-8 có/không BOM, UTF-16) để ghi lại y như cũ. Không phải UTF-8 (ANSI): đọc từng byte (Latin1) để ghi lại không mất dữ liệu.</summary>
    public static string ReadText(string path, out Encoding encoding)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, true); return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = new UnicodeEncoding(true, true); return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = Utf8Bom; return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); }
        try
        {
            var text = Utf8Strict.GetString(bytes);
            encoding = new UTF8Encoding(false);
            return text;
        }
        catch (DecoderFallbackException)
        {
            encoding = Encoding.Latin1;
            return Encoding.Latin1.GetString(bytes);
        }
    }

    // ---- Bước 3: file include / entity tham chiếu ------------------------------------------------------

    /// <summary>Các file mà file nguồn tham chiếu qua &lt;!ENTITY ... SYSTEM "..."&gt; (tương đối so với gốc source) + có tồn tại không.</summary>
    public static List<(string Rel, bool Exists, bool Specific)> References(string sourceRoot, IEnumerable<string> files, string prefix, string code, IReadOnlyCollection<string>? others = null)
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
                var specific = IsOwnFileName(name, prefix, others) ||
                               Regex.IsMatch(name, @"(?<![A-Za-z0-9])" + Regex.Escape(code) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
                seen[r] = (File.Exists(target), specific);
            }
        }
        return seen.Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2)).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- Đổi nội dung --------------------------------------------------------------------------------

    /// <summary>
    /// Đổi nội dung 1 file: SysID và các từ theo tiền tố (1 lượt, xem <see cref="MapWord"/>; không đụng entity giữ tên, khối &lt;Encrypted&gt;);
    /// mã chứng từ ở id="..." và chuỗi '...' / "..."; tiêu đề v/e của chứng từ; và (nếu tạo bảng mới) m48$ / d48$ / c48$ ... → m{số mới}$.
    /// </summary>
    public static string Transform(string text, VoucherCloneSpec s)
    {
        var t = text;
        if (s.SrcPrefix.Length > 0 && (s.SrcSysId != s.DstSysId || s.SrcPrefix != s.DstPrefix))
            t = s.Tokens().Replace(t, m => m.Groups["keep"].Success ? m.Value : MapWord(m.Value, s, false));
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

    /// <summary>Đổi tham số menu (Link ?..., Parameter): như nội dung file, thêm mã ct đứng riêng không có dấu nháy (?ma_ct=PT2, PT2).</summary>
    public static string TransformMenu(string text, VoucherCloneSpec s) =>
        Regex.Replace(Transform(text, s), @"(?<![A-Za-z0-9_])" + Regex.Escape(s.SrcCode) + @"(?![A-Za-z0-9_])", s.DstCode.Replace("$", "$$"), RegexOptions.IgnoreCase);

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
            else CopyWritable(src, dst);
            written.Add(dst);
        }
        return written;
    }

    /// <summary>Chép đè, bỏ thuộc tính chỉ đọc (file nguồn chỉ đọc thì bản chép cũng vậy, lần tạo lại sẽ không ghi đè được).</summary>
    private static void CopyWritable(string src, string dst)
    {
        if (File.Exists(dst)) File.SetAttributes(dst, FileAttributes.Normal);
        File.Copy(src, dst, overwrite: true);
        File.SetAttributes(dst, File.GetAttributes(dst) & ~FileAttributes.ReadOnly);
    }

    /// <summary>Ghi bản file đăng ký dùng chung đã thêm dòng chứng từ mới (giữ đúng tên + bảng mã).</summary>
    public static List<string> WriteRegistry(string sourceRoot, string outputRoot, IEnumerable<string> rels, VoucherCloneSpec s)
    {
        var written = new List<string>();
        foreach (var rel in rels)
        {
            var src = Path.Combine(sourceRoot, rel);
            var text = ReadText(src, out var enc);
            AddRegistryLines(sourceRoot, rel, text, s, out var newText);
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
            CopyWritable(Path.Combine(sourceRoot, rel), dst);
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
