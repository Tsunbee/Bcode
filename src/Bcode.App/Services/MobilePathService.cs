using System.Collections.Concurrent;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// Thư mục source của app mobile (FBO "MobileService") — cấu trúc như site web: App_Data\Controllers\Mobile\{Dir,Filter,Grid,Include,...},
/// Main, bin, Web.config. Dùng cho cây menu Mobile (bảng dvcommand): File Lookup của menu mobile và ô "Table Dir" của form DVCOMMAND.
/// </summary>
public static class MobilePathService
{
    public const string FolderName = "MobileService";

    private static readonly ConcurrentDictionary<string, string?> Guessed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mobile Path khai ở workspace; trống thì đoán theo Source Path: thư mục cha + \MobileService
    /// (\\...\SUNPLAST\SP2262 → \\...\SUNPLAST\MobileService), rồi Source Path\MobileService. Không thấy → null.</summary>
    public static string? Resolve(Workspace? ws)
    {
        if (ws is null) return null;
        if (!string.IsNullOrWhiteSpace(ws.MobilePath)) return ws.MobilePath.Trim();
        var src = (ws.SourcePath ?? "").Trim().TrimEnd('\\', '/');
        if (src.Length == 0) return null;
        return Guessed.GetOrAdd(src, s =>
        {
            foreach (var candidate in new[] { Path.GetDirectoryName(s) is { Length: > 0 } parent ? Path.Combine(parent, FolderName) : null, Path.Combine(s, FolderName) })
            {
                try { if (candidate is not null && Directory.Exists(candidate)) return candidate; }
                catch { /* share rớt */ }
            }
            return null;
        });
    }

    /// <summary>Lời nhắc khi không xác định được thư mục mobile.</summary>
    public static string MissingMessage(Workspace? ws) =>
        "Workspace hiện tại chưa khai báo Mobile Path (Edit Project) và không thấy thư mục \"" + FolderName + "\" cạnh Source Path" +
        (string.IsNullOrWhiteSpace(ws?.SourcePath) ? "." : ":\n" + ws!.SourcePath);

    /// <summary>Thư mục controller của mobile: {gốc}\App_Data\Controllers\Mobile.</summary>
    public static string ControllersDir(string mobileRoot) => Path.Combine(mobileRoot, "App_Data", "Controllers", "Mobile");

    /// <summary>"Table Dir" của 1 sysid mobile: đọc Dir/Grid của MobileService trước, không có thì Dir/Grid của site web (Source Path). Chạy đồng bộ — gọi từ Task.Run.</summary>
    public static CommandTableService.Result ResolveTables(Workspace? ws, string sysId)
    {
        var root = Resolve(ws);
        var mobile = root is null ? null : CommandTableService.ResolveIn(ControllersDir(root), sysId);
        if (mobile is { } m && (m.Master.Length > 0 || m.Details.Count > 0)) return m;
        var web = CommandTableService.Resolve(ws?.SourcePath, sysId);
        if (web.Master.Length > 0 || web.Details.Count > 0) return web with { Note = "Đọc từ Dir của site web (MobileService không có Dir cho sysid này)." };
        return mobile ?? web;
    }
}
