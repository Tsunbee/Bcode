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

    public TreeColorOptions Clone()
    {
        var c = (TreeColorOptions)MemberwiseClone();
        c.WCommandGroups = new Dictionary<string, string>(WCommandGroups ?? new(), StringComparer.OrdinalIgnoreCase);
        return c;
    }

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
        return this;
    }

    public static TreeColorOptions Current => UiTemplate.Current.TreeColors;
}
