using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Biên bản xác nhận (bbxn.html): khai báo khách hàng / hợp đồng / pháp nhân Fast → chọn mẫu → danh sách biên bản + chi tiết → xem trước → xuất Word.</summary>
internal sealed class BbxnPage : NativePage
{
    private static readonly string[] Bound =
    {
        "customer.ten", "customer.tenNgan", "customer.diaChi", "customer.tenEn", "customer.diaChiEn", "customer.daiDienEn", "customer.chucVuEn", "customer.dienThoai", "customer.fax", "customer.daiDien", "customer.chucVu", "customer.mst",
        "contract.loai", "contract.loaiEn", "contract.so", "contract.ngay", "maDa", "ngayLap", "soFormat",
        "fast.ten", "fast.diaChi", "fast.tenEn", "fast.diaChiEn", "fast.daiDienEn", "fast.chucVuEn", "fast.diaDiemEn", "fast.dienThoai", "fast.fax", "fast.daiDien", "fast.chucVu", "fast.mst", "fast.diaDiem", "fast.tenNgan",
    };

    private readonly Dictionary<string, RoundedInput> _b = new();
    private readonly DropDownButton _custSel = new(), _entSel = new(), _eTpl = new(), _eLogo = new();
    private readonly Label _entWarn = new() { AutoSize = true, ForeColor = AppColors.Warning, BackColor = Color.Transparent, Visible = false }, _status = NK.Muted(""), _qCount = NK.Muted(""), _pvNo = NK.Muted("");
    private readonly RoundedInput _tplFilter = NK.Input("Tìm mẫu theo tên / viết tắt…"), _eHau = NK.Input(""), _eSo = NK.Input(""), _eNgay = NK.Input("yyyy-MM-dd"), _eRows = NK.Memo(6, "");
    private readonly TreeView _tplTree = new() { Dock = DockStyle.Top, CheckBoxes = true, HideSelection = false, Height = NativeSkin.S(230), ShowNodeToolTips = true };
    private readonly ListBox _queue = new() { Dock = DockStyle.Top, Height = NativeSkin.S(150), IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly Panel _editor = new() { Dock = DockStyle.Top, AutoSize = true, Visible = false };
    private readonly FlowLayoutPanel _eVars = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Label _eRowsLbl = NK.Muted("", true), _emptyHint = NK.Muted("Tick mẫu ở trên là hiện ngay xem trước — tick nhiều mẫu để tạo nhiều biên bản cùng lúc.", true);
    private readonly FlowLayoutPanel _pvTabs = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent };
    private readonly RichTextBox _paper = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Color.Black };
    private readonly System.Windows.Forms.Timer _saveT = new() { Interval = 500 }, _pvT = new() { Interval = 200 };
    private JsonObject _settings = new();
    private List<JsonObject> _templates = new();
    private List<JsonObject> _docs = new();
    private List<string> _numbers = new();
    private string _ngayLap = "";
    private int _sel = -1;
    private bool _loading;

    public BbxnPage()
    {
        foreach (var k in Bound) { var i = NK.Input(k.EndsWith("ngay") || k == "ngayLap" ? "yyyy-MM-dd" : ""); _b[k] = i; var key = k; i.TextChanged += (_, _) => { if (_loading) return; SetP(key, i.Text); if (key.StartsWith("fast.")) RenderEntities(); _saveT.Stop(); _saveT.Start(); SchedulePreview(); }; }
        _saveT.Tick += (_, _) => { _saveT.Stop(); Post(new { action = "saveSettings", settings = _settings.DeepClone() }); };
        _pvT.Tick += (_, _) => { _pvT.Stop(); Post(new { action = "preview", state = BuildState(), index = _sel }); };
        Disposed += (_, _) => { _saveT.Dispose(); _pvT.Dispose(); };

        // ---- cột trái ----
        var cCust = new CardPanel("Khách hàng (Bên A)");
        var custBar = NK.Row(_custSel, NK.Button("Lưu KH", SaveCustomer, tip: "Lưu khách hàng này vào danh sách để dùng lại"), NK.Button("✕", DelCustomer, tip: "Xoá khỏi danh sách đã lưu"));
        _custSel.Width = NativeSkin.S(240);
        _custSel.SelectionChanged += i => { if (_loading || i <= 0) return; var list = Customers; if (i - 1 < list.Count) { _settings["customer"] = list[i - 1]!.DeepClone(); FillBound(); _saveT.Stop(); _saveT.Start(); SchedulePreview(); } };
        var enCust = new CardPanel("Tiếng Anh (cho mẫu song ngữ)");
        enCust.Body.Controls.Add(NK.FormGrid(140, ("Company name", _b["customer.tenEn"]), ("Address", _b["customer.diaChiEn"]), ("Represented by", _b["customer.daiDienEn"]), ("Position", _b["customer.chucVuEn"])));
        NK.Stack(cCust.Body, custBar, NK.FormGrid(140, ("Tên công ty (viết hoa)", _b["customer.ten"]), ("Tên gọi trong câu “giữa … và …”", _b["customer.tenNgan"]), ("Địa chỉ", _b["customer.diaChi"]), ("Điện thoại", _b["customer.dienThoai"]), ("Fax", _b["customer.fax"]), ("Đại diện (kèm Ông/Bà)", _b["customer.daiDien"]), ("Chức vụ", _b["customer.chucVu"]), ("Mã số thuế", _b["customer.mst"])), enCust);
        var cCon = new CardPanel("Hợp đồng — các câu “Căn cứ…” tự điền theo đây");
        cCon.Body.Controls.Add(NK.FormGrid(140, ("Loại", _b["contract.loai"]), ("Loại (tiếng Anh)", _b["contract.loaiEn"]), ("Số", _b["contract.so"]), ("Ngày ký", _b["contract.ngay"])));
        var cNo = new CardPanel("Số biên bản & ngày lập");
        NK.Stack(cNo.Body, NK.FormGrid(140, ("Mã dự án", _b["maDa"]), ("Ngày lập (trống = chấm …)", _b["ngayLap"]), ("Cách tạo số", _b["soFormat"])), NK.Muted("{ma_da} mã dự án · {abbr} viết tắt của mẫu · {yymm} năm-tháng · {hau_to} hậu tố của từng biên bản.", true));
        var cEnt = new CardPanel("Pháp nhân Fast (Bên B)");
        _entSel.Width = NativeSkin.S(320);
        _entSel.SelectionChanged += OnEntity;
        NK.Stack(cEnt.Body, _entSel, _entWarn);
        var cFast = new CardPanel("Công ty Fast (Bên B) — thông tin của pháp nhân đang chọn, sửa được");
        cFast.Body.Controls.Add(NK.FormGrid(140, ("Tên", _b["fast.ten"]), ("Địa chỉ", _b["fast.diaChi"]), ("Điện thoại", _b["fast.dienThoai"]), ("Fax", _b["fast.fax"]), ("Đại diện", _b["fast.daiDien"]), ("Chức vụ", _b["fast.chucVu"]), ("Mã số thuế", _b["fast.mst"]), ("Nơi lập biên bản", _b["fast.diaDiem"]), ("Tên gọi trong câu “… và …”", _b["fast.tenNgan"]),
            ("Company name (EN)", _b["fast.tenEn"]), ("Address (EN)", _b["fast.diaChiEn"]), ("Represented by (EN)", _b["fast.daiDienEn"]), ("Position (EN)", _b["fast.chucVuEn"]), ("Nơi lập (EN)", _b["fast.diaDiemEn"])));
        var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
        NK.Stack(left, cCust, cCon, cNo, cEnt, cFast);

        // ---- cột giữa ----
        var cTpl = new CardPanel("Chọn mẫu để tạo");
        cTpl.RightBar.Controls.Add(NK.Button("Import…", () => Post(new { action = "importTpl" }), tip: "Import mẫu từ file .json hoặc biên bản Word .docx"));
        cTpl.RightBar.Controls.Add(NK.Button("Export…", ExportTpl, tip: "Xuất mẫu ra file .json để chia sẻ / sao lưu"));
        _tplTree.AfterCheck += OnTplCheck; _tplFilter.TextChanged += (_, _) => RenderTplPick();
        NK.Stack(cTpl.Body, _tplFilter, _tplTree, NK.Row(NK.Button("＋ Thêm 1 bản nữa của mẫu đang chọn", AddAnother, primary: true, tip: "Thêm 1 biên bản nữa cùng mẫu với biên bản đang chọn"), NK.Button("＋ Mẫu mới…", () => OpenTplEditor(null), tip: "Tạo mẫu mới từ đầu")));
        var cQ = new CardPanel("Danh sách biên bản sẽ xuất");
        cQ.RightBar.Controls.Add(_qCount);
        foreach (var (t, tip, act) in new (string, string, System.Action)[] { ("▲", "Lên", () => MoveDoc(-1)), ("▼", "Xuống", () => MoveDoc(1)), ("⧉", "Nhân bản", DupDoc), ("✕", "Bỏ khỏi danh sách", DelDoc) })
        { var b = NK.Button(t, act, tip: tip); b.AutoWidth = false; b.Width = NativeSkin.S(30); cQ.RightBar.Controls.Add(b); }
        _queue.SelectedIndexChanged += (_, _) => { if (_loading || _queue.SelectedIndex < 0) return; _sel = _queue.SelectedIndex; RenderQueue(); RenderEditor(); SchedulePreview(); };
        _eTpl.Dock = DockStyle.Fill; _eLogo.SetItems(new[] { "Logo 1 — FAST + PHẦN MỀM KẾ TOÁN & ERP", "Logo 2 — FAST gạch cong", "Không logo" }, 0);
        _eTpl.SelectionChanged += i => { if (_loading || CurDoc is null || i < 0 || i >= _templates.Count) return; var d = CurDoc; d["templateId"] = S(_templates[i], "id"); var vars = new JsonObject(); foreach (var v in Arr(_templates[i], "vars")) vars[S(v, "key")] = S(v, "default"); d["vars"] = vars; RenderAll(); };
        _eLogo.SelectionChanged += i => { if (!_loading && CurDoc is { } d) { d["logo"] = i == 0 ? "1" : i == 1 ? "2" : ""; SchedulePreview(); } };
        _eHau.TextChanged += (_, _) => { if (!_loading && CurDoc is { } d) { d["hauTo"] = _eHau.Text; SchedulePreview(); } };
        _eSo.TextChanged += (_, _) => { if (!_loading && CurDoc is { } d) { d["soOverride"] = _eSo.Text; SchedulePreview(); } };
        _eNgay.TextChanged += (_, _) => { if (!_loading && CurDoc is { } d) { d["ngayLap"] = _eNgay.Text; SchedulePreview(); } };
        _eRows.TextChanged += (_, _) => { if (!_loading && CurDoc is { } d) { d["rows"] = new JsonArray(_eRows.Text.Replace("\r", "").Split('\n').Select(x => (JsonNode)x).ToArray()); SchedulePreview(); } };
        _eRows.Height = NativeSkin.S(120); _eRows.Dock = DockStyle.Top; _eVars.Dock = DockStyle.Top;
        var edit = NK.Row(NK.Button("✎ Sửa nội dung mẫu này…", () => { var t = CurTpl; if (t is not null) OpenTplEditor(t); }));
        NK.Stack(_editor, NK.FormGrid(140, ("Mẫu", _eTpl), ("Hậu tố số (vd 38DV)", _eHau), ("Logo góc trái", _eLogo), ("Số biên bản (trống = tự động)", _eSo), ("Ngày lập riêng", _eNgay)), _eVars, _eRowsLbl, _eRows, edit);
        NK.Stack(cQ.Body, _queue, _emptyHint, _editor);
        var mid = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
        NK.Stack(mid, cTpl, cQ);

        // ---- cột phải ----
        var cPv = new CardPanel("Xem trước (Word)") { Dock = DockStyle.Fill };
        cPv.RightBar.Controls.Add(_pvNo);
        var bMany = NK.Button("Xuất từng file…", () => Post(new { action = "export", state = BuildState(), mode = "many" }), tip: "Mỗi biên bản 1 file .docx trong thư mục chọn");
        var bOne = NK.Button("⬇ Xuất Word", () => Post(new { action = "export", state = BuildState(), mode = "one" }), primary: true, tip: "Tất cả biên bản trong danh sách vào 1 file .docx, mỗi biên bản 1 trang");
        cPv.RightBar.Controls.Add(bMany); cPv.RightBar.Controls.Add(bOne);
        var pvBody = new Panel { Dock = DockStyle.Fill };
        _paper.Dock = DockStyle.Fill; _paper.Font = new Font("Times New Roman", 12); pvBody.Controls.Add(_paper); pvBody.Controls.Add(_pvTabs);
        cPv.Body.Controls.Add(pvBody); cPv.Body.Dock = DockStyle.Fill;
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) }; right.Controls.Add(cPv);

        var split1 = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        var split2 = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split1.Panel1.Controls.Add(left); split1.Panel2.Controls.Add(split2);
        split2.Panel1.Controls.Add(mid); split2.Panel2.Controls.Add(right);
        HandleCreated += (_, _) => BeginInvoke(() => { try { split1.SplitterDistance = Math.Max(NativeSkin.S(400), (int)(Width * 0.3)); split2.SplitterDistance = Math.Max(300, (int)(split2.Width * 0.42)); } catch { } });

        var head = new BarFlow { Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 4), AutoSize = true };
        head.Controls.AddRange(new Control[] { new Label { Text = "Biên bản xác nhận / nghiệm thu", AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 2, FontStyle.Bold), Margin = new Padding(0, 4, 10, 0) }, NK.Muted("Khai báo khách hàng → chọn mẫu → xem trước → xuất Word"), _status });
        Controls.Add(split1); Controls.Add(head);
    }

    // ---- tiện ích JSON ----
    private static string S(JsonNode? n, string k) => n is JsonObject o && o[k] is { } v ? (v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToString()) : "";
    private static IEnumerable<JsonObject> Arr(JsonNode? n, string k) => n is JsonObject o && o[k] is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();
    private JsonArray Customers => _settings["customers"] as JsonArray ?? (JsonArray)(_settings["customers"] = new JsonArray());
    private JsonObject? CurDoc => _sel >= 0 && _sel < _docs.Count ? _docs[_sel] : null;
    private JsonObject? Tpl(string id) => _templates.FirstOrDefault(t => string.Equals(S(t, "id"), id, StringComparison.OrdinalIgnoreCase));
    private JsonObject? CurTpl => CurDoc is { } d ? Tpl(S(d, "templateId")) : null;

    private string GetP(string path)
    {
        if (path == "ngayLap") return _ngayLap;
        JsonNode? n = _settings; foreach (var k in path.Split('.')) n = (n as JsonObject)?[k];
        return n?.ToString() ?? "";
    }

    private void SetP(string path, string v)
    {
        if (path == "ngayLap") { _ngayLap = v; return; }
        var p = path.Split('.'); var o = _settings;
        for (var i = 0; i < p.Length - 1; i++) { if (o[p[i]] is not JsonObject c) { c = new JsonObject(); o[p[i]] = c; } o = c; }
        o[p[^1]] = v;
    }

    private void FillBound() { _loading = true; foreach (var k in Bound) _b[k].Text = GetP(k); _loading = false; }

    private object BuildState() => new { fast = _settings["fast"]?.DeepClone(), customer = _settings["customer"]?.DeepClone(), contract = _settings["contract"]?.DeepClone(), maDa = GetP("maDa"), soFormat = GetP("soFormat"), ngayLap = _ngayLap, docs = _docs.Select(d => d.DeepClone()).ToList() };
    private void SchedulePreview() { _pvT.Stop(); _pvT.Start(); }
    private void Toast(string t, bool err = false) { _status.Text = t; _status.ForeColor = err ? AppColors.Danger : AppColors.TextMuted; }

    // ---- khách hàng ----
    private void RenderCust()
    {
        _loading = true;
        _custSel.SetItems(new[] { "— Khách hàng đã lưu —" }.Concat(Customers.OfType<JsonObject>().Select(c => S(c, "tenNgan").Length > 0 ? S(c, "tenNgan") : S(c, "ten"))).ToList(), 0);
        _loading = false;
    }

    private void SaveCustomer()
    {
        if (_settings["customer"] is not JsonObject c || S(c, "ten").Trim().Length == 0) { Toast("Nhập tên công ty trước.", true); return; }
        var list = Customers; var copy = (JsonObject)c.DeepClone();
        var i = list.ToList().FindIndex(x => S(x, "ten").Equals(S(c, "ten"), StringComparison.OrdinalIgnoreCase));
        if (i >= 0) list[i] = copy; else list.Add(copy);
        RenderCust(); _saveT.Stop(); _saveT.Start(); Toast($"Đã lưu khách hàng \"{(S(c, "tenNgan").Length > 0 ? S(c, "tenNgan") : S(c, "ten"))}\".");
    }

    private void DelCustomer() { var i = _custSel.SelectedIndex - 1; if (i < 0 || i >= Customers.Count) return; Customers.RemoveAt(i); RenderCust(); _saveT.Stop(); _saveT.Start(); }

    // ---- pháp nhân ----
    private void RenderEntities()
    {
        var list = (_settings["entities"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        _entSel.Parent!.Parent!.Visible = list.Count > 0;
        _loading = true;
        _entSel.SetItems(list.Select(e => S(e, "name")).ToList(), Math.Max(0, list.FindIndex(e => S(e, "id") == S(_settings, "entityId"))));
        _loading = false;
        var f = _settings["fast"] as JsonObject; _entWarn.Visible = S(f, "diaChi").Length == 0 || S(f, "daiDien").Length == 0;
        _entWarn.Text = "Pháp nhân này còn thiếu thông tin (địa chỉ, người đại diện…) — điền ở mục “Công ty Fast” bên dưới, chương trình tự lưu.";
    }

    private void OnEntity(int idx)
    {
        if (_loading) return;
        var list = (_settings["entities"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var old = list.FirstOrDefault(e => S(e, "id") == S(_settings, "entityId")); if (old is not null && _settings["fast"] is { } fastNode) old["party"] = fastNode.DeepClone();
        if (idx < 0 || idx >= list.Count) return;
        var cur = list[idx]; _settings["entityId"] = S(cur, "id"); _settings["fast"] = cur["party"]?.DeepClone() ?? new JsonObject();
        FillBound(); RenderEntities(); _saveT.Stop(); _saveT.Start(); SchedulePreview();
    }

    // ---- chọn mẫu ----
    private void RenderTplPick()
    {
        var used = new HashSet<string>(_docs.Select(d => S(d, "templateId")));
        var q = _tplFilter.Text.Trim().ToLowerInvariant();
        var open = _tplTree.Nodes.Cast<TreeNode>().Where(n => n.IsExpanded).Select(n => (string)n.Tag!).ToHashSet();
        _loading = true;
        _tplTree.BeginUpdate(); _tplTree.Nodes.Clear();
        var groups = new Dictionary<string, List<JsonObject>>();
        foreach (var t in _templates)
        {
            if (q.Length > 0 && !(S(t, "name") + " " + S(t, "abbr") + " " + S(t, "title")).ToLowerInvariant().Contains(q)) continue;
            var g = S(t, "group").Length > 0 ? S(t, "group") : "Khác (mẫu của bạn)";
            if (!groups.TryGetValue(g, out var l)) groups[g] = l = new List<JsonObject>();
            l.Add(t);
        }
        var gi = 0;
        foreach (var (g, list) in groups)
        {
            var n = list.Count(t => used.Contains(S(t, "id")));
            var gn = new TreeNode($"{g}   ({list.Count} mẫu{(n > 0 ? $" · đã chọn {n}" : "")})") { Tag = g, NodeFont = new Font(ThemeManager.BaseFont, FontStyle.Bold) };
            foreach (var t in list) gn.Nodes.Add(new TreeNode($"{S(t, "name")}   [{S(t, "abbr")}]") { Tag = S(t, "id"), Checked = used.Contains(S(t, "id")), ToolTipText = S(t, "title") });
            _tplTree.Nodes.Add(gn);
            if (q.Length > 0 || n > 0 || open.Contains(g) || (open.Count == 0 && gi == 0 && used.Count == 0)) gn.Expand();
            gi++;
        }
        _tplTree.EndUpdate();
        _loading = false;
    }

    private void OnTplCheck(object? s, TreeViewEventArgs e)
    {
        if (_loading || e.Node?.Parent is null) return;
        var id = (string)e.Node.Tag!;
        if (e.Node.Checked) { _docs.Add(NewDoc(id)); _sel = _docs.Count - 1; }
        else { var i = _docs.FindLastIndex(d => S(d, "templateId") == id); if (i >= 0) { _docs.RemoveAt(i); _sel = Math.Min(_sel, _docs.Count - 1); } }
        BeginInvoke(RenderAll);
    }

    private JsonObject NewDoc(string tid)
    {
        var t = Tpl(tid); var vars = new JsonObject();
        if (t is not null) foreach (var v in Arr(t, "vars")) vars[S(v, "key")] = S(v, "default");
        var last = _docs.LastOrDefault();
        return new JsonObject
        {
            ["templateId"] = tid, ["logo"] = last is null ? "1" : S(last, "logo"), ["hauTo"] = last is null ? "" : S(last, "hauTo"), ["soOverride"] = "", ["vars"] = vars,
            ["rows"] = new JsonArray((t?["defaultRows"] as JsonArray)?.Select(x => (JsonNode)(x?.ToString() ?? "")).ToArray() ?? Array.Empty<JsonNode>()), ["ngayLap"] = "",
        };
    }

    private void AddAnother() { var d = CurDoc; if (d is null) { Toast("Tick 1 mẫu ở trên trước.", true); return; } _docs.Add(NewDoc(S(d, "templateId"))); _sel = _docs.Count - 1; RenderAll(); }
    private void DelDoc() { if (_sel < 0) return; _docs.RemoveAt(_sel); _sel = Math.Min(_sel, _docs.Count - 1); RenderAll(); }
    private void DupDoc() { if (_sel < 0) return; _docs.Insert(_sel + 1, (JsonObject)_docs[_sel].DeepClone()); _sel++; RenderAll(); }
    private void MoveDoc(int d) { var j = _sel + d; if (_sel < 0 || j < 0 || j >= _docs.Count) return; (_docs[_sel], _docs[j]) = (_docs[j], _docs[_sel]); _sel = j; RenderAll(); }
    private void ExportTpl() => Post(new { action = "exportTpl", ids = _docs.Select(d => S(d, "templateId")).Distinct().ToList() });

    // ---- hiển thị ----
    private void RenderAll() { RenderTplPick(); RenderQueue(); RenderEditor(); SchedulePreview(); }

    private void RenderQueue()
    {
        _loading = true;
        _queue.BeginUpdate(); _queue.Items.Clear();
        for (var i = 0; i < _docs.Count; i++) _queue.Items.Add($"{i + 1}.  {S(Tpl(S(_docs[i], "templateId")), "name")}   {(i < _numbers.Count ? _numbers[i] : "")}");
        _queue.EndUpdate();
        if (_sel >= 0 && _sel < _queue.Items.Count) _queue.SelectedIndex = _sel;
        _loading = false;
        _qCount.Text = _docs.Count > 0 ? $"({_docs.Count})" : "";
        _emptyHint.Visible = _docs.Count == 0; _queue.Visible = _docs.Count > 0;
        _pvTabs.Controls.Clear();
        for (var i = 0; i < _docs.Count; i++)
        {
            var at = i; var t = Tpl(S(_docs[i], "templateId"));
            var b = NK.Button(S(t, "abbr").Length > 0 ? S(t, "abbr") : S(t, "name"), () => { _sel = at; RenderQueue(); RenderEditor(); SchedulePreview(); }, primary: i == _sel, tip: S(t, "name"));
            _pvTabs.Controls.Add(b);
        }
    }

    private void RenderEditor()
    {
        var d = CurDoc; _editor.Visible = d is not null;
        if (d is null) return;
        _loading = true;
        _eTpl.SetItems(_templates.Select(t => S(t, "name")).ToList(), Math.Max(0, _templates.FindIndex(t => S(t, "id") == S(d, "templateId"))));
        var logo = S(d, "logo"); _eLogo.SetItems(new[] { "Logo 1 — FAST + PHẦN MỀM KẾ TOÁN & ERP", "Logo 2 — FAST gạch cong", "Không logo" }, logo == "2" ? 1 : logo == "" ? 2 : 0);
        _eHau.Text = S(d, "hauTo"); _eSo.Text = S(d, "soOverride"); _eNgay.Text = S(d, "ngayLap");
        _eSo.PlaceholderText = _sel < _numbers.Count ? _numbers[_sel] : "tự động";
        var t = Tpl(S(d, "templateId"));
        _eVars.SuspendLayout(); _eVars.Controls.Clear();
        foreach (var v in Arr(t, "vars"))
        {
            var key = S(v, "key"); var vars = d["vars"] as JsonObject ?? new JsonObject(); d["vars"] = vars;
            var inp = NK.Input(""); inp.Text = vars[key] is { } cur ? cur.ToString() : S(v, "default"); inp.Width = NativeSkin.S(360);
            inp.TextChanged += (_, _) => { if (_loading) return; vars[key] = inp.Text; SchedulePreview(); };
            _eVars.Controls.Add(NK.Muted(S(v, "label").Length > 0 ? S(v, "label") : key)); _eVars.Controls.Add(inp);
        }
        _eVars.ResumeLayout(true);
        var hasTable = t?["table"]?.GetValue<bool>() ?? false;
        _eRows.Visible = _eRowsLbl.Visible = hasTable;
        _eRows.Text = string.Join("\r\n", (d["rows"] as JsonArray)?.Select(x => x?.ToString() ?? "") ?? Enumerable.Empty<string>());
        var cols = (t?["columns"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new List<string>();
        _eRowsLbl.Text = cols.Count > 0 ? "Các dòng của bảng — mỗi dòng 1 hàng, ngăn cột bằng dấu |  (" + string.Join(" | ", cols) + (Regex.IsMatch(cols[0], @"^(stt|no\.?|#)$", RegexOptions.IgnoreCase) ? " — cột đầu tự đánh số, bỏ trống khi nhập" : "") + ")" : "Hạng mục nghiệm thu (mỗi dòng 1 hạng mục → vào bảng)";
        _loading = false;
    }

    // ---- xem trước: HTML đơn giản → RichTextBox ----
    private void ShowPreview(string html)
    {
        _paper.Clear();
        if (string.IsNullOrWhiteSpace(html)) { Append("Chưa có biên bản nào để xem.", HorizontalAlignment.Center, false, false, Color.Gray); return; }
        foreach (Match m in Regex.Matches(html, @"<table\b[^>]*class=""(?<cls>[^""]*)""[^>]*>(?<body>.*?)</table>|<p\b(?<attr>[^>]*)>(?<txt>.*?)</p>", RegexOptions.Singleline))
        {
            if (m.Groups["cls"].Success)
            {
                var cls = m.Groups["cls"].Value; var body = m.Groups["body"].Value;
                var rows = Regex.Matches(body, @"<tr\b[^>]*>(?<r>.*?)</tr>", RegexOptions.Singleline).Select(r => Regex.Matches(r.Groups["r"].Value, @"<t[dh]\b[^>]*>(?<c>.*?)</t[dh]>", RegexOptions.Singleline).Select(c => Plain(c.Groups["c"].Value)).ToList()).ToList();
                if (cls.Contains("hdr")) { foreach (var line in rows.SelectMany(r => r).Where(x => x.Length > 0).SelectMany(x => x.Split('\n')).Where(x => x.Trim().Length > 0)) Append(line.Trim(), HorizontalAlignment.Center, true, false, Color.Black); }
                else if (cls.Contains("party")) foreach (var r in rows) Append(string.Join("", r), HorizontalAlignment.Left, false, false, Color.Black);
                else if (cls.Contains("sign")) foreach (var r in rows) { Append(string.Join("          |          ", r.Select(c => c.Replace("\n", " "))), HorizontalAlignment.Center, true, false, Color.Black); }
                else { for (var i = 0; i < rows.Count; i++) Append(string.Join("  │  ", rows[i]), HorizontalAlignment.Left, i == 0, false, Color.Black); }
                Append("", HorizontalAlignment.Left, false, false, Color.Black);
                continue;
            }
            var attr = m.Groups["attr"].Value; var txt = m.Groups["txt"].Value;
            var al = Regex.IsMatch(attr, @"class=""[^""]*\bc\b") ? HorizontalAlignment.Center : Regex.IsMatch(attr, @"class=""[^""]*\br\b") ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var bold = attr.Contains("font-weight:700"); var ital = attr.Contains("font-style:italic") || attr.Contains(" en");
            AppendInline(txt, al, bold, ital);
        }
        _paper.SelectionStart = 0; _paper.ScrollToCaret();
    }

    private static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(Regex.Replace(html, @"<br\s*/?>", "\n"), @"</p>", "\n"), @"<[^>]+>", "")).Trim();

    private void Append(string text, HorizontalAlignment al, bool bold, bool italic, Color color)
    {
        _paper.SelectionStart = _paper.TextLength; _paper.SelectionAlignment = al;
        _paper.SelectionFont = new Font(_paper.Font, (bold ? FontStyle.Bold : 0) | (italic ? FontStyle.Italic : 0)); _paper.SelectionColor = color;
        _paper.AppendText(text + "\n");
    }

    private void AppendInline(string html, HorizontalAlignment al, bool bold, bool italic)
    {
        _paper.SelectionStart = _paper.TextLength; _paper.SelectionAlignment = al;
        var b = bold; var i = italic;
        foreach (var part in Regex.Split(html, @"(<[^>]+>)"))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith('<')) { var tag = part.ToLowerInvariant(); if (tag is "<b>" or "<strong>") b = true; else if (tag is "</b>" or "</strong>") b = bold; else if (tag is "<i>" or "<em>") i = true; else if (tag is "</i>" or "</em>") i = italic; else if (tag.StartsWith("<br")) _paper.AppendText("\n"); continue; }
            _paper.SelectionFont = new Font(_paper.Font, (b ? FontStyle.Bold : 0) | (i ? FontStyle.Italic : 0)); _paper.SelectionColor = Color.Black;
            _paper.AppendText(WebUtility.HtmlDecode(part));
        }
        _paper.AppendText("\n");
    }

    // ---- hộp sửa / tạo mẫu ----
    private void OpenTplEditor(JsonObject? t)
    {
        using var dlg = new TemplateDialog(t);
        var r = dlg.ShowDialog(FindForm());
        if (r == DialogResult.OK && dlg.Result is { } tpl) Post(new { action = "saveTpl", template = tpl });
        else if (dlg.Action == "saveNew" && dlg.Result is { } tpl2) Post(new { action = "saveTpl", template = tpl2 });
        else if (dlg.Action == "delete" && t is not null) Post(new { action = "deleteTpl", id = S(t, "id") });
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "bbxn.init":
            {
                var d = Arg(a, 0);
                _settings = JsonNode.Parse(PO(d, "settings").GetRawText()) as JsonObject ?? new JsonObject();
                _templates = PA(d, "templates").Select(t => (JsonObject)JsonNode.Parse(t.GetRawText())!).ToList();
                FillBound(); RenderEntities(); RenderCust(); RenderTplPick(); RenderQueue(); RenderEditor();
                break;
            }
            case "bbxn.onTemplates":
                _templates = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(t => (JsonObject)JsonNode.Parse(t.GetRawText())!).ToList();
                _docs = _docs.Where(x => Tpl(S(x, "templateId")) is not null).ToList(); if (_sel >= _docs.Count) _sel = _docs.Count - 1;
                RenderAll(); if (Str(a, 1).Length > 0) Toast(Str(a, 1));
                break;
            case "bbxn.onPreview":
            {
                var r = Arg(a, 0);
                _numbers = PA(r, "numbers").Select(x => x.GetString() ?? "").ToList();
                _pvNo.Text = _sel >= 0 && _sel < _numbers.Count && _numbers[_sel].Length > 0 ? "— " + _numbers[_sel] : "";
                ShowPreview(P(r, "html"));
                RenderQueue();
                if (CurDoc is not null) _eSo.PlaceholderText = _sel < _numbers.Count ? _numbers[_sel] : "tự động";
                break;
            }
            case "bbxn.onExported": Toast($"Đã xuất {P(Arg(a, 0), "count")} biên bản → {P(Arg(a, 0), "path")}"); break;
            case "bbxn.onToast": Toast(Str(a, 0)); break;
            case "bbxn.onError": Toast(Str(a, 0), true); break;
        }
    }

    /// <summary>Hộp sửa / tạo 1 mẫu biên bản (tiêu đề, căn cứ, nội dung, song ngữ, bảng, biến riêng).</summary>
    private sealed class TemplateDialog : ThemedForm
    {
        private readonly JsonObject? _src;
        private readonly RoundedInput _name = NK.Input(""), _abbr = NK.Input(""), _title = NK.Memo(2, ""), _basis = NK.Memo(3, ""), _intro = NK.Input(""), _items = NK.Memo(7, ""), _closing = NK.Memo(2, ""),
            _titleEn = NK.Memo(2, ""), _basisEn = NK.Memo(2, ""), _introEn = NK.Input(""), _itemsEn = NK.Memo(6, ""), _closingEn = NK.Memo(2, ""), _tableIntro = NK.Input(""), _vars = NK.Memo(3, "so_trang|Số trang tài liệu|…");
        private readonly CheckBox _bi = NK.Check("Mẫu song ngữ (mỗi đoạn Việt kèm 1 đoạn Anh)"), _table = NK.Check("Có bảng hạng mục (STT | Nội dung | Xác nhận)");
        public JsonObject? Result { get; private set; }
        public string Action { get; private set; } = "";

        public TemplateDialog(JsonObject? t)
        {
            _src = t;
            Text = t is null ? "Mẫu mới" : "Sửa mẫu: " + S(t, "name"); Width = NativeSkin.S(860); Height = NativeSkin.S(820); StartPosition = FormStartPosition.CenterParent; ShowIcon = false;
            _name.Text = S(t, "name"); _abbr.Text = S(t, "abbr"); _title.Text = S(t, "title").Length > 0 ? S(t, "title") : "BIÊN BẢN ";
            _basis.Text = S(t, "basis").Length > 0 ? S(t, "basis") : "Căn cứ việc thực hiện {loai_hd} số {so_hd} ký ngày {ngay_hd} giữa {ten_a_ngan} và Chi nhánh Công ty CP phần mềm quản lý Doanh Nghiệp Fast tại TP.HCM.";
            _intro.Text = t is null ? "Hai bên thống nhất xác nhận các nội dung sau:" : S(t, "intro"); _items.Text = string.Join("\r\n", Lines(t, "items"));
            _closing.Text = t is null ? "Biên bản này có 01 (một) trang, được lập thành 02 (hai) bản, mỗi bên giữ 01 (một) bản có giá trị pháp lý như nhau." : S(t, "closing");
            _table.Checked = t?["table"]?.GetValue<bool>() ?? false; _tableIntro.Text = S(t, "tableIntro");
            _bi.Checked = t?["bilingual"]?.GetValue<bool>() ?? false; _titleEn.Text = S(t, "titleEn"); _basisEn.Text = S(t, "basisEn"); _introEn.Text = S(t, "introEn"); _itemsEn.Text = string.Join("\r\n", Lines(t, "itemsEn")); _closingEn.Text = S(t, "closingEn");
            _vars.Text = string.Join("\r\n", Arr(t, "vars").Select(v => S(v, "key") + "|" + S(v, "label") + "|" + S(v, "default")));
            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14, 8, 14, 8) };
            NK.Stack(body,
                NK.FormGrid(240, ("Tên mẫu", _name), ("Viết tắt (dùng trong số biên bản)", _abbr), ("Tiêu đề (nhiều dòng = nhiều dòng tiêu đề; dùng được {biến})", _title), ("Các câu “Căn cứ…” (mỗi dòng 1 đoạn)", _basis), ("Câu dẫn trước nội dung", _intro),
                    ("Nội dung xác nhận (mỗi dòng 1 đoạn, tự gạch đầu dòng)", _items), ("Câu kết", _closing)),
                _bi, NK.FormGrid(240, ("Tiêu đề (Anh)", _titleEn), ("Căn cứ (Anh)", _basisEn), ("Câu dẫn (Anh)", _introEn), ("Nội dung (Anh) — mỗi dòng ứng với 1 dòng tiếng Việt", _itemsEn), ("Câu kết (Anh)", _closingEn)),
                _table, NK.FormGrid(240, ("Câu dẫn bảng", _tableIntro), ("Biến riêng (khoá|Nhãn|mặc định)", _vars)),
                NK.Muted("Biến có sẵn: {ten_a} {ten_a_ngan} {so_hd} {ngay_hd} {loai_hd} {loai_hd_ngan} {ma_da}. Chữ đậm: **như vầy**.", true));
            var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
            foot.Controls.Add(NK.Button("Đóng", () => DialogResult = DialogResult.Cancel));
            if (t is not null) foot.Controls.Add(NK.Button(t["builtin"]?.GetValue<bool>() == true ? "Khôi phục" : "Xoá mẫu", () => { Action = "delete"; DialogResult = DialogResult.Cancel; }));
            if (t is not null) foot.Controls.Add(NK.Button("Lưu thành mẫu mới", () => { Result = Read(true); Action = "saveNew"; DialogResult = DialogResult.Cancel; }));
            foot.Controls.Add(NK.Button("Lưu mẫu", () => { Result = Read(false); DialogResult = DialogResult.OK; }, primary: true));
            Controls.Add(body); Controls.Add(foot);
        }

        private static IEnumerable<string> Lines(JsonObject? t, string k) => (t?[k] as JsonArray)?.Select(x => x?.ToString() ?? "") ?? Enumerable.Empty<string>();
        private static JsonArray ArrOf(IEnumerable<string> s) => new(s.Select(x => (JsonNode)x).ToArray());
        private static IEnumerable<string> SplitLines(string s, bool keepEmpty = false) => s.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => keepEmpty || x.Length > 0);

        private JsonObject Read(bool newId)
        {
            var e = _src ?? new JsonObject();
            var name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : _title.Text.Replace("\r", "").Split('\n')[0];
            if (newId && _src is not null && name == S(_src, "name")) name += " (bản sao)";
            return new JsonObject
            {
                ["id"] = newId ? "" : S(e, "id"), ["name"] = name, ["abbr"] = _abbr.Text.Trim(), ["title"] = _title.Text.Replace("\r", ""), ["basis"] = _basis.Text.Replace("\r", ""),
                ["intro"] = _intro.Text, ["items"] = ArrOf(SplitLines(_items.Text)), ["closing"] = _closing.Text.Replace("\r", ""), ["table"] = _table.Checked, ["tableIntro"] = _tableIntro.Text,
                ["bilingual"] = _bi.Checked, ["titleEn"] = _titleEn.Text.Replace("\r", ""), ["basisEn"] = _basisEn.Text.Replace("\r", ""), ["introEn"] = _introEn.Text, ["itemsEn"] = ArrOf(SplitLines(_itemsEn.Text, true)), ["closingEn"] = _closingEn.Text.Replace("\r", ""),
                ["group"] = S(e, "group"), ["layout"] = S(e, "layout"), ["columns"] = e["columns"]?.DeepClone() ?? new JsonArray(), ["signRoles"] = e["signRoles"]?.DeepClone() ?? new JsonArray(),
                ["greeting"] = S(e, "greeting"), ["recipients"] = S(e, "recipients"), ["defaultRows"] = e["defaultRows"]?.DeepClone() ?? new JsonArray(),
                ["vars"] = new JsonArray(SplitLines(_vars.Text).Select(l => { var p = l.Split('|'); return new JsonObject { ["key"] = p[0].Trim(), ["label"] = (p.Length > 1 ? p[1] : "").Trim(), ["default"] = (p.Length > 2 ? p[2] : "").Trim() }; }).Where(v => S(v, "key").Length > 0).Select(v => (JsonNode)v).ToArray()),
            };
        }
    }
}
