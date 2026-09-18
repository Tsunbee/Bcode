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
        if (Cart.Any(c => c.path == path)) return;
        Cart.Add((path, ReadFile(path)));
    }

    public void ClearCart() => Cart.Clear();

    /// <summary>Concatenates every script currently in the cart, one after another with a header comment.</summary>
    public string ViewCartConcatenated()
    {
        var sb = new StringBuilder();
        foreach (var (path, content) in Cart)
        {
            sb.AppendLine($"-- ===== {path} =====");
            sb.AppendLine(content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public void SaveCartBackToDisk()
    {
        foreach (var (path, content) in Cart)
            WriteFile(path, content);
    }
}
