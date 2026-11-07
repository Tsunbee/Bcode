using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Services;
using Microsoft.Data.SqlClient;
using Tpl = Bcode.App.Services.FboSampleTemplateService.Template;

namespace Bcode.App.Forms;

/// <summary>
/// Mẫu điền có tên của tab FSG FBO (nút "▾" cạnh "🎲 Điền mẫu"): lưu form đang mở (master + các dòng chi tiết) thành nhiều mẫu cho cùng 1 form
/// ("Hóa đơn bán – chiết khấu", "… – ngoại tệ") để tester điền lại đúng nghiệp vụ; điền theo mẫu; ghi đè / đổi tên / xoá. Bấm "Lưu" trên form FBO thì tự
/// giữ mẫu "Lần lưu gần nhất" của máy — Điền mẫu ngẫu nhiên lần sau ưu tiên giá trị đó (xem <see cref="PreferSavedAsync"/>).
/// Khi điền theo mẫu: mã chính của danh mục / chứng từ khai báo sinh MÃ MỚI (không trùng, không lồng), số chứng từ để FBO tự sinh, ngày = hôm nay,
/// mã tra cứu được KIỂM lại theo key của ô (mã đã ngừng dùng / không thoả điều kiện thì bỏ và báo, không điền âm thầm). Chỉ đọc database.
/// Lưu chung theo dự án (FboSampleTemplateService: {working}\SampleTemplates\&lt;controller&gt;\).
/// </summary>
public partial class QuickLaunchLoginForm
{
    private (string Url, string? Controller, FboFormLookups.Form? Form) _formCache = ("", null, null);

