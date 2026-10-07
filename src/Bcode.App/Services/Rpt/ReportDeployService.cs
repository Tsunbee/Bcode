using System.Security.Cryptography;
using Bcode.App.Models;

namespace Bcode.App.Services.Rpt;

public record DeployTarget(string Kind, string Local, string Target, bool Exists, long LocalSize, long? TargetSize, DateTime? TargetTime, bool Same);
public record DeployDiffLine(string K, int? N, string T);
public record DeployPlan(List<DeployTarget> Targets, List<DeployDiffLine> XmlDiff, int Added, int Removed, bool ChangedSinceAnalyze, string? Problem, string SourceRoot);
public record DeployResult(bool Ok, List<string> Lines, string? BackupDir);

/// <summary>
/// "Đưa vào source": chép file .xlsx + .xml vừa tạo vào đúng chỗ trong source của project (<c>App_Data\Templates\Excel</c>, <c>App_Data\Controllers\Report</c>).
/// Ghi lên server nên làm thận trọng: (1) <see cref="Plan"/> chỉ so sánh — cho xem diff XML với bản đang có trên server, file nào trùng hệt thì báo "không đổi";
/// (2) báo nếu file XML trên server đã bị người khác sửa kể từ lúc Bcode đọc; (3) <see cref="Deploy"/> luôn BACKUP bản cũ trên máy bạn
/// (%AppData%\Bcode\Backups\...) trước khi ghi đè, rồi đọc lại kích thước để kiểm tra.
/// </summary>
public class ReportDeployService
{
    private const int MaxDiffLines = 400;

    public static string ExcelDir(string root) => Path.Combine(root, "App_Data", "Templates", "Excel");
    public static string ReportDir(string root) => Path.Combine(root, "App_Data", "Controllers", "Report");

    public DeployPlan Plan(string? sourceRoot, string? localXlsx, string? localXml, DateTime? serverXmlStampAtAnalyze)
    {
        var empty = new List<DeployTarget>();
        if (string.IsNullOrWhiteSpace(sourceRoot)) return Fail("Workspace hiện tại chưa khai Source Path (File > Choose Server).");
        if (!Directory.Exists(sourceRoot)) return Fail("Không truy cập được Source Path: " + sourceRoot);
        if (string.IsNullOrWhiteSpace(localXlsx) || string.IsNullOrWhiteSpace(localXml) || !File.Exists(localXlsx) || !File.Exists(localXml))
            return Fail("Chưa có file vừa tạo — bấm Create trước rồi mới đưa vào source.");
        if (!Directory.Exists(ExcelDir(sourceRoot))) return Fail("Không thấy thư mục " + ExcelDir(sourceRoot));
        if (!Directory.Exists(ReportDir(sourceRoot))) return Fail("Không thấy thư mục " + ReportDir(sourceRoot));

        var name = Path.GetFileNameWithoutExtension(localXml);
        var targets = new List<DeployTarget>
        {
            Describe("xlsx", localXlsx, Path.Combine(ExcelDir(sourceRoot), name + ".xlsx")),
            Describe("xml", localXml, Path.Combine(ReportDir(sourceRoot), name + ".xml")),
        };

        var xt = targets[1];
        var oldText = xt.Exists ? SafeRead(xt.Target) : "";
        var newText = File.ReadAllText(localXml);
        var (lines, added, removed) = BuildDiff(oldText, newText);
        var changed = serverXmlStampAtAnalyze is { } stamp && xt.TargetTime is { } t && t.ToUniversalTime() > stamp.ToUniversalTime().AddSeconds(1);
        return new DeployPlan(targets, lines, added, removed, changed, null, sourceRoot);

        DeployPlan Fail(string problem) => new(empty, new(), 0, 0, false, problem, sourceRoot ?? "");
    }

