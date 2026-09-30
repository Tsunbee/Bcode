using System.Drawing.Drawing2D;
using System.Text.Json;
using BcodeViewer.App.Settings;
using BcodeViewer.App.UI;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BcodeViewer.App;

/// <summary>
/// "Hint Code" panel — matches FCodeViewer's own layout (search + Category checkboxes on
/// the left over a card list, New/Edit/Delete + a detail form on the right: Category, Type,
/// Tag/Keywords, Description, code). Backed by BcodeViewer's own local library
/// (<see cref="HintSnippetStore"/>) rather than FCode's — that one lives in FCode's own
/// fcoderdb.s3db (a SQLite file), which is out of scope to read/write here.
///
/// This is also where IntelliSense snippets are authored: a snippet with a Prefix shows up
/// in the editor's suggestion list as you type (see Web/completion.js), so the library is
/// one thing rather than a manual-insert panel plus a separate snippet file somewhere else.
///
/// The code box is a second, standalone WebView2/Monaco instance (Web/hint-editor.html) —
/// the same editor engine, theme (Web/theme.js) and FCode language tokenizer
/// (Web/fcode-language.js) as the main window's editor — rather than a RichTextBox with its
/// own regex-based highlighter. That used to be two different-looking, differently-colored
/// code areas in the same app; now there is one editor, reused.
///
/// The action row (Refresh/New/Save/Delete/Insert/Export) uses <see cref="UI.PillButton"/>
/// instead of stock WinForms Buttons for the same reason — sitting right above a WebView2
/// editor, plain square buttons read as a leftover control glued onto a newer one.
/// </summary>
public class HintCodeForm : Form
{
    private static readonly string[] Categories = { "JS", "SQL", "XML", "CSS" };

    /// <summary>Monaco's language id for each Hint Code category. XML maps to the app's own
    /// FCode tokenizer (registered by Web/fcode-language.js) rather than plain "xml", so a
    /// snippet colors exactly like it would inside a real controller file — embedded
    /// &lt;script&gt;/SQL regions included.</summary>
    private static readonly Dictionary<string, string> MonacoLanguageByCategory = new()
    {
        ["JS"] = "javascript",
        ["SQL"] = "sql",
        ["XML"] = "fcode-xml",
        ["CSS"] = "css",
    };

    private const string HintEditorVirtualHost = "bcodeviewer.local";

    private readonly ViewerSettings _settings;
    private readonly HintSnippetStore _store;
    private readonly Action<string> _insertCode;

