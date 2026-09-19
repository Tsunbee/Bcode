using System.Runtime.InteropServices;
using System.Text.Json;
using BcodeViewer.App.Settings;

namespace BcodeViewer.App.Host;

/// <summary>
/// Exposed to the page via CoreWebView2.AddHostObjectToScript("host", this) — called from
/// JS as chrome.webview.hostObjects.host.&lt;method&gt;(...), each returning a Promise.
/// All file I/O and the AI call go through here rather than page JS touching the filesystem
/// or the Anthropic key directly: arbitrary file access isn't available to page script
/// anyway, and this keeps one place to handle a locked file or a down UNC path.
///
/// [ComVisible]/[ClassInterface(AutoDual)] are required by WebView2's IDispatch-based
/// marshaling for host objects — without them AddHostObjectToScript still "succeeds" but
/// the page sees an object with no callable members.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class EditorBridge
{
    private readonly ClaudeChatService _chat;
    private readonly Func<string, string?> _chooseSaveAsPath;

    public EditorBridge(ViewerSettings settings, Func<string, string?> chooseSaveAsPath)
    {
        _chat = new ClaudeChatService(settings);
        _chooseSaveAsPath = chooseSaveAsPath;
    }

    /// <summary>Raised as the caret moves, so MainForm's status bar can show "Ln X, Col Y"
    /// the way FCodeViewer's own does.</summary>
    public event Action<int, int>? CursorChanged;

    public void NotifyCursorChanged(int line, int column) => CursorChanged?.Invoke(line, column);

    /// <summary>"Save As" needs a native file picker, which only the WinForms side can show
    /// (and only on the UI thread) — <paramref name="suggestedPath"/> seeds the dialog's
    /// initial folder/name; returns null if the user cancels. The delegate passed in from
    /// MainForm's constructor already handles the UI-thread marshaling.</summary>
    public string? ChooseSaveAsPath(string suggestedPath) => _chooseSaveAsPath(suggestedPath);

    /// <summary>Raised whenever the page (a single-document editor — see editor.js) opens a
    /// new file in place of whatever was shown, so MainForm can add it to the left "recent
    /// files by project" tree and update the window title. Fired from whatever thread
    /// WebView2 dispatches this host object call on — never assume it's the UI thread.</summary>
    public event Action<string>? FileOpened;

    /// <summary>Raised when the open file's dirty (unsaved-changes) state changes, so
    /// MainForm can reflect it in the window title.</summary>
    public event Action<string, bool>? DirtyChanged;

    public void NotifyFileOpened(string path) => FileOpened?.Invoke(path);

    public void NotifyDirtyChanged(string path, bool isDirty) => DirtyChanged?.Invoke(path, isDirty);

    public string ReadFile(string path) => File.ReadAllText(path);

    public void WriteFile(string path, string content) => File.WriteAllText(path, content);

    public bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Backs the "file changed on another machine" watch in editor.js: the page
    /// polls this for the currently open file and compares it against the write time it
    /// captured at open/save, showing a reload banner when they differ. Returned as an ISO
    /// "o"-format string rather than ticks/epoch-ms — a DateTime.Ticks value is well past
    /// JS's 2^53 safe-integer range and WebView2's IDispatch marshaling would round-trip it
    /// as a lossy double, breaking the equality check this is used for; string equality has
    /// no such precision concern. Empty string means "can't compare" (file missing/locked),
    /// which the caller treats as "nothing to report" rather than as a change.</summary>
    public string GetFileWriteTimeUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).ToString("o") : "";
        }
        catch
        {
            return ""; // locked/unreadable right now — try again on the next poll
        }
    }

    /// <summary>Backs the "Open Folder" context-menu submenu — opens Explorer at a folder
    /// (or, for a file, opens its containing folder with that file selected), same as
    /// FCode's own quick-access folder shortcuts (Images/Options/Lookup/Templates siblings
    /// of Controllers under App_Data).</summary>
    public void OpenFolder(string path)
    {
        if (File.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
    }

    /// <summary>JSON array of {"name","path","isDirectory"} for one folder level — powers
    /// the page's own "open sibling file" affordance if it wants one beyond the native
    /// WinForms tree on the left; the tree itself is populated directly in MainForm.</summary>
    public string ListDirectory(string path)
    {
        var entries = new List<object>();
        try
        {
            foreach (var d in Directory.GetDirectories(path).OrderBy(x => x))
                entries.Add(new { name = Path.GetFileName(d), path = d, isDirectory = true });
            foreach (var f in Directory.GetFiles(path).OrderBy(x => x))
                entries.Add(new { name = Path.GetFileName(f), path = f, isDirectory = false });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        return JsonSerializer.Serialize(entries);
    }

    // Synchronous on purpose: this WebView2 SDK version's AddHostObjectToScript is the
    // classic IDispatch-based one (no Async suffix), which predates reliable Task<T>
    // marshaling — JS still sees this as a Promise regardless, since WebView2 dispatches
    // every host object call on its own background thread, so blocking here on the HTTP
    // call doesn't touch the WinForms UI thread.
    public string AskAI(string prompt, string? fileContext, string? filePath) =>
        _chat.AskAsync(prompt, fileContext, filePath).GetAwaiter().GetResult();
}
