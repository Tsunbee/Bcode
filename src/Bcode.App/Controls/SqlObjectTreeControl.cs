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
                            if (_loadedOnce) Render(); else await ReloadAsync(); // lọc ngay ở máy, không hỏi lại database
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
            Render();
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
                Render();
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
        Render();
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
