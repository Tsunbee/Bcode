using System.Text.Json;
using System.Text.Json.Serialization;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// 1 "Request" trong Advance Note (Note (New)): ghi chú 1 yêu cầu (y/c) đã chỉnh sửa và khai báo những gì cần
/// đưa vào gói update của yêu cầu đó — controller cần Gen All, đường dẫn file, tên procedure, script đầu gói.
/// Tên thuộc tính JSON viết thường đầu (camelCase) để trang WebView2 dùng trực tiếp.
/// </summary>
/// <summary>1 lần Generate Update của một y/c: thư mục gói đã tạo và thời điểm tạo — để sau này mở lại y/c biết đã gen ra link nào.</summary>
public class GenerationRecord
{
    public string Path { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
}

/// <summary>1 table được chọn đưa vào gói update: sinh script tạo table (Structure) và/hoặc dữ liệu (Data, có thể lọc bằng Where).</summary>
public class TableSelection
{
    public string Name { get; set; } = "";
    /// <summary>true = table ở database Sys, false = App.</summary>
    public bool Sys { get; set; }
    public bool Structure { get; set; }
    public bool Data { get; set; }
    /// <summary>Điều kiện lọc dòng dữ liệu (không cần chữ WHERE); trống = toàn bộ bảng.</summary>
    public string Where { get; set; } = "";
}

public class AdvanceRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Mobile { get; set; }
    public DateTime Begin { get; set; } = DateTime.Now;
    public bool Done { get; set; }
    public DateTime? Updated { get; set; }

    /// <summary>Các lần Generate Update gần nhất của y/c này (mới nhất trước, giữ tối đa <see cref="MaxGenerations"/>).</summary>
    public List<GenerationRecord> Generations { get; set; } = new();
    public const int MaxGenerations = 15;

    /// <summary>"Request Content" — mô tả y/c và những gì đã sửa (đi kèm gói update dạng description.txt).</summary>
    public string Content { get; set; } = "";

    /// <summary>"Note" — lưu ý cho người triển khai (nút 📝 Note), ghi vào note.txt của thư mục gói update.</summary>
    public string Note { get; set; } = "";

    /// <summary>Gen All: tên controller, mỗi dòng (hoặc cách nhau dấu phẩy) 1 tên.</summary>
    public string GenAll { get; set; } = "";

    /// <summary>File Path: mỗi dòng 1 đường dẫn file muốn đưa vào gói update.</summary>
    public string FilePaths { get; set; } = "";

    /// <summary>SQL Object của database APP: tên procedure (hoặc function/view/trigger/table), mỗi dòng 1 tên. Sys có danh sách riêng: <see cref="ProceduresSys"/>.</summary>
    public string Procedures { get; set; } = "";

    /// <summary>SQL Object của database SYS, mỗi dòng 1 tên.</summary>
    public string ProceduresSys { get; set; } = "";

    /// <summary>true = danh sách SQL Object đã tách riêng App / Sys (chuyển bằng nút App | Sys). false = y/c lưu từ bản cũ: một danh sách dùng chung, tìm trong các
    /// database đang tick <see cref="UseApp"/> / <see cref="UseSys"/> — GenAll vẫn xử lý theo cách cũ cho tới khi y/c được mở ra sửa.</summary>
    public bool ObjectsSplit { get; set; }

    /// <summary>Tìm SQL Object trong database App / Sys (mặc định App như màn FCode).</summary>
    public bool UseApp { get; set; } = true;
    public bool UseSys { get; set; }

    /// <summary>Table liên quan (từ Gen All) được chọn đưa vào gói update.</summary>
    public List<TableSelection> Tables { get; set; } = new();

    /// <summary>SQL Top Script của database APP — ghi vào đầu gói update của App (đầu file APP_Script.sql). Sys có bản riêng: <see cref="TopScriptSys"/>.</summary>
    public string TopScript { get; set; } = "";

    /// <summary>SQL Bottom Script của database APP — ghi vào cuối gói update của App, chạy SAU các script khác. Sys có bản riêng: <see cref="BottomScriptSys"/>.</summary>
    public string BottomScript { get; set; } = "";

    /// <summary>SQL Top Script của database SYS — vào thư mục / file script của Sys.</summary>
    public string TopScriptSys { get; set; } = "";

    /// <summary>SQL Bottom Script của database SYS.</summary>
    public string BottomScriptSys { get; set; } = "";

