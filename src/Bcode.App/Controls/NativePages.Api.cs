using System.Text.Json;
using System.Text.Json.Nodes;
using Bcode.App.Services.Api;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Khai báo & quản lý API chuẩn LT3 (apideclaration.html): dự án (kết nối + token), danh sách form; form kiểu Postman — thanh METHOD + URL, tab
/// Params / Authorization / Headers / Body (bảng key–value bật/tắt từng dòng, giá trị bí mật 👁 xem / sửa), kiểm tra theo bảng trường LT3, gửi và xem kết quả
/// (status, thời gian, kích thước, Body pretty/raw, Bảng, Headers phản hồi, Request).</summary>
internal sealed class ApiDeclarationPage : DialogPage
{
    private sealed class CatalogItem { public string Form = "", Kind = "", Title = ""; }

    /// <summary>Màu chữ dễ đọc pha từ màu theme (giống --api-* của trang web): sáng hơn ở theme tối, đậm hơn ở theme sáng.</summary>
    private static class Ink
    {
        public static Color Label => AppColors.Mix(AppColors.Text, AppColors.TextMuted, 0.72);
        public static Color Hint => AppColors.Mix(AppColors.Text, AppColors.TextMuted, 0.45);
        public static Color Danger => AppColors.Mix(AppColors.Danger, AppColors.Text, 0.62);
        public static Color Warn => AppColors.Mix(AppColors.Mix(AppColors.Warning, AppColors.Accent, 0.5), AppColors.Text, 0.75);
        public static Color Ok => AppColors.Mix(AppColors.Success, AppColors.Text, 0.70);
    }

    /// <summary>Nhãn ô nhập (cột trái) — đậm hơn chữ phụ mặc định cho dễ đọc.</summary>
    private sealed class FieldLabel : Label { public override Color ForeColor { get => Ink.Label; set { } } }
    /// <summary>Chú thích (gợi ý biến, mô tả tab…).</summary>
    private sealed class HintLabel : Label { public override Color ForeColor { get => Ink.Hint; set { } } }

    private static readonly (string Json, string Key)[] ProjectFields =
    {
        ("Name", "pName"), ("ProjectId", "pId"), ("Department", "pDept"), ("BaseUrl", "pBase"), ("Username", "pUser"), ("TokenPath", "pTokPath"),
        ("TokenField", "pTokField"), ("ExpiresField", "pExpField"), ("TokenBody", "pTokBody"), ("DocPath", "pDoc"), ("Note", "pNote"),
    };
    private static readonly string[] Kinds = { "SyncData", "SyncVoucher", "GetData", "custom" };
    private static readonly string[] KindLabels = { "SyncData — đồng bộ danh mục", "SyncVoucher — đồng bộ chứng từ", "GetData — lấy dữ liệu", "Khác" };
    private static readonly string[] Auths = { "raw", "bearer", "none" };
    private static readonly string[] AuthLabels = { "Authorization: <token> (chuẩn LT3)", "Authorization: Bearer <token>", "Header tĩnh — không lấy token" };
    private static readonly string[] Methods = { "POST", "GET", "PUT", "PATCH", "DELETE" };
    private static readonly string[] Modes = { "none", "formdata", "urlencoded", "raw" };
    private static readonly string[] ModeLabels = { "none", "form-data", "x-www-form-urlencoded", "raw" };
    private static readonly string[] RawTypes = { "json", "text", "xml", "html", "javascript" };
    private static readonly string[] RawLabels = { "JSON", "Text", "XML", "HTML", "JavaScript" };
    private static readonly string[] FormAuths = { "inherit", "none", "bearer", "basic", "apikey" };
    private static readonly string[] FormAuthLabels = { "Kế thừa từ \"Kết nối\"", "Không xác thực (No Auth)", "Bearer token", "Basic auth (tài khoản + mật khẩu)", "API key" };
    /// <summary>Header bí mật đã lưu hiện thế này (chế độ sửa dạng chữ cũ) — để nguyên thì giữ bản mã hoá cũ.</summary>
    private const string Mask = "••••••";
    private const string Saved = "•••••• (đã lưu, mã hoá — để trống là giữ)";

    private readonly Dictionary<string, RoundedInput> _in = new();
    private readonly DropDownButton _projects = new() { Width = NativeSkin.S(260) }, _auth = new(), _fKind = new(), _fMethod = new() { Width = NativeSkin.S(100) }, _fMode = new() { Width = NativeSkin.S(210) },
        _fRaw = new() { Width = NativeSkin.S(110) }, _aType = new(), _aIn = new();
    private readonly RoundedInput _pass = NK.Input(""), _fName = NK.Input(""), _fEndpoint = NK.Input("api/GetData — dán URL có ?tham số thì tự tách sang tab Params", mono: true), _fDesc = NK.Input(""),
        _fBody = NK.Memo(14, "", mono: true), _filter = NK.Input("Lọc theo tên form…"), _aUser = NK.Input(""), _aKey = NK.Input("vd X-Api-Key", mono: true), _aVal = NK.Input("", mono: true);
    private readonly KvGrid _kvP, _kvF, _kvQ, _kvB;
    private readonly CardPanel _advCard;
    private readonly Label _tokVal = NK.Muted("(chưa lấy)"), _dirty = new Label { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(8, 8, 0, 0) }, _kb = NK.Muted(""), _fCount = NK.Muted(""),
        _url = new HintLabel { AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false, Margin = new Padding(0, 2, 0, 4) }, _aHint = NK.Muted("", true),
        _noneHint = NK.Muted("Request này không gửi body."), _inh = new HintLabel { AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false };
    private readonly ListBox _fList = new() { Dock = DockStyle.Top, Height = NativeSkin.S(220), IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed };

