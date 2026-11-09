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
            if (!Directory.Exists(folder)) return list;
            foreach (var f in Directory.EnumerateFiles(folder, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (string.Equals(f, own, StringComparison.OrdinalIgnoreCase) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new OtherNotes(name[prefix.Length..], f, File.GetLastWriteTime(f), Read(f)));
            }
        }
        catch { /* mất mạng — trả những gì đã đọc được */ }
        return list.OrderByDescending(o => o.Modified).ToList();
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
        return Path.Combine(folder, $"{Safe(ws?.Name ?? "")}@{owner}.json");
    }

    /// <summary>Đọc note của người khác để XEM (chỉ đọc — không bao giờ ghi file này). Khác <see cref="Read"/>: lỗi thì ném ra (báo rõ cho người dùng) thay vì trả danh sách rỗng.</summary>
    public static (List<AdvanceRequest> Requests, DateTime Modified) ReadOther(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? path;
        if (!IsReachable(folder)) throw new IOException("Không vào được thư mục note của dự án: " + folder);
        if (!File.Exists(path)) throw new FileNotFoundException("Không còn file note: " + path);
        var text = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<AdvanceRequest>>(text, JsonOptions) ?? new List<AdvanceRequest>();
        return (list, File.GetLastWriteTime(path));
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

    private static readonly HashSet<string> NotPackages =new(StringComparer.OrdinalIgnoreCase) { "App_Data", "Main", "SQL", "Script", "Web", "Bin", "AdvanceNotes" };

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
