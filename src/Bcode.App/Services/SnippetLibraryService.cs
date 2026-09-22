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
    public List<Snippet> Snippets { get; private set; } = new();

    public SnippetLibraryService(string libraryFolder)
    {
        var folder = string.IsNullOrWhiteSpace(libraryFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode")
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
        File.WriteAllText(_path, json);
    }
    public void Reload()
    {
        // Đọc lại danh sách snippets từ file JSON lưu trên ổ cứng
        Load(); // hoặc gọi lại logic đọc file json của service
    }
}