    private readonly TextBox _searchBox = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, CheckBox> _categoryChecks = new();
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 42, IntegralHeight = false, BorderStyle = BorderStyle.None };

    private readonly ComboBox _categoryCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _typeCombo = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    private readonly TextBox _tagsBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _descriptionBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _prefixBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _pathScopeBox = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _intelliSenseCheck = new()
    {
        Dock = DockStyle.Fill, AutoSize = true, Checked = true,
        Text = "Hiện trong danh sách gợi ý khi gõ",
    };
    private readonly WebView2 _codeEditor = new() { Dock = DockStyle.Fill };

    /// <summary>Resolves once Web/hint-editor.html's own script has created the Monaco
    /// instance and posted "ready" back (see InitEditorAsync). Every call into the editor
    /// awaits this first, so a snippet picked before the page finishes loading queues up
    /// instead of silently landing on a page with no window.bcodeHintEditor yet.</summary>
    private readonly TaskCompletionSource<bool> _editorReady = new();

    private readonly Label _createdLabel = new() { Dock = DockStyle.Top, Height = 18, ForeColor = Color.Gray };
    private readonly Label _modifiedLabel = new() { Dock = DockStyle.Top, Height = 18, ForeColor = Color.Gray };

    // PillButton.Flat auto-sizes its own width from the text, so none of these set Width
    // anymore (the old fixed Width = 90 either clipped a longer label like "Lưu bản riêng"
    // or left extra padding around a short one like "New"). New/Insert stay primary
    // (accent-filled) — the same emphasis the old manual "_newBtn.BackColor = AppColors.
    // Accent" lines gave them, now themed automatically instead of hardcoded once.
    private readonly PillButton _insertBtn = PillButton.Flat("Insert", primary: true);
    private readonly PillButton _saveBtn = PillButton.Flat("Save");
    private readonly PillButton _deleteBtn = PillButton.Flat("Delete");
    private readonly PillButton _newBtn = PillButton.Flat("New", primary: true);
    private readonly PillButton _exportBtn = PillButton.Flat("Export...");

    private List<HintSnippet> _filtered = new();
    private HintSnippet? _editing; // null while creating a not-yet-saved snippet
    private readonly string? _initialCode;

    /// <param name="store">Loaded by the caller rather than here, so the shared folder —
    /// a UNC share — is read before the dialog is constructed instead of during it. See
    /// MainForm.OpenHintCode.</param>
    public HintCodeForm(ViewerSettings settings, HintSnippetStore store, Action<string> insertCode, string? initialCode = null)
    {
        _settings = settings;
        _store = store;
        _insertCode = insertCode;
        _initialCode = initialCode;
        Text = "Hint Code";
        Width = 1000;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;

        var root = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        root.Panel1MinSize = 0;
        root.Panel2MinSize = 0;
        root.SizeChanged += (_, _) =>
        {
            if (root.Width <= 0) return;
            var clamped = Math.Max(0, Math.Min(320, root.Width - root.SplitterWidth - 300));
            if (root.SplitterDistance != clamped) root.SplitterDistance = clamped;
        };

        root.Panel1.Controls.Add(BuildLeftPanel());
        root.Panel2.Controls.Add(BuildRightPanel());
        Controls.Add(root);

        ThemeManager.Apply(this);
        // WebView2 hiển thị màu nền này trong lúc trang chưa tải xong — không set thì có một
        // nháy trắng giữa lúc mở dialog và lúc hint-editor.html tự tô nền tối cho chính nó.
        _codeEditor.DefaultBackgroundColor = AppColors.Panel;

        RefreshList();
        ShowDetail(null);
        if (!string.IsNullOrEmpty(_initialCode)) _prefixBox.Focus();

        Load += async (_, _) => await InitEditorAsync();
    }

    /// <summary>
    /// Boots the second WebView2/Monaco instance. Its own CoreWebView2Environment with its
    /// own profile folder — same reason MainForm's main editor and its Claude panel each get
    /// one (see MainForm_Load): two WebView2s can't share a profile folder while both are
    /// open, and this dialog can be opened while the main window's editor WebView2 is very
    /// much still running.
    /// </summary>
    private async Task InitEditorAsync()
    {
        try
        {
            var profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Bcode", "HintEditorWebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDir);
            await _codeEditor.EnsureCoreWebView2Async(environment);

            _codeEditor.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _codeEditor.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                if (e.TryGetWebMessageAsString() == "ready") _editorReady.TrySetResult(true);
            };

            var webFolder = Path.Combine(AppContext.BaseDirectory, "Web");
            _codeEditor.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HintEditorVirtualHost, webFolder, CoreWebView2HostResourceAccessKind.Allow);
            _codeEditor.CoreWebView2.Navigate($"https://{HintEditorVirtualHost}/hint-editor.html");

            await _editorReady.Task;

            // ShowDetail(null) đã chạy trong constructor lúc trang còn chưa sẵn sàng, nên đẩy
            // lại đúng trạng thái hiện tại (category + code) ngay khi Monaco vừa dựng xong.
            await SetEditorLanguageAsync((string)(_categoryCombo.SelectedItem ?? "JS"));
            await SetEditorValueAsync(_editing?.Code ?? _initialCode ?? "");
        }
        catch (Exception ex)
        {
            // WebView2 Runtime chưa cài, hoặc lỗi khởi tạo khác — vẫn để dialog dùng được
            // bình thường (Save/Insert chỉ đơn giản sẽ thao tác trên một editor trống) thay
            // vì làm treo cả Hint Code vì một lỗi ở đúng mỗi ô code.
            if (!IsDisposed)
                MessageBox.Show(this, $"Không khởi tạo được trình soạn thảo:\n{ex.Message}", "Hint Code",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task RunOnEditorAsync(string js)
    {
        await _editorReady.Task;
        if (_codeEditor.CoreWebView2 is null) return;
        await _codeEditor.ExecuteScriptAsync(js);
    }

    private Task SetEditorValueAsync(string text) =>
        RunOnEditorAsync($"window.bcodeHintEditor.setValue({JsonSerializer.Serialize(text)})");

    private Task SetEditorLanguageAsync(string category) =>
        RunOnEditorAsync($"window.bcodeHintEditor.setLanguage({JsonSerializer.Serialize(
            MonacoLanguageByCategory.GetValueOrDefault(category, "plaintext"))})");

    private async Task<string> GetEditorValueAsync()
    {
        await _editorReady.Task;
        if (_codeEditor.CoreWebView2 is null) return "";
        var raw = await _codeEditor.ExecuteScriptAsync("window.bcodeHintEditor.getValue()");
        return JsonSerializer.Deserialize<string>(raw) ?? "";
    }

    private Control BuildLeftPanel()
    {
        var refreshBtn = PillButton.Flat("Refresh");
        refreshBtn.Dock = DockStyle.Right;
        refreshBtn.Click += (_, _) => RefreshList();

        var searchRow = new Panel { Dock = DockStyle.Top, Height = 28 };
        searchRow.Controls.Add(_searchBox);
        searchRow.Controls.Add(refreshBtn);
        _searchBox.TextChanged += (_, _) => RefreshList();

        var filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 26, WrapContents = false };
        foreach (var cat in Categories)
        {
            var chk = new CheckBox { Text = cat, Checked = true, AutoSize = true, Margin = new Padding(4, 3, 8, 0) };
            chk.CheckedChanged += (_, _) => RefreshList();
            _categoryChecks[cat] = chk;
            filterRow.Controls.Add(chk);
        }

        _list.DrawItem += List_DrawItem;
        _list.SelectedIndexChanged += (_, _) => ShowDetail(_list.SelectedIndex >= 0 && _list.SelectedIndex < _filtered.Count ? _filtered[_list.SelectedIndex] : null);

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_list);
        panel.Controls.Add(filterRow);
        panel.Controls.Add(searchRow);
        return panel;
    }

    private Control BuildRightPanel()
    {
        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        _newBtn.Click += (_, _) => ShowDetail(null);
        _saveBtn.Click += async (_, _) => await SaveCurrentAsync();
        _deleteBtn.Click += (_, _) => DeleteCurrent();
        _insertBtn.Click += async (_, _) =>
        {
            if (_editing is null) return;
            _insertCode(await GetEditorValueAsync());
        };
        _exportBtn.Click += (_, _) => ExportSnippets();
        buttonRow.Controls.AddRange(new Control[] { _newBtn, _saveBtn, _deleteBtn, _insertBtn, _exportBtn });

        _categoryCombo.Items.AddRange(Categories);
        _categoryCombo.SelectedIndex = 0;
        _categoryCombo.SelectedIndexChanged += (_, _) =>
            _ = SetEditorLanguageAsync((string)(_categoryCombo.SelectedItem ?? "JS"));

        // Prefix sits directly under Category because it's the field that decides whether
        // this entry is a passive library item or something that shows up while typing —
        // the single most consequential choice on this panel.
        _prefixBox.PlaceholderText = "vd: fld — gõ từ này trong editor để hiện gợi ý";
        _pathScopeBox.PlaceholderText = @"vd: *\Controllers\*;*\Grid\*  (bỏ trống = mọi file)";

        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8 };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 7; i++) form.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        form.Controls.Add(new Label { Text = "Category", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        form.Controls.Add(_categoryCombo, 1, 0);
        form.Controls.Add(new Label { Text = "Prefix", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        form.Controls.Add(_prefixBox, 1, 1);
        form.Controls.Add(new Label { Text = "Type", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        form.Controls.Add(_typeCombo, 1, 2);
        form.Controls.Add(new Label { Text = "Tag, Keywords", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        form.Controls.Add(_tagsBox, 1, 3);
        form.Controls.Add(new Label { Text = "Description", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        form.Controls.Add(_descriptionBox, 1, 4);
        form.Controls.Add(new Label { Text = "Chỉ ở file", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 5);
        form.Controls.Add(_pathScopeBox, 1, 5);
        form.Controls.Add(_intelliSenseCheck, 1, 6);
        form.Controls.Add(_codeEditor, 0, 7);
        form.SetColumnSpan(_codeEditor, 2);

        var metaPanel = new Panel { Dock = DockStyle.Top, Height = 40 };
        metaPanel.Controls.Add(_modifiedLabel);
        metaPanel.Controls.Add(_createdLabel);

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(form);
        panel.Controls.Add(metaPanel);
        panel.Controls.Add(buttonRow);
        return panel;
    }

    private static readonly Dictionary<string, Color> CategoryColors = new()
    {
        ["JS"] = Color.FromArgb(240, 200, 90),
        ["SQL"] = Color.FromArgb(90, 170, 240),
        ["XML"] = Color.FromArgb(140, 200, 120),
        ["CSS"] = Color.FromArgb(220, 130, 200),
    };

    private void List_DrawItem(object? sender, DrawItemEventArgs e)
    {
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? AppColors.Selection : AppColors.Panel))
            e.Graphics.FillRectangle(bg, e.Bounds);
        if (e.Index < 0 || e.Index >= _filtered.Count) return;
        var s = _filtered[e.Index];
        var badgeColor = CategoryColors.GetValueOrDefault(s.Category, Color.Gray);

        var font = e.Font ?? _list.Font;
        using var badgeBrush = new SolidBrush(badgeColor);
        var badgeRect = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + 5, 40, 16);
        using (var badgePath = RoundedRect(badgeRect, 3))
            e.Graphics.FillPath(badgeBrush, badgePath);
        using var boldFont = new Font(font, FontStyle.Bold);
        e.Graphics.DrawString(s.Category, boldFont, Brushes.Black, badgeRect, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });

        using var mutedBrush = new SolidBrush(AppColors.TextMuted);
        using var textBrush = new SolidBrush(AppColors.Text);

        // Top line: the prefix (what you'd actually type) when there is one, since that is
        // how this entry will be reached day to day; the date is secondary once a snippet
        // is something you invoke rather than browse.
        var topLine = string.IsNullOrWhiteSpace(s.Prefix)
            ? s.ModifiedDate.ToString("dd/MM/yyyy")
            : $"{s.Prefix}  ·  {s.ModifiedDate:dd/MM/yyyy}";
        if (s.IsShared) topLine += $"  ·  chung ({s.SourceLabel})";
        e.Graphics.DrawString(topLine, font, mutedBrush, e.Bounds.Left + 54, e.Bounds.Top + 3);

        var title = string.IsNullOrWhiteSpace(s.Tags) ? s.Description : s.Tags;
        e.Graphics.DrawString(title, font, textBrush, e.Bounds.Left + 54, e.Bounds.Top + 18);

        using var borderPen = new Pen(AppColors.Border);
        e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void RefreshList()
    {
        var query = _searchBox.Text.Trim();
        var activeCats = _categoryChecks.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // _store.All, not _store.Snippets — the team's shared library shows in the same list
        // as personal entries (marked, and read-only; see ShowDetail). Keeping them in a
        // separate tab would mean checking two places for "do we already have a snippet for
        // this", which is exactly when people stop checking.
        _filtered = _store.All
            .Where(s => activeCats.Contains(s.Category))
            .Where(s => query.Length == 0
                || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Tags.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Prefix.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Code.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.IsShared)                 // personal first
            .ThenByDescending(s => s.ModifiedDate)
            .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in _filtered) _list.Items.Add(s.Id);
        _list.EndUpdate();
    }

    private void ShowDetail(HintSnippet? snippet)
    {
        _editing = snippet;
        _categoryCombo.SelectedItem = snippet?.Category ?? "JS";
        _typeCombo.Text = snippet?.Type ?? "Declare";
        _tagsBox.Text = snippet?.Tags ?? "";
        _descriptionBox.Text = snippet?.Description ?? "";
        _prefixBox.Text = snippet?.Prefix ?? "";
        _pathScopeBox.Text = snippet?.PathScope ?? "";
        _intelliSenseCheck.Checked = snippet?.ShowInIntelliSense ?? true;

        // Fire-and-forget: cả hai lệnh tự chờ _editorReady bên trong, nên gọi ShowDetail
        // trước khi trang WebView2 tải xong (constructor gọi ShowDetail(null) ngay từ đầu)
        // vẫn an toàn — chúng chỉ chạy khi Monaco đã sẵn sàng.
        var category = snippet?.Category ?? "JS";
        _ = SetEditorLanguageAsync(category);
        _ = SetEditorValueAsync(snippet?.Code ?? "");

        var isShared = snippet?.IsShared == true;
        _createdLabel.Text = snippet is null ? ""
            : isShared ? $"Thư viện dùng chung — {snippet.SourceLabel} (chỉ đọc)"
            : $"Created: {snippet.CreatedDate:dd/MM/yyyy HH:mm} by {snippet.CreatedBy}";
        _modifiedLabel.Text = snippet is null || isShared ? ""
            : $"Modified: {snippet.ModifiedDate:dd/MM/yyyy HH:mm} by {snippet.ModifiedBy}";

        // A shared snippet stays fully readable and insertable — it just can't be written
        // back, because BcodeViewer never writes to the team folder (see ViewerSettings.
        // SharedTemplatePath). Save on one of these forks a personal copy instead, which is
        // handled in SaveCurrentAsync rather than by disabling the button and dead-ending.
        _saveBtn.Text = isShared ? "Lưu bản riêng" : "Save";
        _deleteBtn.Enabled = snippet != null && !isShared;
        _insertBtn.Enabled = snippet != null;
    }

    private async Task SaveCurrentAsync()
    {
        var code = await GetEditorValueAsync();
        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show(this, "Chưa nhập code.", "Hint Code");
            return;
        }

        HintSnippet current;
        // A shared entry is never written back to the team folder — saving forks it into
        // the personal library, which is the behaviour someone editing one actually wants
        // ("this is nearly right, but for my project...") and the only one that's safe
        // against two people saving over each other on a UNC share.
        if (_editing is null || _editing.IsShared)
        {
            current = new HintSnippet();
            _store.Snippets.Add(current);
        }
        else
        {
            current = _editing;
            current.ModifiedBy = Environment.UserName;
            current.ModifiedDate = DateTime.Now;
        }

        current.Category = (string)(_categoryCombo.SelectedItem ?? "JS");
        current.Type = _typeCombo.Text;
        current.Tags = _tagsBox.Text;
        current.Description = _descriptionBox.Text;
        current.Prefix = _prefixBox.Text.Trim();
        current.PathScope = _pathScopeBox.Text.Trim();
        current.ShowInIntelliSense = _intelliSenseCheck.Checked;
        current.Code = code;

        _store.Save();
        RefreshList();
        var idx = _filtered.FindIndex(s => s.Id == current.Id);
        if (idx >= 0) _list.SelectedIndex = idx;
        else ShowDetail(current);
    }

    private void DeleteCurrent()
    {
        if (_editing is null) return;
        if (_editing.IsShared)
        {
            MessageBox.Show(this,
                "Đây là snippet của thư viện dùng chung — xoá nó phải xoá file trong thư mục chung.",
                "Hint Code", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, "Xóa hint này?", "Hint Code", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _store.Snippets.RemoveAll(s => s.Id == _editing.Id);
        _store.Save();
        RefreshList();
        ShowDetail(null);
    }

    /// <summary>
    /// Writes the personal library out as a .code-snippets file — the publish step for the
    /// shared folder (BcodeViewer only ever reads that folder, so putting a file there stays
    /// a deliberate act, and on a git-backed share it goes through review like anything
    /// else). VSCode's format rather than our own, so the same file works for anyone on the
    /// team editing in VSCode and can live in a repo's .vscode/ folder.
    /// </summary>
    private void ExportSnippets()
    {
        var exportable = _store.Snippets.Where(s => !string.IsNullOrWhiteSpace(s.Code)).ToList();
        if (exportable.Count == 0)
        {
            MessageBox.Show(this, "Chưa có snippet riêng nào để export.", "Hint Code");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Export snippet (định dạng VSCode)",
            FileName = $"bcode-{Environment.UserName}.code-snippets",
            Filter = "VSCode snippets (*.code-snippets)|*.code-snippets|JSON (*.json)|*.json",
            InitialDirectory = Directory.Exists(_settings.SharedTemplatePath) ? _settings.SharedTemplatePath : null,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            HintSnippetStore.ExportVsCodeSnippets(dialog.FileName, exportable);
            MessageBox.Show(this,
                $"Đã ghi {exportable.Count} snippet vào:\n{dialog.FileName}",
                "Hint Code", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không ghi được file:\n{ex.Message}", "Hint Code",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}