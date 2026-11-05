namespace Bcode.App.UI;

/// <summary>
/// Cách tô màu chữ của hai cây: cây menu WCommand (bên trái) và cây File Lookup. Mỗi cây có 3 chế độ:
///   <c>auto</c>   — mặc định của Bcode (WCommand: menu mẹ tô theo từng phân hệ; File Lookup: .f cam, .xml xanh dương);
///   <c>none</c>   — không tô, mọi dòng dùng màu chữ của theme (giữ màu gốc);
///   <c>custom</c> — tự chọn màu bên dưới (ô để trống = màu chữ của theme).
/// Khai báo ở màn hình "Giao diện (Template)" → tab "Màu cây"; áp ngay trên các cây đang mở.
/// </summary>
public sealed class TreeColorOptions
{
    public const string Auto = "auto", None = "none", Custom = "custom";

    public string WCommandMode { get; set; } = Auto;
    /// <summary>Màu chữ menu mẹ (có menu con) khi WCommandMode = custom.</summary>
    public string? WCommandParent { get; set; }
    /// <summary>Màu chữ mục lá khi WCommandMode = custom.</summary>
    public string? WCommandLeaf { get; set; }
    /// <summary>Màu RIÊNG từng phân hệ khi WCommandMode = custom: khoá = đoạn đầu của wmenu_id ("06" trong "06.10.01"), giá trị "#RRGGBB".
    /// Menu mẹ thuộc phân hệ có khai báo thì dùng màu này thay cho <see cref="WCommandParent"/>.</summary>
    public Dictionary<string, string> WCommandGroups { get; set; } = new();

    public string FileMode { get; set; } = Auto;
    public string? FileF { get; set; }
    public string? FileXml { get; set; }
    public string? FileOther { get; set; }
    public string? FileFolder { get; set; }

    /// <summary>Định dạng cây File Lookup theo loại: khoá = đuôi file ".aspx", ".xlsx"... hoặc <see cref="FolderKey"/> / <see cref="OtherKey"/>.
    /// <c>Bold</c> áp ở MỌI chế độ màu; <c>Color</c> chỉ dùng khi FileMode = custom (đuôi không chọn màu thì theo "File khác").
    /// Màu của .f / .xml / File khác / Thư mục vẫn nằm ở FileF / FileXml / FileOther / FileFolder như trước.</summary>
    public Dictionary<string, FileNodeStyle> FileStyles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public const string FolderKey = "folder", OtherKey = "other";

    public TreeColorOptions Clone()
    {
        var c = (TreeColorOptions)MemberwiseClone();
        c.WCommandGroups = new Dictionary<string, string>(WCommandGroups ?? new(), StringComparer.OrdinalIgnoreCase);
        c.FileStyles = (FileStyles ?? new()).ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        return c;
    }

    /// <summary>Khoá định dạng của một node cây: thư mục = "folder", file = đuôi viết thường (".f"); file không đuôi = "other".</summary>
    public static string KeyOf(string name, bool isDirectory)
    {
        if (isDirectory) return FolderKey;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext.Length > 1 ? ext : OtherKey;
    }

    /// <summary>Node thuộc loại này có in đậm không (đuôi chưa khai báo thì theo dòng "File khác"; thư mục / .f / .xml có dòng riêng).</summary>
    public bool IsBold(string key)
        => FileStyles.TryGetValue(key, out var s) ? s.Bold
            : key is not (FolderKey or ".f" or ".xml") && FileStyles.TryGetValue(OtherKey, out var o) && o.Bold;

    /// <summary>Có loại nào được chọn in đậm không (cây cần font đậm làm nền để chữ đậm không bị cắt — xem FileLookupControl).</summary>
    public bool AnyFileBold => FileStyles.Values.Any(s => s.Bold);

    /// <summary>Màu tự chọn (chế độ custom) của một đuôi khác .f/.xml, hoặc null = theo "File khác".</summary>
    public string? ExtColor(string key) => FileStyles.TryGetValue(key, out var s) ? s.Color : null;

    /// <summary>Chặn giá trị lạ: chế độ không hợp lệ về "auto", màu không phải "#RRGGBB" bỏ trống.</summary>
    public TreeColorOptions Clean()
    {
        static string Mode(string? m) => m is Auto or None or Custom ? m : Auto;
        static string? Col(string? c) => UiTemplate.ParseColor(c) is null ? null : c!.Trim();
        WCommandMode = Mode(WCommandMode); FileMode = Mode(FileMode);
        WCommandParent = Col(WCommandParent); WCommandLeaf = Col(WCommandLeaf);
        FileF = Col(FileF); FileXml = Col(FileXml); FileOther = Col(FileOther); FileFolder = Col(FileFolder);
        WCommandGroups = (WCommandGroups ?? new()).Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && Col(kv.Value) is not null)
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        // Khoá: "folder" / "other" / ".đuôi" (thêm dấu chấm nếu người dùng gõ "aspx"); bỏ khoá rỗng và giá trị null.
        var styles = new Dictionary<string, FileNodeStyle>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in FileStyles ?? new())
        {
            if (v is null || string.IsNullOrWhiteSpace(k)) continue;
            var key = k.Trim().ToLowerInvariant();
            if (key is not (FolderKey or OtherKey) && !key.StartsWith('.')) key = "." + key;
            if (key.Length < 2 || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            styles[key] = new FileNodeStyle { Bold = v.Bold, Color = Col(v.Color) };
        }
        FileStyles = styles;
        return this;
    }

    public static TreeColorOptions Current => UiTemplate.Current.TreeColors;
}

/// <summary>Định dạng một loại node trên cây File Lookup (xem <see cref="TreeColorOptions.FileStyles"/>).</summary>
public sealed class FileNodeStyle
{
    public bool Bold { get; set; }
    /// <summary>"#RRGGBB" khi chế độ màu = custom; null = theo dòng "File khác".</summary>
    public string? Color { get; set; }

    public FileNodeStyle Clone() => (FileNodeStyle)MemberwiseClone();
}
