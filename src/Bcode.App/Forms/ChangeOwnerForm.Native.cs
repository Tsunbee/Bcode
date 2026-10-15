using Bcode.App.Controls;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>Giao diện WinForms của "Change Owner" (chế độ App, thay changeowner.html): chọn database, gõ object (gợi ý tên từ cả App Data và Sys Data), nhập schema mới rồi chạy
/// ALTER SCHEMA ... TRANSFER qua <see cref="Services.ChangeOwnerService"/>. Thao tác không hoàn tác được nên luôn hỏi xác nhận trước.</summary>
public partial class ChangeOwnerForm
{
    private DropDownButton _nDb = null!;
    private RoundedInput _nObj = null!, _nSchema = null!;
    private Label _nStatus = null!;
    private PillButton _nRun = null!;

    private void BuildNative()
    {
        KeyPreview = true;
        var head = new Label { Text = "Change Owner", AutoSize = true, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 3, FontStyle.Bold), Padding = new Padding(0, 0, 0, 2) };
        var sub = NK.Wrap("Chuyển 1 object SQL sang schema khác (ALTER SCHEMA ... TRANSFER). Mọi code/view/proc tham chiếu object bằng tên schema cũ sẽ phải sửa lại.", this, 60);

        _nDb = new DropDownButton { Height = NativeSkin.RowHeight(this, 28), Width = 200 };
        _nDb.SetItems(new[] { "App Data", "Sys Data" }, 0);
        _nSchema = NK.Input("vd: dbo");
        _nObj = NK.Input("dbo.tenobject"); _nObj.Text = _preselected;
        _nObj.Inner.AutoCompleteMode = AutoCompleteMode.Suggest; _nObj.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource;
        var warn = new Label { Text = "⚠ Thao tác này không hoàn tác được từ Bcode.", AutoSize = true, ForeColor = AppColors.Warning, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };

        var card = new CardPanel("Thông tin") { Dock = DockStyle.Top };
        card.Body.Controls.Add(warn);
        card.Body.Controls.Add(NK.FormGrid(240, ("Database", _nDb), ("Schema mới", _nSchema), ("Object (schema.name) — gõ để gợi ý", _nObj)));
        card.Body.Controls.SetChildIndex(warn, 0);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 8), AutoScroll = true };
        sub.Dock = DockStyle.Top; head.Dock = DockStyle.Top;
        body.Controls.Add(card); body.Controls.Add(sub); body.Controls.Add(head);

        _nStatus = new Label { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 14, 0) };
        _nRun = NK.Button("Đổi Owner (ALTER SCHEMA TRANSFER)", () => _ = RunNativeAsync(), primary: true);
        var footer = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 7, 10, 7) };
        footer.Controls.Add(_nStatus); footer.Controls.Add(NK.Button("Đóng", Close)); footer.Controls.Add(_nRun);
        Controls.Add(body); Controls.Add(footer);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };
        Shown += (_, _) => { _nObj.Inner.Focus(); _ = LoadNativeSuggestionsAsync(); };
    }

    private void NStatus(string text, bool error = false, bool ok = false)
    {
        _nStatus.Text = text;
        _nStatus.ForeColor = error ? AppColors.Danger : ok ? AppColors.Success : AppColors.TextMuted;
    }

    private async Task LoadNativeSuggestionsAsync()
    {
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
                names.AddRange((await _sqlObjectService.ListObjectsAsync(useSys)).Select(o => o.QualifiedName));
            if (IsDisposed) return;
            var src = new AutoCompleteStringCollection();
            src.AddRange(names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToArray());
            _nObj.Inner.AutoCompleteCustomSource = src;
        }
        catch { /* chưa kết nối — không có gợi ý */ }
    }

    private async Task RunNativeAsync()
    {
        var objectText = _nObj.Text.Trim(); var newSchema = _nSchema.Text.Trim();
        if (objectText.Length == 0 || newSchema.Length == 0) { NStatus("Nhập Object và Schema mới.", error: true); return; }
        var raw = objectText.Replace("[", "").Replace("]", "");
        var parts = raw.Split('.', 2);
        var (schema, name) = parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
        var confirm = MessageBox.Show(this,
            $"Chuyển [{schema}].[{name}] sang schema [{newSchema}]?\nMọi code/view/proc tham chiếu object này bằng tên schema cũ sẽ phải sửa lại.",
            "Bcode — Change Owner", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;
        _nRun.Enabled = false; NStatus("Đang chạy...");
        try
        {
            await _service.ChangeOwnerAsync(_nDb.SelectedIndex == 1, schema, name, newSchema);
            NStatus("Đã đổi owner (schema) thành công.", ok: true);
        }
        catch (Exception ex) { NStatus(ex.Message, error: true); }
        finally { _nRun.Enabled = true; }
    }
}
