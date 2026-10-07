using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Key bản quyền cho 2 tính năng "Create RPT &amp; XML" và "Excel → FRX". Key được khai báo trong source của Bcode (<see cref="LicenseSecret.Key"/>) và build
/// vào chương trình — KHÔNG đọc gì từ workspace / server. Người dùng dán MỘT LẦN ở Settings → "Key bản quyền" chuỗi mã hoá bcrypt (.NET — BCrypt.Net):
/// hợp lệ khi <c>BCrypt.Verify(key khai báo, chuỗi đã dán)</c> đúng. Nếu key khai báo là chuỗi bcrypt thì ngược lại: người dùng dán key gốc và kiểm tra
/// <c>Verify(key đã dán, key khai báo)</c>. Key đã dán được nhớ ở %AppData%\Bcode\license.key và kiểm tra lại mỗi lần mở Bcode (đổi key trong source
/// + build mới thì key cũ hết hiệu lực).
/// </summary>
public static class LicenseService
{
    private const int WorkFactor = 11;
    private static readonly Regex BcryptRx = new(@"^\$2[abxy]?\$\d{2}\$[./A-Za-z0-9]{53}$", RegexOptions.Compiled);
    private static bool? _unlocked;

    /// <summary>Báo khi key được lưu hoặc xoá (để làm mới nhãn menu).</summary>
    public static event Action? Changed;

    private static string KeyPath => Path.Combine(BcodePaths.AppData, "Bcode", "license.key");

    /// <summary>Key đã lưu có khớp key khai báo trong source không (tính 1 lần rồi nhớ — bcrypt chạy chậm có chủ đích).</summary>
    public static bool IsUnlocked => _unlocked ??= Check(LoadKey());

    public static bool HasSavedKey => LoadKey().Length > 0;

    public static string LoadKey()
    {
        try { return File.Exists(KeyPath) ? File.ReadAllText(KeyPath).Trim() : ""; }
        catch { return ""; }
    }

    /// <summary>Kiểm tra key vừa dán; khớp thì lưu (dán 1 lần, lần sau không cần dán lại) và mở khoá.</summary>
    public static (bool Ok, string Message) Save(string? pasted)
    {
        var key = (pasted ?? "").Trim();
        if (key.Length == 0) return (false, "Chưa dán key.");
        if (!Check(key)) return (false, "Key không đúng. Kiểm tra lại chuỗi đã dán (đủ ký tự, không thừa khoảng trắng).");
        _unlocked = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            File.WriteAllText(KeyPath, key);
        }
        catch (Exception ex)
        {
            Changed?.Invoke();
            return (true, "Đã kích hoạt nhưng chưa nhớ được key cho lần mở sau (" + ex.Message + ").");
        }
        Changed?.Invoke();
        return (true, "Đã kích hoạt: có thể dùng Create RPT & XML và Excel → FRX. Key được nhớ lại, lần sau không cần dán lại.");
    }

    /// <summary>Xoá key đã lưu (khoá lại 2 tính năng).</summary>
    public static void Clear()
    {
        try { if (File.Exists(KeyPath)) File.Delete(KeyPath); } catch { /* không xoá được: lần kiểm tra sau vẫn đọc ra key cũ, nhưng cờ dưới đây khoá ngay lúc này */ }
        _unlocked = false;
        Changed?.Invoke();
    }

    /// <summary>Có khớp với key khai báo trong source không (không lưu gì).</summary>
    public static bool Check(string? pasted)
    {
        var p = (pasted ?? "").Trim();
        var declared = LicenseSecret.Key.Trim();
        if (p.Length == 0 || declared.Length == 0) return false;
        try
        {
            if (BcryptRx.IsMatch(p) && !BcryptRx.IsMatch(declared)) return BCrypt.Net.BCrypt.Verify(declared, p);   // dán chuỗi bcrypt, source khai key gốc
            if (BcryptRx.IsMatch(declared) && !BcryptRx.IsMatch(p)) return BCrypt.Net.BCrypt.Verify(p, declared);   // source khai chuỗi bcrypt, dán key gốc
        }
        catch { /* chuỗi bcrypt hỏng */ }
        return false;
    }

    /// <summary>Mã hoá key gốc bằng bcrypt (BCrypt.Net, work factor 11) — dành cho người phát hành key.</summary>
    public static string CreateHash(string plain) => BCrypt.Net.BCrypt.HashPassword(plain.Trim(), WorkFactor);
}
