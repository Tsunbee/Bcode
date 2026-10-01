using System.Diagnostics;
using System.Text;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>
/// Gọi RptGenerator.exe — tiến trình phụ duy nhất còn lại của tool Excel → RPT.
///
/// Vì sao không tham chiếu thẳng: RptGenerator là net48 x86 và dùng CrystalDecisions.*
/// (chỉ có bản .NET Framework 32-bit); Bcode là .NET 8 chạy x64, không nạp được các DLL đó.
/// <code>
/// RptGenerator.exe layout.json out.rpt [template.rpt] [--xsd s.xsd] [--params p.json] [--style s.rpt] [--pdf out.pdf]
/// RptGenerator.exe --inspect x.rpt     # quy cách trình bày (JSON)
/// RptGenerator.exe --fields  x.rpt     # trường của template (JSON)
/// </code>
/// Mã thoát: 0 = OK, 1 = sai tham số, 2 = lỗi.
/// </summary>
internal sealed class RptGeneratorRunner
{
    /// <summary>Crystal thỉnh thoảng treo khi template hỏng — không để tab đứng mãi.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly Func<string> _exePath;

    public RptGeneratorRunner(Func<string> exePath) => _exePath = exePath;

    public string ExePath => _exePath();

    public bool Exists => File.Exists(ExePath);

    /// <summary>Đường dẫn mặc định: &lt;thư mục Bcode&gt;\tools\RptGenerator\RptGenerator.exe (csproj chép sẵn).</summary>
    public static string DefaultExePath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "RptGenerator", "RptGenerator.exe");

    public const string MissingExeMessage =
        "Chua co RptGenerator.exe. Bcode tim o: {0}\n" +
        "Build tu D:\\...\\Convert\\tool (run_convert.bat) roi chep RptGenerator.exe, " +
        "RptGenerator.exe.config, Newtonsoft.Json.dll vao tools\\RptGenerator cua Bcode, " +
        "hoac khai duong dan trong settings.json (RptGeneratorExePath).";

    public const string CrystalRuntimeHint =
        "\nMay nay co the chua cai Crystal Reports runtime: can SAP Crystal Reports runtime 32-bit (CRRuntime_32bit).";

    /// <summary>Chạy exe, trả (mã thoát, stdout+stderr gộp). Chạy nền, không chặn UI.</summary>
    public async Task<(int code, string output)> RunAsync(params string[] args)
    {
        var exe = ExePath;
        if (!File.Exists(exe)) return (-1, string.Format(MissingExeMessage, exe));

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return (-1, "Khong chay duoc RptGenerator.exe: " + ex.Message);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* đã thoát */ }
            return (-2, stdout + stderr.ToString() +
                        $"\nLOI: RptGenerator.exe khong phan hoi sau {Timeout.TotalSeconds:0} giay - da dung. " +
                        "Thuong do template .rpt hong.");
        }
        p.WaitForExit();          // xả hết stdout/stderr bất đồng bộ

        var output = stdout + stderr.ToString();
        if (p.ExitCode != 0 && LooksLikeMissingCrystal(output)) output += CrystalRuntimeHint;
        return (p.ExitCode, output);
    }

    private static bool LooksLikeMissingCrystal(string output) =>
        output.Contains("CrystalDecisions", StringComparison.OrdinalIgnoreCase) &&
        (output.Contains("Could not load", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("FileNotFoundException", StringComparison.OrdinalIgnoreCase));
}
