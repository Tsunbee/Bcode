using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Gen Update" document tab (WCommand &gt; Gen Update in FCode) — NOT to be confused with
/// the older "Gen Update (dòng đã chọn)" SQL UPDATE-statement generator already in SQL
/// Query/Command's result-grid context menu (<see cref="GenUpdateService"/>,
/// Ctrl+Shift+U) — that turns selected grid rows into UPDATE statements and is unrelated.
/// This tab instead packages SOURCE FILES (the .f/.xml/.xlsx under App_Data) tied to one
/// or more WCommand menu items into an "update" folder to send to a client site, matching
/// FCode's own WCommand &gt; Gen Update screen.
///
/// Double-click a node in the WCommand tree while this tab is the active document tab (see
/// MainForm.OpenWCommandItem) to populate the Source File tree on the right; otherwise
/// double-click still opens/targets File Lookup as before.
///
/// Thanh lọc phía trên cây Source File là lối tắt cho khi không muốn/không tiện lần mò
/// trong cây WCommand trước, mô phỏng lại đúng cặp "File [danh mục con Controllers ▼]
/// [tên/từ khoá]" của màn Declaration for Generation bên FCode: chọn 1 danh mục con
/// (Web_Dir/Web_Filter/Web_Grid/...) để giới hạn đúng thư mục con đó trong App_Data\
/// Controllers (mặc định "Tất cả" = quét cả Controllers), gõ tên/từ khoá (khớp CHỨA,
/// không cần đúng tuyệt đối như SysId — gõ 1 phần tên procedure/sysid/tên file đều được)
/// rồi bấm Tìm. Có gợi ý tên (autocomplete) cho ô này, lấy từ tên file thật đang có trong
/// Controllers của workspace hiện tại.
///
/// GIẢ ĐỊNH cần Bee xác nhận lại: tên thư mục thật trên đĩa ứng với mỗi danh mục được suy
/// ra bằng cách bỏ tiền tố "Web_" (Web_Report_Include → Controllers\Report_Include...) —
/// 3 cái Dir/Filter/Grid đã chắc chắn đúng (đang dùng ở BuildTreeForMenuItem/FOnlyFolderNames),
/// các cái còn lại là suy đoán theo quy luật đặt tên, có thể lệch tên thật tuỳ site — báo
/// lại nếu sai để chỉnh bảng ControllerCategories bên dưới. Riêng Web_Main trỏ tới thư mục
/// "Main" nằm NGANG HÀNG App_Data (không phải trong Controllers), theo đúng quy ước Main\
/// đã có sẵn trong BuildTreeForMenuItem.
///
/// Scope note (see README "Cập nhật gần đây"): FCode's real screen also has a "Declaration
/// for Generation" / "Content for Generation" section for declaring ad-hoc SQL objects
/// (Category/File/SELECT FROM/WHERE/Top Script/Bottom Script) inside the same update
/// package — none of the screenshots showed that grid populated, and the description of
/// what was wanted here only covered the file side ("double-click menu -> files show on
/// the right -> Add -> name + path -> Create Update File"), so only the file-packaging
/// half is built. The SQL-object-declaration half is left out until it's actually needed.
///
/// "Gen nhanh Store (Procedure)" (dưới cây App/Sys/Other): gõ tên 1 stored procedure, chọn
/// App hoặc Sys (2 database của workspace — Workspace.AppDatabase/SysDatabase), bấm Gen
/// Script — script (ALTER PROCEDURE, tái dùng SqlObjectBrowserService.GetDefinitionAsync,
/// cùng cơ chế màn "SQL Object") được thêm thẳng vào gói update tại app\script\&lt;tên&gt;.sql
/// hoặc sys\script\&lt;tên&gt;.sql tuỳ theo App/Sys đã chọn — không phải copy file có sẵn trên
/// đĩa như nhánh Source File, nên _batchList giờ chứa BatchItem (có thể là file thật cần
/// copy, hoặc nội dung script sinh ra cần ghi mới) thay vì chuỗi đường dẫn thuần.
/// </summary>
public class GenUpdatePackageControl : UserControl
{
    private readonly FileLookupService _fileLookupService;
    private readonly SqlObjectBrowserService _sqlObjectService;

