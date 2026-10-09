using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.UI;

/// <summary>Một trang web của chương trình (Web/Shell/*.html) trong danh sách "Tuỳ chỉnh HTML &amp; CSS".</summary>
public sealed record UiPageInfo(string Page, string Title, bool HasCss, bool HasHtml, bool Quarantined, bool Stale, bool HasPatches = false);

/// <summary>
/// Lớp tuỳ chỉnh giao diện web của chương trình mà KHÔNG đụng tới xử lý (backend):
///   • CSS riêng — <c>%AppData%\Bcode\ui\global.css</c> (mọi trang) và <c>&lt;trang&gt;.css</c> — đẩy vào trang bằng thẻ &lt;style id="bcode-user-css"&gt;,
///     cập nhật ngay trên các trang đang mở (xem <see cref="UiTemplate.BuildScript"/>);
///   • Ghi đè cả trang HTML — <c>%AppData%\Bcode\ui\&lt;trang&gt;.html</c> (xuất từ bản gốc kèm &lt;base href&gt; để vẫn nạp được shell.css và các script gốc).
///     Trang được phục vụ từ host riêng <see cref="UserHost"/> thay vì <see cref="WebViewEnvironment.Host"/>, xem <see cref="UrlFor"/>.
///
/// "Hợp đồng" với backend: mọi <c>id</c> có trong bản gốc phải còn trong bản ghi đè (script gốc dùng chúng để nối với C#) — bản thiếu id bị bỏ qua
/// và dùng bản gốc. Bản ghi đè báo lỗi script lúc nạp thì bị đưa vào "cách ly" (tự quay về bản gốc). Trang <c>uitemplate.html</c> (chính màn hình
/// tuỳ chỉnh) không bao giờ bị ghi đè nên luôn có đường quay lại. Tắt hẳn mọi tuỳ chỉnh: <see cref="UiTemplate.CustomUiEnabled"/> (menu Actions) hoặc
/// giữ Shift lúc mở chương trình (chế độ an toàn cho phiên đó).
/// </summary>
public static class UiOverrides
{
    /// <summary>Host ảo của các trang đã ghi đè (ánh xạ tới <see cref="Folder"/>).</summary>
    public const string UserHost = "bcode.user";

    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase) { "uitemplate.html" };

    /// <summary>Trang web không đi qua host chung (tự ánh xạ riêng) hoặc không nên tuỳ chỉnh — không hiện trong danh sách.</summary>
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "apideclaration.html", "apischema.html", "contextmenu.html" };

    public static string Folder => Path.Combine(BcodePaths.AppData, "Bcode", "ui");
    private static string StatePath => Path.Combine(Folder, "state.json");

    /// <summary>Giữ Shift lúc mở chương trình: tắt mọi tuỳ chỉnh trong phiên này (không ghi vào cấu hình).</summary>
    public static bool SessionSafe { get; set; }

    public static bool Enabled => UiTemplate.Current.CustomUiEnabled && !SessionSafe;

    /// <summary>Trang <paramref name="page"/> ("*" = tất cả) vừa đổi tuỳ chỉnh HTML / trạng thái bật-tắt: các WebView2 đang hiển thị trang đó nạp lại.</summary>
    public static event Action<string>? PageChanged;
    /// <summary>CSS tuỳ chỉnh vừa đổi: đẩy lại vào các trang đang mở (không cần nạp lại).</summary>
    public static event Action? CssChanged;
    /// <summary>Thông báo cho người dùng (vd bản ghi đè bị cách ly vì lỗi).</summary>
    public static event Action<string>? Notice;

    public static void RaisePageChanged(string page) => PageChanged?.Invoke(page);

    // ------------------------------------------------------------------ trạng thái (cách ly, mã băm bản gốc)

    private sealed class State
    {
        public List<string> Quarantined { get; set; } = new();
        public Dictionary<string, string> Bases { get; set; } = new();
    }

    private static State _state = LoadState();

    private static State LoadState()
    {
        try { if (File.Exists(StatePath)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State(); }
        catch { /* file hỏng — bắt đầu lại */ }
        return new State();
    }

    private static void SaveState()
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* không ghi được thì lần sau mất trạng thái cách ly — chấp nhận */ }
    }

    // ------------------------------------------------------------------ đường dẫn / danh sách trang

    private static string OriginalPath(string page) => Path.Combine(WebViewEnvironment.WebFolder, page);
    private static string HtmlPath(string page) => Path.Combine(Folder, page);
    private static string CssPath(string page) => Path.Combine(Folder, Path.ChangeExtension(page, ".css"));
    private static string PatchPath(string page) => Path.Combine(Folder, Path.ChangeExtension(page, ".patch.json"));
    private static string GlobalCssPath => Path.Combine(Folder, "global.css");

    /// <summary>Tên file trang hợp lệ (không chứa đường dẫn) và có trong Web/Shell.</summary>
    public static bool IsKnownPage(string page) =>
        !string.IsNullOrWhiteSpace(page) && page == Path.GetFileName(page) && page.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && File.Exists(OriginalPath(page));

    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["topbar.html"] = "Thanh trên (Script, Workspace)", ["iconrail.html"] = "Cột biểu tượng bên trái", ["statusbar.html"] = "Thanh trạng thái",
        ["sqlquerybar.html"] = "Thanh Execute (SQL Query)", ["sqleditor.html"] = "Editor SQL", ["resultview.html"] = "Kết quả SQL (lưới)",
        ["genupdate.html"] = "Gen Update", ["cfsdiag.html"] = "Check LCTT / CĐKT", ["bbxn.html"] = "Biên bản xác nhận (Word)", ["advnote.html"] = "Note (New)", ["checkmail.html"] = "Check Mail", ["decryptsql.html"] = "Decrypt SQL Object", ["projects.html"] = "Chọn project", ["uiscale.html"] = "Tỉ lệ giao diện", ["stepparams.html"] = "Debug từng bước — tham số", ["wcommandduplicate.html"] = "Duplicate Menu", ["comparestructure.html"] = "Compare Structure", ["catalogclone.html"] = "Clone danh mục", ["copysourcestandard.html"] = "Copy source standard", ["note.html"] = "Note", ["lookup.html"] = "Lookup", ["commandquery.html"] = "Command (SELECT / FROM / WHERE)", ["wcommandbar.html"] = "Thanh lọc cây WCommand",
        ["sqlobjectbar.html"] = "Thanh lọc cây SQL Object", ["filelookupbar.html"] = "File Lookup — thanh trên", ["filelookuppreview.html"] = "File Lookup — xem trước",
        ["filereference.html"] = "File Reference", ["comparebar.html"] = "Compare Text — thanh trên", ["connections.html"] = "Chọn máy chủ / kết nối",
        ["editproject.html"] = "Sửa project", ["quickaccess.html"] = "Quick Access", ["library.html"] = "Library (Script đã lưu)", ["tablebar.html"] = "Thanh Table",
        ["changeowner.html"] = "Change Owner", ["addsource.html"] = "Thêm nguồn", ["aihistory.html"] = "Lịch sử gợi ý AI", ["sqlprofilerbar.html"] = "Thanh SQL Profiler",
        ["findbar.html"] = "Thanh tìm kiếm", ["searchbox.html"] = "Search Box", ["scriptviewbar.html"] = "Thanh View Script", ["wcommandedit.html"] = "Sửa dòng WCommand",
        ["actionbar.html"] = "Thanh nút hộp thoại", ["filepreview.html"] = "Xem trước file (Monaco)",
    };

    public static List<UiPageInfo> ListPages()
    {
        var result = new List<UiPageInfo>();
        if (!Directory.Exists(WebViewEnvironment.WebFolder)) return result;
        foreach (var file in Directory.EnumerateFiles(WebViewEnvironment.WebFolder, "*.html").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var page = Path.GetFileName(file);
            if (Protected.Contains(page) || Hidden.Contains(page) || page.StartsWith('_')) continue;
            var hasHtml = File.Exists(HtmlPath(page));
            var stale = hasHtml && _state.Bases.TryGetValue(page, out var b) && b != HashOf(File.ReadAllText(file));
            result.Add(new UiPageInfo(page, Titles.TryGetValue(page, out var t) ? t : page, File.Exists(CssPath(page)), hasHtml,
                _state.Quarantined.Contains(page, StringComparer.OrdinalIgnoreCase), stale, File.Exists(PatchPath(page))));
        }
        return result.OrderBy(p => Titles.ContainsKey(p.Page) ? 0 : 1).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))))[..16];

    // ------------------------------------------------------------------ nạp trang

    /// <summary>URL để nạp trang <paramref name="page"/>: bản ghi đè (host riêng) nếu đang dùng được, không thì bản gốc. Thay cho việc ghép
    /// <c>https://{Host}/{page}</c> ở từng chỗ gọi Navigate.</summary>
    public static string UrlFor(string page)
        => $"https://{(UseOverride(page) ? UserHost : WebViewEnvironment.Host)}/{page}";

    private static bool UseOverride(string page)
    {
        if (!Enabled || Protected.Contains(page)) return false;
        if (_state.Quarantined.Contains(page, StringComparer.OrdinalIgnoreCase)) return false;
        var path = HtmlPath(page);
        if (!File.Exists(path) || !File.Exists(OriginalPath(page))) return false;
        try { return Validate(page, File.ReadAllText(path)).Count == 0; }
        catch { return false; }
    }

    private static readonly Regex IdRegex = new(@"\sid\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Các vấn đề của bản HTML ghi đè so với "hợp đồng" của trang gốc (rỗng = ổn): thiếu id nào, thiếu thẻ base.</summary>
    public static List<string> Validate(string page, string html)
    {
        var problems = new List<string>();
        if (!IsKnownPage(page)) { problems.Add("Không có trang gốc này."); return problems; }
        var required = IdRegex.Matches(File.ReadAllText(OriginalPath(page))).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var present = IdRegex.Matches(html).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(id => !present.Contains(id)).OrderBy(id => id).ToList();
        if (missing.Count > 0)
            problems.Add("Thiếu phần tử có id mà xử lý của chương trình cần: " + string.Join(", ", missing.Take(20)) + (missing.Count > 20 ? $" … (+{missing.Count - 20})" : "")
                         + ". Có thể ẩn bằng CSS nhưng phải giữ lại trong HTML.");
        if (!Regex.IsMatch(html, @"<base\s+[^>]*href\s*=\s*[""']https://" + Regex.Escape(WebViewEnvironment.Host) + "/?[\"']", RegexOptions.IgnoreCase))
            problems.Add($"Thiếu thẻ <base href=\"https://{WebViewEnvironment.Host}/\"> trong <head> — thiếu nó thì trang không nạp được shell.css và các tệp đi kèm.");
        return problems;
    }

    /// <summary>Đã có lỗi script khi nạp bản ghi đè → đưa vào "cách ly" (tự dùng lại bản gốc) và báo.</summary>
    public static void Quarantine(string page, string message)
    {
        if (!IsKnownPage(page) || Protected.Contains(page)) return;
        if (!_state.Quarantined.Contains(page, StringComparer.OrdinalIgnoreCase)) { _state.Quarantined.Add(page); SaveState(); }
        Notice?.Invoke($"Trang \"{page}\" bị lỗi script khi dùng bản tuỳ chỉnh ({message}) — đã quay về bản gốc. Sửa lại bản tuỳ chỉnh rồi lưu để thử lại.");
        PageChanged?.Invoke(page);
    }

    // ------------------------------------------------------------------ CSS

    public static string GlobalCss => ReadOrEmpty(GlobalCssPath);
    public static string PageCss(string page) => ReadOrEmpty(CssPath(page));

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; }
    }

    /// <summary>Bản đồ trang → CSS ("*" = chung) đẩy vào mọi trang; rỗng khi tuỳ chỉnh đang tắt.</summary>
    public static Dictionary<string, string> AllCss()
    {
        var map = new Dictionary<string, string>();
        if (!Enabled || !Directory.Exists(Folder)) return map;
        var g = GlobalCss;
        if (g.Trim().Length > 0) map["*"] = g;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.css"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Equals("global", StringComparison.OrdinalIgnoreCase)) continue;
            var css = ReadOrEmpty(file);
            if (css.Trim().Length > 0) map[name + ".html"] = css;
        }
        return map;
    }

    /// <summary>Lưu CSS (page = "*" cho CSS chung). Rỗng thì xoá file. Đẩy ngay vào các trang đang mở.</summary>
    public static string? SaveCss(string page, string css)
    {
        if (page != "*" && !IsKnownPage(page)) return "Không có trang này.";
        try
        {
            Directory.CreateDirectory(Folder);
            var path = page == "*" ? GlobalCssPath : CssPath(page);
            if (string.IsNullOrWhiteSpace(css)) { if (File.Exists(path)) File.Delete(path); }
            else File.WriteAllText(path, css, new UTF8Encoding(false));
            CssChanged?.Invoke();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private const string DesignerBegin = "/* bcode-designer:start */";
    private const string DesignerEnd = "/* bcode-designer:end */";

    /// <summary>Công cụ thiết kế trực quan lưu các quy tắc vào 1 khối có đánh dấu trong CSS của trang — phần CSS tự viết bên ngoài khối giữ nguyên.</summary>
    public static string? SaveDesignerBlock(string page, string block)
    {
        if (!IsKnownPage(page) || Protected.Contains(page)) return "Không có trang này.";
        var css = PageCss(page);
        var re = new Regex(Regex.Escape(DesignerBegin) + @".*?" + Regex.Escape(DesignerEnd), RegexOptions.Singleline);
        var merged = re.IsMatch(css) ? re.Replace(css, _ => block) : (css.TrimEnd() + (css.Trim().Length > 0 ? "\n\n" : "") + block + "\n");
        if (block.Trim() == DesignerBegin + "\n" + DesignerEnd || block.Trim() == DesignerBegin + DesignerEnd)
            merged = re.Replace(css, "").Trim();
        return SaveCss(page, merged);
    }

    // ------------------------------------------------------------------ bản vá cấu trúc (thiết kế trực quan: đổi chữ, thuộc tính, vị trí phần tử)

    /// <summary>
    /// Bản vá cấu trúc của trang — danh sách thao tác khai báo <c>[{op, sel, ...}]</c> lưu ở <c>&lt;trang&gt;.patch.json</c>, áp lên DOM của trang SAU khi nạp
    /// (xem <see cref="PatchApplyScript"/>). Khác với ghi đè cả HTML: không chép cả file nên sống sót qua các lần cập nhật chương trình (thao tác nào không còn
    /// khớp phần tử thì bị bỏ qua), và công cụ thiết kế trực quan sinh ra nó. Các thao tác:
    ///   • text   — {sel, value}: đổi chữ hiển thị của phần tử;
    ///   • attr   — {sel, name, value}: đổi placeholder / title / aria-label / alt / value;
    ///   • move   — {sel, ref, pos: before|after|append}: dời phần tử tới trước/sau/vào trong phần tử tham chiếu.
    /// Ẩn / màu / cỡ chữ... là CSS (khối bcode-designer trong &lt;trang&gt;.css), không nằm ở đây. Xử lý của chương trình tìm phần tử theo id nên dời chỗ không ảnh hưởng.
    /// </summary>
    private static readonly HashSet<string> PatchOps = new() { "text", "attr", "move" };
    private static readonly HashSet<string> PatchAttrs = new(StringComparer.OrdinalIgnoreCase) { "placeholder", "title", "aria-label", "alt", "value" };
    private static readonly HashSet<string> PatchPositions = new() { "before", "after", "append" };
    private const int MaxPatchOps = 300;

    /// <summary>JS áp bản vá của trang hiện tại (window.__bcodePatches[tên trang]) lên DOM, đúng 1 lần mỗi lần nạp (cờ window.__bcodePatchesApplied).
    /// Bộ chọn tìm theo thứ tự thao tác (thao tác sau thấy kết quả của thao tác trước); thao tác không khớp phần tử bị bỏ qua.</summary>
    public const string PatchApplyScript = @"(function(){
  var page=location.pathname.split('/').pop();
  if(window.__bcodePatchesApplied||(window.__bcodeUiProtected||[]).indexOf(page)>=0)return;
  var ops=(window.__bcodePatches||{})[page];
  window.__bcodePatchesApplied=true;
  if(!ops||!ops.length)return;
  var ALLOWED={placeholder:1,title:1,'aria-label':1,alt:1,value:1};
  function q(s){try{var e=document.querySelector(s);return(e&&e!==document.body&&e!==document.documentElement&&!e.closest('#bcode-designer-ui,#bcode-designer-btn'))?e:null;}catch(x){return null;}}
  function setText(el,v){
    for(var i=0;i<el.childNodes.length;i++){var n=el.childNodes[i];if(n.nodeType===3&&n.nodeValue.trim()){n.nodeValue=v;return;}}
    if(!el.children.length){el.textContent=v;return;}
    el.insertBefore(document.createTextNode(v),el.firstChild);
  }
  ops.forEach(function(o){
    var el=q(o.sel);if(!el)return;
    if(o.op==='text')setText(el,String(o.value));
    else if(o.op==='attr'){if(ALLOWED[o.name])el.setAttribute(o.name,String(o.value));}
    else if(o.op==='move'){
      var r=q(o.ref);if(!r||r===el||el.contains(r))return;
      if(o.pos==='before')r.parentNode.insertBefore(el,r);
      else if(o.pos==='after')r.parentNode.insertBefore(el,r.nextSibling);
      else if(o.pos==='append')r.appendChild(el);
    }
  });
})();";

    public static string PagePatches(string page) => ReadOrEmpty(PatchPath(page));

    /// <summary>Bản đồ trang → mảng thao tác (đã kiểm hợp lệ) đẩy vào mọi trang; rỗng khi tuỳ chỉnh đang tắt.</summary>
    public static Dictionary<string, JsonElement> AllPatches()
    {
        var map = new Dictionary<string, JsonElement>();
        if (!Enabled || !Directory.Exists(Folder)) return map;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.patch.json"))
        {
            var page = Path.GetFileName(file)[..^".patch.json".Length] + ".html";
            if (!IsKnownPage(page) || Protected.Contains(page)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (ValidatePatches(doc.RootElement) is null) map[page] = doc.RootElement.Clone();
            }
            catch { /* file hỏng — bỏ qua trang này */ }
        }
        return map;
    }

    /// <summary>Kiểm tra mảng thao tác; trả mô tả lỗi (null = hợp lệ).</summary>
    public static string? ValidatePatches(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return "Bản vá phải là một mảng JSON các thao tác.";
        if (root.GetArrayLength() > MaxPatchOps) return $"Tối đa {MaxPatchOps} thao tác.";
        var i = 0;
        foreach (var op in root.EnumerateArray())
        {
            i++;
            string Field(string n) => op.ValueKind == JsonValueKind.Object && op.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var kind = Field("op");
            if (!PatchOps.Contains(kind)) return $"Thao tác #{i}: loại \"{kind}\" không hợp lệ (chỉ text / attr / move).";
            var sel = Field("sel");
            if (sel.Length == 0 || sel.Length > 400) return $"Thao tác #{i}: thiếu hoặc quá dài bộ chọn (sel).";
            if (kind == "text" && Field("value").Length > 2000) return $"Thao tác #{i}: chữ quá dài.";
            if (kind == "attr" && (!PatchAttrs.Contains(Field("name")) || Field("value").Length > 1000)) return $"Thao tác #{i}: thuộc tính không được phép (chỉ placeholder, title, aria-label, alt, value).";
            if (kind == "move" && (Field("ref").Length is 0 or > 400 || !PatchPositions.Contains(Field("pos")))) return $"Thao tác #{i}: thiếu ref hoặc pos (before / after / append).";
        }
        return null;
    }

    /// <summary>Lưu bản vá (mảng JSON). Mảng rỗng thì xoá file. Trang đang mở nạp lại để áp bản vá từ DOM gốc. Trả mô tả lỗi hoặc null.</summary>
    public static string? SavePatches(string page, string json)
    {
        if (!IsKnownPage(page) || Protected.Contains(page)) return "Không có trang này.";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            var err = ValidatePatches(doc.RootElement);
            if (err is not null) return err;
            Directory.CreateDirectory(Folder);
            if (doc.RootElement.GetArrayLength() == 0) { if (File.Exists(PatchPath(page))) File.Delete(PatchPath(page)); }
            else File.WriteAllText(PatchPath(page), JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
            PageChanged?.Invoke(page); // bản vá chạy trên DOM mới nạp → nạp lại trang
            return null;
        }
        catch (JsonException ex) { return "JSON không hợp lệ: " + ex.Message; }
        catch (Exception ex) { return ex.Message; }
    }

    // ------------------------------------------------------------------ HTML ghi đè

    /// <summary>Tạo bản ghi đè từ bản gốc nếu chưa có (thêm &lt;base&gt;), nhớ mã băm bản gốc; trả nội dung hiện tại của bản ghi đè.</summary>
    public static string ExportOverride(string page)
    {
        if (!IsKnownPage(page) || Protected.Contains(page)) throw new InvalidOperationException("Trang này không cho ghi đè.");
        Directory.CreateDirectory(Folder);
        var path = HtmlPath(page);
        if (!File.Exists(path))
        {
            var original = File.ReadAllText(OriginalPath(page));
            File.WriteAllText(path, WithBase(original), new UTF8Encoding(false));
            _state.Bases[page] = HashOf(original);
            SaveState();
        }
        return File.ReadAllText(path);
    }

    private static string WithBase(string html)
    {
        if (Regex.IsMatch(html, @"<base\s", RegexOptions.IgnoreCase)) return html;
        var tag = $"<base href=\"https://{WebViewEnvironment.Host}/\" />";
        var m = Regex.Match(html, @"<head[^>]*>", RegexOptions.IgnoreCase);
        return m.Success ? html.Insert(m.Index + m.Length, "\n" + tag) : tag + "\n" + html;
    }

    /// <summary>Lưu HTML ghi đè nếu đạt "hợp đồng"; trả danh sách vấn đề (rỗng = đã lưu).</summary>
    public static List<string> SaveHtml(string page, string html)
    {
        if (!IsKnownPage(page) || Protected.Contains(page)) return new List<string> { "Trang này không cho ghi đè." };
        var problems = Validate(page, html);
        if (problems.Count > 0) return problems;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(HtmlPath(page), html, new UTF8Encoding(false));
        if (!_state.Bases.ContainsKey(page)) _state.Bases[page] = HashOf(File.ReadAllText(OriginalPath(page)));
        _state.Quarantined.RemoveAll(p => p.Equals(page, StringComparison.OrdinalIgnoreCase)); // lưu lại = cho thử lại
        SaveState();
        PageChanged?.Invoke(page);
        return problems;
    }

    /// <summary>Về mặc định: xoá bản HTML ghi đè (và/hoặc CSS riêng) của trang.</summary>
    public static void Reset(string page, bool html, bool css, bool patches = false)
    {
        if (!IsKnownPage(page)) return;
        try
        {
            if (html && File.Exists(HtmlPath(page))) File.Delete(HtmlPath(page));
            if (html) { _state.Bases.Remove(page); _state.Quarantined.RemoveAll(p => p.Equals(page, StringComparison.OrdinalIgnoreCase)); SaveState(); }
            if (css && File.Exists(CssPath(page))) File.Delete(CssPath(page));
            if (patches && File.Exists(PatchPath(page))) File.Delete(PatchPath(page));
        }
        catch { /* file đang bị khoá — lần sau thử lại */ }
        if (html || patches) PageChanged?.Invoke(page);
        if (css) CssChanged?.Invoke();
    }

    /// <summary>Bản gốc đã đổi (cập nhật chương trình) → xuất bản gốc mới ra "&lt;trang&gt;.new.html" cạnh bản ghi đè để so sánh (Compare Text) và hợp nhất tay.</summary>
    public static string ExportNewOriginal(string page)
    {
        if (!IsKnownPage(page)) throw new InvalidOperationException("Không có trang này.");
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, Path.ChangeExtension(page, ".new.html"));
        File.WriteAllText(path, WithBase(File.ReadAllText(OriginalPath(page))), new UTF8Encoding(false));
        _state.Bases[page] = HashOf(File.ReadAllText(OriginalPath(page))); // đã biết bản gốc mới — hết cảnh báo
        SaveState();
        return path;
    }

    // ------------------------------------------------------------------ tin nhắn từ trang

    /// <summary>Xử lý thông điệp <c>__ui-*</c> do trang gửi (lỗi script của bản ghi đè, lưu quy tắc từ công cụ thiết kế). Trả true nếu đã xử lý.</summary>
    public static bool HandleMessage(string action, JsonElement root)
    {
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        switch (action)
        {
            case "__ui-error": Quarantine(Str("page"), Str("message")); return true;
            case "__ui-designer-save":
                if (Enabled) SaveDesignerBlock(Str("page"), Str("block"));
                return true;
            case "__ui-patches-save":
                if (Enabled) SavePatches(Str("page"), root.TryGetProperty("ops", out var ops) ? ops.GetRawText() : "[]");
                return true;
        }
        return false;
    }
}
