using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Tab "View Relative Tables" (như FCode): trái = danh sách bảng liên quan tới một chứng từ (relativetables.html), phải = lưới dữ liệu
/// nhúng (một <see cref="TableEditControl"/>). Bấm một bảng / huy hiệu c$ m$ d$ i$ → nạp vào lưới phải **xem tại chỗ**; chuột phải → "Mở sang tab mới"
/// bắn <see cref="OpenTableRequested"/> cho MainForm mở sang tab Table riêng. Danh sách tính bằng <see cref="RelativeTablesService"/>.
/// Trang danh sách là WebView2 (cả chế độ App — chưa có bản native, như tab Trợ giúp); lưới phải dùng lại y hệt tab Table.</summary>
public sealed class RelativeTablesControl : UserControl
{
    /// <summary>Bề rộng thiết kế (px ở 96 DPI, chưa nhân UiScale) của cột danh sách bên trái.</summary>
    private const int ListWidth = 340;

    private readonly WebBarHost _web = new("relativetables.html") { Dock = DockStyle.Fill };
    private readonly RelativeTablesService _service;
    private readonly WCommandItem _item;
    private readonly bool _fbff;
    private readonly TableEditControl _table;
    private readonly Panel _left = new() { Dock = DockStyle.Left, Width = ListWidth };
    private readonly Splitter _splitter = new() { Dock = DockStyle.Left, Width = 4, MinSize = 160, MinExtra = 220 };
    private readonly Panel _right = new() { Dock = DockStyle.Fill };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Chuột phải → "Mở sang tab mới": (Sys Data?, tên bảng, điều kiện WHERE điền sẵn) — MainForm mở sang tab Table riêng.</summary>
    public event Action<bool, string, string>? OpenTableRequested;

    /// <param name="tableFactory">Tạo một <see cref="TableEditControl"/> đã nối sẵn dịch vụ + menu (WireTableMenu) — MainForm cấp vì giữ các service.</param>
    public RelativeTablesControl(RelativeTablesService service, WCommandItem item, bool fbff, Func<TableEditControl> tableFactory)
    {
        Dock = DockStyle.Fill;
        _service = service;
        _item = item;
        _fbff = fbff;
        _table = tableFactory();
        _table.Dock = DockStyle.Fill;

        // Panel + Splitter cổ điển, KHÔNG dùng SplitContainer: SplitContainer tự kiểm SplitterDistance lúc layout và NÉM lỗi
        // "SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize" khi cửa sổ hẹp hơn tổng min-size (ngoài try/catch).
        _left.Controls.Add(_web);
        _right.Controls.Add(_table);
        Controls.Add(_right);
        Controls.Add(_splitter);
        Controls.Add(_left);
        FixDockOrder();
        _splitter.SplitterMoved += (_, _) => _userSized = true;   // chỉ bắn khi người dùng kéo
        Resize += (_, _) => FitList();
        _splitter.BackColor = AppColors.Border;
        Action recolor = () => _splitter.BackColor = AppColors.Border;
        ThemeManager.ThemeChanged += recolor;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= recolor;

        _web.Ready += () => _ = LoadAsync();
        _web.Message += OnMessage;
    }

    /// <summary>WinForms xếp control dock theo z-order NGƯỢC: control ở CUỐI collection dock trước (bám mép), control index 0 dock sau cùng.
    /// Fill phải ở index 0 để chỉ lấy phần còn lại — nếu không, lưới phải phủ cả bề ngang và cột danh sách đè lên (ô Table / Where, cột đầu bị che).
    /// Đặt index tường minh (và đặt lại khi tạo handle) để không lệ thuộc thứ tự Add hay việc z-order bị đảo khi tạo handle ở chế độ App.</summary>
    private void FixDockOrder()
    {
        Controls.SetChildIndex(_right, 0);
        Controls.SetChildIndex(_splitter, 1);
        Controls.SetChildIndex(_left, 2);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FixDockOrder();
        FitList();
    }

    private bool _userSized;

    /// <summary>Bề rộng cột danh sách: theo DPI × UiScale (đặt tuyệt đối, không bị nhân đôi nếu form cha đã tự scale) — tối đa <see cref="ListWidth"/>,
    /// khoảng 35% bề ngang tab, luôn chừa cho lưới bên phải ≥ 260px để tab hẹp không ép lưới. Người dùng đã kéo thanh chia thì giữ bề rộng đó
    /// (chỉ co lại khi không còn đủ chỗ cho lưới).</summary>
    private void FitList()
    {
        if (!IsHandleCreated || Width <= 0) return;
        var want = _userSized ? _left.Width
            : Math.Min(NativeSkin.Px(this, ListWidth), Math.Max(NativeSkin.Px(this, 180), Width * 35 / 100));
        var w = Math.Max(NativeSkin.Px(this, 160), Math.Min(want, Width - NativeSkin.Px(this, 260)));
        if (_left.Width != w) _left.Width = w;
    }

    private async Task LoadAsync()
    {
        _web.Call("window.relativetables && window.relativetables.onLoading()");
        try
        {
            var result = await _service.BuildAsync(_item, _fbff);
            _web.Call($"window.relativetables.init({JsonSerializer.Serialize(result, JsonOpts)})");
            _ = LoadCountsAsync(result.Tables);   // số dòng từng bảng: nạp NỀN, điền vào danh sách khi xong (không chặn)
        }
        catch (Exception ex)
        {
            _web.Call($"window.relativetables.onError({WebBarHost.Json(ex.Message)})");
        }
    }

    /// <summary>Đếm số dòng (theo WHERE) từng bảng ở NỀN rồi điền vào danh sách — không chặn danh sách hiện lên. Lỗi / đã đóng tab thì bỏ qua.</summary>
    private async Task LoadCountsAsync(IReadOnlyList<RelativeTablesService.RelTable> tables)
    {
        if (tables.Count == 0) return;
        try
        {
            var counts = await _service.CountAllAsync(tables);
            if (!IsDisposed) _web.Call($"window.relativetables.setCounts({JsonSerializer.Serialize(counts, JsonOpts)})");
        }
        catch { /* nền — lỗi thì số dòng để "?", danh sách vẫn dùng bình thường */ }
    }

    private void OnMessage(JsonElement root)
    {
        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        switch (action)
        {
            case "open":        // bấm trái = xem tại chỗ (lưới phải)
                if (Parse(root) is { } o1) _ = _table.OpenTableAsync(o1.Sys, "dbo", o1.Table, (Fields: "", Where: o1.Where, Order: "", Top: 0));
                break;
            case "opennew":     // chuột phải → Mở sang tab mới
                if (Parse(root) is { } o2) OpenTableRequested?.Invoke(o2.Sys, o2.Table, o2.Where);
                break;
            case "copy":
                var text = root.TryGetProperty("text", out var x) ? x.GetString() : null;
                if (!string.IsNullOrEmpty(text))
                    try { Clipboard.SetText(text); } catch { /* clipboard bận */ }
                break;
        }
    }

    private static (bool Sys, string Table, string Where)? Parse(JsonElement root)
    {
        var table = root.TryGetProperty("table", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(table)) return null;
        var sys = root.TryGetProperty("sys", out var s) && s.ValueKind == JsonValueKind.True;
        var where = root.TryGetProperty("where", out var w) ? w.GetString() ?? "" : "";
        return (sys, table!, where);
    }
}