    /// <summary>1 dòng trong gói update đang chờ tạo — hoặc copy nguyên 1 file có sẵn
    /// (SourceFilePath khác null), hoặc ghi mới nội dung sinh ra, vd script Gen nhanh Store
    /// (GeneratedContent khác null). ToString() quyết định dòng hiển thị trong _batchList.</summary>
    private sealed class BatchItem
    {
        public required string DisplayName { get; init; }
        public required string RelativeDestPath { get; init; } // đường dẫn tương đối trong gói update (destRoot)
        public string? SourceFilePath { get; init; }
        public string? GeneratedContent { get; init; }
        public override string ToString() => DisplayName;
    }

    private readonly TextBox _programPathBox;
    private readonly TextBox _mobilePathBox;
    private readonly TextBox _saveAtPathBox;
    private readonly TextBox _folderNameBox;
    private readonly TextBox _descriptionBox;
    private readonly RadioButton _appRadio;
    private readonly RadioButton _sysRadio;
    private readonly RadioButton _otherRadio;

    private readonly TreeView _sourceTree;
    private readonly CheckBox _checkAllBox;
    private readonly ComboBox _categoryCombo; // danh mục con của Controllers (Web_Dir/Web_Grid/... hoặc "Tất cả")
    private readonly TextBox _nameBox; // tên/từ khoá — khớp CHỨA trong tên file, không cần đúng tuyệt đối SysId

    // Nhãn hiển thị -> tên thư mục con thật trong App_Data\Controllers (null = quét cả Controllers,
    // "Main" = trường hợp đặc biệt trỏ ra ngoài Controllers — xem SearchAndLoad). Xem chú thích
    // GIẢ ĐỊNH ở đầu file: chỉ Dir/Filter/Grid là chắc chắn đúng, còn lại suy đoán theo quy luật
    // bỏ tiền tố "Web_".
    private static readonly (string Label, string? Folder)[] ControllerCategories =
    {
        ("Tất cả (Web_All)", null),
        ("Web_Dir", "Dir"),
        ("Web_Filter", "Filter"),
        ("Web_Grid", "Grid"),
        ("Web_Lookup", "Lookup"),
        ("Web_Report", "Report"),
        ("Web_Report_Include", "Report_Include"),
        ("Web_Upload", "Upload"),
        ("Web_Upload_Include", "Upload_Include"),
        ("Web_Main", "Main"),
        ("Web_Include", "Include"),
        ("Web_Command", "Command"),
        ("Web_Javascript", "Javascript"),
        ("Web_XML", "XML"),
        ("Web_Excel", "Excel"),
        ("Web_Rpt", "Rpt"),
        ("Web_Image", "Image"),
        ("Web_Bin", "Bin"),
    };

    private readonly ListBox _batchList; // BatchItem entries queued for the update package
    private readonly Label _resultPathLabel;
    private readonly Button _copyLinkButton;
    private readonly TextBox _storeNameBox; // "Gen nhanh Store" — tên stored procedure
    private string? _lastCreatedPath; // destRoot from the most recent successful "Create Update File"

    private readonly string? _sourceRoot; // site root (Workspace.SourcePath) for the workspace this tab was opened with —
                                           // BuildTreeForMenuItem below resolves both Main\ and App_Data\Controllers\ from this

