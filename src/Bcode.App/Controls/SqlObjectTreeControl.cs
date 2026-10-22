using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Left-hand tree for the "SQL Object" tab: tables / views / procedures / functions.
/// Includes a database switcher (Sys Data / App Data) so the same tab can search
/// either of the workspace's two databases, matching FCode's own "switch between
/// databases to filter info" behavior.
/// </summary>
public class SqlObjectTreeControl : UserControl
{
    private readonly TreeView _tree;
    // DB switcher + name filter are now a small WebView2 strip (Web/Shell/sqlobjectbar.html) —
    // same chrome-vs-content split used throughout: this bar is static/low-data, the tree
    // below (often a few hundred+ nodes) stays 100% native WinForms.
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _barWeb = new();
    private bool _useSysDatabase;
    private string _filterText = "";
    private readonly SqlObjectBrowserService _service;

    public event Action<SqlObjectInfo>? ObjectActivated;
    /// <summary>Chuột phải → "Ai đang dùng object này…".</summary>
    public event Action<SqlObjectInfo>? UsagesRequested;

    public SqlObjectTreeControl(SqlObjectBrowserService service)
    {
        _service = service;
        Dock = DockStyle.Fill;

        _barWeb.Dock = DockStyle.Top;
        _barWeb.Height = 120;

        _tree = new SafeTreeView { Dock = DockStyle.Fill, HideSelection = false };
        // Vẽ qua bộ đệm (TVS_EX_DOUBLEBUFFER) ngay khi có handle — ThemeManager chỉ bật được nếu handle đã có lúc áp theme; thiếu nó thì mỗi lần
        // dựng lại cây (gõ lọc) thấy nền xoá trắng rồi từng dòng hiện dần.
        _tree.HandleCreated += (_, _) => SendMessage(_tree.Handle, 0x112C /* TVM_SETEXTENDEDSTYLE */, (IntPtr)0x4, (IntPtr)0x4);
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is SqlObjectInfo obj) ObjectActivated?.Invoke(obj);
        };

        // Chuột phải: chọn đúng node bấm trúng rồi hiện menu (mở / ai đang dùng) — cùng kiểu menu HTML với cây WCommand.
        _tree.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _tree.GetNodeAt(e.Location) is { } hit) _tree.SelectedNode = hit;
        };
        WebMenu.AttachTo(_tree, () =>
        {
            if (_tree.SelectedNode?.Tag is not SqlObjectInfo picked) return null;
            return new WebMenu()
                .Add("Mở định nghĩa", () => ObjectActivated?.Invoke(picked))
                .Add("Ai đang dùng object này…", () => UsagesRequested?.Invoke(picked))
                .Add("Chép tên", () => { try { Clipboard.SetText(picked.QualifiedName); } catch { /* clipboard bận */ } });
        });

        Controls.Add(_tree);
        if (Bcode.App.UI.UiMode.IsNative)
        {
            Controls.Add(BuildNativeBar());   // chế độ App: ô chọn database + ô lọc là control WinForms, không WebView2
            return;
        }
        Controls.Add(_barWeb);

        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToBar;
        Disposed += (_, _) => Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToBar;
        _ = InitBarWebAsync();

        async Task InitBarWebAsync()
        {
            try
            {
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_barWeb);

                _barWeb.CoreWebView2.WebMessageReceived += async (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root = doc.RootElement;
                    switch (root.GetProperty("action").GetString())
                    {
                        case "db":
                            _useSysDatabase = root.GetProperty("value").GetInt32() == 1;
                            _filterText = root.GetProperty("filter").GetString() ?? "";
                            _loadedOnce = false; _currentSignature = null; // mỗi database có cache riêng
                            await ReloadAsync();
                            break;
                        case "reload":
                            _filterText = root.GetProperty("filter").GetString() ?? "";
                            if (_loadedOnce) await RenderAsync(); else await ReloadAsync(); // lọc ngay ở máy, không hỏi lại database
                            break;
                    }
                };

                _barWeb.CoreWebView2.NavigationCompleted += (_, _) => PushThemeToBar();
                _barWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("sqlobjectbar.html"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Không khởi tạo được thanh công cụ (dùng WebView2).\nChi tiết lỗi: " + ex.Message,
                    "Bcode — WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void PushThemeToBar()
        {
            if (_barWeb.CoreWebView2 is null) return;
            var isDark = Bcode.App.UI.AppColors.IsDark ? "true" : "false";
            _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
        }
    }

    private bool UseSysDatabase => _useSysDatabase;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Thanh trên cây bản native: tương đương sqlobjectbar.html (chọn App/Sys Data + lọc theo tên, gõ tới đâu lọc tới đó sau 120ms).</summary>
    private Control BuildNativeBar()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = Bcode.App.UI.UiTemplate.Dens(82), Padding = new Padding(6, 6, 6, 6) };
        var db = new DropDownButton { Dock = DockStyle.Top, Height = Bcode.App.UI.UiTemplate.Dens(28) };
        db.SetItems(new[] { "App Data", "Sys Data" }, 0);
        var filter = new RoundedInput { Dock = DockStyle.Top, PlaceholderText = "Lọc theo tên (gõ là lọc luôn)..." };
        var gap = new Panel { Dock = DockStyle.Top, Height = 4 };
        var timer = new System.Windows.Forms.Timer { Interval = 120 };
        timer.Tick += async (_, _) => { timer.Stop(); _filterText = filter.Text; if (_loadedOnce) await RenderAsync(); else await ReloadAsync(); };
        filter.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        filter.InputKeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true; timer.Stop(); _filterText = filter.Text;
            if (_loadedOnce) await RenderAsync(); else await ReloadAsync();
        };
        db.SelectionChanged += async i =>
        {
            _useSysDatabase = i == 1; _filterText = filter.Text;
            _loadedOnce = false; _currentSignature = null; // mỗi database có cache riêng
            await ReloadAsync();
        };
        Disposed += (_, _) => timer.Dispose();
        panel.Controls.Add(filter); panel.Controls.Add(gap); panel.Controls.Add(db);
        return panel;
    }

    private bool _loadedOnce;
    private int _loadVersion;
    private string? _currentSignature;
    private List<SqlObjectInfo> _items = new();

    /// <summary>Vào mục SQL Object: chưa nạp thì nạp (hiện ngay từ cache nếu có); đã nạp rồi thì chỉ hỏi "có object nào mới/đổi không", có mới nạp lại.</summary>
    public Task EnsureLoadedAsync() => _loadedOnce ? CheckForChangesAsync() : ReloadAsync(silent: true);

    /// <summary>Đổi workspace/dự án: bỏ danh sách cũ, nạp lại (từ cache của project mới) nếu cây đang hiện.</summary>
    public void ResetForWorkspace()
    {
        _loadedOnce = false;
        _currentSignature = null;
        _items = new();
        _loadVersion++;
        _renderSeq++;
        _tree.Nodes.Clear();
        // Chọn project lúc mới mở Bcode: cửa sổ chính chưa hiện nên cây chưa Visible (chưa có handle) → nạp ở OnVisibleChanged ngay khi cây hiện ra
        // (trước đây bỏ qua luôn nên cây trắng cho tới khi bấm đổi mục).
        if (Visible && !DeferAutoLoad) { _needsLoad = false; _ = ReloadAsync(silent: true); }
        else _needsLoad = true;
    }

    private bool _needsLoad;

    /// <summary>Hoãn việc tự nạp khi cây hiện ra. MainForm bật lúc mới mở Bcode trong khi màn Projects còn mở: dựng cây vài nghìn object (sắp xếp +
    /// tạo node vẽ tay trên luồng giao diện, 1–2 s) đúng lúc màn Projects đang hiện làm màn đó mở chậm hẳn (apply-timing.log 22/10).
    /// Đóng màn Projects thì gọi <see cref="ResumeAutoLoad"/>.</summary>
    public bool DeferAutoLoad { get; set; }

    /// <summary>Thôi hoãn: cây đang hiện mà chưa nạp (và chưa có lần nạp nào đang chạy) thì nạp ngay.</summary>
    public void ResumeAutoLoad()
    {
        DeferAutoLoad = false;
        TryAutoLoad();
    }

    private void TryAutoLoad()
    {
        if (DeferAutoLoad || !Visible || !_needsLoad || _loadedOnce) return;
        _needsLoad = false;
        _ = EnsureLoadedAsync();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        TryAutoLoad();
    }

    /// <summary>Nút "Refresh database" trên topbar: bỏ cache danh sách object của project (App + Sys, trên đĩa + cây đã nhóm trong bộ nhớ) rồi hỏi lại
    /// database ngay cho database đang xem (chưa mở mục SQL Object lần nào thì chỉ bỏ cache — lúc mở sẽ nạp mới). Trả về số object đã nạp (-1 = chỉ bỏ cache).
    /// Không hỏi được database: hiện lại danh sách đang có rồi ném lỗi để nơi gọi báo.</summary>
    public async Task<int> RefreshFromDatabaseAsync()
    {
        try
        {
            foreach (var k in new[] { _service.CacheId(false), _service.CacheId(true) }) { _groupCache.Remove(k); _groupCacheOrder.Remove(k); }
        }
        catch (InvalidOperationException) { return -1; /* chưa chọn workspace */ }
        _service.DeleteCache(!UseSysDatabase);   // database còn lại: lần sau chọn sẽ nạp mới từ database
        if (!_loadedOnce && !Visible)
        {
            _service.DeleteCache(UseSysDatabase);
            _items = new(); _currentSignature = null; _loadVersion++; _renderSeq++;
            _tree.Nodes.Clear();
            return -1;
        }

        var version = ++_loadVersion;
        _renderSeq++;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            _tree.Nodes.Add(new TreeNode("Đang nạp lại từ database...") { ForeColor = Color.Gray });
        }
        finally { _tree.EndUpdate(); }
        try
        {
            var sig = await _service.GetSignatureAsync(UseSysDatabase);
            if (version != _loadVersion) return -1;
            await FetchAllAsync(sig);   // ghi đè cache trên đĩa
        }
        catch
        {
            if (version == _loadVersion) await RenderAsync();   // giữ danh sách cũ
            throw;
        }
        if (version != _loadVersion) return -1;
        _loadedOnce = true;
        await RenderAsync();
        return _items.Count;
    }

    private async Task CheckForChangesAsync()
    {
        try
        {
            var sig = await _service.GetSignatureAsync(UseSysDatabase);
            if (sig == _currentSignature) return; // không có object mới/đổi → giữ nguyên, không nạp thêm
            await FetchAllAsync(sig);
            await RenderAsync();
        }
        catch { /* offline / lỗi nhẹ — giữ danh sách đang có */ }
    }

    private async Task FetchAllAsync(string signature)
    {
        var all = await _service.ListObjectsAsync(UseSysDatabase, null, hidePeriodTables: true);
        _service.SaveCache(UseSysDatabase, signature, all);
        _items = all;
        _currentSignature = signature;
    }

    /// <summary>Nạp danh sách rồi hiện (lọc theo ô tìm nếu có). Có cache thì hiện NGAY, sau đó chỉ nạp lại khi chữ ký trong database đã khác.</summary>
    public async Task ReloadAsync(bool silent = false)
    {
        var version = ++_loadVersion;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var cached = _service.LoadCache(UseSysDatabase);
            var tCache = sw.ElapsedMilliseconds;
            if (cached is not null)
            {
                _items = cached.Items;
                _currentSignature = cached.Signature;
                _loadedOnce = true;
                await RenderAsync();
                Bcode.App.UI.ThemeManager.LogTiming($"  SQL Object: đọc cache {tCache} ms + dựng cây {sw.ElapsedMilliseconds - tCache} ms ({cached.Items.Count} object)");
                try
                {
                    var sig = await _service.GetSignatureAsync(UseSysDatabase);
                    Bcode.App.UI.ThemeManager.LogTiming($"  SQL Object: hỏi chữ ký database xong sau {sw.ElapsedMilliseconds} ms");
                    if (version != _loadVersion || sig == cached.Signature) return; // khớp: giữ cache, không nạp thêm
                    await FetchAllAsync(sig);
                }
                catch { return; /* không hỏi được database (offline...) — vẫn dùng cache */ }
            }
            else
            {
                _tree.Nodes.Clear();   // có cache thì không xoá trước — cây thay một lần khi dựng xong (không nháy trắng)
                _tree.Nodes.Add(new TreeNode("Đang tải...") { ForeColor = Color.Gray });
                var sig = await _service.GetSignatureAsync(UseSysDatabase);
                await FetchAllAsync(sig);
            }
        }
        catch (Exception ex)
        {
            _tree.Nodes.Clear();
            if (silent || ex is InvalidOperationException)
            {
                _tree.Nodes.Add(new TreeNode(ex is InvalidOperationException ? "Chưa chọn workspace — chọn project để nạp danh sách." : "Không tải được danh sách: " + ex.Message) { ForeColor = Color.Gray });
                return;
            }
            MessageBox.Show(this, $"Không tải được danh sách object: {ex.Message}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (version != _loadVersion) return; // đã có lần nạp mới hơn
        _loadedOnce = true;
        await RenderAsync();
    }

    // TreeView của Bcode vẽ tay (OwnerDrawText, xem ThemeManager) nên MỖI node thêm vào tốn ~0,1 ms — 5000+ node là >300 ms trên UI.
    // Vì vậy chỉ tạo node con của nhóm khi nhóm được mở, và tạo từng đợt nhỏ để giao diện không khựng. Dữ liệu đã nhóm/sắp xếp
    // (không chứa control) giữ trong bộ nhớ theo (project + database, chữ ký) để đổi qua lại giữa các project khỏi sắp xếp lại.
    private sealed record GroupItems(string Label, SqlObjectInfo[] Items);
    private sealed class GroupState { public GroupItems Data = null!; public bool Filled; }
    private static readonly Dictionary<string, (string Signature, GroupItems[] Groups)> _groupCache = new();
    private static readonly List<string> _groupCacheOrder = new();   // dùng gần nhất ở cuối
    private const int MaxCachedProjects = 3;
    private const int FillChunk = 250;
    private const int InitialFill = 300;   // node con dựng sẵn cho mỗi nhóm mở (phủ dư khung nhìn); phần còn lại nối sau
    private int _renderSeq;
    private bool _beforeExpandHooked;

    /// <summary>Hiện cây. Nhóm + sắp xếp ở luồng nền (giữ trong bộ nhớ), chỉ tạo các node nhóm; node con nạp từng đợt khi nhóm mở.
    /// Có lọc: lọc ngay trên danh sách đã nhóm/sắp xếp đó ở luồng nền (không hỏi database, không sắp xếp lại) rồi mở lần lượt các nhóm —
    /// gõ phím tiếp thì lần hiện cũ dừng ngay, nên gõ "rs_" trên vài nghìn object không còn giật.</summary>
    private async Task RenderAsync()
    {
        var filter = _filterText.Trim();
        string? key = null;
        try { key = _service.CacheId(UseSysDatabase); } catch { /* chưa chọn workspace */ }
        var sig = _currentSignature;
        // Cùng từ khoá + cùng danh sách với cây đang hiện (vd. Enter sau khi đã lọc) → giữ nguyên, không dựng lại (và không cắt ngang phần đang nối).
        var renderKey = $"{key}|{sig}|{UseSysDatabase}|{filter}";
        if (renderKey == _renderedKey && _renderedSeq == _renderSeq && _tree.Nodes.Count > 0 && ReferenceEquals(_tree.Nodes[0], _renderedFirst)) return;   // _renderedSeq == _renderSeq: không có lần dựng nào mới hơn đang chạy
        var seq = ++_renderSeq;
        GroupItems[]? groups = null;
        if (key is not null && sig is not null && _groupCache.TryGetValue(key, out var hit) && hit.Signature == sig) groups = hit.Groups;
        if (groups is null)
        {
            var items = _items;
            groups = await Task.Run(() => BuildGroups(items));
            if (seq != _renderSeq) return;   // trong lúc dựng đã có lần hiện mới hơn (đổi project / gõ lọc)
        }
        if (key is not null && sig is not null)
        {
            _groupCache[key] = (sig, groups);
            _groupCacheOrder.Remove(key); _groupCacheOrder.Add(key);
            while (_groupCacheOrder.Count > MaxCachedProjects) { _groupCache.Remove(_groupCacheOrder[0]); _groupCacheOrder.RemoveAt(0); }
        }
        if (filter.Length > 0)
        {
            var all = groups;
            groups = await Task.Run(() => FilterGroups(all, filter));
            if (seq != _renderSeq) return;   // đã gõ thêm chữ — bỏ kết quả cũ
        }

        if (!_beforeExpandHooked)
        {
            _beforeExpandHooked = true;
            _tree.BeforeExpand += (_, e) =>
            {
                if (e.Node?.Tag is GroupState { Filled: false } st) _ = FillGroupAsync(e.Node, st);
            };
        }
        // Dựng sẵn TOÀN BỘ node cần hiện ngay ở luồng nền (TreeNode chưa gắn vào cây chỉ là object thường), rồi thay cả cây trong MỘT lần
        // BeginUpdate/EndUpdate → chỉ vẽ lại một lần: không còn cảnh cây trắng xoá → hiện vài dòng → đầy đủ (chớp nháy) mỗi phím gõ.
        // Có lọc: mở hết các nhóm như trước; không lọc: mở nhóm đầu. Mỗi nhóm mở dựng trước tối đa InitialFill node (đủ phủ khung nhìn),
        // phần còn lại nối vào cuối theo đợt (nằm dưới khung nhìn nên không thấy nháy).
        var expandAll = filter.Length > 0;
        var built = await Task.Run(() =>
        {
            var list = new List<(TreeNode Node, GroupState St, int Prefilled, bool Expand)>(groups.Length);
            var budget = InitialFill * 2;   // tổng node dựng sẵn — nhóm sau hết phần thì mở với "..." rồi được nối ngay sau (thường nằm dưới khung nhìn)
            for (var gi = 0; gi < groups.Length; gi++)
            {
                var g = groups[gi];
                var st = new GroupState { Data = g };
                var node = new TreeNode($"{g.Label} ({g.Items.Length})") { Tag = st };
                var expand = expandAll || gi == 0;
                var n = expand ? Math.Min(Math.Min(InitialFill, budget), g.Items.Length) : 0;
                budget -= n;
                if (n > 0)
                {
                    var arr = new TreeNode[n];
                    for (var j = 0; j < n; j++) arr[j] = new TreeNode(g.Items[j].QualifiedName) { Tag = g.Items[j] };
                    node.Nodes.AddRange(arr);
                }
                else node.Nodes.Add(new TreeNode("...") { ForeColor = Color.Gray });   // chỗ giữ để có dấu [+]; thay bằng node thật khi mở
                st.Filled = expand;   // nhóm mở sẵn: phần còn lại do vòng nối bên dưới lo, BeforeExpand không nạp lại
                list.Add((node, st, n, expand));
            }
            return list;
        });
        if (seq != _renderSeq) return;   // đã gõ thêm chữ — bỏ kết quả cũ

        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            if (built.Count == 0)
                _tree.Nodes.Add(new TreeNode(filter.Length > 0 ? "Không tìm thấy đối tượng nào khớp." : "Database chưa có đối tượng nào.") { ForeColor = Color.Gray });
            else
            {
                _tree.Nodes.AddRange(built.Select(b => b.Node).ToArray());
                foreach (var b in built) if (b.Expand) b.Node.Expand();
                _tree.TopNode = _tree.Nodes[0];   // về đầu danh sách (không giữ vị trí cuộn của kết quả cũ)
            }
            _renderedKey = renderKey;
            _renderedFirst = _tree.Nodes[0];
            _renderedSeq = seq;
        }
        finally { _tree.EndUpdate(); }

        // Nối phần còn lại của các nhóm đang mở, lần lượt từng nhóm, từng đợt.
        foreach (var b in built)
        {
            if (!b.Expand || b.Prefilled >= b.St.Data.Items.Length) continue;
            if (!await AppendChunksAsync(b.Node, b.St, b.Prefilled, seq)) return;
        }
    }

    private string? _renderedKey;
    private TreeNode? _renderedFirst;
    private int _renderedSeq = -1;

    /// <summary>Lọc theo tên trên các nhóm đã sắp xếp (giữ nguyên thứ tự, bỏ nhóm rỗng) — cùng quy tắc khớp với <see cref="SqlObjectBrowserService.NameMatcher"/>.</summary>
    private static GroupItems[] FilterGroups(GroupItems[] groups, string filter)
    {
        var match = SqlObjectBrowserService.NameMatcher(filter);
        var result = new List<GroupItems>(groups.Length);
        foreach (var g in groups)
        {
            var items = g.Items.Where(match).ToArray();
            if (items.Length > 0) result.Add(new GroupItems(g.Label, items));
        }
        return result.ToArray();
    }

    /// <summary>Nạp node con của một nhóm theo từng đợt <see cref="FillChunk"/> node, nhường luồng UI giữa các đợt — đợt đầu hiện ngay, phần còn lại đổ vào ngay sau đó.</summary>
    private Task FillGroupAsync(TreeNode node, GroupState st)
    {
        st.Filled = true;
        return AppendChunksAsync(node, st, 0, _renderSeq);
    }

    /// <summary>Thêm node con của nhóm từ vị trí <paramref name="start"/> (0 = thay node "..."), từng đợt; false = cây đã dựng lại giữa chừng.</summary>
    private async Task<bool> AppendChunksAsync(TreeNode node, GroupState st, int start, int seq)
    {
        try
        {
            var items = st.Data.Items;
            for (var i = start; i < items.Length; i += FillChunk)
            {
                if (seq != _renderSeq || node.TreeView is null) return false;   // cây đã được dựng lại / node bị gỡ
                var n = Math.Min(FillChunk, items.Length - i);
                var arr = new TreeNode[n];
                for (var j = 0; j < n; j++) arr[j] = new TreeNode(items[i + j].QualifiedName) { Tag = items[i + j] };
                _tree.BeginUpdate();
                try
                {
                    if (i == 0) node.Nodes.Clear();   // bỏ node "..."
                    node.Nodes.AddRange(arr);
                }
                finally { _tree.EndUpdate(); }
                if (i + FillChunk < items.Length) await Task.Delay(1);
            }
            return true;
        }
        catch { st.Filled = false; return false; /* lỗi nhẹ — lần mở sau nạp lại */ }
    }

    private static GroupItems[] BuildGroups(List<SqlObjectInfo> objects)
    {
        var result = new List<GroupItems>();
        foreach (var kind in GroupOrder)
        {
            var items = objects.Where(o => o.Kind == kind).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (items.Length > 0) result.Add(new GroupItems(GroupLabel(kind), items));
        }
        return result.ToArray();
    }

    private static readonly SqlObjectKind[] GroupOrder =
    {
        SqlObjectKind.StoredProcedure, SqlObjectKind.Function, SqlObjectKind.View, SqlObjectKind.Table, SqlObjectKind.Trigger,
    };

    private static string GroupLabel(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.Table => "Tables",
        SqlObjectKind.View => "Views",
        SqlObjectKind.StoredProcedure => "Stored Procedures",
        SqlObjectKind.Trigger => "Triggers",
        _ => "Functions"
    };
}
