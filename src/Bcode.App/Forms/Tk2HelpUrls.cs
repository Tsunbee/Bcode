namespace Bcode.App.Forms;

/// <summary>
/// Ghép đường dẫn cho tab TK2 Help / Forum TK2 Help (Forms/Tk2HelpForm.cs). Không phụ thuộc WinForms/WebView2 để thử riêng được.
/// TK2 Help là Flask sau IIS, chạy ở thư mục ứng dụng con (APP_BASE = "/TK2_HELP"):
/// - Trang đăng nhập đọc "?next=" rồi SERVER TỰ THÊM APP_BASE → next phải là đường dẫn TƯƠNG ĐỐI với APP_BASE ("/forum"),
///   gửi "/TK2_HELP/forum" sẽ thành "/TK2_HELP/TK2_HELP/forum" → Not Found.
/// - Route không có "/" cuối: "/TK2_HELP/forum/" → 404.
/// </summary>
internal static class Tk2HelpUrls
{
    /// <summary>Đường dẫn gõ / lưu ở Template: bỏ khoảng trắng, nháy; thiếu giao thức thì thêm http://; bỏ "/" cuối của đường dẫn (route TK2 Help không có "/" cuối).</summary>
    public static string Normalize(string? url)
    {
        var text = (url ?? "").Trim().Trim('"').Trim();
        if (text.Length == 0) return "";
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return text;
        var path = u.AbsolutePath.TrimEnd('/');
        var b = new UriBuilder(u) { Path = path.Length == 0 ? "/" : path };
        return b.Uri.AbsoluteUri;
    }

    /// <summary>Thư mục ứng dụng ("/TK2_HELP", hoặc "" nếu site ở gốc) — không có "/" cuối.
    /// <paramref name="pageBase"/> = window.APP_BASE của trang (null = không biết → lấy đoạn đầu của đường dẫn).</summary>
    public static string AppBase(Uri page, string? pageBase)
    {
        if (pageBase != null)
        {
            var b = pageBase.Trim().Trim('/');
            return b.Length == 0 ? "" : "/" + b;
        }
        var seg = page.AbsolutePath.Trim('/').Split('/')[0];
        return seg.Length == 0 ? "" : "/" + seg;
    }

    /// <summary>Trang đăng nhập cùng site: gốc + APP_BASE + "/login".</summary>
    public static string LoginUrl(Uri page, string appBase) => page.GetLeftPart(UriPartial.Authority) + appBase + "/login";

    /// <summary>Giá trị "next" cho trang đăng nhập: đường dẫn của <paramref name="page"/> TƯƠNG ĐỐI với APP_BASE (server tự thêm APP_BASE), kèm query.
    /// "/TK2_HELP/forum?x=1" → "/forum?x=1"; "/TK2_HELP/TK2_HELP/forum" (đã lặp) → "/forum".</summary>
    public static string NextFor(Uri page, string appBase)
    {
        var path = StripBase(CollapseBase(page.AbsolutePath, appBase), appBase);
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path.Length == 0 || path[0] != '/') path = "/" + path;
        return path + page.Query;
    }

    /// <summary>Trang đăng nhập kèm "?next=" để đăng nhập xong quay về <paramref name="page"/>.</summary>
    public static string LoginUrlWithNext(Uri page, string appBase) =>
        LoginUrl(page, appBase) + "?next=" + Uri.EscapeDataString(NextFor(page, appBase));

    /// <summary>URL có APP_BASE bị lặp ("/TK2_HELP/TK2_HELP/forum") → URL đúng ("/TK2_HELP/forum"); không lặp → null.</summary>
    public static string? FixRepeatedBase(string? url, string appBase)
    {
        if (appBase.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var path = u.AbsolutePath;
        var fixedPath = CollapseBase(path, appBase);
        if (fixedPath.Length == path.Length) return null;
        return new UriBuilder(u) { Path = fixedPath }.Uri.AbsoluteUri;
    }

    /// <summary>"/B/B/B/x" → "/B/x" (so khớp không phân biệt hoa thường, đúng ranh giới đoạn).</summary>
    private static string CollapseBase(string path, string appBase)
    {
        if (appBase.Length == 0) return path;
        while (StartsWithSegment(path, appBase) && StartsWithSegment(path.Substring(appBase.Length), appBase))
            path = path.Substring(0, appBase.Length) + path.Substring(appBase.Length * 2);   // giữ đoạn đầu, bỏ đoạn lặp
        return path;
    }

    private static string StripBase(string path, string appBase) =>
        appBase.Length > 0 && StartsWithSegment(path, appBase) ? path.Substring(appBase.Length) : path;

    private static bool StartsWithSegment(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (path.Length == prefix.Length || path[prefix.Length] == '/');
}
