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
        if (existingIndex >= 0)
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

    public void ClearCart() => Cart.Clear();

    /// <summary>Concatenates every script currently in the cart, one after another.</summary>
    public string ViewCartConcatenated()
    {
        var sb = new StringBuilder();
        foreach (var (_, content) in Cart)
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