    public GenUpdatePackageControl(FileLookupService fileLookupService, SqlObjectBrowserService sqlObjectService, Workspace? workspace)
    {
        _fileLookupService = fileLookupService;
        _sqlObjectService = sqlObjectService;
        Dock = DockStyle.Fill;

        // ---- Left: Declaration Path and Connection + batch of files queued ----
        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8)
        };

        var sectionTitle = new Label
        {
            Text = "Declaration Path and Connection",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        left.Controls.Add(sectionTitle);

        left.Controls.Add(Labeled("Program Path", _programPathBox = new TextBox { Width = 300, Text = workspace?.ProgramPath ?? "" }));
        left.Controls.Add(Labeled("Mobile Path", _mobilePathBox = new TextBox { Width = 300, Text = workspace?.MobilePath ?? "" }));
        left.Controls.Add(Labeled("Save At Path", _saveAtPathBox = new TextBox { Width = 300, Text = workspace?.WorkingPath ?? "" }));

        var defaultFolder = string.Join("_", new[]
        {
            workspace?.ProjectId,
            Environment.MachineName,
            DateTime.Now.ToString("yyyyMMddHHmmss")
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        left.Controls.Add(Labeled("+ Folder Name", _folderNameBox = new TextBox { Width = 300, Text = defaultFolder }));
        left.Controls.Add(Labeled("Description", _descriptionBox = new TextBox { Width = 300 }));

        // App/Sys/Other: trước đây khai báo nhưng chưa gắn hành vi gì — giờ dùng để quyết
        // định gói update sinh ra cho database nào khi bấm "Gen Script" bên dưới (Other
        // không áp dụng cho Gen Script, chỉ App/Sys có ý nghĩa vì workspace chỉ có 2 database).
        var radioPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        _appRadio = new RadioButton { Text = "App", Checked = true, AutoSize = true };
        _sysRadio = new RadioButton { Text = "Sys", AutoSize = true, Margin = new Padding(10, 0, 0, 0) };
        _otherRadio = new RadioButton { Text = "Other", AutoSize = true, Margin = new Padding(10, 0, 0, 0) };
        radioPanel.Controls.Add(_appRadio);
        radioPanel.Controls.Add(_sysRadio);
        radioPanel.Controls.Add(_otherRadio);
        left.Controls.Add(radioPanel);

        // Gen nhanh Store (Procedure): gõ tên, chọn App/Sys ở trên rồi bấm Gen Script — script
        // được thêm thẳng vào gói update tại app\script\<tên>.sql hoặc sys\script\<tên>.sql.
        left.Controls.Add(new Label { Text = "Gen nhanh Store (Procedure):", AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        var storeBar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        _storeNameBox = new TextBox
        {
            Width = 220,
            Margin = new Padding(0, 2, 8, 0),
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.CustomSource
        };
        storeBar.Controls.Add(_storeNameBox);
        var genScriptButton = PillButton.Flat("Gen Script", primary: true);
        genScriptButton.Click += async (_, _) => await GenStoreScriptAsync();
        storeBar.Controls.Add(genScriptButton);
        left.Controls.Add(storeBar);

        left.Controls.Add(new Label { Text = "File đã thêm vào gói update:", AutoSize = true, Margin = new Padding(0, 4, 0, 2) });
        _batchList = new ListBox { Width = 600, Height = 240, HorizontalScrollbar = true };
        left.Controls.Add(_batchList);

        var batchButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        var removeSelectedButton = PillButton.Flat("Bỏ file đã chọn");
        removeSelectedButton.Click += (_, _) => RemoveSelectedFromBatch();
        var clearBatchButton = PillButton.Flat("Xoá hết");
        clearBatchButton.Margin = new Padding(6, 0, 0, 0);
        clearBatchButton.Click += (_, _) => _batchList.Items.Clear();
        batchButtons.Controls.Add(removeSelectedButton);
        batchButtons.Controls.Add(clearBatchButton);
        left.Controls.Add(batchButtons);

        var createButton = PillButton.Flat("Create Update File", primary: true);
        createButton.Margin = new Padding(0, 14, 0, 4);
        createButton.Click += (_, _) => CreateUpdateFiles();
        left.Controls.Add(createButton);

        _resultPathLabel = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(300, 0) };
        left.Controls.Add(_resultPathLabel);

        _copyLinkButton = PillButton.Flat("Copy link");
        _copyLinkButton.Margin = new Padding(0, 4, 0, 0);
        _copyLinkButton.Enabled = false;
        _copyLinkButton.Click += (_, _) => CopyResultPath();
        left.Controls.Add(_copyLinkButton);

        // ---- Right: Source File tree (populated per double-clicked WCommand menu item, or
        // via the filter bar below when browsing/typing a name directly is faster) ----
        var right = new Panel { Dock = DockStyle.Fill };
        var rightTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false };
        rightTop.Controls.Add(new Label
        {
            Text = "Source File",
            AutoSize = true,
            Padding = new Padding(0, 6, 12, 0),
            Font = new Font(Font, FontStyle.Bold)
        });
        _checkAllBox = new CheckBox { Text = "Check All", AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        _checkAllBox.CheckedChanged += (_, _) => SetAllChecked(_checkAllBox.Checked);
        var addButton = PillButton.Flat("Add", primary: true);
        addButton.Margin = new Padding(10, 2, 0, 0);
        addButton.Click += (_, _) => AddCheckedToBatch();
        rightTop.Controls.Add(_checkAllBox);
        rightTop.Controls.Add(addButton);

        // Thanh lọc: chọn danh mục con của Controllers (hoặc "Tất cả") + gõ tên/từ khoá
        // (khớp chứa, không cần đúng tuyệt đối SysId) rồi bấm Tìm — xem chú thích ở đầu file.
        var filterBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0, 2, 0, 4)
        };
        filterBar.Controls.Add(new Label { Text = "File:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _categoryCombo = new ComboBox { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 2, 10, 2) };
        _categoryCombo.Items.AddRange(ControllerCategories.Select(c => c.Label).ToArray());
        _categoryCombo.SelectedIndex = 0; // "Tất cả (Web_All)"
        filterBar.Controls.Add(_categoryCombo);
        filterBar.Controls.Add(new Label { Text = "Tên/SysId:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _nameBox = new TextBox
        {
            Width = 180,
            Margin = new Padding(0, 2, 14, 2),
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.CustomSource
        };
        filterBar.Controls.Add(_nameBox);
        var searchButton = PillButton.Flat("Tìm", primary: true);
        searchButton.Click += (_, _) => SearchAndLoad();
        filterBar.Controls.Add(searchButton);

        _sourceTree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };

        right.Controls.Add(_sourceTree);
        right.Controls.Add(filterBar);
        right.Controls.Add(rightTop);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 340 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);

        if (workspace is not null && !string.IsNullOrWhiteSpace(workspace.SourcePath))
            _sourceRoot = workspace.SourcePath;

        // Quét tên file trong Controllers 1 lần (nền, không chặn UI — UNC path có thể chậm)
        // để làm nguồn gợi ý (autocomplete) cho ô Tên/SysId. Tìm kiếm bằng nút Tìm vẫn hoạt
        // động bình thường ngay cả khi việc quét này chưa xong hoặc lỗi (UNC tạm mất kết nối).
        _ = LoadAutoCompleteSourceAsync();
        // Tương tự, nạp tên các stored procedure (cả App lẫn Sys) làm gợi ý cho ô Gen nhanh Store.
        _ = LoadStoreNameAutoCompleteAsync();
    }

    /// <summary>Nạp tên mọi stored procedure ở cả App Data lẫn Sys Data làm gợi ý cho ô Gen
    /// nhanh Store — 2 truy vấn độc lập, lỗi 1 bên (site chỉ có 1 trong 2 database, hoặc mất
    /// kết nối tạm thời) không chặn bên còn lại hay chặn thao tác Gen Script thực tế.</summary>
    private async Task LoadStoreNameAutoCompleteAsync()
    {
        if (_sourceRoot is null) return;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var useSys in new[] { false, true })
        {
            try
            {
                var objects = await _sqlObjectService.ListObjectsAsync(useSys);
                foreach (var obj in objects)
                    if (obj.Kind == SqlObjectKind.StoredProcedure)
                        names.Add(obj.Name);
            }
            catch
            {
                // Không kết nối được 1 trong 2 database — bỏ qua, đây chỉ là gợi ý.
            }
        }

        if (IsDisposed || Disposing || names.Count == 0) return;
        var source = new AutoCompleteStringCollection();
        source.AddRange(names.ToArray());
        _storeNameBox.AutoCompleteCustomSource = source;
    }

    /// <summary>"Gen Script" của Gen nhanh Store — tìm đúng 1 stored procedure theo tên đã gõ
    /// trong database App hoặc Sys (theo radio đã chọn), lấy definition qua
    /// SqlObjectBrowserService.GetDefinitionAsync (đã tự đổi CREATE thành ALTER, sẵn sàng
    /// chạy update), rồi thêm vào _batchList dưới dạng BatchItem ghi thẳng nội dung — không
    /// phải copy file — tại app\script\&lt;tên&gt;.sql hoặc sys\script\&lt;tên&gt;.sql.</summary>
    private async Task GenStoreScriptAsync()
    {
        var name = _storeNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Nhập tên stored procedure trước.", "Bcode — Gen Update",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!_appRadio.Checked && !_sysRadio.Checked)
        {
            MessageBox.Show(this, "Chọn App hoặc Sys trước khi Gen Script (Other không áp dụng).",
                "Bcode — Gen Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var useSys = _sysRadio.Checked;
        try
        {
            var matches = await _sqlObjectService.ListObjectsAsync(useSys, name);
            var target = matches.FirstOrDefault(o => o.Kind == SqlObjectKind.StoredProcedure
                    && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault(o => o.Kind == SqlObjectKind.StoredProcedure);

            if (target is null)
            {
                MessageBox.Show(this,
                    $"Không tìm thấy stored procedure nào khớp \"{name}\" trong {(useSys ? "Sys Data" : "App Data")}.",
                    "Bcode — Gen Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var script = await _sqlObjectService.GetDefinitionAsync(target);
            var relative = Path.Combine(useSys ? "sys" : "app", "script", $"{target.Name}.sql");
            var display = $"[{(useSys ? "Sys" : "App")}] {target.Name} → {relative}";

            var item = new BatchItem { DisplayName = display, RelativeDestPath = relative, GeneratedContent = script };
            var existingIndex = -1;
            for (var i = 0; i < _batchList.Items.Count; i++)
                if (_batchList.Items[i] is BatchItem b && string.Equals(b.RelativeDestPath, relative, StringComparison.OrdinalIgnoreCase))
                    existingIndex = i;

            if (existingIndex >= 0) _batchList.Items[existingIndex] = item;
            else _batchList.Items.Add(item);

            MessageBox.Show(this, $"Đã thêm script {target.Name} ({(useSys ? "Sys" : "App")}) vào gói update: {relative}",
                "Bcode — Gen Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Gen Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Quét 1 lần toàn bộ tên file (có đuôi và không đuôi) dưới App_Data\Controllers
    /// của workspace hiện tại để làm gợi ý cho ô Tên/SysId — chạy nền, gán vào UI thread khi
    /// xong. Lỗi (site UNC tạm không vào được, quyền truy cập,...) bị nuốt có chủ đích: đây
    /// chỉ là tiện ích gợi ý, không được phép chặn hay làm hỏng luồng tìm kiếm chính.</summary>
    private async Task LoadAutoCompleteSourceAsync()
    {
        if (_sourceRoot is null) return;
        var controllersRoot = Path.Combine(_sourceRoot, "App_Data", "Controllers");

        List<string> names;
        try
        {
            names = await Task.Run(() =>
            {
                if (!Directory.Exists(controllersRoot)) return new List<string>();
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(controllersRoot, "*", SearchOption.AllDirectories))
                {
                    set.Add(Path.GetFileName(file));           // gợi ý theo tên file đầy đủ
                    set.Add(Path.GetFileNameWithoutExtension(file)); // gợi ý theo tên/SysId không đuôi
                }
                return set.ToList();
            });
        }
        catch
        {
            return;
        }

        if (IsDisposed || Disposing || names.Count == 0) return;
        var source = new AutoCompleteStringCollection();
        source.AddRange(names.ToArray());
        _nameBox.AutoCompleteCustomSource = source;
    }

    private static Control Labeled(string label, Control input)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.TopDown,
            Margin = new Padding(0, 0, 0, 8)
        };
        panel.Controls.Add(new Label { Text = label, AutoSize = true });
        panel.Controls.Add(input);
        return panel;
    }

    /// <summary>Called when a WCommand menu node is double-clicked while this tab is active —
    /// rebuilds the Source File tree from that item's Link/SysId, the same precise resolution
    /// File Lookup itself uses (FileLookupService.BuildTreeForMenuItem: Main\&lt;link&gt; plus the
    /// related App_Data\Controllers\&lt;sysId&gt; files) instead of a naive filename-substring search.</summary>
    public void LoadForMenuItem(WCommandItem item)
    {
        if (_sourceRoot is null)
        {
            MessageBox.Show(this, "Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.",
                "Bcode — Gen Update");
            return;
        }
        if (string.IsNullOrWhiteSpace(item.Link) && string.IsNullOrWhiteSpace(item.SysId))
        {
            MessageBox.Show(this, $"Menu \"{item.Bar}\" không có Link/SysId gắn với source (có thể là mục nhóm/menu cha).",
                "Bcode — Gen Update");
            return;
        }

        // onlyFInGridFilterDir: true — Grid/Filter/Dir already contain the compiled/encrypted
        // ".f" version, so the matching ".xml" source is left out of the update package.
        var root = _fileLookupService.BuildTreeForMenuItem(_sourceRoot, item.Link, item.SysId, onlyFInGridFilterDir: true);

        _sourceTree.Nodes.Clear();
        _sourceTree.Nodes.Add(ToTreeNode(root));
        _sourceTree.ExpandAll();
        _checkAllBox.Checked = false;
    }

    /// <summary>Thanh lọc phía trên cây Source File — lối tắt khi không muốn lần mò trong cây
    /// WCommand trước. Danh mục thu hẹp đúng 1 thư mục con của Controllers ("Tất cả" = quét
    /// cả Controllers); Tên/SysId khớp CHỨA trong tên file (không cần gõ đúng tuyệt đối SysId
    /// — gõ 1 phần tên procedure/sysid/tên file đều tìm ra), để trống thì lấy hết file trong
    /// danh mục đã chọn.</summary>
    private void SearchAndLoad()
    {
        if (_sourceRoot is null)
        {
            MessageBox.Show(this, "Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.",
                "Bcode — Gen Update");
            return;
        }

        var controllersRoot = Path.Combine(_sourceRoot, "App_Data", "Controllers");
        var selectedLabel = _categoryCombo.SelectedItem as string;
        var folder = ControllerCategories.FirstOrDefault(c => c.Label == selectedLabel).Folder;

        string searchRoot;
        if (string.IsNullOrEmpty(folder))
            searchRoot = controllersRoot; // "Tất cả (Web_All)" — quét cả Controllers
        else if (string.Equals(folder, "Main", StringComparison.OrdinalIgnoreCase))
            searchRoot = Path.Combine(_sourceRoot, "Main"); // Main nằm ngang hàng App_Data, không phải trong Controllers
        else
            searchRoot = Path.Combine(controllersRoot, folder);

        if (!Directory.Exists(searchRoot))
        {
            MessageBox.Show(this,
                $"Không tìm thấy thư mục:\n{searchRoot}\n\nTên thư mục thật trên site này có thể khác — báo lại để chỉnh bảng danh mục.",
                "Bcode — Gen Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var nameFilter = _nameBox.Text.Trim();
        // onlyShowFiltered: false — không lọc theo đuôi file, chỉ lọc theo tên chứa từ khoá
        // (hoặc lấy hết nếu để trống).
        var root = _fileLookupService.BuildTree(searchRoot, extensionFilter: "", searchText: nameFilter.Length > 0 ? nameFilter : null, onlyShowFiltered: false);

        _sourceTree.Nodes.Clear();
        _sourceTree.Nodes.Add(ToTreeNode(root));
        _sourceTree.ExpandAll();
        _checkAllBox.Checked = false;

        if (root.Children.Count == 0)
        {
            MessageBox.Show(this, "Không tìm thấy file nào khớp.", "Bcode — Gen Update",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static TreeNode ToTreeNode(FileLookupNode node)
    {
        var treeNode = new TreeNode(node.Name) { Tag = node };
        foreach (var child in node.Children)
            treeNode.Nodes.Add(ToTreeNode(child));
        return treeNode;
    }

    private void SetAllChecked(bool value)
    {
        foreach (TreeNode root in _sourceTree.Nodes)
            SetChecked(root, value);
    }

    private static void SetChecked(TreeNode node, bool value)
    {
        node.Checked = value;
        foreach (TreeNode child in node.Nodes)
            SetChecked(child, value);
    }

    private void AddCheckedToBatch()
    {
        var added = 0;
        foreach (TreeNode root in _sourceTree.Nodes)
            added += CollectChecked(root);

        if (added > 0)
        {
            MessageBox.Show(this, $"Đã thêm {added} file vào gói update.", "Bcode — Gen Update",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private int CollectChecked(TreeNode node)
    {
        var count = 0;
        if (node.Checked && node.Tag is FileLookupNode { IsDirectory: false } file)
        {
            var existing = _batchList.Items.Cast<BatchItem>();
            if (!existing.Any(b => string.Equals(b.SourceFilePath, file.FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                // _sourceRoot chắc chắn khác null ở đây — cây chỉ có file khi đã build được
                // qua LoadForMenuItem/SearchAndLoad, cả hai đều đòi _sourceRoot trước.
                var relative = Path.GetRelativePath(_sourceRoot!, file.FullPath);
                _batchList.Items.Add(new BatchItem { DisplayName = file.FullPath, RelativeDestPath = relative, SourceFilePath = file.FullPath });
                count++;
            }
        }
        foreach (TreeNode child in node.Nodes)
            count += CollectChecked(child);
        return count;
    }

    private void RemoveSelectedFromBatch()
    {
        for (var i = _batchList.SelectedIndices.Count - 1; i >= 0; i--)
            _batchList.Items.RemoveAt(_batchList.SelectedIndices[i]);
    }

    private void CreateUpdateFiles()
    {
        if (_batchList.Items.Count == 0)
        {
            MessageBox.Show(this, "Chưa có file nào trong gói update — check file bên Source File rồi bấm Add.",
                "Bcode — Gen Update");
            return;
        }
        if (string.IsNullOrWhiteSpace(_saveAtPathBox.Text))
        {
            MessageBox.Show(this, "Chưa nhập Save At Path.", "Bcode — Gen Update");
            return;
        }
        if (string.IsNullOrWhiteSpace(_folderNameBox.Text))
        {
            MessageBox.Show(this, "Chưa nhập Folder Name.", "Bcode — Gen Update");
            return;
        }

        var destRoot = Path.Combine(_saveAtPathBox.Text.Trim(), _folderNameBox.Text.Trim());
        try
        {
            Directory.CreateDirectory(destRoot);
            var copied = 0;
            foreach (BatchItem item in _batchList.Items)
            {
                // RelativeDestPath đã được tính sẵn lúc thêm vào _batchList — "Main\..." hay
                // "App_Data\Controllers\..." cho file copy (CollectChecked), "app\script\..."
                // hay "sys\script\..." cho script sinh ra (GenStoreScriptAsync).
                var destFile = Path.Combine(destRoot, item.RelativeDestPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                if (item.GeneratedContent is not null)
                    File.WriteAllText(destFile, item.GeneratedContent);
                else
                    File.Copy(item.SourceFilePath!, destFile, overwrite: true);
                copied++;
            }

            if (!string.IsNullOrWhiteSpace(_descriptionBox.Text))
                File.WriteAllText(Path.Combine(destRoot, "description.txt"), _descriptionBox.Text);

            _resultPathLabel.Text = $"Đã tạo {copied} file tại: {destRoot}";
            _lastCreatedPath = destRoot;
            _copyLinkButton.Enabled = true;
            MessageBox.Show(this, _resultPathLabel.Text, "Bcode — Gen Update",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Create Update File", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Copies the last "Create Update File" destination folder path to the clipboard
    /// so it can be pasted straight into a chat/email to send to the client site.</summary>
    private void CopyResultPath()
    {
        if (string.IsNullOrWhiteSpace(_lastCreatedPath)) return;
        try
        {
            Clipboard.SetText(_lastCreatedPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Copy link", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}