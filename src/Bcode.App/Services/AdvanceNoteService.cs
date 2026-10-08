using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Gen All: tên controller, mỗi dòng (hoặc cách nhau dấu phẩy) 1 tên.</summary>
    public string GenAll { get; set; } = "";

    /// <summary>File Path: mỗi dòng 1 đường dẫn file muốn đưa vào gói update.</summary>
    public string FilePaths { get; set; } = "";

    /// <summary>SQL Object: tên procedure (hoặc function/view/trigger), mỗi dòng 1 tên.</summary>
    public string Procedures { get; set; } = "";

    /// <summary>Tìm SQL Object trong database App / Sys (mặc định App như màn FCode).</summary>
    public bool UseApp { get; set; } = true;
    public bool UseSys { get; set; }

    /// <summary>Table liên quan (từ Gen All) được chọn đưa vào gói update.</summary>
    public List<TableSelection> Tables { get; set; } = new();

    /// <summary>SQL Top Script — ghi vào đầu gói update (00_top.sql) cho các database đã chọn.</summary>
    public string TopScript { get; set; } = "";

    /// <summary>SQL Bottom Script — ghi vào cuối gói update (zzz_bottom.sql) cho các database đã chọn, chạy SAU các script khác.</summary>
    public string BottomScript { get; set; } = "";
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

    private static string FilePath(string workspaceName)
    {
        var safe = string.IsNullOrWhiteSpace(workspaceName) ? "_no_workspace" : workspaceName;
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        return Path.Combine(BcodePaths.AppData, "Bcode", "AdvanceNotes", safe + ".json");
    }

    public List<AdvanceRequest> Load(string workspaceName)
    {
        var path = FilePath(workspaceName);
        try
        {
            if (!File.Exists(path)) return new List<AdvanceRequest>();
            return JsonSerializer.Deserialize<List<AdvanceRequest>>(File.ReadAllText(path), JsonOptions) ?? new List<AdvanceRequest>();
        }
        catch
        {
            return new List<AdvanceRequest>(); // file hỏng — bắt đầu lại thay vì không mở được tab
        }
    }

    public void Save(string workspaceName, List<AdvanceRequest> requests)
    {
        var path = FilePath(workspaceName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Ghi ra file tạm rồi thay thế: mất điện/đóng app giữa chừng không làm hỏng danh sách đang có.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(requests, JsonOptions));
        File.Move(tmp, path, overwrite: true);
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
