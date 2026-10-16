using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Bcode.App.Services.Rpt;

public sealed record RptTableInfo(string Alias, string Name, string Path, List<string> Fields);
public sealed record RptSubreportInfo(string Name, List<RptTableInfo> Tables);
/// <summary>Cấu trúc dữ liệu của 1 file .rpt. <c>Used</c> = các "bảng.cột" (chữ thường) report đang dùng ở layout / công thức / nhóm / sắp xếp / lọc.</summary>
public sealed record RptInfo(string Version, List<RptTableInfo> Tables, List<RptSubreportInfo> Subreports, List<string> Used);
/// <summary>Bảng <c>Alias</c> trong rpt đổi nguồn sang bảng <c>Xsd</c> của file xsd mới (alias giữ nguyên nên công thức {d1.x} không đổi).</summary>
public sealed record RptTableMap(string Alias, string Xsd);
public sealed record RptUpdateResult(List<string> Warnings, List<RptTableInfo> Tables, string BackupPath);

/// <summary>
/// Write Schema → cập nhật bảng trong file Crystal Report (.rpt) — như "Set Datasource Location" + "Verify Database" của Crystal designer.
/// SDK Crystal (CrystalDecisions.*, .NET Framework) không nạp được vào Bcode (.NET 8), nên chạy script Assets/rpt/rptschema.ps1 bằng
/// Windows PowerShell 5.1 (có sẵn trên Windows, chạy .NET Framework) — cần SAP Crystal Reports runtime 13 trong GAC. Trao đổi qua 2 file JSON tạm.
/// </summary>
public static class RptSchemaService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Response
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Version { get; set; }
        public List<RptTableInfo>? Tables { get; set; }
        public List<RptSubreportInfo>? Subreports { get; set; }
        public List<string>? Used { get; set; }
        public List<string>? Warnings { get; set; }
    }

    public static async Task<RptInfo> ReadAsync(string rptPath)
    {
        var r = await RunAsync(new { mode = "read", rpt = rptPath });
        return new RptInfo(r.Version ?? "", r.Tables ?? new(), r.Subreports ?? new(), r.Used ?? new());
    }

    /// <summary>Đổi nguồn / thêm bảng rồi lưu ra file tạm; thành công mới sao lưu bản gốc thành &lt;tên&gt;.rpt.bak và chép đè — lỗi giữa chừng không làm hỏng file gốc.</summary>
    public static async Task<RptUpdateResult> UpdateAsync(string rptPath, string xsdPath, IReadOnlyList<RptTableMap> maps, IReadOnlyList<string> adds, bool subreports)
    {
        var output = Path.Combine(TempDir(), "out_" + Guid.NewGuid().ToString("N") + ".rpt");
        try
        {
            var r = await RunAsync(new { mode = "update", rpt = rptPath, xsd = xsdPath, output, maps, adds, subreports });
            var backup = rptPath + ".bak";
            File.Copy(rptPath, backup, overwrite: true);
            File.Copy(output, rptPath, overwrite: true);
            return new RptUpdateResult(r.Warnings ?? new(), r.Tables ?? new(), backup);
        }
        finally { TryDelete(output); }
    }

    private static async Task<Response> RunAsync(object request)
    {
        var dir = TempDir();
        var id = Guid.NewGuid().ToString("N");
        var reqPath = Path.Combine(dir, "req_" + id + ".json");
        var respPath = Path.Combine(dir, "resp_" + id + ".json");
        try
        {
            await File.WriteAllTextAsync(reqPath, JsonSerializer.Serialize(request, JsonOpts), new UTF8Encoding(false));
            var psi = new ProcessStartInfo(PowerShellPath())
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(), reqPath, respPath })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Không chạy được Windows PowerShell.");
            var stderr = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException("Crystal Reports runtime không trả lời sau 3 phút."); }

            if (!File.Exists(respPath))
                throw new InvalidOperationException("Script Crystal không trả kết quả." + ((await stderr).Trim() is { Length: > 0 } e ? "\r\n" + e : ""));
            var r = JsonSerializer.Deserialize<Response>(await File.ReadAllTextAsync(respPath, Encoding.UTF8), JsonOpts)
                    ?? throw new InvalidOperationException("Kết quả script Crystal rỗng.");
            if (!r.Ok)
            {
                var err = r.Error ?? "Lỗi không rõ.";
                throw new InvalidOperationException(err.StartsWith("NO_RUNTIME: ") ? err["NO_RUNTIME: ".Length..] : err);
            }
            return r;
        }
        finally { TryDelete(reqPath); TryDelete(respPath); }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Bcode", "rptschema");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Chép script nhúng ra thư mục tạm (tên theo mã băm nội dung — đổi script là ra file mới, 2 cửa sổ Bcode không ghi đè nhau).
    /// Ghi UTF-8 CÓ BOM: PowerShell 5.1 đọc file không BOM theo mã ANSI, làm hỏng chuỗi tiếng Việt.</summary>
    private static string ScriptPath()
    {
        using var s = typeof(RptSchemaService).Assembly.GetManifestResourceStream("rpt.rptschema.ps1")
                      ?? throw new InvalidOperationException("Thiếu script rpt.rptschema.ps1 trong Bcode.dll.");
        using var sr = new StreamReader(s, Encoding.UTF8);
        var text = sr.ReadToEnd();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        var path = Path.Combine(TempDir(), "rptschema_" + hash + ".ps1");
        if (!File.Exists(path)) File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    /// <summary>Windows PowerShell 5.1 cùng kiến trúc với Windows (runtime Crystal 64-bit cài vào GAC 64-bit).</summary>
    private static string PowerShellPath()
    {
        var sys = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), sys, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