    /// <summary>true = Top / Bottom Script đã tách riêng App và Sys. false = y/c lưu từ bản cũ (một script dùng chung cho các database đã tick) — xem <see cref="MigrateScripts"/>.</summary>
    public bool ScriptsSplit { get; set; }

    /// <summary>Chuyển y/c kiểu cũ (một Top/Bottom Script dùng chung, đi vào database nào đang tick App / Sys) sang kiểu tách riêng, giữ đúng kết quả gói như trước:
    /// chỉ tick Sys → script thuộc về Sys; tick cả hai → mỗi bên một bản; còn lại → thuộc về App.</summary>
    public void MigrateScripts()
    {
        if (ScriptsSplit) return;
        ScriptsSplit = true;
        if (UseSys)
        {
            if (string.IsNullOrWhiteSpace(TopScriptSys)) TopScriptSys = TopScript;
            if (string.IsNullOrWhiteSpace(BottomScriptSys)) BottomScriptSys = BottomScript;
            if (!UseApp) { TopScript = ""; BottomScript = ""; }
        }
    }
}

/// <summary>
/// Lưu danh sách Request theo từng workspace tại %AppData%\Bcode\AdvanceNotes\&lt;workspace&gt;.json — cùng kiểu
/// với NoteService (file cục bộ, không cần bảng trong database).
/// </summary>
public class AdvanceNoteService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static JsonSerializerOptions Json => JsonOptions;

    /// <summary>Xoá thư mục gói đã gen (để gen lại) và bỏ link đó khỏi lịch sử Generate của mọi y/c. Chỉ nhận đường dẫn có trong lịch sử —
    /// không xoá thư mục bất kỳ. Bản đã chép lên đường dẫn mạng KHÔNG bị đụng tới. Trả về false nếu path không thuộc lịch sử.</summary>
    public static bool DeletePackage(IEnumerable<AdvanceRequest> requests, string path)
    {
        var owners = requests.Where(r => r.Generations.Any(g => string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase))).ToList();
        if (owners.Count == 0 || string.IsNullOrWhiteSpace(path)) return false;
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        foreach (var r in owners) r.Generations.RemoveAll(g => string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private static string Safe(string s)
    {
        var safe = string.IsNullOrWhiteSpace(s) ? "_no_workspace" : s.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        return safe;
    }

    /// <summary>Thư mục cũ (trước khi chuyển lên đường dẫn update của dự án) — dùng để chuyển dữ liệu cũ sang và làm chỗ dự phòng.</summary>
    public static string LocalFolder => Path.Combine(BcodePaths.AppData, "Bcode", "AdvanceNotes");

    /// <summary>Tên máy ghi trong tên file — mỗi người / máy 1 file nên không ghi đè note của nhau (cùng kiểu NoteX_&lt;dự án&gt;@&lt;máy&gt;.xml).</summary>
    public static string Owner => Environment.MachineName;

    /// <summary>Thư mục lưu Advance Note của dự án: setting AdvanceNotesPath (có {working} / {project} / {ws}); trống = "{working}\AdvanceNotes";
    /// dự án chưa khai Working Path = thư mục cục bộ cũ.</summary>
    public static string ResolveFolder(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        var working = (ws?.WorkingPath ?? "").Trim().TrimEnd('\\', '/');
        var tpl = (settings?.AdvanceNotesPath ?? "").Trim();
        if (tpl.Length == 0) tpl = working.Length > 0 ? "{working}\\AdvanceNotes" : "";
        if (tpl.Length == 0 || (tpl.Contains("{working}", StringComparison.OrdinalIgnoreCase) && working.Length == 0)) return LocalFolder;
        var project = ws is null ? "" : (string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId);
        return tpl.Replace("{working}", working, StringComparison.OrdinalIgnoreCase)
                  .Replace("{project}", Safe(project), StringComparison.OrdinalIgnoreCase)
                  .Replace("{ws}", Safe(ws?.Name ?? ""), StringComparison.OrdinalIgnoreCase)
                  .TrimEnd('\\', '/');
    }

    /// <summary>File note của CHÍNH máy này: &lt;thư mục&gt;\&lt;workspace&gt;@&lt;máy&gt;.json (thư mục cục bộ cũ thì giữ tên cũ &lt;workspace&gt;.json).</summary>
    public static string OwnFile(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        var folder = ResolveFolder(ws, settings);
        var name = Safe(ws?.Name ?? "");
        return string.Equals(folder, LocalFolder, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(folder, name + ".json")
            : Path.Combine(folder, $"{name}@{Safe(Owner)}.json");
    }

    private static List<AdvanceRequest> Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<AdvanceRequest>();
            return JsonSerializer.Deserialize<List<AdvanceRequest>>(File.ReadAllText(path), JsonOptions) ?? new List<AdvanceRequest>();
        }
        catch { return new List<AdvanceRequest>(); }   // file hỏng — bắt đầu lại thay vì không mở được tab
    }

    /// <summary>Lỗi lần ghi gần nhất (vd không vào được server → đã lưu vào thư mục ban đầu trên máy); null = ghi bình thường lên thư mục update.</summary>
    public string? LastSaveWarning { get; private set; }

    /// <summary>File ở thư mục BAN ĐẦU (trước khi chuyển lên thư mục update): %AppData%\Bcode\AdvanceNotes\&lt;ws&gt;.json —
    /// nơi lưu khi không vào được server, và nguồn chép sang lần đầu.</summary>
    public static string LocalFile(Workspace? ws) => Path.Combine(LocalFolder, Safe(ws?.Name ?? "") + ".json");

    // ---- server có vào được không: kiểm tra có hạn giờ + nhớ kết quả, để tự lưu (0,7 giây / lần) KHÔNG phải chờ UNC timeout mỗi lần khi server chết ----
    private static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Ok, DateTime At)> Reach = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Thư mục update (cha của thư mục AdvanceNotes) có vào được không. Nhớ "vào được" 5 phút, "không vào được" 30 giây rồi thử lại.</summary>
    public static bool IsReachable(string folder)
    {
        var root = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder;
        var has = Reach.TryGetValue(root, out var c);
        if (has && DateTime.UtcNow - c.At < (c.Ok ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30))) return c.Ok;
        Task<bool> Probe() => Task.Run(() =>
        {
            bool ok; try { ok = Directory.Exists(root); } catch { ok = false; }
            Reach[root] = (ok, DateTime.UtcNow);
            return ok;
        });
        if (has && !c.Ok)
        {
            // Đã biết là không vào được: thử lại NỀN, lần này vẫn trả "không" ngay — tự lưu lúc đang gõ không bị khựng 2,5 giây mỗi 30 giây.
            Reach[root] = (false, DateTime.UtcNow);
            _ = Probe();
            return false;
        }
        var probe = Probe();
        if (probe.Wait(ReachTimeout)) return probe.Result;
        Reach[root] = (false, DateTime.UtcNow);   // quá giờ = coi như không vào được (luồng kiểm tra chạy nốt ở nền, xong thì tự ghi kết quả)
        return false;
    }

    private static void MarkUnreachable(string folder) => Reach[Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder] = (false, DateTime.UtcNow);

    /// <summary>File đang dùng thật: thư mục update nếu vào được server, không thì file ở thư mục ban đầu trên máy.</summary>
    public static string EffectiveFile(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        var own = OwnFile(ws, settings);
        var local = LocalFile(ws);
        if (string.Equals(own, local, StringComparison.OrdinalIgnoreCase)) return own;
        return IsReachable(ResolveFolder(ws, settings)) ? own : local;
    }

    public List<AdvanceRequest> Load(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        var own = OwnFile(ws, settings);
        var local = LocalFile(ws);
        if (string.Equals(own, local, StringComparison.OrdinalIgnoreCase)) return Read(local);
        if (!IsReachable(ResolveFolder(ws, settings))) return Read(local);   // không vào được server: dùng thư mục ban đầu
        try
        {
            // Đưa bản trên máy lên thư mục update khi: (1) lần đầu (trên mạng chưa có), hoặc (2) bản trên máy MỚI HƠN — tức là đã sửa lúc
            // không vào được server. File này chỉ máy này ghi (<ws>@<máy>.json) nên đẩy lên không đè sửa của ai khác.
            if (File.Exists(local))
            {
                var ownExists = File.Exists(own);
                if (!ownExists || File.GetLastWriteTimeUtc(local) > File.GetLastWriteTimeUtc(own).AddSeconds(2))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(own)!);
                    File.Copy(local, own, overwrite: true);
                }
            }
            return Read(own);
        }
        catch
        {
            MarkUnreachable(ResolveFolder(ws, settings));
            return Read(local);
        }
    }

    /// <summary>Dấu phiên bản file đang dùng (giờ ghi + kích thước) — đổi khi BcodeNote / Bcode khác vừa ghi, hoặc khi chuyển giữa server ↔ máy. "" = chưa có file.</summary>
    public string Stamp(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        try
        {
            var path = EffectiveFile(ws, settings);
            var fi = new FileInfo(path);
            return fi.Exists ? path.Length + ":" + fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "";
        }
        catch { return ""; }
    }

    public void Save(Workspace? ws, Bcode.App.Models.AppSettings? settings, List<AdvanceRequest> requests)
    {
        LastSaveWarning = null;
        var json = JsonSerializer.Serialize(requests, JsonOptions);
        var own = OwnFile(ws, settings);
        var local = LocalFile(ws);
        if (!string.Equals(own, local, StringComparison.OrdinalIgnoreCase) && IsReachable(ResolveFolder(ws, settings)))
        {
            try { WriteAtomic(own, json); return; }
            catch (Exception ex)
            {
                MarkUnreachable(ResolveFolder(ws, settings));
                LastSaveWarning = "Không ghi được lên thư mục update (" + ex.Message + ") — đã lưu vào thư mục ban đầu trên máy, vào lại được server sẽ tự đưa lên.";
            }
        }
        else if (!string.Equals(own, local, StringComparison.OrdinalIgnoreCase))
            LastSaveWarning = "Không vào được server — đã lưu vào thư mục ban đầu trên máy (" + LocalFolder + "), vào lại được sẽ tự đưa lên.";
        // Không vào được server (hoặc dự án chưa có Working Path): lưu vào thư mục ban đầu.
        WriteAtomic(local, json);
    }

    private static void WriteAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Ghi ra file tạm rồi thay thế: mất điện/đóng app giữa chừng không làm hỏng danh sách đang có.
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Note của người dùng KHÁC cùng dự án (các file &lt;workspace&gt;@&lt;máy&gt;.json khác trong thư mục) — chỉ đọc.</summary>
    public record OtherNotes(string Owner, string File, DateTime Modified, List<AdvanceRequest> Requests);

    public List<OtherNotes> LoadOthers(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        var list = new List<OtherNotes>();
        var folder = ResolveFolder(ws, settings);
        if (string.Equals(folder, LocalFolder, StringComparison.OrdinalIgnoreCase)) return list;   // thư mục cục bộ: không có người khác
        if (!IsReachable(folder)) return list;   // không vào được server: không chờ UNC timeout
        var own = OwnFile(ws, settings);
        var prefix = Safe(ws?.Name ?? "") + "@";
        try
        {
            if (Directory.Exists(folder))   // chưa có thư mục AdvanceNotes vẫn đọc NoteX ở thư mục update
            foreach (var f in Directory.EnumerateFiles(folder, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (string.Equals(f, own, StringComparison.OrdinalIgnoreCase) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new OtherNotes(name[prefix.Length..], f, File.GetLastWriteTime(f), Read(f)));
            }
        }
        catch { /* mất mạng — trả những gì đã đọc được */ }
        list.AddRange(LoadNoteX(ws, settings));
        return list.OrderByDescending(o => o.Modified).ToList();
    }

    // ---- Note của phần mềm note khác (FCode): NoteX_<dự án>@<máy>.xml trong thư mục update — đọc như note JSON, CHỈ ĐỌC (không bao giờ ghi file này) ----

    /// <summary>Đuôi thêm vào tên người của note NoteX (vd "FTSBAOVQ [NoteX]") — phân biệt với note JSON cùng máy và để <see cref="OtherFile"/> biết đọc file .xml.</summary>
    public const string NoteXTag = " [NoteX]";

    /// <summary>Các thư mục có thể chứa NoteX_*.xml: thư mục update (cha AdvanceNotes), Working Path, chính thư mục AdvanceNotes.</summary>
    private static List<string> NoteXFolders(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        var list = new List<string>();
        void Add(string? p)
        {
            p = (p ?? "").Trim().TrimEnd('\\', '/');
            if (p.Length > 0 && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        }
        var advance = ResolveFolder(ws, settings);
        if (string.Equals(advance, LocalFolder, StringComparison.OrdinalIgnoreCase)) return list;   // dự án chưa có Working Path
        Add(Path.GetDirectoryName(advance));
        Add(ws?.WorkingPath);
        Add(advance);
        return list;
    }

    /// <summary>Tiền tố tên file NoteX của dự án: "NoteX_&lt;tên workspace&gt;@" (và "NoteX_&lt;mã dự án&gt;@" nếu khác).</summary>
    private static List<string> NoteXPrefixes(Workspace? ws)
    {
        var list = new List<string> { "NoteX_" + Safe(ws?.Name ?? "") + "@" };
        if (!string.IsNullOrWhiteSpace(ws?.ProjectId) && !list.Contains("NoteX_" + Safe(ws.ProjectId) + "@", StringComparer.OrdinalIgnoreCase))
            list.Add("NoteX_" + Safe(ws.ProjectId) + "@");
        return list;
    }

    private List<OtherNotes> LoadNoteX(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        var list = new List<OtherNotes>();
        var prefixes = NoteXPrefixes(ws);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in NoteXFolders(ws, settings))
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.EnumerateFiles(folder, "NoteX_*.xml"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    var prefix = prefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                    if (prefix is null || name.Length == prefix.Length || !seen.Add(name)) continue;
                    List<AdvanceRequest> reqs;
                    try { reqs = ReadNoteX(f); } catch { reqs = new(); }   // file hỏng / đang ghi dở — vẫn hiện tên người, 0 y/c
                    list.Add(new OtherNotes(name[prefix.Length..] + NoteXTag, f, File.GetLastWriteTime(f), reqs));
                }
            }
            catch { /* mất mạng — trả những gì đã đọc được */ }
        }
        return list;
    }

    /// <summary>File NoteX của người <paramref name="owner"/> (đã có đuôi <see cref="NoteXTag"/>); không thấy file nào thì trả đường dẫn ở thư mục update.</summary>
    private static string? NoteXFile(Workspace? ws, Bcode.App.Models.AppSettings? settings, string owner)
    {
        var who = owner[..^NoteXTag.Length].Trim();
        if (who.Length == 0) return null;
        string? first = null;
        foreach (var folder in NoteXFolders(ws, settings))
            foreach (var prefix in NoteXPrefixes(ws))
            {
                var p = Path.Combine(folder, prefix + who + ".xml");
                first ??= p;
                try { if (File.Exists(p)) return p; } catch { /* mất mạng */ }
            }
        return first;
    }

    /// <summary>Tên bảng (có thể kèm schema / ngoặc vuông): dbo.dmvt, [dbo].[dm vt], #tmp.</summary>
    private static readonly System.Text.RegularExpressions.Regex TableNameRx =
        new(@"^(\[[^\]]+\]|[\w$#@]+)(\.(\[[^\]]+\]|[\w$#@]+))*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Đọc 1 file NoteX (DataSet XML của FCode) thành danh sách y/c. Table1 = y/c (id, name, created_date, updated_date, mobile_yn, done_yn,
    /// description → Request Content, controller → Gen All, file_path → File Path); Table2 = 2 dòng App / Sys cùng id (sql_object → SQL Object,
    /// sql_script_top / sql_script_bottom → Top / Bottom Script, sql_data "bảng: điều kiện" mỗi dòng → Table lấy dữ liệu). Lỗi thì ném ra.</summary>
    public static List<AdvanceRequest> ReadNoteX(string path)
    {
        var doc = System.Xml.Linq.XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidDataException("File NoteX rỗng: " + path);
        static string T(System.Xml.Linq.XElement e, string name) => (string?)e.Element(name) ?? "";
        static bool B(System.Xml.Linq.XElement e, string name) => string.Equals(T(e, name).Trim(), "true", StringComparison.OrdinalIgnoreCase);
        static DateTime? D(System.Xml.Linq.XElement e, string name) =>
            DateTimeOffset.TryParse(T(e, name).Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.LocalDateTime : null;
        static string Lines(string s) => s.Replace("\r\n", "\n").Trim('\n', '\r', ' ', '\t');   // xuống dòng \n như note JSON (ô nhập của trang)

        var details = root.Elements("Table2").ToLookup(e => T(e, "id").Trim(), StringComparer.OrdinalIgnoreCase);
        var list = new List<AdvanceRequest>();
        foreach (var t in root.Elements("Table1"))
        {
            var id = T(t, "id").Trim();
            var r = new AdvanceRequest
            {
                Id = id.Length > 0 ? id : Guid.NewGuid().ToString("N"),
                Name = T(t, "name").Trim(),
                Mobile = B(t, "mobile_yn"),
                Done = B(t, "done_yn"),
                Begin = D(t, "created_date") ?? File.GetLastWriteTime(path),
                Updated = D(t, "updated_date"),
                Content = Lines(T(t, "description")),
                GenAll = Lines(T(t, "controller")),
                FilePaths = Lines(T(t, "file_path")),
                ObjectsSplit = true,
                ScriptsSplit = true,
            };
            foreach (var d in details[id])
            {
                var sys = string.Equals(T(d, "database").Trim(), "Sys", StringComparison.OrdinalIgnoreCase);
                var objects = Lines(T(d, "sql_object"));
                var top = Lines(T(d, "sql_script_top"));
                var bottom = Lines(T(d, "sql_script_bottom"));
                if (sys) { r.ProceduresSys = Join(r.ProceduresSys, objects); r.TopScriptSys = Join(r.TopScriptSys, top); r.BottomScriptSys = Join(r.BottomScriptSys, bottom); }
                else { r.Procedures = Join(r.Procedures, objects); r.TopScript = Join(r.TopScript, top); r.BottomScript = Join(r.BottomScript, bottom); }
                // sql_data: mỗi dòng "bảng: điều kiện" (hoặc chỉ "bảng"). Có người dán cả script SQL vào ô này — khối nào có dòng không phải dạng đó thì KHÔNG
                // coi là bảng (tránh gen dữ liệu bảng "DELETE wcommand ..."), đưa nguyên văn vào Note của y/c để người dùng thấy và tự xử lý.
                var data = Lines(T(d, "sql_data"));
                if (data.Length == 0) continue;
                var isTables = data.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).All(l =>
                {
                    var c = l.IndexOf(':');
                    var table = (c < 0 ? l : l[..c]).Trim();
                    return TableNameRx.IsMatch(table) && !string.Equals(table, "GO", StringComparison.OrdinalIgnoreCase);
                });
                // Cùng cú pháp với ô thêm tay table của Note (New): tách ở dấu : đầu tiên, bảng lặp gộp điều kiện bằng OR.
                if (isTables)
                    r.Tables.AddRange(GenAllService.ParseTableSpecs(data).Select(t => new TableSelection { Name = t.Name, Sys = sys, Data = true, Where = t.Where ?? "" }));
                else r.Note = Join(r.Note, $"[NoteX — ô dữ liệu bảng ({(sys ? "Sys" : "App")}) không phải dạng \"bảng: điều kiện\", CHƯA đưa vào gói]\n{data}");
            }
            r.UseSys = r.ProceduresSys.Length > 0 || r.TopScriptSys.Length > 0 || r.BottomScriptSys.Length > 0 || r.Tables.Any(x => x.Sys);
            list.Add(r);
        }
        return list;

        static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "\n" + b;
    }

    /// <summary>1 người dùng khác có note trong thư mục AdvanceNotes của dự án — để chọn xem trong Note (New): tên (máy), giờ sửa file, số y/c, số y/c chưa Done.</summary>
    public record OwnerEntry(string Owner, DateTime Modified, int Count, int Pending, string Programmers = "");

    private static readonly System.Text.RegularExpressions.Regex ProgrammerRx = new(@"Lập trình:\s*([^|\r\n]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Lập trình (ma_lt1) của y/c: dòng "Lập trình: X" do Sync yêu cầu ghi vào Request Content; "" nếu không có.</summary>
    public static string ProgrammerOf(AdvanceRequest r)
    {
        var m = ProgrammerRx.Match(r.Content ?? "");
        return m.Success ? m.Groups[1].Value.Trim().ToUpperInvariant() : "";
    }

    /// <summary>Các lập trình trong 1 file note, nhiều y/c nhất trước (tối đa 3), cách nhau dấu phẩy.</summary>
    public static string ProgrammersOf(IEnumerable<AdvanceRequest> reqs) =>
        string.Join(", ", reqs.Select(ProgrammerOf).Where(p => p.Length > 0).GroupBy(p => p).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(3));

    /// <summary>Danh sách người dùng KHÁC có note của dự án (đọc thư mục mạng — gọi ở luồng nền). error ≠ null = không đọc được (chưa có Working Path / mất mạng).</summary>
    public (List<OwnerEntry> Owners, string? Error) ListOwners(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        var folder = ResolveFolder(ws, settings);
        if (string.Equals(folder, LocalFolder, StringComparison.OrdinalIgnoreCase))
            return (new(), "Dự án chưa có Working Path — note chỉ lưu trên máy này, không có note của người khác.");
        if (!IsReachable(folder)) return (new(), "Không vào được thư mục note của dự án: " + folder);
        var list = LoadOthers(ws, settings).Select(o => new OwnerEntry(o.Owner, o.Modified, o.Requests.Count, o.Requests.Count(r => !r.Done), ProgrammersOf(o.Requests))).ToList();
        return (list, null);
    }

    /// <summary>File note của người dùng <paramref name="owner"/> (tên máy, như trong <see cref="ListOwners"/>): &lt;thư mục&gt;\&lt;ws&gt;@&lt;owner&gt;.json. null = tên không hợp lệ.</summary>
    public static string? OtherFile(Workspace? ws, Bcode.App.Models.AppSettings? settings, string owner)
    {
        owner = (owner ?? "").Trim();
        if (owner.Length == 0 || owner.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || owner.Contains("..")) return null;
        var folder = ResolveFolder(ws, settings);
        if (string.Equals(folder, LocalFolder, StringComparison.OrdinalIgnoreCase)) return null;
        if (owner.EndsWith(NoteXTag, StringComparison.OrdinalIgnoreCase)) return NoteXFile(ws, settings, owner);   // note NoteX_*.xml (FCode)
        return Path.Combine(folder, $"{Safe(ws?.Name ?? "")}@{owner}.json");
    }

    /// <summary>Đọc note của người khác để XEM (chỉ đọc — không bao giờ ghi file này). Khác <see cref="Read"/>: lỗi thì ném ra (báo rõ cho người dùng) thay vì trả danh sách rỗng.</summary>
    public static (List<AdvanceRequest> Requests, DateTime Modified) ReadOther(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? path;
        if (!IsReachable(folder)) throw new IOException("Không vào được thư mục note của dự án: " + folder);
        if (!File.Exists(path)) throw new FileNotFoundException("Không còn file note: " + path);
        return ReadNoteFile(path);
    }

    /// <summary>Đọc 1 file note bất kỳ (chuột phải Request List → Mở / Gộp file note): .xml = NoteX của FCode (<see cref="ReadNoteX"/>), còn lại = note JSON của Bcode.
    /// Chỉ đọc; lỗi thì ném ra để báo rõ.</summary>
    public static (List<AdvanceRequest> Requests, DateTime Modified) ReadNoteFile(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase)) return (ReadNoteX(path), File.GetLastWriteTime(path));   // NoteX (FCode)
        var text = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<AdvanceRequest>>(text, JsonOptions) ?? new List<AdvanceRequest>();
        return (list, File.GetLastWriteTime(path));
    }

    /// <summary>Tên người hiện cho 1 file note mở bằng tay: NoteX_&lt;dự án&gt;@&lt;máy&gt;.xml → "&lt;máy&gt; [NoteX]", &lt;dự án&gt;@&lt;máy&gt;.json → "&lt;máy&gt;", còn lại = tên file.</summary>
    public static string OwnerLabelOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var at = name.LastIndexOf('@');
        var who = at >= 0 && at < name.Length - 1 ? name[(at + 1)..] : name;
        return string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase) && name.StartsWith("NoteX_", StringComparison.OrdinalIgnoreCase)
            ? who + NoteXTag : who;
    }

    /// <summary>Gộp các y/c đọc từ file (<paramref name="incoming"/>) vào note của mình (<paramref name="mine"/>, sửa tại chỗ): trùng Id thì lấy nội dung trong file
    /// (giữ lịch sử Generate của mình), chưa có thì thêm mới GIỮ Id (gộp lại lần sau sẽ cập nhật đúng y/c đó, không nhân bản), giống hệt thì bỏ qua.
    /// Y/c chỉ có trong note của mình giữ nguyên. Trả số thêm / cập nhật / giống + Id các y/c thêm hoặc cập nhật.</summary>
    public static (int Added, int Updated, int Same, List<string> Ids) MergeInto(List<AdvanceRequest> mine, IEnumerable<AdvanceRequest> incoming)
    {
        int added = 0, updated = 0, same = 0;
        var ids = new List<string>();
        static AdvanceRequest Copy(AdvanceRequest r) => JsonSerializer.Deserialize<AdvanceRequest>(JsonSerializer.Serialize(r, JsonOptions), JsonOptions)!;
        static string Key(AdvanceRequest r) { var c = Copy(r); c.Generations = new(); return JsonSerializer.Serialize(c, JsonOptions); }
        foreach (var src in incoming)
        {
            var i = mine.FindIndex(r => string.Equals(r.Id, src.Id, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                if (Key(mine[i]) == Key(src)) { same++; continue; }
                var copy = Copy(src);
                copy.Id = mine[i].Id;
                copy.Generations = mine[i].Generations;
                mine[i] = copy;
                updated++; ids.Add(copy.Id);
            }
            else
            {
                var copy = Copy(src);
                copy.Generations = new();   // lịch sử gen của người khác — không thuộc note của mình
                mine.Insert(0, copy);
                added++; ids.Add(copy.Id);
            }
        }
        return (added, updated, same, ids);
    }

    /// <summary>Bản sao 1 y/c của người khác để đưa vào note của mình: Id mới, chưa xong, bỏ lịch sử gen, tên "&lt;tên&gt; (từ &lt;owner&gt;)" không trùng tên y/c đã có.</summary>
    public static AdvanceRequest CloneForMine(AdvanceRequest src, string owner, IEnumerable<AdvanceRequest> mine)
    {
        var copy = JsonSerializer.Deserialize<AdvanceRequest>(JsonSerializer.Serialize(src, JsonOptions), JsonOptions)!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Done = false;
        copy.Generations = new();
        copy.Begin = DateTime.Now;
        copy.Updated = null;
        var names = new HashSet<string>(mine.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var name = $"{src.Name} (từ {owner})";
        for (var i = 2; names.Contains(name); i++) name = $"{src.Name} (từ {owner}) {i}";
        copy.Name = name;
        return copy;
    }

    private static readonly HashSet<string> NotPackages =new(StringComparer.OrdinalIgnoreCase) { "App_Data", "Main", "SQL", "Script", "Web", "Mobile", "Bin", "AdvanceNotes" };

    /// <summary>1 gói update có trong thư mục update của dự án (do bất kỳ ai gen / kéo về).</summary>
    public record PackageEntry(string Name, string Path, DateTime Modified, bool IsFolder, string? Description, string? Note);

    /// <summary>Thư mục update của dự án = cha của thư mục AdvanceNotes (vd \\...\BMT_FBO\update); không xác định được = Working Path.</summary>
    public static string? UpdateFolder(Workspace? ws, Bcode.App.Models.AppSettings? settings)
    {
        var folder = ResolveFolder(ws, settings);
        if (!string.Equals(folder, LocalFolder, StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(folder);
        var working = (ws?.WorkingPath ?? "").Trim();
        return working.Length > 0 ? working : null;
    }

    /// <summary>Các gói update trong thư mục update của dự án (thư mục con + file .zip / .rar), mới nhất trước — kèm description.txt / note.txt nếu có.</summary>
    public List<PackageEntry> ListPackages(Workspace? ws, Bcode.App.Models.AppSettings? settings, int max = 300)
    {
        var list = new List<PackageEntry>();
        var root = UpdateFolder(ws, settings);
        if (root is null) return list;
        var advance = ResolveFolder(ws, settings);
        if (!string.Equals(advance, LocalFolder, StringComparison.OrdinalIgnoreCase) && !IsReachable(advance)) return list;   // không vào được server
        try
        {
            if (!Directory.Exists(root)) return list;
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(d.TrimEnd('\\'), advance, StringComparison.OrdinalIgnoreCase)) continue;
                // Thư mục cấu trúc bị giải nén thẳng vào update (không phải 1 gói) — bỏ qua.
                if (Path.GetFileName(d) is var n && NotPackages.Contains(n)) continue;
                static string? Txt(string p) { try { return File.Exists(p) ? File.ReadAllText(p).Trim() : null; } catch { return null; } }
                list.Add(new PackageEntry(Path.GetFileName(d), d, Directory.GetLastWriteTime(d), true,
                    Txt(Path.Combine(d, "description.txt")), Txt(Path.Combine(d, "note.txt"))));
            }
            foreach (var f in Directory.EnumerateFiles(root))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".zip" or ".rar" or ".7z")
                    list.Add(new PackageEntry(Path.GetFileName(f), f, File.GetLastWriteTime(f), false, null, null));
            }
        }
        catch { /* mất mạng */ }
        return list.OrderByDescending(p => p.Modified).Take(max).ToList();
    }

    /// <summary>Tên y/c mới chưa dùng: "Request 1", "Request 2", ...</summary>
    public static string SuggestName(IEnumerable<AdvanceRequest> existing)
    {
        var names = new HashSet<string>(existing.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var i = 1;
        while (names.Contains($"Request {i}")) i++;
        return $"Request {i}";
    }
}
