using System.IO.Pipes;
using System.Text;

namespace BcodeViewer.App;

internal static class Program
{
    /// <summary>Also referenced by MainForm's pipe-server loop (see StartPipeServer) —
    /// keeping the name here means the client (this file) and the server (MainForm) can't
    /// drift apart.</summary>
    public const string SingleInstancePipeName = "BcodeViewer.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
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
        using var mutex = new Mutex(true, "Global\\BcodeViewer.SingleInstance.Mutex", out var createdNew);
        if (!createdNew)
        {
            if (initialFile is not null) TryHandOffToRunningInstance(initialFile, projectName);
            return;
        }

        ApplicationConfiguration.Initialize();
        var mainForm = new MainForm(initialFile, projectName);
        Application.Run(mainForm);
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
}
