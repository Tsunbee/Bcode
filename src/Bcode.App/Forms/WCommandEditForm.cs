using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "WCOMMAND - Edit" — the New/Edit dialog for a single wcommand (+ command) row,
/// opened from the WCommand tree's right-click menu. One form handles both New
/// (pass <paramref name="existing"/> = null) and Edit (pass the selected item) —
/// FCode's own dialog is the same shape either way, just with Delete disabled for
/// a row that doesn't exist yet.
///
/// Deliberately narrower than FCode's real dialog: no "Table Dir"/"Report Form"/
/// "File" tabs (those belong to a different part of FCode, not the wcommand row
/// itself) and no embedded Refresh Data/New toolbar — the tree's own context menu
/// already covers New/Edit/Delete/Refresh, so duplicating them inside the dialog
/// would just be two ways to do the same thing.
/// </summary>
public class WCommandEditForm : Bcode.App.UI.ThemedForm
{
    private readonly WCommandService _service;
    private readonly WCommandItem? _existing;

    private readonly TextBox _wmenuId;
    private readonly TextBox _wmenuId0;
    private readonly TextBox _menuId;
    private readonly TextBox _bar;
    private readonly TextBox _bar2;
    private readonly TextBox _link;
    private readonly TextBox _parameter;
    private readonly TextBox _iconUrl;
    private readonly TextBox _status;
    private readonly TextBox _icon;
    private readonly TextBox _sysId;
    private readonly TextBox _type;
    private readonly TextBox _sysCode;
    private readonly TextBox _msys;
    private readonly TextBox _target;
    private readonly TextBox _xtype;
    private readonly TextBox _edition;
    private readonly NumericUpDown _explIcon;

    private readonly Button _saveButton;
    private readonly Button _deleteButton;
    private readonly Button? _suggestWMenuIdButton;
    private readonly Label _statusLabel;

    /// <param name="existing">Non-null = Edit mode (Delete enabled, Save deletes this row's own
    /// id before inserting). Null = New mode.</param>
    /// <param name="template">New mode only — the menu the user right-clicked "New" on, if any.
    /// Every field is prefilled from it (same parent, same link/sysid/icon/...), so creating a
    /// menu similar to an existing one is just "New" on it, tweak the id/name, Save — instead of
    /// typing all 18 fields from scratch.</param>
    public WCommandEditForm(WCommandService service, WCommandItem? existing, WCommandItem? template = null)
    {
        _service = service;
        _existing = existing;
        var isNew = existing is null;
        var seed = existing ?? template;

        Text = isNew ? "WCOMMAND - New" : "WCOMMAND - Edit";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 560;
        Height = 620;
        StartPosition = FormStartPosition.CenterParent;

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _wmenuId = new TextBox { Width = 220, Text = seed?.WMenuId ?? "" };
        _wmenuId0 = new TextBox { Width = 320, Text = seed?.WMenuId0 ?? "" };
        _menuId = new TextBox { Width = 320, Text = seed?.MenuId ?? "" };
        _bar = new TextBox { Width = 320, Text = seed?.Bar ?? "" };
        _bar2 = new TextBox { Width = 320, Text = seed?.Bar2 ?? "" };
        _link = new TextBox { Width = 320, Text = seed?.Link ?? "" };
        _parameter = new TextBox { Width = 320, Text = seed?.Parameter ?? "" };
        _iconUrl = new TextBox { Width = 320, Text = seed?.IconUrl ?? "" };
        _status = new TextBox { Width = 320, Text = seed?.Status ?? "" };
        _icon = new TextBox { Width = 320, Text = seed?.Icon ?? "" };
        _sysId = new TextBox { Width = 320, Text = seed?.SysId ?? "" };
        _type = new TextBox { Width = 320, Text = seed?.Type ?? "" };
        _sysCode = new TextBox { Width = 320, Text = seed?.SysCode ?? "" };
        _msys = new TextBox { Width = 320, Text = (seed?.Msys ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        _target = new TextBox { Width = 320, Text = seed?.Target ?? "" };
        _xtype = new TextBox { Width = 320, Text = seed?.XType ?? "" };
        _edition = new TextBox { Width = 320, Text = seed?.Edition ?? "" };
        _explIcon = new NumericUpDown { Width = 320, Minimum = 0, Maximum = 255, Value = seed?.ExplIcon ?? 0 };

        // "Suggest" only makes sense in New mode — Edit's WMenu Id is the row's own real id.
        // Dock=Fill (textbox) + Dock=Right (button) inside a fixed-size Panel — same proven
        // pattern as WCommandTreeControl's filter+Refresh row and FileLookupControl's path+Load
        // row (Fill control added to .Controls FIRST, then the edge-docked one, so the fixed-
        // width edge control still gets its width instead of being squeezed out) — more
        // reliable here than an AutoSize FlowLayoutPanel sitting in a Percent-width
        // TableLayoutPanel column, which was the previous approach.
        Control wmenuIdField = _wmenuId;
        if (isNew)
        {
            var wmenuIdRow = new Panel { Width = 300, Height = 23 };
            _wmenuId.Dock = DockStyle.Fill;
            _suggestWMenuIdButton = new Button { Text = "Suggest", Dock = DockStyle.Right, Width = 70 };
            _suggestWMenuIdButton.Click += async (_, _) => await SuggestWMenuIdAsync();
            wmenuIdRow.Controls.Add(_wmenuId);
            wmenuIdRow.Controls.Add(_suggestWMenuIdButton);
            wmenuIdField = wmenuIdRow;
        }

        AddRow(form, "WMenu Id", wmenuIdField);
        AddRow(form, "WMenu Id0 (parent)", _wmenuId0);
        AddRow(form, "Menu Id", _menuId);
        AddRow(form, "Bar (tên tiếng Việt)", _bar);
        AddRow(form, "Bar2 (English name)", _bar2); // relabeled per request — this is the menu's English label
        AddRow(form, "Link", _link);
        AddRow(form, "Parameter", _parameter);
        AddRow(form, "Icon Url", _iconUrl);
        AddRow(form, "Status", _status);
        AddRow(form, "Icon", _icon);
        AddRow(form, "Sysid", _sysId);
        AddRow(form, "Type", _type);
        AddRow(form, "Syscode", _sysCode);
        AddRow(form, "Msys", _msys);
        AddRow(form, "Target", _target);
        AddRow(form, "Xtype", _xtype);
        AddRow(form, "Edition", _edition);
        AddRow(form, "Expl Icon (0-255)", _explIcon);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _saveButton = new Button { Text = "Save", AutoSize = true };
        _saveButton.Click += async (_, _) => await SaveAsync();
        _deleteButton = new Button { Text = "Delete", AutoSize = true, Enabled = !isNew };
        _deleteButton.Click += async (_, _) => await DeleteAsync();
        var closeButton = new Button { Text = "Close", AutoSize = true };
        closeButton.Click += (_, _) => Close();
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_deleteButton);
        buttons.Controls.Add(closeButton);

        form.Controls.Add(new Panel(), 0, form.RowCount);
        form.Controls.Add(buttons, 1, form.RowCount - 1);
        form.RowCount++;

        _statusLabel = new Label { AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = Color.Firebrick };
        form.Controls.Add(new Panel(), 0, form.RowCount);
        form.Controls.Add(_statusLabel, 1, form.RowCount - 1);

        Controls.Add(form);

        if (isNew)
            // Auto-fill a free id right away instead of making "Suggest" the first thing the
            // user has to think about — this also replaces a cloned template's own (already
            // taken) id, which is exactly the one field a clone MUST change before Save.
            Load += async (_, _) => await SuggestWMenuIdAsync();
    }

    private async Task SuggestWMenuIdAsync()
    {
        if (_suggestWMenuIdButton is null) return; // New-mode-only control

        _suggestWMenuIdButton.Enabled = false;
        try
        {
            var suggestion = await _service.SuggestNextWMenuIdAsync(_wmenuId0.Text.Trim());
            _wmenuId.Text = suggestion;
            _wmenuId.SelectAll();
            _wmenuId.Focus();
        }
        catch (Exception ex)
        {
            // Non-fatal — the field is still a plain editable textbox, just left as-is.
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = $"Không gợi ý được WMenu Id: {ex.Message}";
        }
        finally
        {
            _suggestWMenuIdButton.Enabled = true;
        }
    }

    private static void AddRow(TableLayoutPanel form, string label, Control input)
    {
        var row = form.RowCount;
        form.RowCount = row + 1;
        form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, row);
        form.Controls.Add(input, 1, row);
    }

