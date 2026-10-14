using System.Text.Json;
using Jint;

namespace Bcode.App.Services;

/// <summary>Một gợi ý do engine trả về. <see cref="Start"/>/<see cref="End"/> = khoảng văn bản (ký tự, theo bản đã đổi xuống dòng thành \n) mà gợi ý thay thế.</summary>
public sealed record HintItem(string Label, int Kind, string Detail, string Doc, string Insert, bool Snippet, int Start, int End, string Sort, string Filter);

/// <summary>Trợ giúp chữ ký khi gõ tham số: nhãn đầy đủ, vị trí [start,end) của từng tham số trong nhãn, tham số đang gõ.</summary>
public sealed record HintSignature(string Label, IReadOnlyList<(int Start, int End)> Params, int Active);

/// <summary>Cảnh báo "dùng biến chuẩn mà chưa khai báo": tên, kiểu, vị trí + độ dài (ký tự, văn bản đã chuẩn hoá \n).</summary>
public sealed record HintIssue(string Name, string Type, int Offset, int Length);

/// <summary>
/// Chạy NGUYÊN FILE <c>Web/Shell/sqlhints.js</c> (cùng bộ gợi ý code SQL của editor Monaco ở chế độ Web) bằng Jint — trình thông dịch JavaScript thuần .NET, không cần
/// WebView2 — nên chế độ App có đúng các gợi ý: mẫu gõ-tắt, tên procedure/function kèm tham số, cột sau alias, mở rộng cột có tiền tố, tên trong options, mẫu thông minh
/// (part/sel/ins/cur/sysrep/bil + bảng), trợ giúp chữ ký, cảnh báo biến chưa khai báo. Sửa gợi ý ở sqlhints.js / SqlHintCatalog là cả hai chế độ cùng đổi.
/// Một engine dùng chung cả tiến trình, nạp lười lần đầu (~90ms); mọi lệnh gọi tuần tự (khoá) và nên chạy ở luồng nền.
/// "Monaco" ở đây chỉ là lớp vỏ giả (<see cref="Shim"/>): gom các provider rồi gọi chúng với 1 "model" dựng từ chuỗi văn bản.
/// </summary>
public sealed class SqlHintEngine
{
    public static SqlHintEngine Shared { get; } = new();

    private readonly object _gate = new();
    private Engine? _engine;
    private string _routinesKey = "";
    private string _userJson = "";
    private Func<string, string>? _columns;

    public bool Available { get; private set; } = true;

    /// <summary>Văn bản dùng khi hỏi engine: đổi \r\n thành \n (engine tính vị trí theo \n).</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n");

    private Engine Ensure()
    {
        if (_engine is not null) return _engine;
        var e = new Engine(o => o.Strict(false));
        e.SetValue("__cols", new Func<string, string>(t => { try { return _columns?.Invoke(t) ?? "[]"; } catch { return "[]"; } }));
        e.Execute("globalThis.window = globalThis;");
        var js = Path.Combine(AppContext.BaseDirectory, "Web", "Shell", "sqlhints.js");
        e.Execute(File.ReadAllText(js));
        e.Execute(Shim);
        e.Execute("BcodeHints.setCatalog(" + JsonSerializer.Serialize(SqlHintCatalog.ToEditorPayload()) + ");");
        _engine = e;
        return e;
    }

    /// <summary>Gắn dữ liệu theo database/workspace: danh sách procedure/function + options (JSON của <see cref="SqlHintService"/>), tên bảng, snippet Library (đã lọc dự án).
    /// Đổi <paramref name="key"/> (workspace + Sys/App) thì bộ nhớ đệm cột bị xoá.</summary>
    private void Prepare(string key, string routinesJson, IReadOnlyList<string> dbTables, string userJson, Func<string, string> columns)
    {
        var e = Ensure();
        _columns = columns;
        if (key != _routinesKey)
        {
            _routinesKey = key;
            e.Execute("BcodeHints.cols = {}; BcodeHints.setRoutines(" + (string.IsNullOrEmpty(routinesJson) ? "{r:[],o:[]}" : routinesJson) + ");");
        }
        e.Execute("window._dbTables = " + JsonSerializer.Serialize(dbTables.Select(n => new { name = n })) + ";");
        if (userJson != _userJson) { _userJson = userJson; e.Execute("BcodeHints.user = " + (userJson.Length == 0 ? "[]" : userJson) + ";"); }
    }

    /// <summary>Hỏi tất cả provider tại vị trí <paramref name="offset"/> (ký tự) của <paramref name="text"/> (đã Normalize). Chạy đồng bộ — gọi trong Task.Run.</summary>
    public List<HintItem> Complete(string text, int offset, string key, string routinesJson, IReadOnlyList<string> dbTables, string userJson, Func<string, string> columns)
    {
        lock (_gate)
        {
            try
            {
                Prepare(key, routinesJson, dbTables, userJson, columns);
                var json = _engine!.Evaluate($"__complete({JsonSerializer.Serialize(text)}, {offset})").UnwrapIfPromise().ToString();
                var list = new List<HintItem>();
                using var doc = JsonDocument.Parse(json);
                foreach (var x in doc.RootElement.EnumerateArray())
                    list.Add(new HintItem(S(x, "label"), x.GetProperty("kind").GetInt32(), S(x, "detail"), S(x, "doc"), S(x, "insert"), x.GetProperty("snippet").GetBoolean(),
                        x.GetProperty("start").GetInt32(), x.GetProperty("end").GetInt32(), S(x, "sort"), S(x, "filter")));
                return list;
            }
            catch (Exception ex) { Log("complete: " + ex.Message); return new(); }
        }
    }

