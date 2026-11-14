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
    public static string PipeName => _pipeName;
    private static readonly string _pipeName = CurrentPipeName();
    private static string CurrentPipeName() { using var p = Process.GetCurrentProcess(); return $"Bcode.Control.{p.SessionId}"; }

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
        // Cửa sổ Bcode thứ 2 trở đi: pipe (1 instance) đang do cửa sổ đầu giữ → tạo pipe ném IOException "All pipe instances are busy".
        // Trước đây thử lại mỗi 500 ms mãi mãi (exception 2 lần/s vào vòng đệm ErrorLog). Nay lùi dần tới 5 s; cửa sổ đầu đóng thì
        // cửa sổ này nhận pipe chậm nhất ~5 s.
        var busyDelay = 500;
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                busyDelay = 500;
            }
            catch (IOException)
            {
                try { await Task.Delay(busyDelay, _cts.Token); } catch (OperationCanceledException) { return; }
                busyDelay = Math.Min(busyDelay * 2, 5000);
                continue;
            }
            catch { await Task.Delay(500); continue; }
            try
            {
                await using var owned = server;   // đóng pipe sau mỗi lượt như trước
                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) continue;

                // Giao thức có phiên bản (Shared/PipeProtocol.cs). Dạng MỚI "bcp1|verb|b64..." thì args đã giải base64; dạng CŨ giữ nguyên để
                // BcodeViewer / BcodeNote bản cũ vẫn gửi được: fsg| (thô), sql| / sqlp| (base64 thủ công như trước).
                var msg = Bcode.Shared.PipeProtocol.Parse(line);
                string Decode(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));
                switch (msg.Verb)
                {
                    case "fsg" when msg.Arg(0).Length > 0:
                        RunMenuRequested?.Invoke(msg.Arg(0), msg.Arg(1));   // fsg| gửi thô ở cả hai dạng (đường dẫn / tên dự án không có '|')
                        break;
                    case "sql":
                        if (msg.IsVersioned) SqlScriptRequested?.Invoke(msg.Arg(0), msg.Arg(1));
                        else SqlScriptRequested?.Invoke(Decode(msg.Arg(0)), msg.Args.Length > 1 ? Decode(msg.Arg(1)) : "");
                        break;
                    case "sqlp":
                        if (msg.IsVersioned)
                            SqlScriptForProjectRequested?.Invoke(msg.Arg(0), msg.Arg(1), msg.Arg(2), msg.Arg(3) == "1");
                        else
                            SqlScriptForProjectRequested?.Invoke(Decode(msg.Arg(0)), msg.Args.Length > 1 ? Decode(msg.Arg(1)) : "",
                                msg.Args.Length > 2 ? Decode(msg.Arg(2)) : "", msg.Args.Length > 3 && msg.Arg(3) == "1");
                        break;
                    // verb lạ (bản sau) → bỏ qua, không vỡ.
                }
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(500); } // pipe hỏng / client ngắt giữa chừng — thử lại vòng sau
        }
    }

    public void Dispose() => _cts.Cancel();
}
