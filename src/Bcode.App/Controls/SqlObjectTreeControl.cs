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

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
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
        if (Visible) _ = ReloadAsync(silent: true);
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
        _tree.Nodes.Clear();
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
    private int _renderSeq;
    private bool _beforeExpandHooked;

    /// <summary>Hiện cây. Không lọc: nhóm + sắp xếp ở luồng nền, chỉ tạo 5 node nhóm rồi mở nhóm đầu (node con nạp từng đợt). Có lọc: ít kết quả nên dựng thẳng như cũ.</summary>
    private async Task RenderAsync()
    {
        if (_filterText.Trim().Length > 0) { _renderSeq++; Render(); return; }
        var seq = ++_renderSeq;
        string? key = null;
        try { key = _service.CacheId(UseSysDatabase); } catch { /* chưa chọn workspace */ }
        var sig = _currentSignature;
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

        if (!_beforeExpandHooked)
        {
            _beforeExpandHooked = true;
            _tree.BeforeExpand += (_, e) =>
            {
                if (e.Node?.Tag is GroupState { Filled: false } st) _ = FillGroupAsync(e.Node, st);
            };
        }
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            if (groups.Length == 0)
                _tree.Nodes.Add(new TreeNode("Database chưa có đối tượng nào.") { ForeColor = Color.Gray });
            else
            {
                foreach (var g in groups)
                {
                    var node = new TreeNode($"{g.Label} ({g.Items.Length})") { Tag = new GroupState { Data = g } };
                    node.Nodes.Add(new TreeNode("...") { ForeColor = Color.Gray });   // chỗ giữ để có dấu [+]; thay bằng node thật khi mở
                    _tree.Nodes.Add(node);
                }
                _tree.Nodes[0].Expand();
            }
        }
        finally { _tree.EndUpdate(); }
    }

    /// <summary>Nạp node con của một nhóm theo từng đợt <see cref="FillChunk"/> node, nhường luồng UI giữa các đợt — đợt đầu hiện ngay, phần còn lại đổ vào ngay sau đó.</summary>
    private async Task FillGroupAsync(TreeNode node, GroupState st)
    {
        st.Filled = true;
        var seq = _renderSeq;
        try
        {
            var items = st.Data.Items;
            for (var i = 0; i < items.Length; i += FillChunk)
            {
                if (seq != _renderSeq || node.TreeView is null) return;   // cây đã được dựng lại / node bị gỡ
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
        }
        catch { st.Filled = false; /* lỗi nhẹ — lần mở sau nạp lại */ }
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

    /// <summary>Dựng cây từ danh sách đang có: nhóm theo thứ tự Stored Procedures → Functions → Views → Tables → Triggers; lọc theo tên ở máy (không hỏi lại database).</summary>
    private void Render()
    {
        var filter = _filterText.Trim();
        var objects = filter.Length == 0 ? _items : _items.Where(o => o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            if (objects.Count == 0)
            {
                _tree.Nodes.Add(new TreeNode(filter.Length > 0 ? "Không tìm thấy đối tượng nào khớp." : "Database chưa có đối tượng nào.") { ForeColor = Color.Gray });
                return;
            }
            foreach (var kind in GroupOrder)
            {
                var items = objects.Where(o => o.Kind == kind).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (items.Count == 0) continue;
                var groupNode = new TreeNode($"{GroupLabel(kind)} ({items.Count})");
                foreach (var obj in items)
                    groupNode.Nodes.Add(new TreeNode(obj.QualifiedName) { Tag = obj });
                _tree.Nodes.Add(groupNode);
            }
            // Có từ khoá: mở hết (ít kết quả). Nạp sẵn toàn bộ: chỉ mở nhóm đầu để cây không lag.
            if (filter.Length > 0) _tree.ExpandAll();
            else if (_tree.Nodes.Count > 0) _tree.Nodes[0].Expand();
        }
        finally { _tree.EndUpdate(); }
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