    private bool TryBuildItem(out WCommandItem item)
    {
        item = new WCommandItem
        {
            WMenuId = _wmenuId.Text.Trim(),
            WMenuId0 = _wmenuId0.Text.Trim(),
            MenuId = _menuId.Text.Trim(),
            Bar = _bar.Text,
            Bar2 = _bar2.Text,
            Link = _link.Text,
            Parameter = _parameter.Text,
            IconUrl = _iconUrl.Text,
            Status = _status.Text,
            Icon = _icon.Text,
            SysId = _sysId.Text,
            Type = _type.Text,
            SysCode = _sysCode.Text,
            Target = _target.Text,
            XType = _xtype.Text,
            Edition = _edition.Text,
            ExplIcon = (byte)_explIcon.Value,
        };

        if (string.IsNullOrWhiteSpace(item.WMenuId) || string.IsNullOrWhiteSpace(item.MenuId) || string.IsNullOrWhiteSpace(item.Bar))
        {
            _statusLabel.Text = "Nhập WMenu Id, Menu Id và Bar trước.";
            return false;
        }

        if (!decimal.TryParse(_msys.Text.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var msys))
        {
            _statusLabel.Text = "Msys phải là số.";
            return false;
        }
        item.Msys = msys;

        return true;
    }

    private async Task SaveAsync()
    {
        if (!TryBuildItem(out var item)) return;

        _saveButton.Enabled = false;
        _statusLabel.ForeColor = SystemColors.ControlText;
        _statusLabel.Text = "Đang lưu...";
        try
        {
            await _service.SaveAsync(item, _existing?.WMenuId, _existing?.MenuId);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = ex.Message;
        }
        finally
        {
            _saveButton.Enabled = true;
        }
    }

    private async Task DeleteAsync()
    {
        if (_existing is null) return;

        var confirm = MessageBox.Show(this,
            $"Xóa menu '{_existing.Bar}' ({_existing.WMenuId})?",
            "Bcode — WCommand", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _deleteButton.Enabled = false;
        try
        {
            await _service.DeleteAsync(_existing);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = ex.Message;
        }
        finally
        {
            _deleteButton.Enabled = true;
        }
    }
}
