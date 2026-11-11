// Bản RỖNG của FEngine.cs — được commit; csproj chỉ build file này khi máy KHÔNG có FEngine.cs
// (engine giải mã thật, gitignore, viết lại từ fsystem của FCode — không đưa lên repo công khai).
// Bản build từ đây KHÔNG giải mã được khối <Encrypted> (Decode trả nguyên văn, Available = false).
// Xem src/Bcode.FSystem/README.md.
namespace Bcode.FSystem;

public static class FEngine
{
    /// <summary>false = bản build không có engine thật (chỉ có FEngine.Empty.cs).</summary>
    public static bool Available => false;

    /// <summary>Không có engine → trả nguyên văn.</summary>
    public static string Decode(string src, string hashKey, bool beauty = false) => src;

    /// <summary>Không có engine → trả nguyên văn.</summary>
    public static string Decrypt(string cipherText, string pwd = "") => cipherText;
}