    private readonly Label _issuesText = NK.Muted("", true), _notesText = NK.Muted("", true), _rPill = new() { AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold) },
        _rMeta = new FieldLabel { AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false, Margin = new Padding(0, 9, 6, 3) }, _rUrl = new HintLabel { AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false };
    private readonly CardPanel _resCard;
    private readonly Panel _rTables = new() { Dock = DockStyle.Top, AutoSize = true }, _pnParams, _pnAuth, _pnHeaders, _pnBody, _aUserRow, _aKeyRow, _aValRow;
    private readonly RoundedInput _rResp = NK.Memo(12, "", mono: true), _rReq = NK.Memo(12, "", mono: true);
    private readonly DataGridView _rHdrs = NK.Grid(("k", "Header", 220), ("v", "Giá trị", 0));
    private readonly SegmentedBar _fTabs, _rTabs, _rFmt;
    private readonly PillButton _bToken, _bSend, _bCopyTok, _bOld, _bValidate, _bFormat, _bFormDoc, _aEye;
    private readonly List<int> _view = new();
    private JsonObject? _p;
    private JsonElement _lastResult;
    private List<string> _projectNames = new();
    private List<CatalogItem> _catalog = new();
    private string _docUrl = "", _original = "", _token = "", _authSaved = "", _rTab = "";
    private int _sel = -1;
    private bool _isDirty, _hasPassword, _loading, _authShown;

    public ApiDeclarationPage()
    {
        foreach (var (_, key) in ProjectFields) _in[key] = NK.Input("", mono: key is "pBase" or "pTokPath" or "pTokField" or "pExpField");
        _in["pTokBody"] = NK.Memo(4, "", mono: true); _in["pNote"] = NK.Memo(3, "", mono: false);
        _pass.Inner.UseSystemPasswordChar = true; _aVal.Inner.UseSystemPasswordChar = true;
        _auth.SetItems(AuthLabels, 0);
        _fKind.SetItems(KindLabels, 2); _fMethod.SetItems(Methods, 0); _fMode.SetItems(ModeLabels, 3); _fRaw.SetItems(RawLabels, 0);
        _aType.SetItems(FormAuthLabels, 0); _aIn.SetItems(new[] { "Header", "Query (?key=value)" }, 0);
        _fBody.Dock = DockStyle.Top; _fBody.Height = NativeSkin.S(260);
        _kvP = new KvGrid("p", "Header", secret: true, Reveal); _kvF = new KvGrid("f", "Header", secret: true, Reveal);
        _kvQ = new KvGrid("q", "Tham số", secret: true, Reveal); _kvB = new KvGrid("b", "Trường", secret: false, Reveal);
        foreach (var kv in new[] { _kvP, _kvF, _kvQ, _kvB }) kv.Changed += () => { if (!_loading && _p is not null) { SetDirty(true); Counts(); UrlPreview(); } };

        // ---- thanh trên ----
        _bOld = NK.Button("Chuyển hồ sơ cũ", () => Post(new { action = "import-old" }), tip: "Chuyển hồ sơ kiểu cũ (mật khẩu chữ thường) sang dạng mới, mật khẩu mã hoá"); _bOld.Visible = false;
        var bDelete = NK.Button("Xoá", DeleteProject, tip: "Xoá dự án khỏi máy này");
        var top = TopBar();
        top.Controls.AddRange(new Control[]
        {
            NK.Text("Dự án"), _projects,
            NK.Button("＋ Mới từ mẫu LT3", () => OpenPicker(true), primary: true, tip: "Tạo dự án mới từ mẫu chuẩn phòng LT3 (31 form của tài liệu API)"),
            NK.Button("Nhập Postman…", () => Post(new { action = "import-postman" })), _bOld,
            NK.Button("Lưu", SaveProject, tip: "Lưu dự án (Ctrl+S)"), NK.Button("Xuất Postman…", ExportPostman),
            bDelete, _dirty, NK.Button("Tài liệu API LT3 ↗", () => Post(new { action = "open-path", path = _docUrl })),
        });
        _projects.SelectionChanged += i => { if (!_loading && i > 0 && i - 1 < _projectNames.Count) Post(new { action = "open", name = _projectNames[i - 1] }); };

        // ---- cột trái ----
        var cConn = new CardPanel("Kết nối") { Dock = DockStyle.Top };
        _bToken = NK.Button("Lấy token", () => { if (_p is not null) { Busy(true); Post(Payload("token")); } }, tip: "Lấy token mới (Ctrl+Shift+T)");
        _bCopyTok = NK.Button("Chép", () => Post(new { action = "copy", text = _token }), tip: "Chép token đầy đủ"); _bCopyTok.Enabled = false;
        var docRow = new Panel { Height = NativeSkin.RowHeight(this, 28) };
        var bDoc = NK.Button("…", () => Post(new { action = "browse-doc" })); var bOpen = NK.Button("Mở", () => Post(new { action = "open-path", path = _in["pDoc"].Text }));
        bDoc.Dock = DockStyle.Right; bOpen.Dock = DockStyle.Right; _in["pDoc"].Dock = DockStyle.Fill; docRow.Controls.Add(_in["pDoc"]); docRow.Controls.Add(bDoc); docRow.Controls.Add(bOpen);
        var advCard = _advCard = new CardPanel("Lấy token (nâng cao)") { Dock = DockStyle.Top };
        NK.Stack(advCard.Body,
            Fields(110, ("Đường dẫn", _in["pTokPath"]), ("Trường token", _in["pTokField"]), ("Trường hạn", _in["pExpField"]), ("Body", _in["pTokBody"])),
            Hint("{{username}} / {{password}} được thay lúc gửi. Response chuẩn LT3: token.accesstoken, token.expires."));
        NK.Stack(cConn.Body,
            Fields(110, ("Tên dự án", _in["pName"]), ("Mã dự án", _in["pId"]), ("Bộ phận", _in["pDept"]), ("Địa chỉ gốc", _in["pBase"]), ("Xác thực", _auth), ("Tài khoản", _in["pUser"]), ("Mật khẩu", _pass)),
            Caption("Header chung — gửi kèm mọi form; form có header cùng tên thì form thắng"), _kvP,
            Hint("Giá trị bí mật (cột 🔒: Authorization / Authentication / token / key…) lưu mã hoá trên máy này — ô để trống là giữ giá trị cũ, gõ là đổi, \"👁 Hiện bí mật\" để xem / sửa."),
            advCard,
            NK.Row(_bToken, _tokVal, _bCopyTok),
            Fields(110, ("Tài liệu", docRow), ("Ghi chú", _in["pNote"])));

        var cForms = new CardPanel("Form") { Dock = DockStyle.Top };
        _fList.ItemHeight = (int)(ThemeManager.BaseFont.Height * 1.7);
        _fList.DrawItem += DrawForm;
        _fList.SelectedIndexChanged += (_, _) => { if (_loading || _fList.SelectedIndex < 0 || _fList.SelectedIndex >= _view.Count) return; SaveForm(); Select(_view[_fList.SelectedIndex]); };
        _filter.TextChanged += (_, _) => RenderForms();
        var bDelForm = NK.Button("Xoá form", DeleteForm);
        void Danger() { bDelete.ForeOverride = bDelForm.ForeOverride = Ink.Danger; bDelete.Invalidate(); bDelForm.Invalidate(); }
        Danger(); ThemeManager.Subscribe(this, Danger);
        var fBtns = NK.Row(
            NK.Button("＋ Từ mẫu LT3", () => { if (_p is not null) OpenPicker(false); }, tip: "Thêm form từ mẫu chuẩn LT3"), NK.Button("＋ Form trống", AddBlank), NK.Button("Nhân bản", Duplicate),
            NK.Button("▲", () => Move(-1), tip: "Lên"), NK.Button("▼", () => Move(1), tip: "Xuống"), bDelForm);
        NK.Stack(cForms.Body, _fCount, _filter, _fList, fBtns);

        var leftCol = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 6, 6, 6) };
        NK.Stack(leftCol, cConn, cForms);

        // ---- cột phải: form đang chọn (kiểu Postman) ----
        var cSel = new CardPanel("Form đang chọn") { Dock = DockStyle.Top };
        _bFormDoc = NK.Button("Tài liệu form", FormDoc, tip: "Bảng trường theo tài liệu chuẩn LT3"); cSel.RightBar.Controls.Add(_bFormDoc);
        _bFormat = NK.Button("Định dạng", Format, tip: "Định dạng JSON (Ctrl+Shift+F)"); _bValidate = NK.Button("Kiểm tra (LT3)", Validate, tip: "Kiểm tra theo bảng trường chuẩn LT3 (Ctrl+Shift+K)");
        _bSend = NK.Button("Gửi ▶", () => Send(false), primary: true, tip: "Gửi (Ctrl+Shift+Enter, hoặc Enter ở ô Endpoint)");
        var urlBar = NK.Padded(NK.GridRow(6, (_fMethod, 0), (_fEndpoint, 1), (_bSend, 0)), 0, 4, 0, 2);
        _fTabs = new SegmentedBar(("params", "Params"), ("auth", "Authorization"), ("headers", "Headers"), ("body", "Body"));
        _fTabs.Changed += ShowFTab;

        _pnParams = Pane(Caption("Tham số query — nối vào URL dạng ?key=value, giá trị dùng được biến {{today}}…"), _kvQ);
        _aEye = NK.Button("👁", ToggleAuthEye, tip: "Hiện / ẩn (giải mã giá trị đã lưu)");
        _aUserRow = Fields(110, ("Tài khoản", _aUser));
        _aKeyRow = Fields(110, ("Tên key", _aKey), ("Gửi ở", _aIn));
        _aValRow = Fields(110, ("Giá trị", NK.InputWithButton(_aVal, _aEye)));

        _pnAuth = Pane(Fields(110, ("Kiểu", _aType)), _aUserRow, _aKeyRow, _aValRow, _aHint);
        _pnHeaders = Pane(Caption("Header riêng của form — gộp với Header chung ở Kết nối, trùng tên thì form thắng."), _kvF, Caption("Header tự thêm / kế thừa khi gửi:"), _inh);
        var hint = Hint("Biến: {{today}} (yyyy-MM-dd) · {{now}} · {{key}} (khoá duy nhất mỗi lần gửi — dùng cho VoucherId) · {{year}} · {{month}} · {{monthStart}}");
        _pnBody = Pane(NK.Row(NK.Muted("Kiểu"), _fMode, _fRaw, _bFormat, _bValidate, _kb), _fBody, _kvB, _noneHint);
        _pnParams.Visible = _pnAuth.Visible = _pnHeaders.Visible = false;
        _fTabs.Select("body", raise: false);

        NK.Stack(cSel.Body,
            Fields(110, ("Tên", _fName), ("Loại", _fKind), ("Mô tả", _fDesc)),
            urlBar, _url, _fTabs, NK.Spacer(6), _pnParams, _pnAuth, _pnHeaders, _pnBody, hint, _issuesText, _notesText);

        // ---- kết quả ----
        _resCard = new CardPanel("Kết quả") { Dock = DockStyle.Top, Visible = false };
        _rTabs = new SegmentedBar(("body", "Body"), ("tables", "Bảng"), ("hdrs", "Headers"), ("req", "Request"));
        _rTabs.Changed += t => { _rTab = t; ShowTab(t); };
        _rFmt = new SegmentedBar(("pretty", "Pretty"), ("raw", "Raw"));
        _rFmt.Changed += _ => RenderBody();
        _rResp.ReadOnly = _rReq.ReadOnly = true; _rResp.Visible = _rReq.Visible = false; _rResp.Height = _rReq.Height = NativeSkin.S(300);
        _rHdrs.Dock = DockStyle.Top; _rHdrs.Height = NativeSkin.S(240); _rHdrs.Visible = false;
        NK.Stack(_resCard.Body,
            NK.Row(_rPill, _rMeta, NK.Button("Chép", CopyResult, tip: "Chép nội dung tab đang xem")),
            NK.Row(_rTabs, _rFmt),
            _rTables, _rResp, _rHdrs, _rReq, _rUrl);

        var rightCol = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6, 6, 10, 6) };
        NK.Stack(rightCol, cSel, _resCard);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(leftCol); split.Panel2.Controls.Add(rightCol);
        HandleCreated += (_, _) => BeginInvoke(() => { try { split.SplitterDistance = Math.Max(380, (int)(Width * 0.36)); } catch { } });

        foreach (var c in _in.Values.Concat(new[] { _pass, _fName, _fEndpoint, _fDesc, _fBody, _aUser, _aKey, _aVal })) c.TextChanged += (_, _) => { if (!_loading && _p is not null) SetDirty(true); };
        _auth.SelectionChanged += _ => { AuthUi(); if (!_loading && _p is not null) SetDirty(true); };
        _fMethod.SelectionChanged += _ => { if (!_loading && _p is not null) { SetDirty(true); SaveForm(); _fList.Invalidate(); } };
        _fMode.SelectionChanged += _ => { ModeUi(); if (!_loading && _p is not null) SetDirty(true); };
        _fRaw.SelectionChanged += _ => { if (!_loading && _p is not null) SetDirty(true); };
        _aType.SelectionChanged += _ => { FormAuthUi(); if (!_loading && _p is not null) SetDirty(true); };
        _aIn.SelectionChanged += _ => { UrlPreview(); if (!_loading && _p is not null) SetDirty(true); };
        _aKey.TextChanged += (_, _) => UrlPreview();
        _in["pBase"].TextChanged += (_, _) => UrlPreview();
        _fEndpoint.TextChanged += (_, _) => UrlPreview();
        _fEndpoint.Inner.Leave += (_, _) => SplitQuery();
        _fEndpoint.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Control && !e.Shift) { e.Handled = e.SuppressKeyPress = true; SplitQuery(); Send(false); } };
        _fKind.SelectionChanged += i =>
        {
            if (_loading || _p is null) return;
            var ep = i switch { 0 => "api/SyncData", 1 => "api/SyncVoucher", 2 => "api/GetData", _ => null };
            if (ep is not null && _fEndpoint.Text.Length == 0) _fEndpoint.Text = ep;
            SaveForm(); RenderForms(); SetDirty(true);
        };
        _fName.TextChanged += (_, _) => { if (!_loading && _p is not null) { SaveForm(); RenderForms(); } };

        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("Đóng", () => Post(new { action = "close" })));
        Layout(top, split);
        Select(-1);
    }

    // ---- dựng giao diện ----
    /// <summary>Lưới "nhãn | ô" như <see cref="NK.FormGrid"/> nhưng nhãn màu <see cref="Ink.Label"/> (dễ đọc hơn chữ phụ mặc định).</summary>
    private static TableLayoutPanel Fields(int labelWidth, params (string Label, Control Field)[] rows)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, field) in rows)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new FieldLabel { Text = label, AutoSize = true, MaximumSize = new Size(Math.Max(40, labelWidth - 8), 0), UseMnemonic = false, BackColor = Color.Transparent, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 10, 8, 4) };
            field.Dock = DockStyle.Fill; field.Margin = new Padding(0, 4, 0, 4);
            t.Controls.Add(l); t.Controls.Add(field);
        }
        return t;
    }

    private static Label Hint(string text) => NK.Muted(text, true);
    private static Label Caption(string text) => new FieldLabel { Text = text, AutoSize = true, UseMnemonic = false, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 4) };

    private static Panel Pane(params Control[] items)
    {
        var p = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent };
        NK.Stack(p, items);
        return p;
    }

    // ---- mô hình ----
    private JsonArray Forms => _p!["Forms"] as JsonArray ?? (JsonArray)(_p["Forms"] = new JsonArray());
    private JsonObject? CurForm => _p is not null && _sel >= 0 && _sel < Forms.Count ? Forms[_sel] as JsonObject : null;
    private static string S(JsonNode? n, string k) { if (n is not JsonObject o || o[k] is not { } v) return ""; return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToString(); }

    private void Collect()
    {
        if (_p is null) return;
        foreach (var (j, key) in ProjectFields) _p[j] = _in[key].Text;
        _p["AuthScheme"] = Auths[Math.Clamp(_auth.SelectedIndex, 0, Auths.Length - 1)];
        _p["Headers"] = _kvP.Get();
        SaveForm();
    }

    /// <summary>Header tĩnh: ẩn phần lấy token, khoá tài khoản / mật khẩu.</summary>
    private void AuthUi()
    {
        var tok = _auth.SelectedIndex != 2;
        _advCard.Visible = tok; _in["pUser"].Enabled = tok; _pass.Enabled = tok; _bToken.Enabled = tok;
        FormAuthUi();
    }

    private object Payload(string action, object? extra = null)
    {
        Collect();
        var d = new Dictionary<string, object?> { ["action"] = action, ["project"] = _p?.DeepClone(), ["originalName"] = _original, ["password"] = _pass.Text };
        if (extra is not null) foreach (var pr in extra.GetType().GetProperties()) d[pr.Name] = pr.GetValue(extra);
        return d;
    }

    private void SetDirty(bool d) { _isDirty = d; _dirty.Text = d ? "● chưa lưu" : ""; _dirty.ForeColor = Ink.Warn; }
    private void Busy(bool on) { _bToken.Enabled = !on && _auth.SelectedIndex != 2; _bSend.Enabled = !on && _sel >= 0; }

    /// <summary>Thanh trạng thái: màu pha từ theme cho dễ đọc (vàng cảnh báo / đỏ lỗi của theme tối quá tối trên nền tối).</summary>
    private void Status(string text, string? kind = null)
    {
        Say(text, kind);
        StatusLabel.ForeColor = kind switch { "err" => Ink.Danger, "ok" => Ink.Ok, "warn" => Ink.Warn, _ => Ink.Label };
    }

    private void FillProject()
    {
        _loading = true;
        foreach (var (j, key) in ProjectFields) _in[key].Text = _p is null ? "" : S(_p, j);
        _auth.SetItems(AuthLabels, _p is null ? 0 : Math.Max(0, Array.IndexOf(Auths, S(_p, "AuthScheme"))));
        _kvP.Set(_p?["Headers"]);
        _pass.Text = ""; _pass.PlaceholderText = _hasPassword ? "•••••• (đã lưu, mã hoá)" : "(chưa lưu)";
        _tokVal.Text = "(chưa lấy)"; _bCopyTok.Enabled = false; _token = "";
        _loading = false;
        AuthUi();
        RenderForms();
        Select(_p is not null && Forms.Count > 0 ? Math.Min(Math.Max(_sel, 0), Forms.Count - 1) : -1);
        SetDirty(_isDirty);
    }

    private void RenderForms()
    {
        _loading = true;
        var q = _filter.Text.Trim().ToLowerInvariant();
        _fList.BeginUpdate(); _fList.Items.Clear(); _view.Clear();
        if (_p is not null)
            for (var i = 0; i < Forms.Count; i++)
            {
                var f = Forms[i]; var name = S(f, "Name");
                if (q.Length > 0 && !name.ToLowerInvariant().Contains(q)) continue;
                _view.Add(i); _fList.Items.Add(name);
            }
        _fList.EndUpdate();
        var at = _view.IndexOf(_sel); if (at >= 0) _fList.SelectedIndex = at;
        _fCount.Text = _p is not null && Forms.Count > 0 ? $"({Forms.Count})" : "";
        _loading = false;
    }

    private void DrawForm(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _view.Count || _p is null) return;
        var f = Forms[_view[e.Index]]; var sel = (e.State & DrawItemState.Selected) != 0;
        using (var br = new SolidBrush(sel ? AppColors.Accent : AppColors.Input)) e.Graphics.FillRectangle(br, e.Bounds);
        var kind = S(f, "Kind"); var fore = sel ? AppColors.OnAccent : AppColors.Text; var muted = sel ? AppColors.OnAccent : Ink.Hint;
        var method = S(f, "Method").Length > 0 ? S(f, "Method").ToUpperInvariant() : "POST";
        var mColor = sel ? AppColors.OnAccent : method == "GET" ? Ink.Ok : method == "DELETE" ? Ink.Danger : Ink.Warn;
        var kw = TextRenderer.MeasureText(kind, _fList.Font).Width + 8;
        var mw = TextRenderer.MeasureText("DELETE", _fList.Font).Width;
        TextRenderer.DrawText(e.Graphics, method, _fList.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, mw, e.Bounds.Height), mColor, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, kind, _fList.Font, new Rectangle(e.Bounds.Right - kw - 6, e.Bounds.Y, kw, e.Bounds.Height), muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        var name = S(f, "Name"); if (name.Length == 0) name = "(chưa đặt tên)";
        TextRenderer.DrawText(e.Graphics, name, _fList.Font, new Rectangle(e.Bounds.X + mw + 12, e.Bounds.Y, e.Bounds.Width - kw - mw - 24, e.Bounds.Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static bool KvMode(string m) => m is "urlencoded" or "formdata";
    private string Mode => Modes[Math.Clamp(_fMode.SelectedIndex, 0, Modes.Length - 1)];

    private void Select(int i)
    {
        _sel = i; var f = CurForm;
        _loading = true;
        _fName.Text = f is null ? "" : S(f, "Name");
        _fKind.SetItems(KindLabels, f is null ? 2 : Math.Max(0, Array.IndexOf(Kinds, S(f, "Kind"))));
        _fEndpoint.Text = f is null ? "" : S(f, "Endpoint"); _fDesc.Text = f is null ? "" : S(f, "Description");
        var m = f is null || S(f, "Method").Length == 0 ? "POST" : S(f, "Method").ToUpperInvariant();
        var methods = Methods.Contains(m) ? Methods : Methods.Append(m).ToArray();
        _fMethod.SetItems(methods, Array.IndexOf(methods, m));
        var bm = f is null || S(f, "BodyMode").Length == 0 ? "raw" : S(f, "BodyMode");
        _fMode.SetItems(ModeLabels, Math.Max(0, Array.IndexOf(Modes, bm)));
        _fRaw.SetItems(RawLabels, f is null ? 0 : Math.Max(0, Array.IndexOf(RawTypes, S(f, "RawType"))));
        // Body raw và body key–value giữ riêng trong lúc sửa; lưu lấy theo kiểu đang chọn.
        _fBody.Text = f is not null && !KvMode(bm) ? S(f, "Body") : "";
        _kvB.SetRows(f is not null && KvMode(bm) ? ApiClient.Rows(S(f, "Body")) : new());
        _kvF.Set(f?["Headers"]); _kvQ.Set(f?["Params"]);
        FillAuth(f?["Auth"] as JsonObject);
        _loading = false;
        foreach (var c in new Control[] { _fName, _fKind, _fMethod, _fMode, _fRaw, _fEndpoint, _fDesc, _fBody, _bSend, _bValidate, _bFormat, _bFormDoc, _kvF, _kvQ, _kvB, _aType, _aUser, _aKey, _aIn, _aVal, _aEye }) c.Enabled = f is not null;
        _issuesText.Text = "";
        ModeUi(); FormAuthUi();
        RenderForms();
    }

    private void SaveForm()
    {
        var f = CurForm; if (f is null) return;
        f["Name"] = _fName.Text; f["Kind"] = Kinds[Math.Max(0, _fKind.SelectedIndex)]; f["Endpoint"] = _fEndpoint.Text; f["Description"] = _fDesc.Text;
        f["Method"] = _fMethod.SelectedIndex >= 0 ? _fMethod.Text : "POST"; f["BodyMode"] = Mode; f["RawType"] = RawTypes[Math.Clamp(_fRaw.SelectedIndex, 0, RawTypes.Length - 1)];
        f["Body"] = KvMode(Mode) ? _kvB.Text() : _fBody.Text.Length > 0 || Mode == "raw" ? _fBody.Text : _kvB.Text();
        f["Headers"] = _kvF.Get(); f["Params"] = _kvQ.Get(); f["Auth"] = ReadAuth();
    }

    private void ModeUi()
    {
        var m = Mode;
        _fBody.Visible = m == "raw"; _kvB.Visible = KvMode(m); _noneHint.Visible = m == "none";
        _fRaw.Visible = _bFormat.Visible = _bValidate.Visible = m == "raw";
        Counts();
    }

    private void ShowFTab(string t)
    {
        _pnParams.Visible = t == "params"; _pnAuth.Visible = t == "auth"; _pnHeaders.Visible = t == "headers"; _pnBody.Visible = t == "body";
        if (t == "headers") Inherited();
    }

    /// <summary>Số dòng đang bật trên nhãn tab (như Postman: "Headers (3)").</summary>
    private void Counts()
    {
        string C(int n) => n > 0 ? $" ({n})" : "";
        var labels = new[] { "Params" + C(_kvQ.Count), "Authorization" + (_aType.SelectedIndex > 0 ? " ●" : ""), "Headers" + C(_kvF.Count), "Body" + (Mode == "none" ? "" : KvMode(Mode) ? C(_kvB.Count) : " ●") };
        for (var i = 0; i < labels.Length && i < _fTabs.Controls.Count; i++) if (_fTabs.Controls[i].Text != labels[i]) _fTabs.Controls[i].Text = labels[i];
    }

    // ---- thanh địa chỉ: "...?a=1&b=2" → tách sang Params; xem trước URL đầy đủ ----
    private void SplitQuery()
    {
        var ep = _fEndpoint.Text; var i = ep.IndexOf('?');
        if (i < 0 || CurForm is null) return;
        var rows = _kvQ.GetRows();
        foreach (var part in ep[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            string Dec(string s) { try { return Uri.UnescapeDataString(s.Replace('+', ' ')); } catch { return s; } }
            rows.Add((Dec(eq < 0 ? part : part[..eq]), eq < 0 ? "" : Dec(part[(eq + 1)..]), false));
        }
        _fEndpoint.Text = ep[..i];
        _kvQ.SetRows(rows); SetDirty(true); Counts(); UrlPreview();
        Status($"Đã tách tham số query sang tab Params.", "ok"); _fTabs.Select("params", raise: true);
    }

    private void UrlPreview()
    {
        if (_p is null || _sel < 0) { _url.Text = ""; return; }
        var ep = _fEndpoint.Text.Trim(); var b = _in["pBase"].Text.Trim().TrimEnd('/');
        var url = ep.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? ep : b + "/" + ep.TrimStart('/');
        var q = _kvQ.GetSecretAware().Where(x => !x.Disabled).Select(x => Uri.EscapeDataString(x.Key) + "=" + (x.Secret ? Mask : Uri.EscapeDataString(x.Value))).ToList();
        if (_aType.SelectedIndex == 4 && _aIn.SelectedIndex == 1 && _aKey.Text.Trim().Length > 0) q.Add(Uri.EscapeDataString(_aKey.Text.Trim()) + "=" + Mask);
        if (q.Count > 0) url += (url.Contains('?') ? "&" : "?") + string.Join("&", q);
        _url.Text = "→ " + url;
    }

    /// <summary>Header gửi kèm mà bảng của form không có: header chung, header từ tab Authorization, token, Accept, Content-Type.</summary>
    private void Inherited()
    {
        var own = new HashSet<string>(_kvF.GetSecretAware().Where(h => !h.Disabled).Select(h => h.Key), StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string k, string v, string why) { if (!own.Contains(k) && seen.Add(k)) lines.Add($"{k}: {v}   ← {why}"); }
        var at = FormAuths[Math.Max(0, _aType.SelectedIndex)];
        if (at == "bearer") Add("Authorization", "Bearer " + Mask, "tab Authorization");
        else if (at == "basic") Add("Authorization", "Basic " + Mask, "tab Authorization");
        else if (at == "apikey" && _aIn.SelectedIndex == 0 && _aKey.Text.Trim().Length > 0) Add(_aKey.Text.Trim(), Mask, "tab Authorization");
        foreach (var h in _kvP.GetSecretAware().Where(h => !h.Disabled)) Add(h.Key, h.Secret ? Mask : h.Value, "Header chung (Kết nối)");
        if (at == "inherit" && _auth.SelectedIndex != 2) Add("Authorization", (_auth.SelectedIndex == 1 ? "Bearer " : "") + "<token>", "tự lấy token (Kết nối)");
        Add("Accept", "*/*", "tự thêm");
        if (Mode != "none") Add("Content-Type", Mode == "raw" ? ApiClient.RawContentType(RawTypes[Math.Max(0, _fRaw.SelectedIndex)]) : Mode == "formdata" ? "multipart/form-data; boundary=…" : "application/x-www-form-urlencoded", "theo kiểu Body");
        _inh.Text = lines.Count > 0 ? string.Join("\n", lines) : "(không có)";
    }

    // ---- tab Authorization của form ----
    private void FillAuth(JsonObject? a)
    {
        _aType.SetItems(FormAuthLabels, Math.Max(0, Array.IndexOf(FormAuths, a is null ? "inherit" : S(a, "Type"))));
        _aUser.Text = a is null ? "" : S(a, "Username"); _aKey.Text = a is null ? "" : S(a, "KeyName");
        _aIn.SetItems(new[] { "Header", "Query (?key=value)" }, a is not null && S(a, "In") == "query" ? 1 : 0);
        _aVal.Text = a is null ? "" : S(a, "Value"); _authSaved = a is null || S(a, "Value").Length > 0 ? "" : S(a, "ValueProtected"); _authShown = false;
    }

    private JsonObject? ReadAuth()
    {
        var t = FormAuths[Math.Max(0, _aType.SelectedIndex)];
        if (t == "inherit") return null;
        return new JsonObject { ["Type"] = t, ["Username"] = _aUser.Text, ["KeyName"] = _aKey.Text.Trim(), ["In"] = _aIn.SelectedIndex == 1 ? "query" : "header", ["Value"] = _aVal.Text, ["ValueProtected"] = _aVal.Text.Length > 0 ? "" : _authSaved };
    }

    private void FormAuthUi()
    {
        var t = FormAuths[Math.Max(0, _aType.SelectedIndex)];
        _aUserRow.Visible = t == "basic"; _aKeyRow.Visible = t == "apikey"; _aValRow.Visible = t is "bearer" or "basic" or "apikey";
        if (_aValRow.Controls.Count > 0) _aValRow.Controls[0].Text = t == "basic" ? "Mật khẩu" : t == "apikey" ? "Giá trị" : "Token";
        _aVal.Inner.UseSystemPasswordChar = !_authShown;
        _aVal.PlaceholderText = _authSaved.Length > 0 ? Saved : "";
        var scheme = _auth.SelectedIndex;
        _aHint.Text = t switch
        {
            "inherit" => scheme == 2 ? "Dùng \"Kết nối\" kiểu Header tĩnh: không lấy token, gửi Header chung (Authentication, TaxCode…) + header của form."
                : $"Dùng token của \"Kết nối\": tự gọi {(_in["pTokPath"].Text.Length > 0 ? _in["pTokPath"].Text : "api/getToken")} rồi gửi Authorization: {(scheme == 1 ? "Bearer " : "")}<token>.",
            "none" => "Không gửi xác thực, không lấy token. Header chung của Kết nối vẫn gửi — bỏ tick ở bảng Header chung nếu không muốn.",
            "bearer" => "Gửi Authorization: Bearer <token> — không lấy token từ Kết nối.",
            "basic" => "Gửi Authorization: Basic base64(tài khoản:mật khẩu).",
            _ => "Gửi <tên key>: <giá trị> ở header, hoặc thêm ?<tên key>=<giá trị> vào URL.",
        } + (t == "inherit" ? "" : " Header cùng tên trong tab Headers của form vẫn thắng. Giá trị lưu mã hoá trên máy này.");
        Counts(); UrlPreview();
    }

    private void ToggleAuthEye()
    {
        if (_authShown) { _authShown = false; FormAuthUi(); return; }
        if (_authSaved.Length > 0 && _aVal.Text.Length == 0) { Reveal("auth", new List<string> { _authSaved }); return; }
        _authShown = true; FormAuthUi();
    }

    /// <summary>Nhờ C# giải mã giá trị bí mật đã lưu (DPAPI) — kết quả về qua api.revealed(key, list).</summary>
    private void Reveal(string key, List<string> values) => Post(new { action = "reveal", key, values });

    private void AddBlank()
    {
        if (_p is null) return; SaveForm();
        Forms.Add(new JsonObject { ["Name"] = "formMoi", ["Kind"] = "GetData", ["Endpoint"] = "api/GetData", ["Method"] = "POST", ["BodyMode"] = "raw", ["RawType"] = "json", ["Headers"] = new JsonArray(), ["Params"] = new JsonArray(), ["Body"] = "{\n    \"form\": \"\",\n    \"Id\": \"\",\n    \"dateFrom\": \"{{monthStart}}\",\n    \"dateTo\": \"{{today}}\"\n}", ["Description"] = "" });
        Select(Forms.Count - 1); SetDirty(true); _fName.Inner.Focus(); _fName.Inner.SelectAll();
    }

    private void Duplicate() { if (_p is null || _sel < 0) return; SaveForm(); var c = (JsonObject)Forms[_sel]!.DeepClone(); c["Name"] = S(c, "Name") + " (2)"; Forms.Insert(_sel + 1, c); Select(_sel + 1); SetDirty(true); }
    private void DeleteForm() { if (_p is null || _sel < 0) return; Forms.RemoveAt(_sel); Select(Math.Min(_sel, Forms.Count - 1)); SetDirty(true); }
    private void Move(int d) { if (_p is null || _sel < 0) return; SaveForm(); var j = _sel + d; if (j < 0 || j >= Forms.Count) return; var n = Forms[_sel]; Forms.RemoveAt(_sel); Forms.Insert(j, n); Select(j); SetDirty(true); }

    private void Format()
    {
        if (Mode != "raw") { Status("Định dạng chỉ dùng cho body raw (JSON).", "warn"); return; }
        try { _fBody.Text = JsonNode.Parse(_fBody.Text)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("  ", "    "); SaveForm(); SetDirty(true); Status("Đã định dạng body.", "ok"); }
        catch (Exception ex) { Status("Body không phải JSON hợp lệ: " + ex.Message, "err"); }
    }

    private void Validate()
    {
        SaveForm();
        if (Mode != "raw") { Status("Kiểm tra (LT3) chỉ dùng cho body raw (JSON).", "warn"); return; }
        Post(new { action = "validate", name = _fName.Text, body = _fBody.Text });
    }
    private void Send(bool confirmWrite) { if (_p is null || _sel < 0) return; SaveForm(); Busy(true); Post(Payload("send", new { index = _sel, confirmWrite })); }

    private void FormDoc()
    {
        var form = _fName.Text;
        try { if (JsonNode.Parse(_fBody.Text) is JsonObject o && o["form"]?.ToString() is { Length: > 0 } fm) form = fm; } catch { /* body chưa hợp lệ */ }
        Post(new { action = "form-doc", form });
    }

    private void ExportPostman()
    {
        if (_p is null) return;
        Collect();
        var rows = new[] { _p["Headers"] }.Concat(Forms.SelectMany(f => new[] { f?["Headers"], f?["Params"] })).OfType<JsonArray>().SelectMany(a => a.OfType<JsonObject>());
        var secret = rows.Any(h => S(h, "Secret") == "true")
            || Forms.Any(f => f?["Auth"] is JsonObject a && S(a, "Type") is "bearer" or "basic" or "apikey" && (S(a, "Value").Length > 0 || S(a, "ValueProtected").Length > 0));
        var withSecrets = secret && NK.Confirm(FindForm()!, "Dự án có giá trị bí mật (header Authentication / Authorization, token, mật khẩu…).\n\nCó: kèm giá trị dạng chữ — file chạy ngay trong Postman, đừng chia sẻ / commit.\nKhông: thay bằng biến collection để trống.", "Xuất Postman");
        Post(Payload("export-postman", new { withSecrets }));
    }

    private void SaveProject() { if (_p is not null) Post(Payload("save")); }

    private void DeleteProject()
    {
        if (_original.Length == 0) return;
        if (NK.Confirm(FindForm()!, $"Xoá dự án \"{_original}\" khỏi máy này (file trong thư mục ApiProjects)?", "Xoá dự án?")) Post(new { action = "delete", name = _original });
    }

    private void ShowTab(string t)
    {
        _rTabs.Select(t, raise: false);
        _rTables.Visible = t == "tables"; _rResp.Visible = t == "body"; _rHdrs.Visible = t == "hdrs"; _rReq.Visible = t == "req";
        _rFmt.Visible = t == "body";
    }

    private void CopyResult()
    {
        if (_lastResult.ValueKind != JsonValueKind.Object) return;
        var t = _rTabs.Value;
        var text = t == "hdrs" ? P(_lastResult, "responseHeaders") : t == "req" ? _rReq.Text : t == "body" && _rFmt.Value == "pretty" ? Pretty(P(_lastResult, "responseText")) : P(_lastResult, "responseText");
        Post(new { action = "copy", text });
    }

    private void OpenPicker(bool isNew)
    {
        var have = new HashSet<string>(!isNew && _p is not null ? Forms.Select(f => S(f, "Name").ToLowerInvariant()) : Enumerable.Empty<string>());
        using var dlg = new TemplatePicker(isNew, _catalog, have);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (isNew) Post(new { action = "new-template", name = dlg.ProjectName, projectId = dlg.ProjectId, baseUrl = dlg.BaseUrl, forms = dlg.Forms });
        else if (dlg.Forms.Count > 0) { SaveForm(); Post(new { action = "add-forms", forms = dlg.Forms }); }
    }

    protected override bool HandleKey(Keys k)
    {
        if (k == (Keys.Control | Keys.Shift | Keys.Enter)) { Send(false); return true; }
        if (k == (Keys.Control | Keys.S)) { SaveProject(); return true; }
        if (k == (Keys.Control | Keys.Shift | Keys.T)) { if (_p is not null) { Busy(true); Post(Payload("token")); } return true; }
        if (k == (Keys.Control | Keys.Shift | Keys.F)) { Format(); return true; }
        if (k == (Keys.Control | Keys.Shift | Keys.K)) { Validate(); return true; }
        return false;
    }

    // ---- gọi từ C# ----
    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "api.init":
            {
                var st = Arg(a, 0);
                _projectNames = PA(st, "projects").Select(n => n.GetString() ?? "").ToList();
                _catalog = PA(st, "catalog").Select(c => new CatalogItem { Form = P(c, "form"), Kind = P(c, "kind"), Title = P(c, "title") }).ToList();
                _docUrl = P(st, "docUrl");
                _loading = true;
                _projects.SetItems(new[] { $"— chọn dự án ({_projectNames.Count}) —" }.Concat(_projectNames).ToList(), Math.Max(0, _projectNames.IndexOf(_original) + 1));
                _loading = false;
                var old = PI(st, "oldProfiles"); _bOld.Visible = old > 0; _bOld.Text = $"Chuyển {old} hồ sơ cũ";
                if (_p is null) { FillProject(); Status(_projectNames.Count > 0 ? "Chọn dự án, hoặc \"＋ Mới từ mẫu LT3\" / \"Nhập Postman…\". Hồ sơ lưu riêng trên máy này: " + P(st, "folder") : "Chưa có dự án — bấm \"＋ Mới từ mẫu LT3\" (31 form chuẩn) hoặc \"Nhập Postman…\"."); }
                _kb.Text = "Gửi: Ctrl+Shift+Enter";
                break;
            }
            case "api.project":
            {
                var o = Arg(a, 0);
                _p = JsonNode.Parse(PO(o, "project").GetRawText()) as JsonObject;
                _hasPassword = PB(o, "hasPassword"); _sel = 0;
                _original = PB(o, "dirty") ? "" : (_p is null ? "" : S(_p, "Name"));
                if (!PB(o, "dirty")) { _loading = true; _projects.SetItems(new[] { $"— chọn dự án ({_projectNames.Count}) —" }.Concat(_projectNames).ToList(), Math.Max(0, _projectNames.IndexOf(_original) + 1)); _loading = false; }
                _issuesText.Text = ""; _notesText.Text = ""; _resCard.Visible = false;
                _isDirty = PB(o, "dirty");
                FillProject();
                break;
            }
            case "api.addForms":
                if (_p is null || a.Count == 0 || a[0].ValueKind != JsonValueKind.Array) break;
                { var n = 0; foreach (var f in a[0].EnumerateArray()) { Forms.Add(JsonNode.Parse(f.GetRawText())); n++; } Select(Forms.Count - 1); SetDirty(true); Status($"Đã thêm {n} form từ mẫu LT3.", "ok"); }
                break;
            case "api.cleared": _p = null; _original = ""; _sel = -1; FillProject(); SetDirty(false); break;
            case "api.token":
            {
                var t = Arg(a, 0); _token = P(t, "full");
                _tokVal.Text = P(t, "masked") + " · hết hạn " + P(t, "expires"); _bCopyTok.Enabled = true; Busy(false);
                break;
            }
            case "api.revealed":
            {
                var key = Str(a, 0);
                var list = (a.Count > 1 && a[1].ValueKind == JsonValueKind.Array ? a[1].EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(x => x.GetString() ?? "").ToList();
                if (key == "auth") { _loading = true; _aVal.Text = list.FirstOrDefault() ?? ""; _loading = false; _authShown = true; FormAuthUi(); break; }
                foreach (var kv in new[] { _kvP, _kvF, _kvQ, _kvB }) if (kv.Key == key) kv.Revealed(list);
                break;
            }
            case "api.result": ShowResult(Arg(a, 0)); break;
            case "api.issues":
            {
                var list = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(x => x.GetString() ?? "").ToList();
                _issuesText.ForeColor = list.Count > 0 ? Ink.Danger : Ink.Ok;
                _issuesText.Text = list.Count > 0 ? string.Join("\n", list.Select(x => "• " + x)) : "✓ Body khớp bảng trường chuẩn LT3 (hoặc form riêng của dự án — không có bảng để so).";
                break;
            }
            case "api.notes": _notesText.Text = string.Join("\n", (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(x => "• " + (x.GetString() ?? ""))); break;
            case "api.formDoc": ShowDoc(Arg(a, 0)); break;
            case "api.confirmWrite":
                if (NK.Confirm(FindForm()!, $"Form \"{Str(a, 0)}\" là {Kinds[Math.Max(0, _fKind.SelectedIndex)]} — sẽ GHI dữ liệu vào hệ thống Fast của dự án ({_in["pBase"].Text}). Chỉ gửi lên môi trường test / khi chắc chắn.", "Gửi form ghi dữ liệu?")) Send(true); else Busy(false);
                break;
            case "api.docPath": _in["pDoc"].Text = Str(a, 0); SetDirty(true); break;
            case "api.busy": Busy(Bool(a, 0)); break;
            case "api.status": Status(Str(a, 0), Str(a, 1)); break;
        }
    }

    private void ShowResult(JsonElement r)
    {
        Busy(false);
        _lastResult = r.Clone();
        _resCard.Visible = true;
        var ok = PB(r, "ok");
        _rPill.Text = P(r, "status") + " " + P(r, "reason") + (ok ? "" : " · Lỗi"); _rPill.ForeColor = ok ? Ink.Ok : Ink.Danger;
        var size = r.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
        _rMeta.Text = $"Thời gian {P(r, "ms")} ms  ·  Kích thước {FmtSize(size)}" + (P(r, "contentType").Length > 0 ? " · " + P(r, "contentType") : "") + (P(r, "message").Length > 0 ? "  ·  " + P(r, "message") : "");
        _rUrl.Text = P(r, "url");
        RenderBody();
        _rReq.Text = (P(r, "requestHead") is { Length: > 0 } head ? head.Replace("\n", "\r\n") : "POST " + P(r, "url")) + (P(r, "requestBody").Length > 0 ? "\r\n\r\n" + Pretty(P(r, "requestBody")).Replace("\n", "\r\n") : "");
        _rHdrs.Rows.Clear();
        foreach (var line in P(r, "responseHeaders").Split('\n', StringSplitOptions.RemoveEmptyEntries)) { var i = line.IndexOf(':'); _rHdrs.Rows.Add(i < 0 ? line : line[..i], i < 0 ? "" : line[(i + 1)..].Trim()); }
        _rTables.Controls.Clear();
        var tables = PA(r, "tables").ToList();
        if (tables.Count == 0) _rTables.Controls.Add(new Label { Text = "Response không có dữ liệu dạng bảng — xem tab Body.", AutoSize = true, ForeColor = Ink.Hint, BackColor = Color.Transparent, Dock = DockStyle.Top });
        foreach (var t in Enumerable.Reverse(tables))
        {
            var rows = PA(t, "rows").ToList();
            var g = NK.Grid(); g.Dock = DockStyle.Top; g.Height = Math.Min(320, 30 + 24 * Math.Min(rows.Count + 1, 12));
            g.Columns.Add("#", "#");
            foreach (var c in PA(t, "columns")) g.Columns.Add(c.GetString() ?? "", c.GetString() ?? "");
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            var i = 0;
            foreach (var row in rows.Take(500)) g.Rows.Add(new object[] { ++i }.Concat(row.EnumerateArray().Select(v => (object)(v.ValueKind == JsonValueKind.Null ? "null" : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()))).ToArray());
            var cap = new Label { Text = P(t, "name") + $"  ({rows.Count} dòng)", AutoSize = true, BackColor = Color.Transparent, Dock = DockStyle.Top, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold), Padding = new Padding(0, 6, 0, 2) };
            _rTables.Controls.Add(g); _rTables.Controls.Add(cap);
        }
        // Tab người dùng chọn lần trước được giữ; chưa chọn thì có bảng → Bảng, không → Body.
        var want = _rTab.Length > 0 ? _rTab : tables.Count > 0 ? "tables" : "body";
        ShowTab(want == "tables" && tables.Count == 0 ? "body" : want);
    }

    private void RenderBody()
    {
        if (_lastResult.ValueKind != JsonValueKind.Object) return;
        var t = P(_lastResult, "responseText");
        _rResp.Text = (t.Length == 0 ? "(body rỗng)" : _rFmt.Value == "pretty" ? Pretty(t) : t).Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private static string FmtSize(long n) => n < 1024 ? n + " B" : n < 1048576 ? (n / 1024.0).ToString(n < 10240 ? "0.00" : "0.0") + " KB" : (n / 1048576.0).ToString("0.00") + " MB";
    private static string Pretty(string s) { try { return JsonNode.Parse(s)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); } catch { return s; } }

    private void ShowDoc(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object) { Status("Form này không có trong tài liệu chuẩn LT3 (form riêng của dự án).", "warn"); return; }
        using var dlg = new ThemedForm { Text = "Tài liệu form — " + P(d, "form"), Width = NativeSkin.S(900), Height = NativeSkin.S(640), StartPosition = FormStartPosition.CenterParent, ShowIcon = false };
        var head = NK.Muted($"{P(d, "form")}  [{P(d, "kind")}]  {P(d, "title")}  ·  POST {P(d, "endpoint")}\n{P(d, "description")}", true); head.Dock = DockStyle.Top; head.Padding = new Padding(12, 10, 12, 4);
        var g = NK.Grid(("sec", "Nhóm", 130), ("name", "Trường", 200), ("type", "Kiểu", 100), ("req", "Bắt buộc", 70), ("desc", "Mô tả", 0)); g.Dock = DockStyle.Fill;
        foreach (var s in PA(d, "sections")) foreach (var f in PA(s, "fields")) g.Rows.Add(P(s, "label").Length > 0 ? P(s, "label") : P(s, "name"), P(f, "name"), P(f, "type"), PB(f, "required") ? "✔" : "", P(f, "description"));
        var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
        foot.Controls.Add(NK.Button("Đóng", dlg.Close, primary: true)); foot.Controls.Add(NK.Button("Mở trang tài liệu ↗", () => Post(new { action = "open-path", path = P(d, "doc") })));
        dlg.Controls.Add(g); dlg.Controls.Add(head); dlg.Controls.Add(foot);
        dlg.ShowDialog(FindForm());
    }

    /// <summary>Bảng key–value kiểu Postman (Params / Headers / Body form-data): ✓ bật/tắt từng dòng · Key · Value · 🔒 bí mật; dòng trống cuối để thêm, Delete xoá dòng.
    /// Giá trị bí mật hiện •••••• (đã lưu: ô trống = giữ bản mã hoá cũ); "👁 Hiện bí mật" giải mã (qua C#) để xem / sửa.</summary>
    private sealed class KvGrid : Panel
    {
        public readonly string Key;
        private readonly DataGridView _g;
        private readonly bool _secret;
        private readonly Action<string, List<string>> _reveal;
        private readonly PillButton? _bEye;
        private readonly List<DataGridViewRow> _pending = new();
        private bool _shown, _filling;
        public event Action? Changed;

        public KvGrid(string key, string keyTitle, bool secret, Action<string, List<string>> reveal)
        {
            Key = key; _secret = secret; _reveal = reveal;
            Dock = DockStyle.Top; BackColor = Color.Transparent;
            _g = NK.Grid(("key", keyTitle, 0), ("value", "Giá trị", 0));
            _g.ReadOnly = false; _g.AllowUserToAddRows = true; _g.AllowUserToDeleteRows = true; _g.SelectionMode = DataGridViewSelectionMode.CellSelect; _g.MultiSelect = true;
            _g.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            _g.Columns["key"]!.FillWeight = 40; _g.Columns["value"]!.FillWeight = 60;
            _g.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "✓", Width = NativeSkin.S(34), SortMode = DataGridViewColumnSortMode.NotSortable, ToolTipText = "Bỏ tick = giữ dòng nhưng không gửi" });
            if (secret) _g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sec", HeaderText = "🔒", Width = NativeSkin.S(38), SortMode = DataGridViewColumnSortMode.NotSortable, ToolTipText = "Bí mật: lưu mã hoá trên máy này, hiện ••••••" });
            _g.Columns.Add(new DataGridViewTextBoxColumn { Name = "prot", Visible = false });
            _g.Dock = DockStyle.Fill;
            _g.CurrentCellDirtyStateChanged += (_, _) => { if (_g.IsCurrentCellDirty && _g.CurrentCell is DataGridViewCheckBoxCell) _g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _g.DefaultValuesNeeded += (_, e) => { e.Row.Cells["on"].Value = true; if (_secret) e.Row.Cells["sec"].Value = false; e.Row.Cells["prot"].Value = ""; };
            _g.CellValueChanged += (_, e) =>
            {
                if (_filling || e.RowIndex < 0) return;
                var row = _g.Rows[e.RowIndex];
                // Tên kiểu Authorization / token / key… → tự đánh dấu bí mật (khi chưa có giá trị đã lưu).
                if (_secret && _g.Columns[e.ColumnIndex].Name == "key" && Str(row, "prot").Length == 0 && Str(row, "value").Length == 0)
                { _filling = true; row.Cells["sec"].Value = ApiProjectStore.IsSecretHeader(Str(row, "key")); _filling = false; }
                _g.InvalidateRow(e.RowIndex);
                Changed?.Invoke();
            };
            _g.UserDeletedRow += (_, _) => Changed?.Invoke();
            _g.CellFormatting += (_, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= _g.Rows.Count || _g.Rows[e.RowIndex].IsNewRow) return;
                var row = _g.Rows[e.RowIndex];
                if (row.Cells["on"].Value is false) e.CellStyle!.ForeColor = Ink.Hint;
                if (_secret && _g.Columns[e.ColumnIndex].Name == "value" && row.Cells["sec"].Value is true && !_shown)
                {
                    var v = Str(row, "value");
                    e.Value = v.Length > 0 ? new string('•', Math.Min(12, Math.Max(6, v.Length))) : Str(row, "prot").Length > 0 ? Saved : "";
                    if (v.Length == 0) e.CellStyle!.ForeColor = Ink.Hint;
                    e.FormattingApplied = true;
                }
            };
            _g.EditingControlShowing += (_, e) =>
            {
                if (e.Control is not TextBox tb) return;
                var c = _g.CurrentCell;
                tb.UseSystemPasswordChar = _secret && c is not null && _g.Columns[c.ColumnIndex].Name == "value" && _g.Rows[c.RowIndex].Cells["sec"].Value is true && !_shown;
            };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent };
            bar.Controls.Add(NK.Button("Xoá dòng", DeleteRows, tip: "Xoá các dòng đang chọn (hoặc phím Delete)"));
            if (secret) { _bEye = NK.Button("👁 Hiện bí mật", ToggleEye, tip: "Hiện / ẩn giá trị bí mật (giải mã giá trị đã lưu)"); bar.Controls.Add(_bEye); }
            Controls.Add(_g); Controls.Add(bar);
            Height = NativeSkin.S(190);
        }

        private static string Str(DataGridViewRow r, string col) => r.DataGridView!.Columns.Contains(col) ? r.Cells[col].Value?.ToString() ?? "" : "";



        private IEnumerable<DataGridViewRow> Real => _g.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow);

        public int Count => Real.Count(r => Str(r, "key").Trim().Length > 0 && r.Cells["on"].Value is not false);

        public void Set(JsonNode? list)
        {
            _filling = true; _shown = false; if (_bEye is not null) _bEye.Text = "👁 Hiện bí mật";
            _g.Rows.Clear();
            foreach (var h in (list as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var i = _g.Rows.Add();
                var r = _g.Rows[i];
                r.Cells["on"].Value = S(h, "Disabled") != "true"; r.Cells["key"].Value = S(h, "Key"); r.Cells["value"].Value = S(h, "Value"); r.Cells["prot"].Value = S(h, "ValueProtected");
                if (_secret) r.Cells["sec"].Value = S(h, "Secret") == "true";
            }
            _filling = false;
        }

        public void SetRows(List<(string Key, string Value, bool Disabled)> rows) =>
            Set(new JsonArray(rows.Select(x => (JsonNode)new JsonObject { ["Key"] = x.Key, ["Value"] = x.Value, ["Disabled"] = x.Disabled }).ToArray()));

        public List<(string Key, string Value, bool Disabled)> GetRows() =>
            Real.Where(r => Str(r, "key").Trim().Length > 0).Select(r => (Str(r, "key").Trim(), Str(r, "value"), r.Cells["on"].Value is false)).ToList();

        public List<(string Key, string Value, bool Secret, bool Disabled)> GetSecretAware() =>
            Real.Where(r => Str(r, "key").Trim().Length > 0).Select(r => (Str(r, "key").Trim(), Str(r, "value"), _secret && r.Cells["sec"].Value is true, r.Cells["on"].Value is false)).ToList();

        /// <summary>Dòng → JSON (ApiHeader): giá trị bí mật chưa sửa giữ bản mã hoá cũ (ValueProtected), C# mã hoá giá trị mới khi lưu.</summary>
        public JsonArray Get()
        {
            _g.EndEdit();
            var arr = new JsonArray();
            foreach (var r in Real)
            {
                var k = Str(r, "key").Trim(); if (k.Length == 0) continue;
                var v = Str(r, "value"); var sec = _secret && r.Cells["sec"].Value is true;
                arr.Add(new JsonObject { ["Key"] = k, ["Value"] = v, ["Secret"] = sec, ["ValueProtected"] = v.Length > 0 ? "" : Str(r, "prot"), ["Disabled"] = r.Cells["on"].Value is false });
            }
            return arr;
        }

        /// <summary>Body form-data / urlencoded: mỗi dòng "key: value", dòng tắt "// key: value" (như <see cref="ApiClient.Rows"/>).</summary>
        public string Text() => string.Join("\n", GetRows().Select(x => (x.Disabled ? "// " : "") + x.Key + ": " + x.Value));

        private void DeleteRows()
        {
            var rows = _g.SelectedCells.Cast<DataGridViewCell>().Select(c => c.OwningRow).Concat(_g.CurrentRow is { } cr ? new[] { cr } : Array.Empty<DataGridViewRow>()).Distinct().Where(r => !r.IsNewRow).ToList();
            foreach (var r in rows) _g.Rows.Remove(r);
            if (rows.Count > 0) Changed?.Invoke();
        }

        private void ToggleEye()
        {
            _g.EndEdit();
            if (_shown) { _shown = false; _bEye!.Text = "👁 Hiện bí mật"; _g.Invalidate(); return; }
            _pending.Clear();
            _pending.AddRange(Real.Where(r => r.Cells["sec"].Value is true && Str(r, "value").Length == 0 && Str(r, "prot").Length > 0));
            if (_pending.Count == 0) { Revealed(new()); return; }
            _reveal(Key, _pending.Select(r => Str(r, "prot")).ToList());
        }

        /// <summary>Kết quả giải mã (cùng thứ tự <see cref="_pending"/>): điền giá trị chữ để xem / sửa (lưu lại sẽ mã hoá lại).</summary>
        public void Revealed(List<string> plain)
        {
            _filling = true;
            for (var i = 0; i < _pending.Count && i < plain.Count; i++) if (_pending[i].DataGridView == _g) _pending[i].Cells["value"].Value = plain[i];
            _filling = false; _pending.Clear();
            _shown = true; if (_bEye is not null) _bEye.Text = "🙈 Ẩn bí mật"; _g.Invalidate();
        }
    }

    /// <summary>Chọn form từ mẫu chuẩn LT3: tạo dự án mới (kèm tên / mã / địa chỉ gốc) hoặc thêm form vào dự án đang mở.</summary>
    private sealed class TemplatePicker : ThemedForm
    {
        private readonly TreeView _tree = new() { CheckBoxes = true, Dock = DockStyle.Fill, HideSelection = false };
        private readonly RoundedInput _name = NK.Input("vd NISSHIN_FBO"), _id = NK.Input("vd NISSHIN"), _base = NK.Input("http://172.168.5.14/NISSHIN_API", mono: true);
        public string ProjectName => _name.Text.Trim(); public string ProjectId => _id.Text.Trim(); public string BaseUrl => _base.Text.Trim();
        public List<string> Forms { get; private set; } = new();

        public TemplatePicker(bool isNew, List<CatalogItem> catalog, HashSet<string> have)
        {
            Text = isNew ? "Dự án mới từ mẫu chuẩn LT3" : "Thêm form từ mẫu chuẩn LT3"; Width = NativeSkin.S(720); Height = NativeSkin.S(700); StartPosition = FormStartPosition.CenterParent; ShowIcon = false;
            var top = new Panel { Dock = DockStyle.Top, AutoSize = true, Visible = isNew, Padding = new Padding(12, 8, 12, 0) };
            top.Controls.Add(NK.FormGrid(130, ("Tên dự án", _name), ("Mã dự án", _id), ("Địa chỉ gốc", _base)));
            var groups = new[] { ("SyncData", "Đồng bộ danh mục (SyncData)"), ("SyncVoucher", "Đồng bộ chứng từ (SyncVoucher)"), ("GetData", "Lấy dữ liệu (GetData)") };
            _tree.BeginUpdate();
            foreach (var (k, label) in groups)
            {
                var g = new TreeNode(label);
                foreach (var f in catalog.Where(c => c.Kind == k))
                {
                    var disabled = have.Contains(f.Form.ToLowerInvariant());
                    g.Nodes.Add(new TreeNode(f.Form + "  —  " + f.Title) { Tag = disabled ? null : f.Form, Checked = isNew && !disabled, ForeColor = disabled ? AppColors.TextMuted : AppColors.Text });
                }
                _tree.Nodes.Add(g); g.Expand();
            }
            _tree.EndUpdate();
            _tree.AfterCheck += (_, e) => { if (e.Action != TreeViewAction.Unknown) { if (e.Node!.Tag is null && e.Node.Parent is not null) e.Node.Checked = false; else foreach (TreeNode c in e.Node.Nodes) if (c.Tag is not null) c.Checked = e.Node.Checked; } };
            var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
            foot.Controls.Add(NK.Button(isNew ? "Tạo" : "Thêm", () => { Forms = _tree.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>()).Where(n => n.Checked && n.Tag is string).Select(n => (string)n.Tag!).ToList(); DialogResult = DialogResult.OK; }, primary: true));
            foot.Controls.Add(NK.Button("Huỷ", () => DialogResult = DialogResult.Cancel));
            Controls.Add(_tree); Controls.Add(top); Controls.Add(foot);
        }
    }

    public override void FocusPrimary() { }
}
