using System.Security.Cryptography;
using System.Text;
using Bcode.App.Models;

using Bcode.ReportBuilder;

namespace Bcode.App.Services.Rpt.Builder;

public sealed record DeployDiffLine(string K, int? N, string T);
public sealed record BuildFile(string Kind, string Rel, byte[] Bytes, bool IsText);
public sealed record FilePlan(string Kind, string Rel, string Target, bool Exists, bool Same, long Size, long? TargetSize, DateTime? TargetTime, List<DeployDiffLine> Diff, int Added, int Removed, bool IsText);
public sealed record FilesPlan(List<FilePlan> Files, string? Problem, string SourceRoot);
public sealed record FilesDeployResult(bool Ok, List<string> Lines, string? BackupDir);

/// <summary>
/// "Lưu vào source" cho báo cáo tự tạo: chép Filter / Grid / Report / Main (.aspx) và mẫu Excel vào đúng chỗ trong source của project
/// (<c>App_Data\Controllers\{Filter,Grid,Report}</c>, <c>Main</c>, <c>App_Data\Templates\Excel</c>). Làm thận trọng như <see cref="ReportDeployService"/>:
/// <see cref="Plan"/> chỉ so sánh (file nào mới / trùng hệt / sẽ ghi đè, kèm diff), <see cref="Deploy"/> luôn BACKUP bản cũ ở máy bạn
/// (%AppData%\Bcode\Backups) trước khi ghi đè rồi đọc lại kích thước để kiểm tra. File chữ ghi UTF-8 có BOM như các file source Fast.
/// </summary>
public sealed class ReportFilesDeployService
{
    private const int MaxDiffLines = 300;

    public static List<BuildFile> FilesFor(ReportSpec spec, ReportBuildResult r, byte[]? xlsx)
    {
        byte[] T(string s) => new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(s)).ToArray();
        var list = new List<BuildFile>
        {
            new("Filter", Path.Combine("App_Data", "Controllers", "Filter", spec.Controller + ".xml"), T(r.FilterXml), true),
            new("Grid", Path.Combine("App_Data", "Controllers", "Grid", spec.Controller + ".xml"), T(r.GridXml), true),
            new("Report", Path.Combine("App_Data", "Controllers", "Report", spec.Controller + ".xml"), T(r.ReportXml), true),
            new("Main", Path.Combine("Main", spec.MainFile + ".aspx"), T(r.MainAspx), true),
        };
        if (xlsx is not null) list.Add(new("Excel", Path.Combine("App_Data", "Templates", "Excel", spec.Controller + ".xlsx"), xlsx, false));
        return list;
    }

    public FilesPlan Plan(string? sourceRoot, IReadOnlyList<BuildFile> files)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot)) return Fail("Workspace hiện tại chưa khai Source Path (File > Choose Server).");
        if (!Directory.Exists(sourceRoot)) return Fail("Không truy cập được Source Path: " + sourceRoot);
        foreach (var f in files)
        {
            var dir = Path.GetDirectoryName(Path.Combine(sourceRoot, f.Rel))!;
            if (!Directory.Exists(dir)) return Fail("Không thấy thư mục " + dir);
        }
        var plans = new List<FilePlan>();
        foreach (var f in files)
        {
            var target = Path.Combine(sourceRoot, f.Rel);
            var exists = File.Exists(target);
            long? ts = exists ? new FileInfo(target).Length : null;
            DateTime? tt = exists ? new FileInfo(target).LastWriteTime : null;
            var same = exists && ts == f.Bytes.Length && Hash(File.ReadAllBytes(target)) == Hash(f.Bytes);
            var diff = new List<DeployDiffLine>(); int add = 0, rem = 0;
            if (f.IsText && exists && !same)
            {
                var oldText = SafeRead(target); var newText = Encoding.UTF8.GetString(f.Bytes).TrimStart('﻿');
                (diff, add, rem) = BuildDiff(oldText, newText);
            }
            plans.Add(new FilePlan(f.Kind, f.Rel, target, exists, same, f.Bytes.Length, ts, tt, diff, add, rem, f.IsText));
        }
        return new FilesPlan(plans, null, sourceRoot);

        FilesPlan Fail(string problem) => new(new(), problem, sourceRoot ?? "");
    }

    public FilesDeployResult Deploy(string? sourceRoot, string workspaceName, IReadOnlyList<BuildFile> files, ISet<string> kinds)
    {
        var plan = Plan(sourceRoot, files);
        if (plan.Problem is not null) return new(false, new() { plan.Problem }, null);
        var log = new List<string>(); var ok = true; string? backup = null;
        foreach (var p in plan.Files.Where(p => kinds.Contains(p.Kind)))
        {
            var f = files.First(x => x.Kind == p.Kind);
            try
            {
                if (p.Exists)
                {
                    backup ??= NewBackupDir(workspaceName);
                    var bdir = Path.Combine(backup, p.Kind); Directory.CreateDirectory(bdir);
                    File.Copy(p.Target, Path.Combine(bdir, Path.GetFileName(p.Target)), overwrite: true);
                }
                File.WriteAllBytes(p.Target, f.Bytes);
                var written = new FileInfo(p.Target).Length;
                if (written != f.Bytes.Length) throw new IOException($"kích thước sau khi ghi ({written}) khác dự kiến ({f.Bytes.Length})");
                log.Add($"✓ {(p.Exists ? "Ghi đè" : "Tạo mới")}: {p.Target} ({written:N0} byte)");
            }
            catch (Exception ex) { ok = false; log.Add($"✗ {p.Target}: {ex.Message}"); }
        }
        if (backup is not null) log.Add("Bản cũ đã backup trên máy bạn: " + backup);
        return new(ok, log, backup);
    }

    private static string NewBackupDir(string workspaceName)
    {
        var safe = string.Concat((string.IsNullOrWhiteSpace(workspaceName) ? "workspace" : workspaceName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(ReportBuilderEnv.AppData, "Bcode", "Backups", safe, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_taobaocao");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
    private static string SafeRead(string path) { try { return File.ReadAllText(path); } catch { return ""; } }

    private static (List<DeployDiffLine> Lines, int Added, int Removed) BuildDiff(string oldText, string newText)
    {
        var diff = new DiffService().Diff(oldText, newText);
        int added = diff.Count(d => d.Kind == DiffKind.Added), removed = diff.Count(d => d.Kind == DiffKind.Removed);
        var keep = new bool[diff.Count];
        for (var i = 0; i < diff.Count; i++)
            if (diff[i].Kind != DiffKind.Equal)
                for (var k = Math.Max(0, i - 2); k <= Math.Min(diff.Count - 1, i + 2); k++) keep[k] = true;
        var lines = new List<DeployDiffLine>(); var skipped = false;
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
