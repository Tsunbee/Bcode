using System.Text.Json;

namespace Bcode.Chat.Net;

/// <summary>Màn đăng nhập nhớ gì: %AppData%\Bcode\bhoo\login.json — các Bhoo ID đã dùng trên máy này, ID lần trước, tự đăng nhập, đăng nhập ẩn.
/// Mỗi Bhoo ID 1 thư mục dữ liệu riêng (users\&lt;id&gt;\: danh bạ, lịch sử, tin chờ gửi) → 1 máy dùng được nhiều nick.
/// Không có mật khẩu: không có máy chủ để kiểm tra, ai ngồi máy này cũng mở được nick đã nhớ.</summary>
public sealed class LoginPrefs
{
    public List<string> Ids { get; set; } = new();
    public string LastId { get; set; } = "";
    public bool Remember { get; set; } = true;
    public bool AutoLogin { get; set; }
    public bool Invisible { get; set; }

    private string _path = "";
    public string Root { get; private set; } = "";

    public static LoginPrefs Load(string? root)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "bhoo");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "login.json");
        LoginPrefs p;
        try { p = File.Exists(path) ? JsonSerializer.Deserialize<LoginPrefs>(File.ReadAllText(path), Wire.Json) ?? new() : new(); }
        catch { p = new(); }
        p._path = path;
        p.Root = root;
        p.Ids = p.Ids.Where(Nick.Valid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return p;
    }

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions(Wire.Json) { WriteIndented = true })); }
        catch { }
    }

    /// <summary>Thư mục dữ liệu của 1 Bhoo ID.</summary>
    public string UserDir(string nick) => Path.Combine(Root, "users", Nick.IdOf(nick));

    public void Remembered(string nick)
    {
        Ids.RemoveAll(x => string.Equals(x, nick, StringComparison.OrdinalIgnoreCase));
        if (Remember) Ids.Insert(0, nick);
        LastId = Remember ? nick : "";
        Save();
    }
}
