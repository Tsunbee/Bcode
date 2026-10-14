using System.Text.Json;
using System.Text.Json.Nodes;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Khai báo & quản lý API chuẩn LT3 (apideclaration.html): dự án (kết nối + token), danh sách form, soạn body JSON, kiểm tra theo bảng trường LT3, gửi và xem kết quả dạng bảng.</summary>
internal sealed class ApiDeclarationPage : DialogPage
{
    private sealed class CatalogItem { public string Form = "", Kind = "", Title = ""; }

    private static readonly (string Json, string Key)[] ProjectFields =
    {
        ("Name", "pName"), ("ProjectId", "pId"), ("Department", "pDept"), ("BaseUrl", "pBase"), ("Username", "pUser"), ("TokenPath", "pTokPath"),
        ("TokenField", "pTokField"), ("ExpiresField", "pExpField"), ("TokenBody", "pTokBody"), ("DocPath", "pDoc"), ("Note", "pNote"),
    };
    private static readonly string[] Kinds = { "SyncData", "SyncVoucher", "GetData", "custom" };
    private static readonly string[] KindLabels = { "SyncData — đồng bộ danh mục", "SyncVoucher — đồng bộ chứng từ", "GetData — lấy dữ liệu", "Khác" };

    private readonly Dictionary<string, RoundedInput> _in = new();
    private readonly DropDownButton _projects = new() { Width = NativeSkin.S(260) }, _auth = new(), _fKind = new();
    private readonly RoundedInput _pass = NK.Input(""), _fName = NK.Input(""), _fEndpoint = NK.Input("api/GetData", mono: true), _fDesc = NK.Input(""), _fBody = NK.Memo(14, "", mono: true), _filter = NK.Input("Lọc theo tên form…");
    private readonly Label _tokVal = NK.Muted("(chưa lấy)"), _dirty = new Label { AutoSize = true, ForeColor = AppColors.Warning, BackColor = Color.Transparent, Margin = new Padding(8, 8, 0, 0) }, _kb = NK.Muted(""), _fCount = NK.Muted("");
    private readonly ListBox _fList = new() { Dock = DockStyle.Top, Height = NativeSkin.S(220), IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed };
    
