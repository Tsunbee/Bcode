using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Sơ đồ phụ thuộc": nút đang xem ở giữa, bên trái "được dùng bởi" (menu, controller, procedure gọi tới), bên phải "dùng tới" (bảng, procedure,
/// include). Bấm 1 nút = chuyển sơ đồ sang nút đó; "Phân tích ảnh hưởng" dò ngược nhiều tầng tới tận menu. Giao diện là trang WebView2
/// (Web/Shell/depgraph.html); dữ liệu do <see cref="DependencyGraphService"/> tra ngầm khi cần (chỉ đọc, không làm chậm lúc sửa procedure).
/// </summary>
public class DependencyGraphControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebBarHost _web = new("depgraph.html") { Dock = DockStyle.Fill };
    private readonly DependencyGraphService _service;
    private DepNode _current;
    private CancellationTokenSource? _cts;

    /// <summary>Mở nút: object database → tab SQL, file → mở như File Lookup, menu → chọn trên cây WCommand.</summary>
    public event Action<DepNode>? OpenRequested;

    /// <summary>
    /// Menu chuột phải trên một nút: mode = sql (ALTER / định nghĩa bảng) · sqlDropCreate · usages (Ai đang dùng) · viewer (BcodeViewer) ·
    /// scriptTab (tab của Bcode) · folder (mở thư mục chứa file).
    /// </summary>
    public event Action<DepNode, string>? ContextActionRequested;

    /// <summary>Nút đang xem đổi (bấm nút, lùi / tới) — để đặt lại tên tab.</summary>
    public event Action<DepNode>? CurrentChanged;

    public DependencyGraphControl(DependencyGraphService service, DepNode start)
    {
        _service = service;
        _current = start;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => Handle(root.GetRawText());
        _web.Ready += () => _ = LoadAsync(_current, force: false);
        Disposed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); };
    }

    /// <summary>Chuyển sơ đồ sang nút khác (vd mở lại tab cho object khác).</summary>
    public void ShowNode(DepNode node)
    {
        if (_web.IsReady) Js($"depgraph.push({J(node)})");
        _ = LoadAsync(node, force: false);
    }

    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private static DepNode? NodeOf(JsonElement root) =>
        root.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.Object ? n.Deserialize<DepNode>(JsonOpts) : null;

    private void Handle(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            switch (root.GetProperty("action").GetString())
            {
                case "focus": if (NodeOf(root) is { } f) _ = LoadAsync(f, force: false); break;
                case "refresh": _ = LoadAsync(_current, force: true); break;
                case "open": if (NodeOf(root) is { } o) OpenRequested?.Invoke(o); break;
                case "ctx":
                    if (NodeOf(root) is { } cn && root.TryGetProperty("mode", out var md) && md.GetString() is { Length: > 0 } mode)
                        ContextActionRequested?.Invoke(cn, mode);
                    break;
                case "impact": _ = ImpactAsync(_current); break;
                case "copy": try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard bận */ } break;
            }
        }
        catch { /* tin nhắn lỗi — bỏ qua */ }
    }

    private CancellationToken Restart()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private async Task LoadAsync(DepNode node, bool force)
    {
        _current = node;
        if (InvokeRequired) { try { BeginInvoke(() => CurrentChanged?.Invoke(node)); } catch { /* đang đóng */ } }
        else CurrentChanged?.Invoke(node);
        var ct = Restart();
        Js($"depgraph.onLoading({J(node)})");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = await Task.Run(() => _service.NeighborsAsync(node, force, msg => Js($"depgraph.onProgress({J(msg)})"), ct), ct);
            if (ct.IsCancellationRequested) return;
            Js($"depgraph.onData({J(new { center = res.Center, up = res.Up, down = res.Down, warnings = res.Warnings, ms = sw.ElapsedMilliseconds })})");
            if (res.StdPending) _ = WatchStandardIndexAsync(node, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Js($"depgraph.onError({J(ex.Message)})"); }
    }

    /// <summary>Chỉ mục source chuẩn (file .f mã hoá) đang dựng nền: 3 giây hỏi 1 lần để cập nhật dòng báo (bước đang làm, %), dựng xong thì tự tải lại
    /// sơ đồ — không phải bấm Làm mới. Chuyển sang nút khác / đóng tab thì dừng (ct bị huỷ).</summary>
    private async Task WatchStandardIndexAsync(DepNode node, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);
                var st = await Task.Run(() => _service.StandardBuildState(), ct);
                if (st is null || ct.IsCancellationRequested) return;
                if (st.Value.Building) { Js($"depgraph.onStdProgress({J(st.Value.Text)})"); continue; }
                _ = LoadAsync(node, force: false);   // dựng xong → tra lại (kết quả lúc đang chờ không được nhớ)
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch { /* hỏi trạng thái lỗi — thôi, người dùng vẫn bấm Làm mới được */ }
    }

    private async Task ImpactAsync(DepNode node)
    {
        var ct = Restart();
        Js($"depgraph.onImpactStart({J(node)})");
        try
        {
            var (rows, truncated, warnings) = await Task.Run(() => _service.ImpactAsync(node, msg => Js($"depgraph.onProgress({J(msg)})"), ct), ct);
            if (ct.IsCancellationRequested) return;
            Js($"depgraph.onImpact({J(new { rows = rows.Select(r => new { level = r.Level, node = r.Node, via = r.Via }), truncated, warnings })})");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) Js($"depgraph.onError({J(ex.Message)})"); }
    }
}
