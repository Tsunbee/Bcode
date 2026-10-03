using System.Text.Json;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// "Library" tool: a simple named snippet store (SQL/JS/HTML fragments you
/// reuse across scripts), persisted as JSON — one file, easy to back up or
/// share with teammates by copying the file.
/// </summary>
public class SnippetLibraryService
{
    private readonly string _path;

    /// <summary>Đường dẫn file snippets.json đang dùng trên máy này (để báo cho người dùng biết lưu ở đâu).</summary>
    public string FilePath => _path;
    public List<Snippet> Snippets { get; private set; } = new();

    public SnippetLibraryService(string libraryFolder)
    {
        var folder = string.IsNullOrWhiteSpace(libraryFolder)
            ? Path.Combine(BcodePaths.AppData, "Bcode")
            : libraryFolder;
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "snippets.json");
        Load();
    }

    public void Load()
    {
        if (!File.Exists(_path)) { Snippets = new(); return; }
        try
        {
            var json = File.ReadAllText(_path);
            Snippets = JsonSerializer.Deserialize<List<Snippet>>(json) ?? new();
        }
        catch
        {
            Snippets = new();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Snippets, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, json);
        WriteLog($"Đã lưu {Snippets.Count} snippet -> {_path}");
    }

    /// <summary>File log cạnh snippets.json: mỗi lần lưu ghi 1 dòng (giờ, số snippet, ĐƯỜNG DẪN ĐẦY ĐỦ) để biết máy này lưu vào đâu mà không cần
    /// hiện đường dẫn dài trên giao diện. Lỗi ghi log bị bỏ qua — log không bao giờ được làm hỏng việc lưu snippet.</summary>
    private void WriteLog(string message)
    {
        try
        {
            var logPath = Path.Combine(Path.GetDirectoryName(_path)!, "library.log");
            File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* bỏ qua */ }
    }
    public void Reload()
    {
        // Đọc lại danh sách snippets từ file JSON lưu trên ổ cứng
        Load(); // hoặc gọi lại logic đọc file json của service
    }
}
