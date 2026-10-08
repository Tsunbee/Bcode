using System.Data;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// The SELECT / FROM / WHERE / ORDER BY + Run tool (WCommand &gt; Table in FCode).
/// Wraps SqlQueryService, which is what expands a "...$000000" FROM target into a
/// UNION ALL over every real period table.
///
/// Cột trái là trang WebView2 (Web/Shell/commandquery.html — tự co giãn theo cửa sổ, ăn theo Template giao diện): các ô SELECT / FROM / WHERE / ORDER BY / Top,
/// nút Run + Add Script và danh sách "Fields" của bảng đang ở FROM (tên + "(PK)" cho khoá chính) — tick cột thì SELECT được dựng lại từ các cột đã tick,
/// nên xây câu truy vấn là "gõ FROM, tick cột cần lấy" thay vì gõ tên cột bằng tay. Kết quả vẫn ở lưới WinForms bên phải (lưới nhiều dòng giữ native cho nhanh, kèm
/// menu chuột phải Gen Insert / Gen Update...). "Add Script" gói mọi dòng đang nạp trong lưới thành script DELETE + INSERT hàng loạt cho bảng FROM
/// — xem DataScriptService cho định dạng (khớp output của FCode).
/// </summary>
public class SqlQueryControl : UserControl
{
    private readonly WebBarHost _web = new("commandquery.html") { Dock = DockStyle.Fill };
    private readonly DataGridView _grid;
    private readonly Label _statusLabel;
    private readonly SqlQueryService _service;
    private readonly GenInsertService _genInsert;
    private readonly GenUpdateService _genUpdate;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly DataScriptService _dataScript;
    private readonly ScriptFileService _scriptFileService;

    // Giá trị đặt trước khi trang web nạp xong (SetFrom / SetWhere gọi lúc tab còn chưa hiện) — đẩy xuống khi Ready.
    private string? _pendingFrom, _pendingWhere;
    private bool _ready;
    // Bumped on every field reload — a slow metadata query for a FROM value the user already changed past is a no-op
    // instead of overwriting the list with stale columns (same pattern FileLookupControl.PreviewFile uses for its own background reads).
    private int _fieldsRequestVersion;
    private string _currentFrom = "";

    private static string J(object? o) => JsonSerializer.Serialize(o);

    public SqlQueryControl(SqlQueryService service, GenInsertService genInsert, GenUpdateService genUpdate,
        SqlObjectBrowserService sqlObjectService, DataScriptService dataScript, ScriptFileService scriptFileService)
    {
        _service = service;
        _genInsert = genInsert;
        _genUpdate = genUpdate;
        _sqlObjectService = sqlObjectService;
        _dataScript = dataScript;
        _scriptFileService = scriptFileService;
        Dock = DockStyle.Fill;

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray };

        // AutoSizeColumnsMode.DisplayedCells left continuously ON recalculates every column's width on basically every paint/scroll —
        // GridDisplayHelper.BindOptimized runs that sizing pass ONCE right after data loads instead, then leaves column widths fixed (None).
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };

        // Right-click menu is HTML/CSS now (Controls/WebMenu.cs) and is rebuilt per click.
        WebMenu.AttachTo(_grid, () =>
        {
            if (ResultGridMenu.TryBuildHeaderMenu(_grid) is { } headerMenu) return headerMenu;
            var menu = new WebMenu()
                .AddCaption("Gen script")
                .Add("Gen Insert (dòng đã chọn)", GenInsertSelected)
                .Add("Gen Update (dòng đã chọn)", GenUpdateSelected, shortcut: "Ctrl+Shift+U");
            return ResultGridMenu.AddItemsTo(menu, _grid);
        });
        ResultGridMenu.WireShortcuts(_grid);
        _grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.U) { e.Handled = true; GenUpdateSelected(); }
        };

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_grid);
        right.Controls.Add(_statusLabel);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(_web);
        split.Panel2.Controls.Add(right);
        split.Panel1MinSize = 0;
        split.Panel2MinSize = 0;
        // Bề rộng cột trái theo cỡ màn hình / hệ số UI Scale (không cố định 300px).
        void ApplySplitterDistance()
        {
            if (split.Width <= 0) return;
            var desired = (int)Math.Round(Math.Max(300, Math.Min(460, split.Width * 0.27)) * Bcode.App.UI.UiScale.Factor);
            var clamped = Math.Max(0, Math.Min(desired, split.Width - split.SplitterWidth));
            if (split.SplitterDistance != clamped) split.SplitterDistance = clamped;
        }
        bool sized = false;
        split.SizeChanged += (_, _) => { if (!sized) { sized = true; ApplySplitterDistance(); } };
        Controls.Add(split);

        _web.Ready += OnWebReady;
        _web.Message += root => { var m = root.Clone(); _ = HandleAsync(m); };
    }

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    public void SetFrom(string fromClause) { _pendingFrom = fromClause; if (_ready) Js($"cmdq.setFrom({J(fromClause)})"); }
    public void SetWhere(string whereClause) { _pendingWhere = whereClause; if (_ready) Js($"cmdq.setWhere({J(whereClause)})"); }

    /// <summary>Fired after a successful Run, carrying the result table (consumed e.g. by Create *.xlsx).</summary>
    public event Action<DataTable>? ResultReady;

    private void OnWebReady()
    {
        _ready = true;
        Js($"cmdq.init({J(new { maxRows = SqlQueryService.DefaultMaxRows })})");
        if (_pendingFrom is not null) Js($"cmdq.setFrom({J(_pendingFrom)})");
        if (_pendingWhere is not null) Js($"cmdq.setWhere({J(_pendingWhere)})");
        _ = LoadFromSuggestionsAsync();
    }

    /// <summary>Gợi ý tên table/view cho ô FROM — nạp 1 lần khi tab mở, gộp cả App Data lẫn Sys Data (FROM có thể tham chiếu bảng ở cả 2).
    /// Im lặng khi lỗi: đây chỉ là tiện ích, không được chặn / làm phiền khi workspace chưa kết nối.</summary>
    private async Task LoadFromSuggestionsAsync()
    {
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys);
                foreach (var o in objs.Where(o => o.Kind is SqlObjectKind.Table or SqlObjectKind.View)) { names.Add(o.QualifiedName); names.Add(o.Name); }
            }
            Js($"cmdq.onSuggestions({J(names.Distinct().ToArray())})");
        }
        catch { /* Chưa kết nối / lỗi tạm thời — người dùng vẫn gõ FROM tay được như cũ. */ }
    }

    private async Task HandleAsync(JsonElement m)
    {
        try
        {
            switch (m.TryGetProperty("action", out var a) ? a.GetString() : "")
            {
                case "run":
                    await RunAsync(S(m, "select"), S(m, "from"), S(m, "where"), S(m, "orderBy"), S(m, "top"));
                    break;
                case "fromChanged": await ReloadFieldsAsync(S(m, "from")); break;
                case "addScript": await GenDataScriptAsync(S(m, "from")); break;
            }
        }
        catch (Exception ex) { _statusLabel.Text = "Lỗi."; MessageBox.Show(this, ex.Message, "Bcode — SQL Query", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static string S(JsonElement m, string name) => m.TryGetProperty(name, out var e) ? e.GetString() ?? "" : "";

    /// <summary>(schema, table) parsed out of a "dbo.dmkh" / "[dbo].[dmkh]" / "dmkh" FROM
    /// value — same convention TableEditControl.ParseTableRef uses. Anything more complex
    /// (a join, a "$000000" period placeholder, a subquery) isn't a single real table, so the
    /// Fields list just stays empty for it rather than guessing.</summary>
    private static (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Trim('[', ']');
        var parts = raw.Replace("[", "").Replace("]", "").Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    /// <summary>Reloads the Fields checklist for whatever's currently in FROM. A FROM with a
    /// space (join / alias / "$000000" placeholder) is skipped — GetColumnsAsync only makes
    /// sense for a single bare table reference — and the list is just cleared instead.</summary>
    private async Task ReloadFieldsAsync(string fromText)
    {
        var version = ++_fieldsRequestVersion;
        fromText = fromText.Trim();
        _currentFrom = fromText;

        if (fromText.Length == 0 || fromText.Contains(' ') || fromText.Contains('$'))
        {
            Js($"cmdq.onFields({J(new { status = "", columns = Array.Empty<object>() })})");
            return;
        }

        var (schema, table) = ParseTableRef(fromText);
        Js($"cmdq.onFields({J(new { status = "Đang tải...", columns = Array.Empty<object>() })})");
        try
        {
            var columns = await _sqlObjectService.GetColumnsAsync(false, schema, table);
            if (columns.Count == 0)
                columns = await _sqlObjectService.GetColumnsAsync(true, schema, table);
            if (version != _fieldsRequestVersion) return; // FROM moved on again while this was loading

            Js($"cmdq.onFields({J(new { status = columns.Count == 0 ? "(không có cột)" : $"{columns.Count} cột", columns = columns.Select(c => new { name = c.Name, pk = c.IsPrimaryKey }) })})");
        }
        catch
        {
            if (version != _fieldsRequestVersion) return;
            Js($"cmdq.onFields({J(new { status = "", columns = Array.Empty<object>() })})");
            // Không kết nối được / tên bảng chưa hợp lệ — bỏ qua, SELECT vẫn gõ tay được.
        }
    }

    private async Task RunAsync(string select, string from, string where, string orderBy, string top)
    {
        var maxRows = int.TryParse(top, out var n) ? n : SqlQueryService.DefaultMaxRows;

        _statusLabel.Text = "Đang chạy...";
        Js("cmdq.onBusy(true)");
        try
        {
            var table = await _service.RunAsync(select, from, where, orderBy, maxRows);
            GridDisplayHelper.BindOptimized(_grid, table);
            var capNote = maxRows > 0 ? $"(tối đa {maxRows})" : "(không giới hạn)";
            _statusLabel.Text = $"{table.Rows.Count} dòng {capNote} · SQL đã thực thi: {_service.LastSql.Replace('\n', ' ')}";
            ResultReady?.Invoke(table);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — SQL Query", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Js("cmdq.onBusy(false)");
        }
    }

    private void GenInsertSelected()
    {
        if (_grid.DataSource is not DataTable table || _grid.SelectedRows.Count == 0) return;

        var rows = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.DataBoundItem is DataRowView)
            .Select(r => ((DataRowView)r.DataBoundItem!).Row);

        var targetName = Bcode.App.Forms.SimplePromptForm.Show(this, "Gen Insert", "Tên bảng đích cho câu lệnh INSERT:", table.TableName);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        var sql = _genInsert.GenerateInsertStatements(table, targetName, rows);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh INSERT và copy vào clipboard.", "Bcode",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void GenUpdateSelected()
    {
        if (_grid.DataSource is not DataTable table || _grid.SelectedRows.Count == 0) return;

        var rows = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.DataBoundItem is DataRowView)
            .Select(r => ((DataRowView)r.DataBoundItem!).Row);

        var targetName = Bcode.App.Forms.SimplePromptForm.Show(this, "Gen Update", "Tên bảng đích cho câu lệnh UPDATE:", table.TableName);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        var keyInput = Bcode.App.Forms.SimplePromptForm.Show(this, "Gen Update",
            "Cột khoá (key) làm điều kiện WHERE, cách nhau bởi dấu phẩy (vd: stt_rec hoặc ma_ct,ky):",
            table.Columns.Count > 0 ? table.Columns[0].ColumnName : "");
        if (string.IsNullOrWhiteSpace(keyInput)) return;

        var keyColumns = keyInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sql = _genUpdate.GenerateUpdateStatements(table, targetName, keyColumns, rows);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh UPDATE và copy vào clipboard.", "Bcode",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>"Add Script" — packages every row currently loaded in the grid (not just a
    /// selection, unlike Gen Insert/Gen Update above) into a DELETE + bulk-INSERT script for
    /// the target table. Defaults the target table name to whatever's in FROM, same as Gen
    /// Insert/Gen Update default to the result table's own name.
    ///
    /// Không hiện script ở hộp RichTextBox (đã thử nhiều cách mà script 18k+ dòng vẫn làm "Not Responding"): copy thẳng vào clipboard + báo ở thanh trạng thái,
    /// và thêm (trong bộ nhớ, không file) vào Script Cart ở luồng nền để View/Save/Copy Script ở thanh trên vẫn lấy được.</summary>
    private async Task GenDataScriptAsync(string fromText)
    {
        if (_grid.DataSource is not DataTable table || table.Rows.Count == 0)
        {
            MessageBox.Show(this, "Chưa có dữ liệu để sinh Script — bấm Run trước.", "Bcode — Add Script");
            return;
        }

        var (_, defaultTable) = ParseTableRef(fromText);
        var targetName = Bcode.App.Forms.SimplePromptForm.Show(this, "Add Script",
            "Tên bảng đích (DELETE toàn bộ rồi nạp lại từ dữ liệu đang xem):",
            string.IsNullOrWhiteSpace(defaultTable) ? table.TableName : defaultTable);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        Js("cmdq.onBusy(true)");
        _statusLabel.Text = $"Đang sinh script cho {table.Rows.Count} dòng...";
        try
        {
            var script = await Task.Run(() => _dataScript.GenerateDeleteAndReloadScript(table, targetName));

            Clipboard.SetText(script);
            _scriptFileService.AddTextToCart(script);

            _statusLabel.Text = $"Đã sinh script ({table.Rows.Count} dòng) và copy vào clipboard.";
            MessageBox.Show(this, "Đã sinh script và copy vào clipboard.", "Bcode — Add Script",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Add Script", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Js("cmdq.onBusy(false)");
        }
    }
}
