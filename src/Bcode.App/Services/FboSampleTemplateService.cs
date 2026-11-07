using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// Mẫu điền của tab FSG FBO ("🎲 Điền mẫu ▾"): người dùng lưu form đang mở (master + các dòng chi tiết) thành mẫu CÓ TÊN — mỗi form nhiều mẫu
/// ("Hóa đơn bán – chiết khấu", "… – ngoại tệ") để tester điền lại đúng nghiệp vụ; bấm Lưu trên form FBO thì tự giữ 1 mẫu "Lần lưu gần nhất" của máy
/// (Điền mẫu ngẫu nhiên lần sau ưu tiên các giá trị này). Lưu CHUNG theo dự án như Advance Note: <c>{working}\SampleTemplates\&lt;controller&gt;\*.json</c>
/// (dự án chưa có Working Path → %AppData%\Bcode\SampleTemplates\&lt;ws&gt;\…) — cả nhóm thấy mẫu của nhau. Mỗi mẫu 1 file (2 người lưu cùng lúc không đè nhau).
/// </summary>
public static class FboSampleTemplateService
{
    public sealed class Template
    {
        public string Name { get; set; } = "";
        public string Controller { get; set; } = "";
        public string Author { get; set; } = "";
        public DateTime SavedAt { get; set; }
        /// <summary>Mẫu tự giữ khi bấm Lưu trên form FBO (1 bản / máy / form).</summary>
        public bool Auto { get; set; }
        public Dictionary<string, string> Master { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Các dòng chi tiết (lưới), mỗi dòng: field → giá trị hiển thị.</summary>
        public List<Dictionary<string, string>> Rows { get; set; } = new();
        /// <summary>Đường dẫn file (không lưu vào JSON).</summary>
        [System.Text.Json.Serialization.JsonIgnore] public string Path { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string LocalRoot => System.IO.Path.Combine(BcodePaths.AppData, "Bcode", "SampleTemplates");

    /// <summary>Thư mục mẫu của dự án: {working}\SampleTemplates (dùng chung cả nhóm); chưa có Working Path → AppData theo workspace.</summary>
    public static string Root(Workspace ws)
    {
        var working = (ws.WorkingPath ?? "").Trim().TrimEnd('\\', '/');
        return working.Length > 0 ? System.IO.Path.Combine(working, "SampleTemplates") : System.IO.Path.Combine(LocalRoot, Safe(ws.Name));
    }

    public static string FolderOf(Workspace ws, string controller) => System.IO.Path.Combine(Root(ws), Safe(controller));

    private static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Trim()) sb.Append(System.IO.Path.GetInvalidFileNameChars().Contains(ch) || ch == '.' && sb.Length == 0 ? '_' : ch);
        var r = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return r.Length == 0 ? "_" : r.Length > 80 ? r[..80] : r;
    }

    private static string AutoFile => "_auto@" + Safe(Environment.MachineName) + ".json";

    /// <summary>Các mẫu của 1 form, mới lưu trước (mẫu tự giữ của máy này xếp cuối).</summary>
    public static List<Template> List(Workspace ws, string controller)
    {
        var res = new List<Template>();
        var dir = FolderOf(ws, controller);
        try
        {
            if (!Directory.Exists(dir)) return res;
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
                try
                {
                    var t = JsonSerializer.Deserialize<Template>(File.ReadAllText(f, Encoding.UTF8), Json);
                    if (t is null) continue;
                    t.Path = f;
                    if (t.Name.Length == 0) t.Name = System.IO.Path.GetFileNameWithoutExtension(f);
                    res.Add(t);
                }
                catch { /* file hỏng / đang ghi dở — bỏ */ }
        }
        catch { /* mất mạng — coi như chưa có mẫu */ }
        return res.OrderBy(t => t.Auto ? 1 : 0).ThenByDescending(t => t.SavedAt).ToList();
    }

    /// <summary>Mẫu tự giữ mới nhất (của bất kỳ máy nào) — Điền mẫu ngẫu nhiên ưu tiên giá trị trong đó.</summary>
    public static Template? LatestAuto(Workspace ws, string controller) =>
        List(ws, controller).Where(t => t.Auto).OrderByDescending(t => t.SavedAt).FirstOrDefault();

    /// <summary>Lưu mẫu (tên trùng → ghi đè file cùng tên). Trả về đường dẫn file.</summary>
    public static string Save(Workspace ws, Template t)
    {
        var dir = FolderOf(ws, t.Controller);
        Directory.CreateDirectory(dir);
        t.SavedAt = DateTime.Now;
        if (t.Author.Length == 0) t.Author = Environment.UserName + "@" + Environment.MachineName;
        var path = t.Auto ? System.IO.Path.Combine(dir, AutoFile) : System.IO.Path.Combine(dir, Safe(t.Name) + ".json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(t, Json), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        t.Path = path;
        return path;
    }

    public static void Delete(Template t)
    {
        if (t.Path.Length > 0 && File.Exists(t.Path)) File.Delete(t.Path);
    }

    /// <summary>Đổi tên mẫu: ghi file tên mới, xoá file cũ.</summary>
    public static void Rename(Workspace ws, Template t, string newName)
    {
        var old = t.Path;
        t.Name = newName.Trim();
        t.Auto = false;
        Save(ws, t);
        if (old.Length > 0 && !old.Equals(t.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(old)) File.Delete(old);
    }

    /// <summary>Số hiển thị trên lưới FBO ("1.500.000", "1,500,000.50", "0.00") → số gõ lại được ("1500000", "1500000.5"). Không phải số → giữ nguyên.</summary>
    public static string PlainNumber(string v)
    {
        var s = (v ?? "").Trim();
        if (!Regex.IsMatch(s, @"^-?[\d.,]+$") || !s.Any(char.IsDigit)) return s;
        int dot = s.LastIndexOf('.'), comma = s.LastIndexOf(',');
        string intPart, frac = "";
        if (dot >= 0 && comma >= 0)
        {
            var dec = Math.Max(dot, comma);
            intPart = s[..dec]; frac = s[(dec + 1)..];
        }
        else
        {
            var sep = dot >= 0 ? '.' : comma >= 0 ? ',' : '\0';
            if (sep == '\0') return s;
            var parts = s.Split(sep);
            // 1 dấu, sau nó không đúng 3 chữ số → dấu thập phân ("0.00", "12,5"); nhiều dấu / đúng 3 chữ số → dấu nghìn
            if (parts.Length == 2 && parts[1].Length != 3) { intPart = parts[0]; frac = parts[1]; }
            else intPart = s;
        }
        intPart = Regex.Replace(intPart, @"[.,]", "");
        frac = frac.TrimEnd('0');
        return frac.Length > 0 ? intPart + "." + frac : intPart;
    }
}
