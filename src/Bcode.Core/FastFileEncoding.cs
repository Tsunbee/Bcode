using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.Core;

/// <summary>
/// Cách GHI file nguồn Fast (.xml / .f / .ent) — dùng chung cho Bcode và BcodeViewer. Ghi y như FCode:
/// UTF-8 CÓ BOM và dòng khai báo <c>&lt;?xml version="1.0" encoding="utf-8"?&gt;</c>.
///
/// LÝ DO (lỗi thật 11/2026): sửa 1 file Grid .xml bằng Bcode rồi lưu, vào chương trình Fast chạy menu đó thì Fast tự biên dịch .xml → .f
/// và file .f sinh ra toàn chữ Trung Quốc; cùng file đó mở bằng FCode rồi lưu lại thì .f sinh ra bình thường. Khác nhau đúng 3 byte BOM:
/// FCode (fcontrol EditorViewer.SaveFile) ghi <c>new StreamWriter(path, false, Encoding.UTF8)</c> = UTF-8 CÓ BOM, còn Bcode ghi
/// UTF-8 KHÔNG BOM. Bộ đọc của Fast không thấy BOM thì đọc theo UTF-16 → mỗi cặp byte ASCII thành 1 chữ CJK ("&lt;?" → 㼼, "xm" → 浸).
/// Fast biên dịch đúng khi file là UTF-8 và khai encoding="utf-8" — nên file UTF-16 / khai "utf-16" cũng quy về UTF-8 lúc lưu (FCode cũng vậy).
///
/// Quy tắc: file nguồn Fast → luôn UTF-8 có BOM; dòng &lt;?xml …?&gt; ở đầu file (nếu có) chuẩn hoá thành encoding="utf-8".
/// File không có dòng khai báo (mảnh include, &lt;!ENTITY …&gt;…) KHÔNG tự thêm. File khác (.sql, .js, .txt…) không đổi gì.
/// </summary>
public static class FastFileEncoding
{
    private static readonly string[] FastExtensions = { ".xml", ".f", ".ent" };

    /// <summary>File nguồn Fast mà chương trình Fast tự biên dịch / nạp: .xml, .f, .ent.</summary>
    public static bool IsFastSource(string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var e in FastExtensions)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>UTF-8 có BOM — giống FCode.</summary>
    public static Encoding Utf8Bom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Bảng mã để ghi <paramref name="path"/>: file nguồn Fast → UTF-8 có BOM; file khác → <paramref name="otherwise"/> (cách ghi cũ của nơi gọi).</summary>
    public static Encoding ForWrite(string path, Encoding otherwise) => IsFastSource(path) ? Utf8Bom : otherwise;

    /// <summary>Nội dung để ghi <paramref name="path"/>: file nguồn Fast → chuẩn hoá dòng khai báo (<see cref="NormalizeDeclaration"/>); file khác giữ nguyên.</summary>
    public static string PrepareText(string path, string text) => IsFastSource(path) ? NormalizeDeclaration(text) : text;

    /// <summary>Dòng khai báo chuẩn của file Fast (FCode ghi đúng dòng này).</summary>
    public const string StandardDeclaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";

    // Dòng <?xml ...?> ở ĐẦU file (cho phép BOM / khoảng trắng trước) và thuộc tính standalone (nếu có) trong đó.
    private static readonly Regex Declaration = new(@"\A(﻿?\s*)<\?xml(\s[^?]*)?\?>", RegexOptions.CultureInvariant);
    private static readonly Regex StandaloneAttr = new(@"\s+standalone\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Dòng &lt;?xml …?&gt; ở đầu <paramref name="text"/> → đúng <see cref="StandardDeclaration"/> (giữ standalone="…" nếu có):
    /// khai utf-16 / UTF-8 hoa / thiếu encoding / nháy đơn… đều quy về một dạng. Phần còn lại của file giữ nguyên từng ký tự.
    /// Không có dòng khai báo ở đầu file thì trả nguyên văn (không tự thêm — file mảnh include / &lt;!ENTITY&gt; không có dòng này).</summary>
    public static string NormalizeDeclaration(string text)
    {
        var m = Declaration.Match(text);
        if (!m.Success) return text;
        var standalone = StandaloneAttr.Match(m.Groups[2].Value);
        var decl = standalone.Success
            ? StandardDeclaration[..^2] + " standalone=\"" + standalone.Groups[1].Value.Trim('"', '\'') + "\"?>"
            : StandardDeclaration;
        var start = m.Index + m.Groups[1].Length;
        if (string.Equals(text.Substring(start, m.Length - m.Groups[1].Length), decl, StringComparison.Ordinal)) return text;   // đã đúng — không đụng
        return text[..start] + decl + text[(m.Index + m.Length)..];
    }
}
