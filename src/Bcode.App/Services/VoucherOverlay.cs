using System.Collections.Concurrent;

namespace Bcode.App.Services;

/// <summary>
/// Source "như sau khi tạo chứng từ", chỉ nằm trong bộ nhớ: file đích đọc ra bản đã đổi mã, file đăng ký đọc ra bản đã thêm dòng,
/// còn lại đọc nguyên từ source. Dùng cho "Xem chứng từ" — xem được màn hình mới trước khi ghi bất cứ gì xuống đĩa.
/// </summary>
public sealed class VoucherOverlay
{
    private readonly SourceTree _tree;
    private readonly VoucherCloneSpec _spec;
    private readonly Dictionary<string, string> _sourceOf = new(StringComparer.OrdinalIgnoreCase);   // file đích → file nguồn
    private readonly HashSet<string> _registry;
    private readonly ConcurrentDictionary<string, string?> _text = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _specLock = new();   // Transform ghi nhớ regex trong spec

    public VoucherOverlay(SourceTree tree, VoucherCloneSpec spec, IEnumerable<(string Rel, string Target)> files, IEnumerable<string> registry)
    {
        _tree = tree;
        _spec = spec;
        foreach (var (rel, target) in files) _sourceOf[target] = rel;
        _registry = new HashSet<string>(registry, StringComparer.OrdinalIgnoreCase);
    }

    public string PathOf(string rel) => Path.Combine(_tree.Root, rel);

    /// <summary>Đường dẫn tương đối so với gốc source; ngoài source thì null (không đọc).</summary>
    private string? RelOf(string path)
    {
        try
        {
            var rel = Path.GetRelativePath(_tree.Root, Path.GetFullPath(path));
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public bool Exists(string path) => RelOf(path) is { } rel && (_sourceOf.ContainsKey(rel) || _tree.Exists(rel));

    /// <summary>Đọc song song các file sắp cần (đường mạng: tránh đọc lần lượt từng include).</summary>
    public void Preload(IEnumerable<string> paths) =>
        _tree.Preload(paths.Select(RelOf).OfType<string>().Select(r => _sourceOf.GetValueOrDefault(r, r)).Where(_tree.Exists).ToList());

    public string? Read(string path) => RelOf(path) is { } rel ? _text.GetOrAdd(rel, Build) : null;

    private string? Build(string rel)
    {
        if (_sourceOf.TryGetValue(rel, out var source))
        {
            if (_tree.Text(source) is not { } text) return null;
            lock (_specLock) return VoucherCloneService.Transform(text, _spec);
        }
        if (!_tree.Exists(rel) || _tree.Text(rel) is not { } original) return null;
        if (!_registry.Contains(rel)) return original;
        lock (_specLock)
        {
            VoucherCloneService.AddRegistryLines(_tree.Root, rel, original, _spec, out var added);
            return added;
        }
    }
}
