using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>Hỏi giá trị tham số của store/function trước khi debug từng bước — mỗi tham số 1 ô, nhập đúng dạng T-SQL
/// ('abc', 5, NULL, '2026-01-31'...). Ô điền sẵn: giá trị truyền trong câu EXEC (nếu có), không thì DEFAULT của tham số, không thì NULL.
/// Giao diện là trang WebView2 (Web/Shell/stepparams.html).</summary>
public sealed class StepParamsForm : WebDialogForm
{
    private readonly string _routine;
    private readonly List<StepParam> _params;
    private readonly Dictionary<string, string> _initial;
    private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public StepParamsForm(string routine, IReadOnlyList<StepParam> parameters, IReadOnlyDictionary<string, string> initial)
        : base("Debug từng bước — tham số", "stepparams.html", 640, Math.Min(260 + parameters.Count * 44, 760), 460, 300)
    {
        _routine = routine;
        _params = parameters.Where(p => !p.ReadOnly).ToList();
        _initial = new Dictionary<string, string>(initial, StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, string> Values => _values;

    protected override void OnReady() =>
        Js($"stepParams.init({J(new
        {
            routine = _routine,
            parameters = _params.Select(p => new
            {
                name = p.Name, type = p.Type,
                value = _initial.TryGetValue(p.Name, out var v) ? v : (p.Default.Length > 0 ? p.Default : "NULL"),
            }),
        })})");

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action == "ok")
        {
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in msg.GetProperty("values").EnumerateObject()) _values[p.Name] = (p.Value.GetString() ?? "").Trim();
            CloseWith(DialogResult.OK);
        }
        return Task.CompletedTask;
    }

    /// <summary>Đọc tham số từ 1 câu gọi "EXEC proc @a = 1, 'x', @c = NULL": theo tên nếu có "@tên =", còn lại theo thứ tự vị trí.</summary>
    public static Dictionary<string, string> ParseCallArgs(string? callText, IReadOnlyList<StepParam> parameters)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(callText)) return result;

        var m = System.Text.RegularExpressions.Regex.Match(callText, @"\bEXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?[\w\.\[\]#$]+(?<args>.*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) return result;
        var args = m.Groups["args"].Value.Trim().TrimEnd(';');
        if (args.Length == 0) return result;

        // Tách theo dấu phẩy ngoài chuỗi/ngoặc.
        var parts = new List<string>();
        var depth = 0; var inStr = false; var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '\'') inStr = !inStr;
            else if (!inStr && c == '(') depth++;
            else if (!inStr && c == ')') depth--;
            else if (!inStr && depth == 0 && c == ',') { parts.Add(args[start..i]); start = i + 1; }
        }
        parts.Add(args[start..]);

        var positional = 0;
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            var named = System.Text.RegularExpressions.Regex.Match(part, @"^(@[\w#$]+)\s*=\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (named.Success) { result[named.Groups[1].Value] = named.Groups[2].Value.Trim(); continue; }
            if (positional < parameters.Count) result[parameters[positional].Name] = part;
            positional++;
        }
        return result;
    }
}