    /// <summary>Ghi đè vào source. <paramref name="force"/> = người dùng đã xem diff và chấp nhận dù file XML trên server vừa bị sửa.</summary>
    public DeployResult Deploy(string? sourceRoot, string workspaceName, string? localXlsx, string? localXml, bool doXlsx, bool doXml, bool force, DateTime? serverXmlStampAtAnalyze)
    {
        var plan = Plan(sourceRoot, localXlsx, localXml, serverXmlStampAtAnalyze);
        var log = new List<string>();
        if (plan.Problem is not null) return new(false, new() { plan.Problem }, null);
        if (!doXlsx && !doXml) return new(false, new() { "Chưa chọn file nào để đưa vào source." }, null);
        if (plan.ChangedSinceAnalyze && !force)
            return new(false, new() { "File XML trên server đã bị sửa sau khi Bcode đọc. Xem lại diff rồi tick \"vẫn ghi đè\"." }, null);

        string? backupRoot = null;
        var ok = true;
        foreach (var t in plan.Targets.Where(t => t.Kind == "xlsx" ? doXlsx : doXml))
        {
            try
            {
                if (t.Exists)
                {
                    backupRoot ??= NewBackupDir(workspaceName);
                    var dir = Path.Combine(backupRoot, t.Kind == "xlsx" ? "Templates_Excel" : "Controllers_Report");
                    Directory.CreateDirectory(dir);
                    File.Copy(t.Target, Path.Combine(dir, Path.GetFileName(t.Target)), overwrite: true);
                }
                File.Copy(t.Local, t.Target, overwrite: true);
                var written = new FileInfo(t.Target).Length;
                if (written != t.LocalSize) throw new IOException($"kích thước sau khi ghi ({written}) khác file gốc ({t.LocalSize})");
                log.Add($"✓ {(t.Exists ? "Ghi đè" : "Tạo mới")}: {t.Target} ({written:N0} byte)");
            }
            catch (Exception ex) { ok = false; log.Add($"✗ {t.Target}: {ex.Message}"); }
        }
        if (backupRoot is not null) log.Add("Bản cũ đã backup trên máy bạn: " + backupRoot);
        return new(ok, log, backupRoot);
    }

    private static string NewBackupDir(string workspaceName)
    {
        var safe = string.Concat((string.IsNullOrWhiteSpace(workspaceName) ? "workspace" : workspaceName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(BcodePaths.AppData, "Bcode", "Backups", safe, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static DeployTarget Describe(string kind, string local, string target)
    {
        var li = new FileInfo(local);
        if (!File.Exists(target)) return new(kind, local, target, false, li.Length, null, null, false);
        var ti = new FileInfo(target);
        return new(kind, local, target, true, li.Length, ti.Length, ti.LastWriteTime, li.Length == ti.Length && Hash(local) == Hash(target));
    }

    private static string Hash(string path)
    {
        try { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
        catch { return Guid.NewGuid().ToString(); }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    /// <summary>Diff XML ở dạng gọn: chỉ các dòng đổi + 2 dòng ngữ cảnh mỗi bên, tối đa <see cref="MaxDiffLines"/> dòng.</summary>
    private static (List<DeployDiffLine> Lines, int Added, int Removed) BuildDiff(string oldText, string newText)
    {
        var diff = new DiffService().Diff(oldText, newText);
        if (oldText.Length == 0) diff = diff.Where(d => d.Kind != DiffKind.Removed).ToList();   // file chưa có: chuỗi rỗng không phải "1 dòng bị xoá"
        int added = diff.Count(d => d.Kind == DiffKind.Added), removed = diff.Count(d => d.Kind == DiffKind.Removed);
        var keep = new bool[diff.Count];
        for (var i = 0; i < diff.Count; i++)
            if (diff[i].Kind != DiffKind.Equal)
                for (var k = Math.Max(0, i - 2); k <= Math.Min(diff.Count - 1, i + 2); k++) keep[k] = true;

        var lines = new List<DeployDiffLine>();
        var skipped = false;
        for (var i = 0; i < diff.Count && lines.Count < MaxDiffLines; i++)
        {
            if (!keep[i]) { if (!skipped && lines.Count > 0) lines.Add(new("…", null, "")); skipped = true; continue; }
            skipped = false;
            var d = diff[i];
            lines.Add(new(d.Kind == DiffKind.Added ? "+" : d.Kind == DiffKind.Removed ? "-" : " ", d.Kind == DiffKind.Added ? d.RightLineNo : d.LeftLineNo, d.Text));
        }
        return (lines, added, removed);
    }
}