    /// <summary>Ô FBO tự sinh khi Lưu — mẫu không điền lại.</summary>
    private static readonly HashSet<string> TplSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "stt_rec", "stt_rec0", "stt_rec_hd", "so_ct", "ma_ct", "ma_ct0", "datetime0", "datetime2", "user_id0", "user_id2", "line_nbr",
    };

    private static readonly Regex DateVal = new(@"^\d{2}/\d{2}/\d{4}$");

    /// <summary>Đơn vị của phiên đăng nhập FBO ("Người sử dụng: FAST02 | 00 | …") — đưa vào giá trị form làm ma_dvcs / @@unit khi form không có ô ma_dvcs,
    /// không thì Điền mẫu tự chọn đơn vị khác với phiên → mã kho / vật tư lọc theo đơn vị bị FBO báo không tồn tại.</summary>
    private async Task AddSessionUnitAsync(Dictionary<string, string> formValues)
    {
        if (formValues.ContainsKey("ma_dvcs")) return;
        try
        {
            var raw = await _web.ExecuteScriptAsync("window.__bcodeSessionUnit ? window.__bcodeSessionUnit() : ''");
            if (JsonSerializer.Deserialize<string>(raw) is { Length: > 0 and < 20 } u) formValues["ma_dvcs"] = u.Trim();
        }
        catch { /* trang khác / chưa đăng nhập */ }
    }

    /// <summary>Controller + form đang mở (theo URL trang, nhớ theo URL). Không đọc được controller → khoá theo tên trang + tham số.</summary>
    private async Task<(string Key, string? Controller, FboFormLookups.Form? Form)> ResolveFormAsync(string? url = null)
    {
        url ??= _web.Source?.ToString() ?? "";
        if (_formCache.Url == url && _formCache.Controller is not null) return (_formCache.Controller, _formCache.Controller, _formCache.Form);
        var cands = ControllerCandidates?.Invoke(url) ?? Array.Empty<string>();
        var src = _ws.SourcePath;
        var (ctrl, form) = await Task.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(src))
                foreach (var c in cands)
                    if (FboFormLookups.Load(src, c) is { } f) return (Path.GetFileNameWithoutExtension(f.File), f);
            return (cands.FirstOrDefault(), (FboFormLookups.Form?)null);
        });
        _formCache = (url, ctrl, form);
        return (ctrl ?? PageKey(url), ctrl, form);
    }

    private static string PageKey(string url)
    {
        try
        {
            var u = new Uri(url);
            var page = Path.GetFileNameWithoutExtension(u.AbsolutePath);
            var id = System.Web.HttpUtility.ParseQueryString(u.Query)["id"];
            return string.IsNullOrEmpty(id) ? page : page + "_" + id;
        }
        catch { return "trang"; }
    }

    // ---------------------------------------------------------------- menu "▾"

    private async Task ShowTemplateMenuAsync(Control anchor)
    {
        if (_web.CoreWebView2 is null) return;
        var (key, _, form) = await ResolveFormAsync();
        var list = await Task.Run(() => FboSampleTemplateService.List(_ws, key));
        var m = new ContextMenuStrip { ShowImageMargin = false };
        m.Items.Add("🎲 Điền ngẫu nhiên theo dữ liệu", null, async (_, _) => await FillSampleAsync());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripLabel($"Mẫu của form {key}  ({list.Count})") { ForeColor = SystemColors.GrayText });
        if (list.Count == 0) m.Items.Add(new ToolStripMenuItem("(chưa có mẫu — điền form rồi bấm \"Lưu form hiện tại thành mẫu…\")") { Enabled = false });
        foreach (var t in list)
        {
            var who = t.Author.Split('@')[0];
            var it = new ToolStripMenuItem($"{(t.Auto ? "⟲" : "📄")} {t.Name}   ·  {t.Rows.Count} dòng chi tiết · {who} {t.SavedAt:dd/MM HH:mm}");
            var tt = t;
            it.DropDownItems.Add("Điền theo mẫu này", null, async (_, _) => await FillTemplateAsync(tt));
            it.DropDownItems.Add("Ghi đè mẫu này bằng form hiện tại", null, async (_, _) => await SaveTemplateAsync(key, tt.Auto ? "Lần lưu gần nhất" : tt.Name, tt.Auto ? null : tt));
            it.DropDownItems.Add("Đổi tên…", null, async (_, _) =>
            {
                if (AskName("Đổi tên mẫu", tt.Name) is not { } nn) return;
                try { await Task.Run(() => FboSampleTemplateService.Rename(_ws, tt, nn)); SetStatus($"Đã đổi tên mẫu thành \"{nn}\"."); }
                catch (Exception ex) { SetStatus("Không đổi tên được mẫu: " + ex.Message); }
            });
            it.DropDownItems.Add("Xoá mẫu", null, async (_, _) =>
            {
                if (MessageBox.Show(this, $"Xoá mẫu \"{tt.Name}\" của form {key}?\n(Mẫu lưu chung — người khác trong nhóm cũng mất mẫu này.)", "Bcode — Mẫu điền",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                try { await Task.Run(() => FboSampleTemplateService.Delete(tt)); SetStatus($"Đã xoá mẫu \"{tt.Name}\"."); }
                catch (Exception ex) { SetStatus("Không xoá được mẫu: " + ex.Message); }
            });
            it.Click += async (_, _) => await FillTemplateAsync(tt);
            m.Items.Add(it);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("💾 Lưu form hiện tại thành mẫu…", null, async (_, _) =>
        {
            var suggest = form is { IsVoucher: true } ? "Nghiệp vụ " + (list.Count(t => !t.Auto) + 1) : "Mẫu " + (list.Count(t => !t.Auto) + 1);
            if (AskName($"Lưu mẫu cho form {key}", suggest) is not { } name) return;
            var existing = list.FirstOrDefault(t => !t.Auto && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && MessageBox.Show(this, $"Đã có mẫu \"{name}\". Ghi đè?", "Bcode — Mẫu điền", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            await SaveTemplateAsync(key, name, existing);
        });
        m.Items.Add("📂 Mở thư mục mẫu", null, (_, _) =>
        {
            try
            {
                var dir = FboSampleTemplateService.FolderOf(_ws, key);
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { SetStatus("Không mở được thư mục mẫu: " + ex.Message); }
        });
        m.Closed += (_, _) => BeginInvoke(new Action(m.Dispose));
        m.Show(anchor, new Point(0, anchor.Height));
    }

    private string? AskName(string title, string initial)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi, Padding = new Padding(12),
            ClientSize = new Size(Bcode.App.UI.NativeSkin.S(420), Bcode.App.UI.NativeSkin.S(110)),
        };
        var lbl = new Label { Text = "Tên mẫu (vd: Hóa đơn bán – chiết khấu):", Dock = DockStyle.Top, AutoSize = true };
        var txt = new TextBox { Text = initial, Dock = DockStyle.Top };
        var ok = new Button { Text = "Lưu", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Huỷ", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        f.Controls.Add(buttons); f.Controls.Add(txt); f.Controls.Add(lbl);
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Shown += (_, _) => { txt.Focus(); txt.SelectAll(); };
        if (f.ShowDialog(this) != DialogResult.OK) return null;
        var name = txt.Text.Trim();
        return name.Length == 0 ? null : name;
    }

    // ---------------------------------------------------------------- lưu

    private static (Dictionary<string, string> Master, List<Dictionary<string, string>> Rows) ParseFormJson(string json)
    {
        var master = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Dictionary<string, string>>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("master", out var m) && m.ValueKind == JsonValueKind.Object)
            foreach (var p in m.EnumerateObject())
                if (!TplSkip.Contains(p.Name)) master[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
        if (doc.RootElement.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array)
            foreach (var row in r.EnumerateArray())
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in row.EnumerateObject()) if (!TplSkip.Contains(p.Name)) d[p.Name] = p.Value.GetString() ?? "";
                if (d.Count > 0) rows.Add(d);
            }
        return (master, rows);
    }

    private async Task SaveTemplateAsync(string key, string name, Tpl? overwrite)
    {
        try
        {
            var raw = await _web.ExecuteScriptAsync("window.__bcodeReadForm ? window.__bcodeReadForm() : ''");
            var json = JsonSerializer.Deserialize<string>(raw) ?? "";
            if (json.Length < 3) { SetStatus("Không đọc được form đang mở để lưu mẫu."); return; }
            var (master, rows) = ParseFormJson(json);
            if (master.Values.All(string.IsNullOrWhiteSpace) && rows.Count == 0) { SetStatus("Form đang trống — điền form rồi hãy lưu mẫu."); return; }
            var t = overwrite ?? new Tpl { Name = name, Controller = key };
            t.Name = name; t.Controller = key; t.Auto = false; t.Author = "";
            t.Master = master; t.Rows = rows;
            var path = await Task.Run(() => FboSampleTemplateService.Save(_ws, t));
            // không đọc được dòng chi tiết nào → ghi kèm chẩn đoán cấu trúc lưới (số cột, ô dưới tiêu đề) để biết vì sao
            string diag = "";
            if (rows.Count == 0)
                try { using var d = JsonDocument.Parse(json); if (d.RootElement.TryGetProperty("diag", out var dg)) diag = " · ⚠ không đọc được dòng chi tiết (" + (dg.GetString() is { Length: > 0 } s ? s : "không thấy tiêu đề cột lưới nào") + ")"; }
                catch { }
            SetStatus($"Đã lưu mẫu \"{name}\" ({master.Count(kv => kv.Value.Length > 0)} ô, {rows.Count} dòng chi tiết){diag} → {path}");
        }
        catch (Exception ex) { SetStatus("Không lưu được mẫu: " + ex.Message); }
    }

    /// <summary>Người dùng bấm "Lưu" trên form FBO: giữ giá trị form làm mẫu "Lần lưu gần nhất" (1 bản / máy / form, ghi đè).</summary>
    private async void OnFboFormSaved(string url, string json)
    {
        try
        {
            var (key, ctrl, _) = await ResolveFormAsync(url);
            if (ctrl is null) return;   // không biết form nào — không giữ (tránh lẫn mẫu)
            var (master, rows) = ParseFormJson(json);
            if (master.Values.All(string.IsNullOrWhiteSpace) && rows.Count == 0) return;
            var t = new Tpl { Name = "Lần lưu gần nhất", Controller = key, Auto = true, Master = master, Rows = rows };
            await Task.Run(() => FboSampleTemplateService.Save(_ws, t));
        }
        catch { /* mất mạng / thư mục chỉ đọc — không ảnh hưởng việc lưu của FBO */ }
    }

    // ---------------------------------------------------------------- kiểm giá trị tra cứu

    /// <summary>Giá trị <paramref name="value"/> của ô tra cứu <paramref name="lk"/> có trong bảng tra cứu và thoả key (không dịch được key thì check;
    /// cả hai trống = chỉ cần tồn tại). null = không kiểm được (macro lạ / lỗi SQL) — coi như hợp lệ.</summary>
    private async Task<bool?> LookupValidAsync(SqlConnection conn, FboFormLookups.Form form, FboFormLookups.Lookup lk, string value, Func<string, string?> valueOf, string unit)
    {
        var sys = _ws.SysDatabase ?? ""; var app = _ws.EffectiveAppDatabase ?? "";
        string? Sq(string e) => FboFormLookups.ToSql(e, valueOf, form.VoucherId, sys, app, unit);
        var w = string.IsNullOrWhiteSpace(lk.Key) && string.IsNullOrWhiteSpace(lk.Check) ? "" : Sq(lk.Key) ?? Sq(lk.Check);
        if (w is not null && lk.Filter.Length > 0) w = Sq(lk.Filter) is { } fw ? (w.Length > 0 && fw.Length > 0 ? $"({w}) AND ({fw})" : w + fw) : null;
        if (w is null) return null;
        var table = lk.Sys && sys.Length > 0 ? $"[{sys.Replace("]", "]]")}].[dbo].[{lk.Table.Replace("]", "]]")}]" : $"[dbo].[{lk.Table.Replace("]", "]]")}]";
        try
        {
            await using var cmd = new SqlCommand($"SELECT TOP 1 1 FROM {table} WITH (NOLOCK) WHERE LTRIM(RTRIM(CAST([{lk.Code}] AS nvarchar(400)))) = @v" + (w.Length > 0 ? $" AND ({w})" : ""), conn) { CommandTimeout = 10 };
            cmd.Parameters.AddWithValue("@v", value.Trim());
            return await cmd.ExecuteScalarAsync() is not null;
        }
        catch { return null; }
    }

    /// <summary>
    /// "🎲 Điền mẫu" ngẫu nhiên: giá trị của mẫu "Lần lưu gần nhất" (form này, mọi máy) thay cho giá trị chọn ngẫu nhiên — sát cách người dùng đã nhập / sửa.
    /// Ô tra cứu chỉ lấy khi giá trị đó còn thoả key; mã chính (mã mới) / ngày / ô FBO tự sinh giữ nguyên lựa chọn của bộ chọn.
    /// </summary>
    private async Task<List<FboSampleDataService.Pick>> PreferSavedAsync(SqlConnection conn, FboFormLookups.Form? form, List<FboSampleDataService.Pick> picks,
        string? primary, IReadOnlyDictionary<string, string> formValues)
    {
        if (form is null) return picks;
        var auto = FboSampleTemplateService.LatestAuto(_ws, Path.GetFileNameWithoutExtension(form.File));
        if (auto is null) return picks;
        var byField = picks.ToDictionary(p => p.Field, p => p.Value, StringComparer.OrdinalIgnoreCase);
        string? ValueOf(string n) => byField.TryGetValue(n, out var v) ? v : formValues.TryGetValue(n, out var fv) ? fv : null;
        var unit = ValueOf("ma_dvcs") ?? "";
        var firstRow = auto.Rows.FirstOrDefault() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var res = new List<FboSampleDataService.Pick>();
        foreach (var p in picks)
        {
            var saved = auto.Master.TryGetValue(p.Field, out var mv) && mv.Trim().Length > 0 ? mv.Trim()
                : firstRow.TryGetValue(p.Field, out var gv) && gv.Trim().Length > 0 ? FboSampleTemplateService.PlainNumber(gv) : null;
            if (saved is null || p.Field.Equals(primary, StringComparison.OrdinalIgnoreCase) || p.Source.Contains("mã mới") || DateVal.IsMatch(saved) || TplSkip.Contains(p.Field))
            { res.Add(p); continue; }
            if (form.Fields.TryGetValue(p.Field, out var lk) && await LookupValidAsync(conn, form, lk, saved, ValueOf, unit) == false) { res.Add(p); continue; }
            res.Add(p with { Value = saved, Source = "mẫu lần lưu gần nhất" });
            byField[p.Field] = saved;
        }
        return res;
    }

    // ---------------------------------------------------------------- điền theo mẫu

    private async Task FillTemplateAsync(Tpl t)
    {
        if (_filling || _web.CoreWebView2 is null) return;
        _filling = true;
        try
        {
            var all = await ReadFormFieldsAsync();
            if (all.Count == 0) { SetStatus("Không thấy form nào đang mở (mở form ở chế độ Thêm rồi chọn mẫu)."); return; }
            var (_, _, form) = await ResolveFormAsync();
            var masterIn = all.Where(f => f.Kind == "text" && !f.Disabled && !f.ReadOnly)
                .GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var gridIn = all.Where(f => f.Kind == "grid").GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var formValues = all.Where(f => f.Kind != "grid" && !string.IsNullOrWhiteSpace(f.Value))
                .GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.OrdinalIgnoreCase);
            await AddSessionUnitAsync(formValues);
            var today = DateTime.Today.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
            string? prim = form is not null && form.Code.Length > 0 && !form.Code.Contains(',') && !form.Code.Equals("stt_rec", StringComparison.OrdinalIgnoreCase) ? form.Code : null;
            var refNames = form is null ? new HashSet<string>() : new HashSet<string>(form.Fields.Values.Where(l => l.Reference.Length > 0).Select(l => l.Reference), StringComparer.OrdinalIgnoreCase);

            SetStatus($"Đang kiểm mẫu \"{t.Name}\" với database {_ws.Name}...");
            var (master, rows, rejected) = await Task.Run(async () =>
            {
                var conns = new DbConnectionService();
                conns.SetWorkspace(_ws);
                await using var conn = conns.CreateConnection(useSysDatabase: false);
                await conn.OpenAsync();
                var mv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var bad = new List<string>();
                // mã chính: mã MỚI (không trùng / không lồng) — điền lại mã của mẫu thì Lưu báo trùng
                if (prim is not null && masterIn.ContainsKey(prim))
                {
                    var p = await FboSampleDataService.PickAsync(conn, new[] { new FboSampleDataService.Field(prim, masterIn[prim].MaxLength) }, prim, form, formValues,
                        _ws.SysDatabase ?? "", appDatabase: _ws.EffectiveAppDatabase ?? "");
                    if (p.FirstOrDefault(x => x.Field.Equals(prim, StringComparison.OrdinalIgnoreCase)) is { } np) mv[prim] = np.Value;
                }
                foreach (var (f, v0) in t.Master)
                {
                    var v = v0.Trim();
                    if (v.Length == 0 || mv.ContainsKey(f) || TplSkip.Contains(f) || !masterIn.ContainsKey(f) || refNames.Contains(f)) continue;
                    if (DateVal.IsMatch(v) && f.StartsWith("ngay", StringComparison.OrdinalIgnoreCase)) v = today;
                    mv[f] = v;
                }
                string? MV(string n) => mv.TryGetValue(n, out var x) ? x : formValues.TryGetValue(n, out var y) ? y : null;
                var unit = MV("ma_dvcs") ?? "";
                if (form is not null)
                    foreach (var f in mv.Keys.ToList())
                        if (!f.Equals(prim, StringComparison.OrdinalIgnoreCase) && form.Fields.TryGetValue(f, out var lk) && !lk.Grid
                            && await LookupValidAsync(conn, form, lk, mv[f], MV, unit) == false)
                        { bad.Add($"{f}={mv[f]}"); mv.Remove(f); }
                var rv = new List<Dictionary<string, string>>();
                for (var i = 0; i < t.Rows.Count; i++)
                {
                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (f, v0) in t.Rows[i])
                    {
                        var v = v0.Trim();
                        if (v.Length == 0 || EmptyNum.IsMatch(v) || !gridIn.ContainsKey(f) || TplSkip.Contains(f) || refNames.Contains(f)) continue;
                        var isLookup = form?.Fields.TryGetValue(f, out var glk) == true && glk.Grid;
                        row[f] = isLookup ? v : FboSampleTemplateService.PlainNumber(v);
                    }
                    if (form is not null)
                        foreach (var f in row.Keys.ToList())
                            if (form.Fields.TryGetValue(f, out var lk) && lk.Grid
                                && await LookupValidAsync(conn, form, lk, row[f], n => row.TryGetValue(n, out var x) ? x : MV(n), unit) == false)
                            { bad.Add($"dòng {i + 1}.{f}={row[f]}"); row.Remove(f); }
                    if (row.Count > 0) rv.Add(row);
                }
                return (mv, rv, bad);
            });
            if (IsDisposed) return;

            // Thứ tự: mã chính → mã (FBO tra mã → điền tên…) → ô khác; rồi từng dòng chi tiết: ô tra cứu (mã hàng trước) → ô số.
            var fill = new List<object>();
            foreach (var (f, v) in master.OrderBy(kv => kv.Key.Equals(prim, StringComparison.OrdinalIgnoreCase) ? 0 : FboSampleDataService.IsKey(kv.Key) || form?.Fields.ContainsKey(kv.Key) == true ? 1 : 2))
                fill.Add(new { tag = masterIn[f].Tag, value = v, grid = false, row = 0 });
            for (var i = 0; i < rows.Count; i++)
                foreach (var (f, v) in rows[i].OrderBy(kv => kv.Key.Equals("ma_vt", StringComparison.OrdinalIgnoreCase) ? 0 : form?.Fields.ContainsKey(kv.Key) == true || FboSampleDataService.IsKey(kv.Key) ? 1 : 2))
                    fill.Add(new { tag = gridIn[f].Tag, value = v, grid = true, row = i });
            if (fill.Count > 0) await FillAsync(fill);
            var badNote = rejected.Count == 0 ? "" : " · ⚠ BỎ (không còn hợp lệ theo điều kiện tra cứu): " + string.Join(", ", rejected.Take(10)) + (rejected.Count > 10 ? ", …" : "");
            SetStatus($"Đã điền theo mẫu \"{t.Name}\": {master.Count} ô, {rows.Count} dòng chi tiết{(prim is not null && master.ContainsKey(prim) ? $" · mã mới {master[prim]}" : "")}{badNote} — xem lại rồi tự bấm Lưu.");
        }
        catch (Exception ex) { SetStatus("Không điền được theo mẫu: " + ex.Message); }
        finally { _filling = false; }
    }
}
