using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>Tab "Hỏi Fast": chat tra kiến thức Fast không qua AI (<see cref="FastAskService"/>). Trang WebView2 Web/Shell/fastask.html (cả chế độ App,
/// như Trợ giúp). Nút trên thẻ trả lời → MainForm chèn vào tab SQL Query đang dùng / mở tab mới / mở định nghĩa object / mở Tạo nhanh danh mục.</summary>
public class FastAskControl : UserControl
{
    private readonly WebBarHost _web = new("fastask.html") { Dock = DockStyle.Fill };
    private readonly FastAskService _service;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Chèn code: (nội dung, true = mở tab SQL Query mới thay vì chèn vào tab đang dùng).</summary>
    public event Action<string, bool>? InsertRequested;
    /// <summary>Mở định nghĩa store / function: (tên, true = Sys Data, loại sys.objects.type: P / FN / IF / TF).</summary>
    public event Action<string, bool, string>? OpenObjectRequested;
    /// <summary>Mở Tạo nhanh danh mục theo luật Fast: (tên danh mục, mô tả cột).</summary>
    public event Action<string, string>? QuickListRequested;

    public FastAskControl(FastAskService service)
    {
        Dock = DockStyle.Fill;
        _service = service;
        Controls.Add(_web);
        _web.Message += OnMessage;
    }

    /// <summary>Gửi sẵn 1 câu hỏi (vd mở từ menu kèm chữ đang chọn).</summary>
    public void Ask(string text) => _web.Call($"fa.ask({WebBarHost.Json(text)})");

    private void OnMessage(JsonElement root)
    {
        switch (root.TryGetProperty("action", out var a) ? a.GetString() : null)
        {
            case "ask":
                _ = AskAsync(root.GetProperty("id").GetInt32(), root.GetProperty("text").GetString() ?? "",
                    root.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
                        ? st.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString())
                        : new Dictionary<string, string>(),
                    root.TryGetProperty("sys", out var sy) && sy.ValueKind == JsonValueKind.True);
                break;
            case "feedback":
                // ✔ / ✘ trên 1 thẻ → trả lời như 1 lượt chat (✘ thì hỏi tiếp cách đúng)
                var fr = _service.Feedback(root.GetProperty("vote").GetInt32(), root.GetProperty("ref").GetString() ?? "", root.GetProperty("sig").GetString() ?? "",
                    root.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "");
                _web.Call($"fa.reply({root.GetProperty("id").GetInt32()}, {JsonSerializer.Serialize(fr, Json)})");
                break;
            case "learn":
                _ = LearnAsync(root.GetProperty("id").GetInt32(), root.TryGetProperty("sys", out var ls) && ls.ValueKind == JsonValueKind.True);
                break;
            case "session-save":
                try { FastAskSessions.Save(root.GetProperty("sid").GetString() ?? "", root.GetProperty("data").GetRawText()); } catch { /* ổ đĩa lỗi → bỏ qua */ }
                break;
            case "session-list":
                _web.Call($"fa.sessions({JsonSerializer.Serialize(FastAskSessions.List())})");
                break;
            case "session-load":
                var sid = root.GetProperty("sid").GetString() ?? "";
                _web.Call($"fa.loadSession({WebBarHost.Json(sid)}, {FastAskSessions.Load(sid) ?? "null"})");
                break;
            case "session-delete":
                FastAskSessions.Delete(root.GetProperty("sid").GetString() ?? "");
                _web.Call($"fa.sessions({JsonSerializer.Serialize(FastAskSessions.List())})");
                break;
            case "session-delete-all":
                if (MessageBox.Show(this, "Xoá toàn bộ lịch sử chat Hỏi Fast?", "Bcode — Hỏi Fast", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) FastAskSessions.DeleteAll();
                _web.Call($"fa.sessions({JsonSerializer.Serialize(FastAskSessions.List())})");
                break;
            case "act":
                var kind = root.GetProperty("kind").GetString();
                var arg = root.GetProperty("arg").GetString() ?? "";
                var arg2 = root.TryGetProperty("arg2", out var a2) ? a2.GetString() ?? "" : "";
                switch (kind)
                {
                    case "insert": InsertRequested?.Invoke(arg, false); break;
                    case "newtab": InsertRequested?.Invoke(arg, true); break;
                    case "copy": try { Clipboard.SetText(arg); } catch { /* clipboard bận */ } break;
                    case "open-object": OpenObjectRequested?.Invoke(arg, arg2.StartsWith("sys"), arg2.Contains('|') ? arg2[(arg2.IndexOf('|') + 1)..] : "P"); break;
                    case "quicklist": QuickListRequested?.Invoke(arg, arg2); break;
                    case "ask": _web.Call($"fa.ask({WebBarHost.Json(arg)}, {WebBarHost.Json(arg2)})"); break;   // arg2 = chữ hiện ở bong bóng người hỏi
                }
                break;
        }
    }

    /// <summary>"Học dự án này": tiến độ hiện ngay trong chat, xong thì trả bản tóm tắt.</summary>
    private async Task LearnAsync(int id, bool sys)
    {
        void Progress(string s) { if (IsDisposed) return; try { BeginInvoke(() => _web.Call($"fa.progress({id}, {WebBarHost.Json(s)})")); } catch { /* đang đóng */ } }
        try
        {
            var summary = await _service.LearnProjectAsync(sys, Progress);
            var reply = new FastAskService.AskReply(summary, Array.Empty<FastAskService.AskCard>(), Array.Empty<FastAskService.AskChoice>(), new Dictionary<string, string>());
            if (!IsDisposed) _web.Call($"fa.reply({id}, {JsonSerializer.Serialize(reply, Json)})");
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _web.Call($"fa.fail({id}, {WebBarHost.Json(ex.Message)})");
        }
    }

    private async Task AskAsync(int id, string text, Dictionary<string, string> state, bool sys)
    {
        try
        {
            var reply = await _service.AskAsync(text, state, sys);
            if (!IsDisposed) _web.Call($"fa.reply({id}, {JsonSerializer.Serialize(reply, Json)})");
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _web.Call($"fa.fail({id}, {WebBarHost.Json(ex.Message)})");
        }
    }
}