    private readonly Label _issuesText = NK.Muted("", true), _notesText = NK.Muted("", true), _rPill = new() { AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold) }, _rMeta = NK.Muted("");
    private readonly CardPanel _resCard;
    private readonly Panel _rTables = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly RoundedInput _rResp = NK.Memo(12, "", mono: true), _rReq = NK.Memo(12, "", mono: true);
    private readonly PillButton _bToken, _bSend, _bCopyTok, _bOld, _bValidate, _bFormat, _bFormDoc, _bTabTables, _bTabResp, _bTabReq;
    private readonly List<int> _view = new();
    private JsonObject? _p;
    private List<string> _projectNames = new();
    private List<CatalogItem> _catalog = new();
    private string _docUrl = "", _original = "", _token = "", _lastResponse = "";
    private int _sel = -1;
    private bool _isDirty, _hasPassword, _loading;

    public ApiDeclarationPage()
    {
        foreach (var (_, key) in ProjectFields) _in[key] = NK.Input("", mono: key is "pBase" or "pTokPath" or "pTokField" or "pExpField");
        _in["pTokBody"] = NK.Memo(4, "", mono: true); _in["pNote"] = NK.Memo(3, "", mono: false);
        _pass.Inner.UseSystemPasswordChar = true;
        _auth.SetItems(new[] { "Authorization: <token> (chuẩn LT3)", "Authorization: Bearer <token>" }, 0);
        _fKind.SetItems(KindLabels, 2);
        _fBody.Dock = DockStyle.Top; _fBody.Height = NativeSkin.S(260);

        // ---- thanh trên ----
        _bOld = NK.Button("Chuyển hồ sơ cũ", () => Post(new { action = "import-old" }), tip: "Chuyển hồ sơ kiểu cũ (mật khẩu chữ thường) sang dạng mới, mật khẩu mã hoá"); _bOld.Visible = false;
        var top = TopBar();
        top.Controls.AddRange(new Control[]
        {
            NK.Text("Dự án"), _projects,
            NK.Button("＋ Mới từ mẫu LT3", () => OpenPicker(true), primary: true, tip: "Tạo dự án mới từ mẫu chuẩn phòng LT3 (31 form của tài liệu API)"),
            NK.Button("Nhập Postman…", () => Post(new { action = "import-postman" })), _bOld,
            NK.Button("Lưu", SaveProject, tip: "Lưu dự án (Ctrl+S)"), NK.Button("Xuất Postman…", () => { if (_p is not null) Post(Payload("export-postman")); }),
            NK.Button("Xoá", DeleteProject), _dirty, NK.Button("Tài liệu API LT3 ↗", () => Post(new { action = "open-path", path = _docUrl })),
        });
        _projects.SelectionChanged += i => { if (!_loading && i > 0 && i - 1 < _projectNames.Count) Post(new { action = "open", name = _projectNames[i - 1] }); };

        // ---- cột trái ----
        var cConn = new CardPanel("Kết nối") { Dock = DockStyle.Top };
        _bToken = NK.Button("Lấy token", () => { if (_p is not null) { Busy(true); Post(Payload("token")); } }, tip: "Lấy token mới (Ctrl+Shift+T)");
        _bCopyTok = NK.Button("Chép", () => Post(new { action = "copy", text = _token }), tip: "Chép token đầy đủ"); _bCopyTok.Enabled = false;
        var docRow = new Panel { Height = NativeSkin.RowHeight(this, 28) };
        var bDoc = NK.Button("…", () => Post(new { action = "browse-doc" })); var bOpen = NK.Button("Mở", () => Post(new { action = "open-path", path = _in["pDoc"].Text }));
        bDoc.Dock = DockStyle.Right; bOpen.Dock = DockStyle.Right; _in["pDoc"].Dock = DockStyle.Fill; docRow.Controls.Add(_in["pDoc"]); docRow.Controls.Add(bDoc); docRow.Controls.Add(bOpen);
        var advCard = new CardPanel("Lấy token (nâng cao)") { Dock = DockStyle.Top };
        NK.Stack(advCard.Body,
            NK.FormGrid(110, ("Đường dẫn", _in["pTokPath"]), ("Trường token", _in["pTokField"]), ("Trường hạn", _in["pExpField"]), ("Body", _in["pTokBody"])),
            NK.Muted("{{username}} / {{password}} được thay lúc gửi. Response chuẩn LT3: token.accesstoken, token.expires.", true));
        NK.Stack(cConn.Body,
            NK.FormGrid(110, ("Tên dự án", _in["pName"]), ("Mã dự án", _in["pId"]), ("Bộ phận", _in["pDept"]), ("Địa chỉ gốc", _in["pBase"]), ("Tài khoản", _in["pUser"]), ("Mật khẩu", _pass), ("Header", _auth)),
            advCard,
            NK.Row(_bToken, _tokVal, _bCopyTok),
            NK.FormGrid(110, ("Tài liệu", docRow), ("Ghi chú", _in["pNote"])));

        var cForms = new CardPanel("Form") { Dock = DockStyle.Top };
        _fList.ItemHeight = (int)(ThemeManager.BaseFont.Height * 1.7);
        _fList.DrawItem += DrawForm;
        _fList.SelectedIndexChanged += (_, _) => { if (_loading || _fList.SelectedIndex < 0 || _fList.SelectedIndex >= _view.Count) return; SaveForm(); Select(_view[_fList.SelectedIndex]); };
        _filter.TextChanged += (_, _) => RenderForms();
        var fBtns = NK.Row(
            NK.Button("＋ Từ mẫu LT3", () => { if (_p is not null) OpenPicker(false); }, tip: "Thêm form từ mẫu chuẩn LT3"), NK.Button("＋ Form trống", AddBlank), NK.Button("Nhân bản", Duplicate),
            NK.Button("▲", () => Move(-1), tip: "Lên"), NK.Button("▼", () => Move(1), tip: "Xuống"), NK.Button("Xoá form", DeleteForm));
        NK.Stack(cForms.Body, _fCount, _filter, _fList, fBtns);

        var leftCol = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 6, 6, 6) };
        NK.Stack(leftCol, cConn, cForms);

        // ---- cột phải ----
        var cSel = new CardPanel("Form đang chọn") { Dock = DockStyle.Top };
        _bFormDoc = NK.Button("Tài liệu form", FormDoc, tip: "Bảng trường theo tài liệu chuẩn LT3");
        _bFormat = NK.Button("Định dạng", Format, tip: "Định dạng JSON (Ctrl+Shift+F)"); _bValidate = NK.Button("Kiểm tra (LT3)", Validate, tip: "Kiểm tra theo bảng trường chuẩn LT3 (Ctrl+Shift+K)");
        _bSend = NK.Button("Gửi ▶", () => Send(false), primary: true, tip: "Gửi (Ctrl+Shift+Enter)");
        var hint = NK.Muted("Biến: {{today}} (yyyy-MM-dd) · {{now}} · {{key}} (khoá duy nhất mỗi lần gửi — dùng cho VoucherId) · {{year}} · {{month}} · {{monthStart}}", true);
        NK.Stack(cSel.Body,
            NK.FormGrid(110, ("Tên", _fName), ("Loại", _fKind), ("Endpoint", _fEndpoint), ("Mô tả", _fDesc)),
            NK.Row(NK.Muted("Body (JSON)"), _bFormat, _bValidate, _bSend, _bFormDoc, _kb),
            _fBody, hint, _issuesText, _notesText);

        _resCard = new CardPanel("Kết quả") { Dock = DockStyle.Top, Visible = false };
        _bTabTables = NK.Button("Bảng", () => ShowTab(0), primary: true); _bTabResp = NK.Button("Response", () => ShowTab(1)); _bTabReq = NK.Button("Request", () => ShowTab(2));
        _rResp.ReadOnly = _rReq.ReadOnly = true; _rResp.Visible = _rReq.Visible = false; _rResp.Height = _rReq.Height = NativeSkin.S(300);
        NK.Stack(_resCard.Body,
            NK.Row(_rPill, _rMeta, NK.Button("Chép response", () => Post(new { action = "copy", text = _lastResponse }))),
            NK.Row(_bTabTables, _bTabResp, _bTabReq),
            _rTables, _rResp, _rReq);

        var rightCol = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6, 6, 10, 6) };
        NK.Stack(rightCol, cSel, _resCard);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(leftCol); split.Panel2.Controls.Add(rightCol);
        HandleCreated += (_, _) => BeginInvoke(() => { try { split.SplitterDistance = Math.Max(380, (int)(Width * 0.42)); } catch { } });

        foreach (var c in _in.Values.Concat(new[] { _pass, _fName, _fEndpoint, _fDesc, _fBody })) c.TextChanged += (_, _) => { if (!_loading && _p is not null) SetDirty(true); };
        _auth.SelectionChanged += _ => { if (!_loading && _p is not null) SetDirty(true); };
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

    // ---- mô hình ----
    private JsonArray Forms => _p!["Forms"] as JsonArray ?? (JsonArray)(_p["Forms"] = new JsonArray());
    private JsonObject? CurForm => _p is not null && _sel >= 0 && _sel < Forms.Count ? Forms[_sel] as JsonObject : null;
    private static string S(JsonNode? n, string k) { if (n is not JsonObject o || o[k] is not { } v) return ""; return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToString(); }

    private void Collect()
    {
        if (_p is null) return;
        foreach (var (j, key) in ProjectFields) _p[j] = _in[key].Text;
        _p["AuthScheme"] = _auth.SelectedIndex == 1 ? "bearer" : "raw";
        SaveForm();
    }

    private object Payload(string action, object? extra = null)
    {
        Collect();
        var d = new Dictionary<string, object?> { ["action"] = action, ["project"] = _p?.DeepClone(), ["originalName"] = _original, ["password"] = _pass.Text };
        if (extra is not null) foreach (var pr in extra.GetType().GetProperties()) d[pr.Name] = pr.GetValue(extra);
        return d;
    }

    private void SetDirty(bool d) { _isDirty = d; _dirty.Text = d ? "● chưa lưu" : ""; }
    private void Busy(bool on) { _bToken.Enabled = !on; _bSend.Enabled = !on && _sel >= 0; }
    private void Status(string text, string? kind = null) => Say(text, kind == "warn" ? "warn" : kind);

    private void FillProject()
    {
        _loading = true;
        foreach (var (j, key) in ProjectFields) _in[key].Text = _p is null ? "" : S(_p, j);
        _auth.SetItems(new[] { "Authorization: <token> (chuẩn LT3)", "Authorization: Bearer <token>" }, _p is not null && S(_p, "AuthScheme") == "bearer" ? 1 : 0);
        _pass.Text = ""; _pass.PlaceholderText = _hasPassword ? "•••••• (đã lưu, mã hoá)" : "(chưa lưu)";
        _tokVal.Text = "(chưa lấy)"; _bCopyTok.Enabled = false; _token = "";
        _loading = false;
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
        var kind = S(f, "Kind"); var fore = sel ? AppColors.OnAccent : AppColors.Text; var muted = sel ? AppColors.OnAccent : AppColors.TextMuted;
        var kw = TextRenderer.MeasureText(kind, _fList.Font).Width + 8;
        TextRenderer.DrawText(e.Graphics, kind, _fList.Font, new Rectangle(e.Bounds.Right - kw - 6, e.Bounds.Y, kw, e.Bounds.Height), muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        var name = S(f, "Name"); if (name.Length == 0) name = "(chưa đặt tên)";
        TextRenderer.DrawText(e.Graphics, name, _fList.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - kw - 20, e.Bounds.Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void Select(int i)
    {
        _sel = i; var f = CurForm;
        _loading = true;
        _fName.Text = f is null ? "" : S(f, "Name");
        _fKind.SetItems(KindLabels, f is null ? 2 : Math.Max(0, Array.IndexOf(Kinds, S(f, "Kind"))));
        _fEndpoint.Text = f is null ? "" : S(f, "Endpoint"); _fDesc.Text = f is null ? "" : S(f, "Description"); _fBody.Text = f is null ? "" : S(f, "Body");
        _loading = false;
        foreach (var c in new Control[] { _fName, _fKind, _fEndpoint, _fDesc, _fBody, _bSend, _bValidate, _bFormat, _bFormDoc }) c.Enabled = f is not null;
        _issuesText.Text = "";
        RenderForms();
    }

    private void SaveForm()
    {
        var f = CurForm; if (f is null) return;
        f["Name"] = _fName.Text; f["Kind"] = Kinds[Math.Max(0, _fKind.SelectedIndex)]; f["Endpoint"] = _fEndpoint.Text; f["Description"] = _fDesc.Text; f["Body"] = _fBody.Text;
    }

    private void AddBlank()
    {
        if (_p is null) return; SaveForm();
        Forms.Add(new JsonObject { ["Name"] = "formMoi", ["Kind"] = "GetData", ["Endpoint"] = "api/GetData", ["Method"] = "POST", ["Body"] = "{\n    \"form\": \"\",\n    \"Id\": \"\",\n    \"dateFrom\": \"{{monthStart}}\",\n    \"dateTo\": \"{{today}}\"\n}", ["Description"] = "" });
        Select(Forms.Count - 1); SetDirty(true); _fName.Inner.Focus(); _fName.Inner.SelectAll();
    }

    private void Duplicate() { if (_p is null || _sel < 0) return; SaveForm(); var c = (JsonObject)Forms[_sel]!.DeepClone(); c["Name"] = S(c, "Name") + " (2)"; Forms.Insert(_sel + 1, c); Select(_sel + 1); SetDirty(true); }
    private void DeleteForm() { if (_p is null || _sel < 0) return; Forms.RemoveAt(_sel); Select(Math.Min(_sel, Forms.Count - 1)); SetDirty(true); }
    private void Move(int d) { if (_p is null || _sel < 0) return; SaveForm(); var j = _sel + d; if (j < 0 || j >= Forms.Count) return; var n = Forms[_sel]; Forms.RemoveAt(_sel); Forms.Insert(j, n); Select(j); SetDirty(true); }

    private void Format()
    {
        try { _fBody.Text = JsonNode.Parse(_fBody.Text)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("  ", "    "); SaveForm(); SetDirty(true); Status("Đã định dạng body.", "ok"); }
        catch (Exception ex) { Status("Body không phải JSON hợp lệ: " + ex.Message, "err"); }
    }

    private void Validate() { SaveForm(); Post(new { action = "validate", name = _fName.Text, body = _fBody.Text }); }
    private void Send(bool confirmWrite) { if (_p is null || _sel < 0) return; SaveForm(); Busy(true); Post(Payload("send", new { index = _sel, confirmWrite })); }

    private void FormDoc()
    {
        var form = _fName.Text;
        try { if (JsonNode.Parse(_fBody.Text) is JsonObject o && o["form"]?.ToString() is { Length: > 0 } fm) form = fm; } catch { /* body chưa hợp lệ */ }
        Post(new { action = "form-doc", form });
    }

    private void SaveProject() { if (_p is not null) Post(Payload("save")); }

    private void DeleteProject()
    {
        if (_original.Length == 0) return;
        if (NK.Confirm(FindForm()!, $"Xoá dự án \"{_original}\" khỏi máy này (file trong thư mục ApiProjects)?", "Xoá dự án?")) Post(new { action = "delete", name = _original });
    }

    private void ShowTab(int t)
    {
        _bTabTables.IsPrimary = t == 0; _bTabResp.IsPrimary = t == 1; _bTabReq.IsPrimary = t == 2;
        _bTabTables.Invalidate(); _bTabResp.Invalidate(); _bTabReq.Invalidate();
        _rTables.Visible = t == 0; _rResp.Visible = t == 1; _rReq.Visible = t == 2;
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
            case "api.result": ShowResult(Arg(a, 0)); break;
            case "api.issues":
            {
                var list = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(x => x.GetString() ?? "").ToList();
                _issuesText.ForeColor = list.Count > 0 ? AppColors.Danger : AppColors.Success;
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
        _lastResponse = P(r, "responseText");
        _resCard.Visible = true;
        var ok = PB(r, "ok");
        _rPill.Text = "HTTP " + P(r, "status") + (ok ? " · OK" : " · Lỗi"); _rPill.ForeColor = ok ? AppColors.Success : AppColors.Danger;
        _rMeta.Text = P(r, "ms") + " ms" + (P(r, "message").Length > 0 ? " · " + P(r, "message") : "") + " · " + P(r, "url");
        _rResp.Text = Pretty(_lastResponse); _rReq.Text = "POST " + P(r, "url") + "\r\n\r\n" + Pretty(P(r, "requestBody"));
        _rTables.Controls.Clear();
        var tables = PA(r, "tables").ToList();
        if (tables.Count == 0) _rTables.Controls.Add(new Label { Text = "Response không có dữ liệu dạng bảng — xem tab Response.", AutoSize = true, ForeColor = AppColors.TextMuted, BackColor = Color.Transparent, Dock = DockStyle.Top });
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
        ShowTab(tables.Count > 0 ? 0 : 1);
    }

    private static string Pretty(string s) { try { return JsonNode.Parse(s)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); } catch { return s; } }

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