    public HintSignature? Signature(string text, int offset, string key, string routinesJson)
    {
        lock (_gate)
        {
            try
            {
                Prepare(key, routinesJson, Array.Empty<string>(), _userJson, _columns ?? (_ => "[]"));
                var r = _engine!.Evaluate($"__signature({JsonSerializer.Serialize(text)}, {offset})");
                if (r.IsNull() || r.IsUndefined()) return null;
                using var doc = JsonDocument.Parse(r.ToString());
                var root = doc.RootElement;
                var ps = root.GetProperty("params").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())).ToList();
                return new HintSignature(S(root, "label"), ps, root.GetProperty("active").GetInt32());
            }
            catch (Exception ex) { Log("signature: " + ex.Message); return null; }
        }
    }

    public List<HintIssue> Lint(string text)
    {
        lock (_gate)
        {
            try
            {
                var json = Ensure().Evaluate($"__lint({JsonSerializer.Serialize(text)})").ToString();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.EnumerateArray().Select(x => new HintIssue(S(x, "name"), S(x, "type"), x.GetProperty("offset").GetInt32(), x.GetProperty("length").GetInt32())).ToList();
            }
            catch (Exception ex) { Log("lint: " + ex.Message); return new(); }
        }
    }

    private static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static void Log(string text)
    {
        try
        {
            var path = Path.Combine(BcodePaths.AppData, "Bcode", "hint-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} [native] {text}{Environment.NewLine}");
        }
        catch { /* log chỉ để chẩn đoán */ }
    }

    /// <summary>Lớp vỏ "Monaco" giả: gom các provider mà sqlhints.js đăng ký, dựng "model" từ chuỗi + vị trí, đổi khoảng thay thế (dòng/cột) sang vị trí ký tự. setTimeout là hàm rỗng
    /// (cột được trả lời ngay trong cùng lệnh gọi nên không cần chờ).</summary>
    private const string Shim = """
globalThis.window = globalThis;
var __providers = [], __sig = null;
var monaco = {
  languages: {
    CompletionItemKind: { Method: 0, Function: 1, Field: 3, Constant: 14, Snippet: 27 },
    CompletionItemInsertTextRule: { InsertAsSnippet: 4 },
    registerCompletionItemProvider: function (sel, p) { __providers.push({ sel: sel, p: p }); },
    registerSignatureHelpProvider: function (sel, p) { __sig = p; },
    registerCodeActionProvider: function () {}
  },
  editor: { setModelMarkers: function () {} },
  MarkerSeverity: { Warning: 4 }
};
var editor = { getModel: function () { return null; }, onDidChangeModelContent: function () {}, onDidFocusEditorText: function () {}, onDidType: function () {}, getContribution: function () { return null; }, trigger: function () {} };
function setTimeout(fn, ms) { return 0; }
function clearTimeout() {}
BcodeHints.install(monaco, editor, function (msg) {
  if (msg && msg.action === 'hint-columns') {
    var r = __cols(msg.table);
    BcodeHints.setColumns(msg.reqId, msg.table, JSON.parse(r));
  }
});
function __mk(text, offset) {
  var lines = text.split('\n'), starts = [], acc = 0, i;
  for (i = 0; i < lines.length; i++) { starts.push(acc); acc += lines[i].length + 1; }
  var ln = 0; for (i = 0; i < lines.length; i++) if (starts[i] <= offset) ln = i;
  var pos = { lineNumber: ln + 1, column: offset - starts[ln] + 1 };
  var model = {
    getValue: function () { return text; },
    getOffsetAt: function (p) { return starts[p.lineNumber - 1] + p.column - 1; },
    getPositionAt: function (o) { var l = 0; for (var k = 0; k < lines.length; k++) if (starts[k] <= o) l = k; return { lineNumber: l + 1, column: o - starts[l] + 1 }; },
    getLineContent: function (n) { return lines[n - 1] || ''; }
  };
  return { model: model, pos: pos, off: function (lineNumber, column) { return starts[lineNumber - 1] + column - 1; } };
}
function __complete(text, offset) {
  var c = __mk(text, offset), ps = [];
  __providers.forEach(function (x) {
    try { ps.push(Promise.resolve(x.p.provideCompletionItems(c.model, c.pos)).catch(function () { return null; })); } catch (e) { ps.push(Promise.resolve(null)); }
  });
  return Promise.all(ps).then(function (all) {
    var out = [];
    all.forEach(function (r) {
      var list = r && r.suggestions || [];
      list.forEach(function (s) {
        var rg = s.range || {};
        out.push({ label: s.label, kind: s.kind, detail: s.detail || '', doc: s.documentation && s.documentation.value || '', insert: s.insertText, snippet: !!s.insertTextRules,
          start: c.off(rg.startLineNumber || c.pos.lineNumber, rg.startColumn || c.pos.column), end: c.off(rg.endLineNumber || c.pos.lineNumber, rg.endColumn || c.pos.column),
          sort: s.sortText || s.label, filter: s.filterText || s.label });
      });
    });
    return JSON.stringify(out);
  });
}
function __signature(text, offset) {
  if (!__sig) return null;
  var c = __mk(text, offset);
  var r = __sig.provideSignatureHelp(c.model, c.pos);
  if (!r) return null;
  var v = r.value, sg = v.signatures[0];
  return JSON.stringify({ label: sg.label, params: sg.parameters.map(function (p) { return p.label; }), active: v.activeParameter });
}
function __lint(text) { return JSON.stringify(BcodeHints.analyze(text).map(function (i) { return { name: i.name, type: i.type, offset: i.offset, length: i.length }; })); }
""";
}
