using System.Text;

namespace Bcode.App.Services;

/// <summary>
/// Backs Add/View/Clear/Save/Copy Script: a small in-memory "cart" of script
/// files the user is currently working with, plus the actual file read/write.
/// </summary>
public class ScriptFileService
{
    public List<(string path, string content)> Cart { get; } = new();

    public string ReadFile(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void AddToCart(string path)
    {
        // Đọc nội dung file
        var content = ReadFile(path);
        
        // Nếu trong cart đã có file này rồi thì cập nhật nội dung mới, ngược lại thì thêm mới vào cuối danh sách
        var existingIndex = Cart.FindIndex(c => c.path == path);
        if (existingIndex >= 0 && _viewText is not null && existingIndex < _viewBase)
        {
            // File đã nằm trong phần View Script người dùng sửa tay → chuyển xuống cuối giỏ để lần View sau hiện bản mới (nối sau phần đã sửa).
            Cart.RemoveAt(existingIndex);
            _viewBase--;
            Cart.Add((path, content));
        }
        else if (existingIndex >= 0)
        {
            Cart[existingIndex] = (path, content);
        }
        else
        {
            Cart.Add((path, content));
        }
    }

    /// <summary>Script sinh ra từ Add Script (Table / SQL Query): giữ thẳng nội dung trong cart,
    /// không ghi ra file nào — path rỗng đánh dấu mục không gắn với file trên đĩa.</summary>
    public void AddTextToCart(string content) => Cart.Add(("", content));

    public void ClearCart()
    {
        Cart.Clear();
        _viewText = null;
        _viewBase = 0;
    }

    // Nội dung cửa sổ View Script lúc người dùng đóng (đã gõ / sửa tay) — null = chưa sửa, View dựng lại từ giỏ.
    // _viewBase: số mục đầu giỏ đã nằm trong _viewText; mục thêm sau đó (Add Script) nối tiếp phía sau ở lần View sau.
    private string? _viewText;
    private int _viewBase;

    /// <summary>Nội dung mở cửa sổ View Script: phần người dùng đã sửa lần trước (nếu có) + các script thêm vào giỏ sau lần đó.</summary>
    public string ViewCartText()
    {
        if (_viewText is null) return ViewCartConcatenated();
        var tail = Concatenate(Cart.Skip(_viewBase));
        if (tail.Length == 0) return _viewText;
        var head = _viewText.TrimEnd();
        return head.Length == 0 ? tail : head + Environment.NewLine + Environment.NewLine + tail;
    }

    /// <summary>Đóng cửa sổ View Script: nhớ nội dung đang có trong cửa sổ để lần View sau hiện lại (chỉ mất khi Clear).
    /// <paramref name="cartCountShown"/> = số mục giỏ lúc mở cửa sổ (đã nằm trong nội dung).</summary>
    public void RememberViewText(string text, int cartCountShown)
    {
        cartCountShown = Math.Clamp(cartCountShown, 0, Cart.Count);
        if (Normalize(text) == Normalize(Concatenate(Cart.Take(cartCountShown)))) { _viewText = null; _viewBase = 0; return; }   // không sửa gì
        _viewText = text;
        _viewBase = cartCountShown;
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    /// <summary>Concatenates every script currently in the cart, one after another.</summary>
    public string ViewCartConcatenated() => Concatenate(Cart);

    private static string Concatenate(IEnumerable<(string path, string content)> items)
    {
        var sb = new StringBuilder();
        foreach (var (_, content) in items)
        {
            var text = content.TrimEnd();
            sb.AppendLine(text);
            // Nối 2 script: thiếu GO ở cuối thì thêm để các batch không dính vào nhau.
            if (text.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(text, @"(^|\n)[ \t]*GO[ \t]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                sb.AppendLine("GO");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public void SaveCartBackToDisk()
    {
        foreach (var (path, content) in Cart)
            if (path.Length > 0) WriteFile(path, content);
    }
}
