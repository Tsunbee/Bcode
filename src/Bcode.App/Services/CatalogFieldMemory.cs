using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bcode.App.Services;

/// <summary>
/// "Nhớ những gì bạn sửa" của Tạo nhanh danh mục: lúc Tạo file, cột nào AI / luật Fast gợi ý mà bạn đã đổi tên field, kiểu hoặc tra cứu
/// thì ghi lại <c>tiêu đề gợi ý (bỏ dấu) → field bạn chốt</c> vào <c>%AppData%\Bcode\quicklist-fields.json</c>. Lần sau gặp đúng tiêu đề đó,
/// luật Fast điền thẳng field này (ưu tiên trên source dự án và header.xml), AI được gửi kèm, bước kiểm tra nhắc nếu đặt khác.
/// Xoá 1 mục: sửa / xoá file khi Bcode đã tắt.
/// </summary>
public static class CatalogFieldMemory
{
    public sealed class Item
    {
        public string Name { get; set; } = "";
        public string SqlType { get; set; } = "";
        public string E { get; set; } = "";
        public string Controller { get; set; } = "";
        public string Reference { get; set; } = "";
        public string Key { get; set; } = "";
        public string Check { get; set; } = "";
        public string Information { get; set; } = "";
        /// <summary>Tiêu đề Việt gốc (có dấu) — để đọc file cho dễ.</summary>
        public string V { get; set; } = "";
        public int Times { get; set; }
        public DateTime At { get; set; }

        [JsonIgnore]
        public FieldLookup? Lookup => Controller.Length == 0 ? null : new FieldLookup(Controller, Reference, Key, Check, Information);
    }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static Dictionary<string, Item>? _items;

    public static string FilePath => Path.Combine(BcodePaths.AppData, "Bcode", "quicklist-fields.json");

    private static Dictionary<string, Item> Items
    {
        get
        {
            if (_items is not null) return _items;
            try
            {
                if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, Item>>(File.ReadAllText(FilePath)) is { } d)
                    return _items = new(d, StringComparer.Ordinal);
            }
            catch { /* file hỏng: bắt đầu lại */ }
            return _items = new(StringComparer.Ordinal);
        }
    }

    /// <summary>Field bạn đã chốt cho tiêu đề này (so bỏ dấu), null nếu chưa có.</summary>
    public static Item? Get(string header)
    {
        lock (Gate) return Items.TryGetValue(FastCatalogRules.Fold(header), out var i) ? i : null;
    }

    /// <summary>Mục đã nhớ có tên field này (lấy tra cứu bạn đã chọn cho field đó).</summary>
    public static Item? ByName(string name)
    {
        lock (Gate) return Items.Values.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => i.At).FirstOrDefault();
    }

    /// <summary>Các mục có tiêu đề xuất hiện (nguyên cụm, bỏ dấu) trong <paramref name="foldedText"/> — gửi kèm AI.</summary>
    public static List<(string Header, Item Item)> InText(string foldedText)
    {
        lock (Gate)
            return Items.Where(kv => kv.Key.Length >= 3 && System.Text.RegularExpressions.Regex.IsMatch(foldedText, @"(?<![a-z0-9])" + System.Text.RegularExpressions.Regex.Escape(kv.Key) + @"(?![a-z0-9])"))
                .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Ghi nhớ (tiêu đề gợi ý → field chốt). Trả số mục mới / đổi.</summary>
    public static int Learn(IEnumerable<(string Header, Item Item)> items)
    {
        var n = 0;
        lock (Gate)
        {
            foreach (var (header, item) in items)
            {
                var key = FastCatalogRules.Fold(header);
                if (key.Length == 0 || item.Name.Length == 0) continue;
                var times = Items.TryGetValue(key, out var old) ? old.Times : 0;
                item.Times = times + 1;
                item.At = DateTime.Now;
                if (item.V.Length == 0) item.V = header;
                Items[key] = item;
                n++;
            }
            if (n == 0) return 0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, JsonOpts));
            }
            catch { /* chỉ là gợi ý — ghi không được thì thôi */ }
        }
        return n;
    }
}
