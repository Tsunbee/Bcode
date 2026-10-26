using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Bcode.App.Services;

/// <summary>
/// Cổng nhận lệnh từ BcodeViewer (tiến trình riêng): F5 trong BcodeViewer = lưu file rồi gửi sang đây
/// "fsg|&lt;đường dẫn file&gt;|&lt;tên project&gt;" để Bcode bung FSG FBO và chạy menu của file đó.
///
/// Tên pipe gắn với Windows session (giống single-instance của BcodeViewer) để 2 người cùng đăng nhập
/// Remote Desktop không gửi nhầm lệnh sang Bcode của nhau. BcodeViewer tính tên này riêng ở
/// Host/EditorBridge.cs — 2 bên không tham chiếu nhau nên phải giữ chuỗi trùng khớp.
/// </summary>
public sealed class ViewerControlServer : IDisposable
{
    public static string PipeName => $"Bcode.Control.{Process.GetCurrentProcess().SessionId}";

    /// <summary>(đường dẫn file, tên project hoặc ""). Bắn ở thread nền — người nhận tự marshal về UI.</summary>
    public event Action<string, string>? RunMenuRequested;

    /// <summary>(script, tiêu đề tab) — "Debug trong Bcode" của hộp Chạy SQL bên BcodeViewer: mở tab SQL Query
    /// mới chứa script (đã gộp entity, thay macro, khai báo sẵn tham số). Bắn ở thread nền.</summary>
    public event Action<string, string>? SqlScriptRequested;

    /// <summary>(script, tiêu đề tab, tên dự án, true = Sys Data) — BcodeNote.exe: mở script vào tab SQL Query mới, đúng database App / Sys.
    /// Dự án khác dự án Bcode đang mở thì tab vẫn mở nhưng có dòng cảnh báo đầu script. Bắn ở thread nền.</summary>
    public event Action<string, string, string, bool>? SqlScriptForProjectRequested;

    private readonly CancellationTokenSource _cts = new();

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) continue;

                var parts = line.Split('|');
                if (parts.Length >= 2 && parts[0] == "fsg" && parts[1].Length > 0)
                    RunMenuRequested?.Invoke(parts[1], parts.Length > 2 ? parts[2] : "");
                // "sql|<base64 script>|<base64 tiêu đề>": base64 vì script có xuống dòng và dấu |.
                else if (parts.Length >= 2 && parts[0] == "sql")
                {
                    string Decode(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));
                    SqlScriptRequested?.Invoke(Decode(parts[1]), parts.Length > 2 ? Decode(parts[2]) : "");
                }
                // "sqlp|<b64 script>|<b64 tiêu đề>|<b64 tên dự án>|<0 = App, 1 = Sys>": BcodeNote.exe mở script của gói (Note (New) nhiều dự án) vào tab SQL Query.
                else if (parts.Length >= 2 && parts[0] == "sqlp")
                {
                    string Decode(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));
                    SqlScriptForProjectRequested?.Invoke(Decode(parts[1]), parts.Length > 2 ? Decode(parts[2]) : "",
                        parts.Length > 3 ? Decode(parts[3]) : "", parts.Length > 4 && parts[4] == "1");
                }
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(500); } // pipe hỏng / client ngắt giữa chừng — thử lại vòng sau
        }
    }

    public void Dispose() => _cts.Cancel();
}
