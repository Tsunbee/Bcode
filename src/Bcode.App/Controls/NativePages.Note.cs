using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Note / Note (New) (note.html): ô tên note (gợi ý theo các note đã lưu) + Mở / Save, vùng soạn chữ đơn cách chiếm hết phần còn lại.
/// Chủ là <see cref="NoteControl"/> — không phải sửa gì ở đó: WebBarHost tự dùng trang này ở chế độ App.</summary>
internal sealed class NotePage : NativePage
{
    private readonly RoundedInput _name = NK.Input("Tên note...");
    private readonly AutoCompleteStringCollection _notes = new();
    private readonly TextBox _text = new()
    {
        Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, AcceptsTab = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, BorderStyle = BorderStyle.None, PlaceholderText = "Ghi chú...",
    };
    private readonly Panel _edit = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 4, 4) };   // lề trong cho vùng soạn (TextBox không có padding)
    private readonly Label _status = NK.Muted("");
    private bool _dirty, _loading;

    public NotePage()
    {
        _name.Width = NativeSkin.S(300);
        _name.Inner.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _name.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _name.Inner.AutoCompleteCustomSource = _notes;
        _name.TextChanged += (_, _) => { if (!_loading) Post(new { action = "name", name = _name.Text }); };
        _name.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Open(); } };

        _text.Font = ThemeManager.MonoFont;
        NK.DarkScroll(_text);
        _name.Margin = new Padding(0, 0, 8, 0);
        _text.TextChanged += (_, _) => { if (_loading) return; SetDirty(true); Say("Chưa lưu...", ""); };

        var lbl = NK.Muted("Note:"); lbl.Margin = new Padding(0, 8, 6, 0);
        var bar = new BarFlow { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(10, 6, 10, 6) };
        _status.Margin = new Padding(6, 8, 0, 0);
        bar.Controls.AddRange(new Control[] { lbl, _name, NK.Button("Mở", Open), NK.Button("💾 Save", Save, primary: true), _status });

        _edit.Controls.Add(_text);
        Controls.Add(_edit); Controls.Add(bar);
        Recolor();
        ThemeManager.ThemeChanged += Recolor;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= Recolor;
    }

    private void Recolor() { _text.BackColor = _edit.BackColor = AppColors.Input; _text.ForeColor = AppColors.Text; }

    private void Say(string text, string kind) { _status.Text = text; _status.ForeColor = kind == "err" ? AppColors.Danger : kind == "ok" ? AppColors.Success : AppColors.TextMuted; }
    private void SetDirty(bool v) { if (_dirty == v) return; _dirty = v; Post(new { action = "dirty", value = v }); }
    private void Save() => Post(new { action = "save", name = _name.Text, text = _text.Text });
    private void Open()
    {
        if (_dirty && !NK.Confirm(this, "Note đang sửa chưa lưu — bỏ thay đổi và mở note khác?")) return;
        Post(new { action = "open", name = _name.Text });
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S)) { Save(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public override void FocusPrimary() => _text.Focus();

    private void FillNotes(JsonElement d) { _notes.Clear(); _notes.AddRange(PA(d, "notes").Select(n => n.GetString() ?? "").Where(n => n.Length > 0).ToArray()); }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "note.init":
            {
                var d = Arg(a, 0);
                _loading = true; _name.Text = P(d, "name"); _loading = false;
                FillNotes(d);
                break;
            }
            case "note.onLoaded":
            {
                var d = Arg(a, 0);
                _loading = true; _name.Text = P(d, "name"); _text.Text = P(d, "text"); _loading = false;
                _dirty = false; Say("", "");
                break;
            }
            case "note.onSaved": FillNotes(Arg(a, 0)); _dirty = false; Say("Đã lưu.", "ok"); break;
            case "note.onStatus": Say(Str(a, 0), Str(a, 1)); break;
        }
    }
}
