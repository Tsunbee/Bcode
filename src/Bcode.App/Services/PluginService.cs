using System.Diagnostics;
using System.Text.Json;

namespace Bcode.App.Services;

/// <summary>
/// Plugin "cành mọc thêm" (mục 4 hướng Cây thế giới) — mức AN TOÀN: KHÔNG nạp DLL lạ vào tiến trình Bcode (tránh plugin lỗi làm treo / mã độc chạy trong Bcode),
/// mà mỗi plugin chỉ là một tờ khai JSON gọi một chương trình ngoài (exe) hoặc mở một đường dẫn/URL. Bỏ file khai vào thư mục là có thêm mục trong
/// "Tool mở rộng" + Ctrl+P, không phải build lại Bcode.
///
/// Nơi tìm: <c>&lt;thư mục Bcode.exe&gt;\Plugins\*.json</c> và <c>%AppData%\Bcode\plugins\*.json</c>. Mỗi file = 1 plugin:
/// <code>{ "id": "mytool", "name": "Mở công cụ X", "type": "exe", "target": "tools\\x.exe", "args": "--fast", "shortcut": "Ctrl+Shift+1" }</code>
/// type = "exe" (chạy chương trình; target tương đối thì tính từ thư mục chứa file khai) hoặc "url" (mở link/đường dẫn bằng ứng dụng mặc định).
/// Plugin chỉ chạy khi NGƯỜI DÙNG bấm — không tự chạy gì lúc nạp.
/// </summary>
public sealed record PluginManifest(string Id, string Name, string Type, string Target, string? Args, string? Shortcut, string? Cwd, string Dir)
{
    public bool IsUrl => string.Equals(Type, "url", StringComparison.OrdinalIgnoreCase);

    /// <summary>Đường dẫn target đã phân giải: URL / đường dẫn tuyệt đối giữ nguyên; tên lệnh trơn (không có dấu \ hay /, vd "notepad.exe") để
    /// hệ điều hành tự tìm theo PATH; còn lại (vd "tools\x.exe") tính tương đối từ thư mục chứa file khai.</summary>
    public string ResolvedTarget =>
        IsUrl || Path.IsPathRooted(Target) ? Target
        : Target.IndexOfAny(new[] { '\\', '/' }) < 0 ? Target
        : Path.GetFullPath(Path.Combine(Dir, Target));
}

public static class PluginService
{
    public static IEnumerable<string> Folders()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Plugins");
        yield return Path.Combine(BcodePaths.AppData, "Bcode", "plugins");
    }

    /// <summary>Đọc mọi tờ khai hợp lệ (id + name + target). Lỗi 1 file không chặn các file khác. Trùng id thì file gặp trước thắng.</summary>
    public static List<PluginManifest> Load()
    {
        var list = new List<PluginManifest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Folders())
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var r = doc.RootElement;
                    string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var id = S("id") ?? Path.GetFileNameWithoutExtension(file);
                    var name = S("name");
                    var target = S("target");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target)) continue;
                    if (!seen.Add(id)) continue;
                    list.Add(new PluginManifest(id, name!, S("type") ?? "exe", target!, S("args"), S("shortcut"), S("cwd"), Path.GetDirectoryName(file)!));
                }
                catch { /* file khai hỏng — bỏ qua, không chặn Bcode */ }
            }
        }
        return list;
    }

    /// <summary>Chạy plugin (chỉ khi người dùng bấm). exe: mở chương trình với tham số; url: mở link/đường dẫn bằng ứng dụng mặc định.</summary>
    public static void Run(PluginManifest p, IWin32Window? owner)
    {
        try
        {
            if (p.IsUrl)
            {
                Process.Start(new ProcessStartInfo(p.ResolvedTarget) { UseShellExecute = true });
                return;
            }
            var psi = new ProcessStartInfo(p.ResolvedTarget) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(p.Args)) psi.Arguments = p.Args;
            psi.WorkingDirectory = !string.IsNullOrWhiteSpace(p.Cwd) ? p.Cwd : p.Dir;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Không chạy được plugin \"{p.Name}\":\n{ex.Message}\n\nTarget: {p.ResolvedTarget}", "Bcode — Plugin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
