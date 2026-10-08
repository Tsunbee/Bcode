using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Note" / "Note (New)" tool — small per-workspace scratch notes saved to
/// disk (see NoteService). One NoteControl instance edits one note at a
/// time; "Note (New)" in MainForm opens a fresh NoteControl with a new,
/// not-yet-used name, "Note" reopens/creates the default one.
/// Giao diện là trang WebView2 (Web/Shell/note.html — tự co giãn, ăn theo Template giao diện); control chỉ đọc / ghi file qua <see cref="NoteService"/>.
/// </summary>
public class NoteControl : UserControl
{
    private readonly WebBarHost _web = new("note.html") { Dock = DockStyle.Fill };
    private readonly NoteService _service;
    private readonly string _workspaceName;
    private string _name;
    private bool _dirty;

    private static string J(object? o) => JsonSerializer.Serialize(o);

    public string NoteName => _name.Trim();

    public NoteControl(NoteService service, string workspaceName, string noteName)
    {
        _service = service;
        _workspaceName = workspaceName;
        _name = noteName;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Ready += SendInit;
        _web.Message += root =>
        {
            var m = root.Clone();
            var action = m.TryGetProperty("action", out var a) ? a.GetString() : "";
            var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            switch (action)
            {
                case "open": _name = name; LoadCurrent(); break;
                case "save": _name = name; SaveCurrent(m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""); break;
                case "dirty": _dirty = m.TryGetProperty("value", out var d) && d.GetBoolean(); break;
                case "name": _name = name; break;
            }
        };
    }

    private void Js(string script) { if (!IsDisposed) _web.Call(script); }

    private void SendInit()
    {
        Js($"note.init({J(new { name = _name, notes = _service.ListNotes(_workspaceName) })})");
        LoadCurrent();
    }

    private void LoadCurrent()
    {
        var name = NoteName;
        if (string.IsNullOrWhiteSpace(name)) return;
        _dirty = false;
        Js($"note.onLoaded({J(new { name, text = _service.LoadNote(_workspaceName, name) })})");
    }

    private void SaveCurrent(string text)
    {
        var name = NoteName;
        if (string.IsNullOrWhiteSpace(name)) { Js($"note.onStatus({J("Nhập tên note trước khi lưu.")}, 'err')"); return; }
        _service.SaveNote(_workspaceName, name, text);
        _dirty = false;
        Js($"note.onSaved({J(new { name, notes = _service.ListNotes(_workspaceName) })})");
    }

    /// <summary>Whether there are unsaved edits — MainForm's tab-close confirm reuses this
    /// the same way it does for ScriptEditorControl.IsDirty.</summary>
    public bool IsDirty => _dirty;
}
