using System.Text;

namespace Bcode.Shared;

/// <summary>
/// Giao thức dòng lệnh nhỏ, CÓ PHIÊN BẢN cho các pipe liên-tiến-trình của bộ Bcode (Bcode ↔ BcodeViewer ↔ BcodeNote).
/// File này được LINK vào cả Bcode.App và BcodeViewer.App (và BcodeNote qua Bcode.App) nên hai bên luôn mã hoá/giải mã cùng một cách.
///
/// Mỗi tin là MỘT dòng kết thúc bằng '\n'. Hai dạng, đọc được lẫn nhau để tương thích ngược:
///  • Dạng CŨ (giữ nguyên): "<verb>|<arg>|<arg>..." — các verb cũ fsg|, sql|, sqlp|, và "đường_dẫn|dự_án" của Viewer vẫn chạy y như trước.
///  • Dạng MỚI có phiên bản: "bcp1|<verb>|<b64 arg>|<b64 arg>..." — mọi tham số đưa qua base64(UTF-8) nên chứa '|' hay xuống dòng đều an toàn.
///    Bên nhận không hiểu verb mới thì BỎ QUA (không vỡ), nên thêm verb mới không làm hỏng bản cũ.
/// </summary>
public static class PipeProtocol
{
    public const string Version = "bcp1";

    public static string B64(string? s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
    public static string Un64(string s) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return ""; } }

    /// <summary>Soạn 1 tin dạng mới: "bcp1|verb|b64(arg0)|b64(arg1)...\n".</summary>
    public static string Encode(string verb, params string?[] args)
    {
        var sb = new StringBuilder(Version).Append('|').Append(verb);
        foreach (var a in args) sb.Append('|').Append(B64(a));
        return sb.Append('\n').ToString();
    }

    /// <summary>1 tin đã nhận. IsVersioned = dạng mới (Args đã giải base64); nếu không, Args là các mảnh thô tách bằng '|' như xưa.</summary>
    public readonly record struct Message(string Verb, string[] Args, bool IsVersioned)
    {
        public string Arg(int i) => i >= 0 && i < Args.Length ? Args[i] : "";
        public int ArgInt(int i, int fallback = 0) => int.TryParse(Arg(i), out var v) ? v : fallback;
    }

    /// <summary>Phân giải 1 dòng. Dạng mới "bcp1|verb|..." thì giải base64 từng tham số; còn lại giữ nguyên các mảnh (verb = mảnh đầu).</summary>
    public static Message Parse(string? line)
    {
        var parts = (line ?? "").TrimEnd('\r', '\n').Split('|');
        if (parts.Length >= 2 && parts[0] == Version)
            return new Message(parts[1], parts.Skip(2).Select(Un64).ToArray(), true);
        return new Message(parts.Length > 0 ? parts[0] : "", parts.Skip(1).ToArray(), false);
    }
}
