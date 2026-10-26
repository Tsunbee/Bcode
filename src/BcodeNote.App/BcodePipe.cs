using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Bcode.App.Services;

namespace BcodeNote.App;

/// <summary>
/// Gửi script sang Bcode đang mở qua pipe Bcode.Control.&lt;session&gt; (Services/ViewerControlServer.cs của Bcode):
/// "sqlp|&lt;b64 script&gt;|&lt;b64 tiêu đề&gt;|&lt;b64 dự án&gt;|&lt;0 App / 1 Sys&gt;" → Bcode mở tab SQL Query mới đúng database App / Sys.
/// </summary>
internal static class BcodePipe
{
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>"" = đã gửi; khác = lý do không gửi được (Bcode chưa mở, bản Bcode cũ...).</summary>
    public static string SendScript(string script, string title, string project, bool sys)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        try
        {
            AllowSetForegroundWindow(-1);   // ASFW_ANY: cho Bcode đưa cửa sổ của nó lên trước khi mở tab
            using var client = new NamedPipeClientStream(".", ViewerControlServer.PipeName, PipeDirection.Out);
            client.Connect(1500);
            var bytes = Encoding.UTF8.GetBytes($"sqlp|{B64(script)}|{B64(title)}|{B64(project)}|{(sys ? "1" : "0")}\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return "";
        }
        catch (TimeoutException) { return "Bcode chưa mở."; }
        catch (Exception ex) { return "Không gửi được sang Bcode: " + ex.Message; }
    }
}
