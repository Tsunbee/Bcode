using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace BcodeViewer.App;

internal static class Program
{
    /// <summary>The Windows session this process belongs to. Both single-instance names
    /// below are scoped to it — see <see cref="SingleInstancePipeName"/>.</summary>
    private static readonly int SessionId = Process.GetCurrentProcess().SessionId;

    /// <summary>
    /// Also referenced by MainForm's pipe-server loop (see StartPipeServer) — deriving both
    /// ends from this one place means the client (this file) and the server (MainForm)
    /// can't drift apart.
    ///
    /// The session id is part of the name because named pipes have no session namespace of
    /// their own: <c>\\.\pipe\name</c> is machine-wide whatever prefix is used. Without it
    /// the pipe would undo what <see cref="MutexName"/> achieves, and on a Remote Desktop
    /// host the second person's launch would hand their file to the first person's window —
    /// in a session they cannot see.
    /// </summary>
    public static string SingleInstancePipeName => $"BcodeViewer.SingleInstance.{SessionId}";

    /// <summary>
    /// Scoped to the session rather than the machine. The name used to start with
    /// <c>Global\</c>, which is shared across every session on the host: two people signed
    /// in over Remote Desktop would fight over one instance, and — because a Global object
    /// created by one user is not openable by another — the second person's launch could
    /// fail outright with UnauthorizedAccessException before any window existed.
    /// <c>Local\</c> is per-session, which on an ordinary single-user desktop is exactly
    /// what the old name did anyway.
    /// </summary>
    private const string MutexName = @"Local\BcodeViewer.SingleInstance.Mutex";

    [STAThread]
    private static void Main(string[] args)
    {
        // Wired before anything else can throw. Without it an exception on the way up — the
        // Mutex below was one candidate — ended the process with no window and no message,
        // which is indistinguishable from "the app didn't start" and leaves nothing to
        // investigate afterwards.
        InstallCrashHandlers();

        // args[0] is the file Bcode.App (or the user, via a shortcut/"Open with") launched
        // this with — optional so BcodeViewer can still start with nothing open. args[1] is
        // the project/workspace name Bcode.App's File Lookup was on when it launched this —
        // used to group the recent-files panel like FCodeViewer's own (falls back to
        // "#Other" for a standalone launch that doesn't know a project name).
        var initialFile = args.Length > 0 ? args[0] : null;
        var projectName = args.Length > 1 ? args[1] : "#Other";

        // BcodeViewer is meant to behave as one app window, not one window per file — the
        // left tree (grouped by project) is the file switcher (see MainForm's doc comment),
        // so opening a second file while a window is already running should land in that
        // same window instead of spawning another one. A named Mutex is the usual way to
        // detect "is another instance of this exe already running" without relying on
        // process enumeration (which can't tell "our exe" apart from an unrelated one with
        // the same name, and races against the other instance still starting up).
        Mutex? mutex = null;
        var createdNew = true;
        try
        {
            mutex = new Mutex(true, MutexName, out createdNew);
        }
        catch (Exception ex)
        {
            // Can't tell whether another instance is running. Opening a second window is a
            // far smaller problem than refusing to start, so carry on as the first one.
            LogCrash("single-instance check failed (continuing as a new window)", ex);
        }

        try
        {
            if (!createdNew)
            {
                if (initialFile is not null) TryHandOffToRunningInstance(initialFile, projectName);
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(initialFile, projectName));
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    /// <summary>Sends "path|projectName" to the already-running instance's pipe server (see
    /// MainForm.StartPipeServer) and exits without ever showing a window. If the other
    /// instance is unresponsive (e.g. mid-shutdown — a narrow race between the mutex check
    /// above and the old instance actually tearing down its pipe server), falls back to
    /// opening a normal window rather than silently dropping the file the user asked for.</summary>
    private static void TryHandOffToRunningInstance(string filePath, string projectName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SingleInstancePipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(filePath + "|" + projectName);
        }
        catch
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(filePath, projectName));
        }
    }

    // ---- Crash reporting ------------------------------------------------------------

    /// <summary>
    /// Turns the three ways a .NET WinForms process can die quietly into a written record
    /// and a message the user can screenshot.
    ///
    /// Deliberately not a logging framework — that is a separate, larger piece of work.
    /// This is the floor: one file, appended to, so a crash report stops being "it just
    /// closed" and starts being a stack trace with a timestamp.
    /// </summary>
    private static void InstallCrashHandlers()
    {
        // Routes exceptions from the UI thread's message loop here instead of to WinForms'
        // own dialog. Must be set before the first window exists.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            // The message loop survives this, so the window and any unsaved work stay —
            // which is the right trade for a one-off failure in a handler.
            LogCrash("UI thread", e.Exception);
            ShowCrashDialog(e.Exception, fatal: false);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // The runtime is tearing the process down regardless; all that's left is to
            // write it down and say so.
            var ex = e.ExceptionObject as Exception;
            LogCrash("background thread (fatal)", ex);
            ShowCrashDialog(ex, fatal: true);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Recorded but not shown: by definition nobody was waiting on this task, and
            // since .NET 4.5 it no longer kills the process. A dialog here would interrupt
            // the user over something that did not affect them.
            LogCrash("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "crash.log");

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = CrashLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Keep it from growing without limit on a machine that crashes repeatedly.
            // One generation back is kept: the first crash is usually the informative one.
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, path + ".1", overwrite: true);
            }
            catch { /* locked by another instance — append anyway */ }

            File.AppendAllText(path,
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}  pid {Environment.ProcessId}  session {SessionId}  [{source}]{Environment.NewLine}" +
                $"{ex?.ToString() ?? "(no exception object)"}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Out of disk, no permission, %AppData% redirected somewhere unwritable. There
            // is nowhere left to report this to, and throwing from a crash handler would
            // replace a diagnosable failure with an undiagnosable one.
        }
    }

    private static void ShowCrashDialog(Exception? ex, bool fatal)
    {
        try
        {
            var headline = fatal
                ? "BcodeViewer gặp lỗi và phải đóng."
                : "BcodeViewer gặp lỗi. Cửa sổ vẫn mở — nên lưu lại file đang sửa.";

            MessageBox.Show(
                $"{headline}\n\n{ex?.Message ?? "(không rõ)"}\n\nChi tiết đã ghi vào:\n{CrashLogPath}",
                "BcodeViewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* no message pump left to show it on */ }
    }
}
