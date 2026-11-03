using System.Text.Json;

namespace SqlWatch;

internal sealed class BcodeWorkspace
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string SysDatabase { get; set; } = "";
    public string AppDatabase { get; set; } = "";
    public string DbAccess { get; set; } = "";
    public IReadOnlyList<string> Databases { get; set; } = [];

    /// <summary>Database theo dõi của dự án: Sys, App và eInv (database có "eInv" trong tên, lấy từ DB Access — chỉ khi có khai báo).
    /// Các database khác trong DB Access (thường lặp lại Sys / App) không theo dõi.</summary>
    public void BuildDatabases()
    {
        var list = new List<string>();
        Add(SysDatabase);
        Add(AppDatabase);
        Add((DbAccess ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(name => name.Contains("einv", StringComparison.OrdinalIgnoreCase)));
        Databases = list;

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || list.Count >= 8)
                return;
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (list.Count >= 8)
                    return;
                if (part.Length is 0 or > 128 || part.IndexOfAny(['\'', '"', ';', '[', ']', ' ']) >= 0)
                    continue;
                if (!list.Contains(part, StringComparer.OrdinalIgnoreCase))
                    list.Add(part);
            }
        }
    }
}

internal sealed class BcodeCatalog
{
    public required IReadOnlyList<BcodeWorkspace> Workspaces { get; init; }
    public string LastWorkspace { get; init; } = "";

    /// <summary>Bcode nhúng SqlWatch thì chỉ đúng settings.json Bcode đang dùng (có thể khác %AppData% khi đặt BCODE_DATA_DIR).</summary>
    public static string? SettingsPathOverride { get; set; }

    public static string SettingsPath => SettingsPathOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "settings.json");

    public static BcodeCatalog Load()
    {
        var json = File.ReadAllText(SettingsPath);
        var file = JsonSerializer.Deserialize<BcodeFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("File settings rỗng.");

        var workspaces = (file.Workspaces ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var workspace in workspaces)
            workspace.BuildDatabases();

        return new BcodeCatalog
        {
            Workspaces = workspaces,
            LastWorkspace = file.LastWorkspace?.Trim() ?? ""
        };
    }

    private sealed class BcodeFile
    {
        public List<BcodeWorkspace>? Workspaces { get; set; }
        public string? LastWorkspace { get; set; }
    }
}
