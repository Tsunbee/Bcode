using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Ô nhập thả xuống của nút "Check Fields" (SQL Query, cả chế độ Web lẫn App) — như FCode: nhãn "Fields", ô nhập, nút ✓ ngay dưới nút trên thanh.
/// Dán danh sách field (ma_vt, ten_vt…) hoặc đường dẫn file Grid / Dir / Filter rồi Enter / ✓ → bên gọi đối chiếu với bảng kết quả đang chọn.
/// Dòng dưới giữ chức năng cũ của nút: kiểm bảng / cột trong script bằng SET NOEXEC ON.
/// Chữ đã nhập được nhớ cho lần mở sau (trong phiên Bcode), mở ra là chọn sẵn để dán đè.
/// </summary>
internal static class CheckFieldsPopup
{
    private static string _last = "";

    /// <param name="owner">Control chứa nút (thanh công cụ); <paramref name="at"/> = toạ độ trong owner (góc trên-trái popup).</param>
    /// <param name="onCheck">Bấm ✓ / Enter với ô không rỗng — chạy sau khi popup đã đóng.</param>
    /// <param name="onScriptCheck">Bấm dòng "Kiểm bảng / cột trong script (NOEXEC)".</param>
    public static void Show(Control owner, Point at, Action<string> onCheck, Action onScriptCheck)
    {
        int S(int v) => NativeSkin.Px(owner, v);
        var panel = new Panel { BackColor = AppColors.Panel, Size = new Size(S(470), S(70)) };
        var label = new Label { Text = "Fields", AutoSize = true, ForeColor = AppColors.Text, BackColor = Color.Transparent, Location = new Point(S(12), S(13)), Font = ThemeManager.BaseFont };
        var input = new RoundedInput { Location = new Point(S(62), S(6)), Width = S(350), PlaceholderText = "ma_vt, ten_vt, ten_kh%l …  hoặc đường dẫn file Grid / Dir", Text = _last };
        var ok = PillButton.Flat("✓", primary: true);
        ok.AutoWidth = false; ok.Size = new Size(S(40), input.NaturalHeight); ok.Location = new Point(S(418), S(6));
        var hint = new ToolTip();
        hint.SetToolTip(ok, "Đối chiếu với bảng kết quả đang chọn (Enter)");
        var noexec = new LinkLabel
        {
            Text = "Kiểm bảng / cột trong script (SET NOEXEC ON)", AutoSize = true, BackColor = Color.Transparent, Font = ThemeManager.BaseFont,
            LinkColor = AppColors.Accent, ActiveLinkColor = AppColors.AccentHover, LinkBehavior = LinkBehavior.HoverUnderline,
        };
        panel.Controls.AddRange([label, input, ok, noexec]);
        panel.Height = input.Bottom + S(8) + noexec.PreferredHeight + S(8);
        noexec.Location = new Point(S(62), input.Bottom + S(6));

        var host = new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = Padding.Empty, Padding = Padding.Empty };
        var drop = new ToolStripDropDown { Padding = new Padding(1), AutoSize = true, BackColor = AppColors.Border, DropShadowEnabled = true };   // nền = viền 1px
        drop.Items.Add(host);

        void Run(Action a)
        {
            drop.Close(ToolStripDropDownCloseReason.ItemClicked);
            // Hộp thông báo mở sau khi popup đóng hẳn (không thì bị popup che / mất focus).
            if (owner.IsHandleCreated) owner.BeginInvoke(a); else a();
        }
        void Check()
        {
            var text = input.Text.Trim();
            if (text.Length == 0) { input.Inner.Focus(); return; }
            _last = input.Text;
            Run(() => onCheck(text));
        }
        ok.Click += (_, _) => Check();
        noexec.LinkClicked += (_, _) => { _last = input.Text; Run(onScriptCheck); };
        input.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Check(); }
            else if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert))
            {
                // Ô 1 dòng: dán danh sách nhiều dòng (cột copy từ Excel / file XML) → nối bằng ", " cho khỏi mất các dòng sau.
                string clip;
                try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { clip = ""; }
                if (clip.IndexOfAny(['\r', '\n']) < 0) return;
                e.Handled = e.SuppressKeyPress = true;
                var lines = clip.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().TrimEnd(',', ';')).Where(l => l.Length > 0);
                input.Inner.SelectedText = string.Join(", ", lines);
            }
            else if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; _last = input.Text; drop.Close(ToolStripDropDownCloseReason.Keyboard); }
        };
        drop.Closed += (_, _) => { _last = input.Text; hint.Dispose(); if (owner.IsHandleCreated) owner.BeginInvoke(new Action(drop.Dispose)); };

        drop.Show(owner, at);
        input.Inner.Focus();
        input.Inner.SelectAll();
    }

    /// <summary>Đối chiếu <paramref name="input"/> với bảng kết quả rồi báo bằng hộp thông báo (dài thì mở cửa sổ xem chữ) — dùng chung Web / App.</summary>
    /// <param name="tables">Cột của mọi bảng kết quả (theo thứ tự); <paramref name="chosen"/> = bảng đang chọn, -1 = chưa chọn.</param>
    public static void Report(Control owner, string input, IReadOnlyList<IReadOnlyList<Services.ResultFieldChecker.Column>> tables, int chosen)
    {
        const string title = "Bcode — Check Fields";
        var form = owner.FindForm();
        if (tables.Count == 0)
        {
            MessageBox.Show(form, "Chưa có bảng kết quả — chạy câu SELECT trước rồi Check Fields.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // File Grid + kết quả nhiều bảng: bảng đổ ra lưới = <dir id="N"> của file Filter cùng tên (N tính từ 0) — ưu tiên hơn bảng đang bấm.
        string? via = null;
        if (tables.Count > 1 && Services.ResultFieldChecker.AsFilePath(input) is { } file
            && Services.ResultFieldChecker.FilterTableIndex(file) is { } fid && fid >= 0 && fid < tables.Count)
        { chosen = fid; via = $" (theo <dir id=\"{fid}\"> của Filter)"; }
        if (chosen < 0 || chosen >= tables.Count)
        {
            MessageBox.Show(form, $"Có {tables.Count} bảng kết quả — bấm vào bảng cần kiểm rồi Check Fields lại.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var r = Services.ResultFieldChecker.Check(input, tables[chosen]);
        var head = tables.Count > 1 ? $"Bảng {chosen + 1}/{tables.Count}{via} · {tables[chosen].Count} cột\r\n\r\n" : "";
        var text = head + r.Text;
        if (text.Count(c => c == '\n') > 25)
            Forms.SourceViewForm.OpenValue("Check Fields", $"{r.Errors} lỗi / {r.Total} field", text, form);
        else
            MessageBox.Show(form, text, title, MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
}
