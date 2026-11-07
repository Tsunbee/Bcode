using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>Nút "🎲 Điền mẫu" của tab FSG FBO: lấy các ô của form đang mở (FboFieldInspector — window.__bcodeFormFields: ô nhập của master + cột lưới
/// chi tiết), chọn giá trị mẫu từ database App của dự án (FboSampleDataService — chỉ đọc) rồi điền vào các ô ĐANG TRỐNG (window.__bcodeFillFields: gõ giá trị
/// + bắn input/change/blur để FBO tra mã → tên; ô lưới thì bấm vào ô dòng 1 cho FBO mở ô nhập rồi gõ + Tab).
/// Đọc được controller của menu (FboFormLookups — kèm file include, tìm controller qua <see cref="ControllerCandidates"/>) thì CHỈ điền ô bắt buộc
/// (allowNulls="false") của form + mã / tên của danh mục đang thêm: ô phụ (Hợp đồng, Người mua…) có sự kiện của FBO tự đổi ô khác — điền bừa thì FBO xoá
/// ngược ô bắt buộc vừa điền. Lưới: cột bắt buộc + mã hàng / kho + số lượng = 1 + giá. Chứng từ lấy giá trị hay dùng nhất của chính loại chứng từ đó.
/// Điền xong đọc lại form: ô bắt buộc bị FBO xoá thì điền lại 1 lần, còn trống thì báo tên ô ở thanh trạng thái. Không bấm Lưu: người dùng xem lại rồi tự lưu.</summary>
public partial class QuickLaunchLoginForm
{
    private bool _filling;

    /// <summary>URL trang FBO đang mở → tên controller Dir có thể của menu đó (sysid, tên trang trong link) — MainForm đối chiếu cây WCommand.</summary>
    public Func<string, IReadOnlyList<string>>? ControllerCandidates { get; set; }

    private sealed record FormField(string Tag, string Field, string Kind, string Value, bool Disabled, bool ReadOnly, int MaxLength);

    private static readonly Regex EmptyNum = new(@"^0+([.,]0+)?$");
    private static readonly Regex PriceCol = new(@"^gia(_nt)?2?$", RegexOptions.IgnoreCase);

    private static bool IsBlank(FormField f) => string.IsNullOrWhiteSpace(f.Value) || (f.Kind == "grid" && EmptyNum.IsMatch(f.Value.Trim()));

    private async Task<List<FormField>> ReadFormFieldsAsync()
    {
        var raw = await _web.ExecuteScriptAsync("window.__bcodeFormFields ? window.__bcodeFormFields() : '[]'");
        var json = JsonSerializer.Deserialize<string>(raw) ?? "[]";
        return JsonSerializer.Deserialize<List<FormField>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }

    private Task<string> FillAsync(IEnumerable<object> fill) =>
        _web.ExecuteScriptAsync($"window.__bcodeFillFields ? window.__bcodeFillFields({JsonSerializer.Serialize(JsonSerializer.Serialize(fill))}) : 0");

    private async Task FillSampleAsync()
    {
        if (_filling || _web.CoreWebView2 is null) return;
        _filling = true;
        try
        {
            var all = await ReadFormFieldsAsync();
            if (all.Count == 0) { SetStatus("Không thấy form nào đang mở (mở 1 chứng từ / danh mục ở chế độ Thêm hoặc Sửa rồi bấm lại)."); return; }
            var emptyMaster = all.Where(f => f.Kind == "text" && !f.Disabled && !f.ReadOnly && string.IsNullOrWhiteSpace(f.Value)).ToList();
            // Lưới chi tiết: cột còn trống ở dòng 1 (ô số hiện "0" / "0.00" cũng là trống)
            var gridCols = all.Where(f => f.Kind == "grid" && IsBlank(f))
                .GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var formValues = all.Where(f => f.Kind != "grid" && !string.IsNullOrWhiteSpace(f.Value))
                .GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.OrdinalIgnoreCase);
            await AddSessionUnitAsync(formValues);   // @@unit = đơn vị đang đăng nhập FBO

            var url = _web.Source?.ToString() ?? "";
            var candidates = ControllerCandidates?.Invoke(url) ?? Array.Empty<string>();
            SetStatus($"Đang đọc controller + lấy dữ liệu mẫu từ database {_ws.Name}...");
            var (picks, form, primary, master, grid) = await Task.Run(async () =>
            {
                FboFormLookups.Form? frm = null;
                if (!string.IsNullOrWhiteSpace(_ws.SourcePath))
                    foreach (var c in candidates)
                        if ((frm = FboFormLookups.Load(_ws.SourcePath, c)) is not null) break;

                // Danh mục đang thêm → mã chính (dir code) là mã mới. Không đọc được controller: form không có so_ct / ngay_ct thì coi là danh mục,
                // mã chính = ô mã đầu tiên.
                string? prim = null;
                // chứng từ khai báo (FATran, TS2Tran…: dir code="ma_ts" chứ không phải stt_rec) cũng có mã chính phải là mã mới
                if (frm is not null) { if (frm.Code.Length > 0 && !frm.Code.Contains(',') && !frm.Code.Equals("stt_rec", StringComparison.OrdinalIgnoreCase)) prim = frm.Code; }
                else if (!all.Any(f => f.Field is "so_ct" or "ngay_ct" or "ngay_lct" or "so_seri" || f.Kind == "grid"))
                    prim = all.FirstOrDefault(f => f.Kind == "text" && FboSampleDataService.IsKey(f.Field))?.Field;
                if (prim is not null && !emptyMaster.Any(f => f.Field.Equals(prim, StringComparison.OrdinalIgnoreCase))) prim = null;   // mã đã có (đang Sửa)

                // Có controller: chỉ ô bắt buộc (+ mã / tên của danh mục đang thêm). Không có: như cũ — mọi ô trống.
                var strict = frm is { Required.Count: > 0 } || frm is { GridRequired.Count: > 0 };
                var primName = prim is not null && prim.StartsWith("ma_", StringComparison.OrdinalIgnoreCase) ? "ten_" + prim[3..] : null;
                var mf = !strict ? emptyMaster : emptyMaster.Where(f => frm!.Required.Contains(f.Field)
                    || f.Field.Equals(prim, StringComparison.OrdinalIgnoreCase)
                    || (primName is not null && f.Field.TrimEnd('2').Equals(primName, StringComparison.OrdinalIgnoreCase))).ToList();

                // Lưới: cột bắt buộc (theo thứ tự trên màn hình) + mã hàng / kho + số lượng + giá — còn lại để FBO tự lấy theo mã.
                // Danh mục (Thêm vật tư…): lưới là phần phụ tuỳ chọn (Đvt quy đổi, Lô, Vùng…) — điền vào là sinh dòng rác, nên không điền.
                var gsel = new List<FormField>();
                FormField? G(Func<FormField, bool> p) => strict && !frm!.IsVoucher ? null : gridCols.FirstOrDefault(p);
                if (strict && frm!.IsVoucher) gsel.AddRange(gridCols.Where(f => frm.GridRequired.Contains(f.Field)));
                else if ((G(f => f.Field.Equals("ma_vt", StringComparison.OrdinalIgnoreCase))
                          ?? G(f => frm?.Fields.TryGetValue(f.Field, out var l) == true && l.Grid) ?? G(f => FboSampleDataService.IsKey(f.Field))) is { } item)
                    gsel.Add(item);
                foreach (var pred in new Func<FormField, bool>[]
                {
                    f => f.Field.Equals("ma_vt", StringComparison.OrdinalIgnoreCase),
                    f => f.Field.Equals("ma_kho", StringComparison.OrdinalIgnoreCase),
                    f => f.Field.Equals("so_luong", StringComparison.OrdinalIgnoreCase),
                    f => PriceCol.IsMatch(f.Field),
                })
                    if (G(pred) is { } gf && !gsel.Contains(gf)) gsel.Add(gf);

                var conns = new DbConnectionService();
                conns.SetWorkspace(_ws);
                await using var conn = conns.CreateConnection(useSysDatabase: false);
                await conn.OpenAsync();
                var fieldsIn = mf.Select(f => new FboSampleDataService.Field(f.Field, f.MaxLength))
                    .Concat(gsel.Where(f => !f.Field.Equals("so_luong", StringComparison.OrdinalIgnoreCase)).Select(f => new FboSampleDataService.Field(f.Field, f.MaxLength, Grid: true)))
                    .ToList();
                var p = await FboSampleDataService.PickAsync(conn, fieldsIn, prim, frm, formValues, _ws.SysDatabase ?? "", appDatabase: _ws.EffectiveAppDatabase ?? "");
                // ưu tiên giá trị người dùng đã nhập / sửa ở lần bấm Lưu gần nhất của form này (còn hợp lệ theo điều kiện tra cứu) — xem Templates.cs
                p = await PreferSavedAsync(conn, frm, p, prim, formValues);
                // Số lượng mẫu = 1 (lấy số lượng có sẵn trong dữ liệu dễ ra số lạ — 0,5 / 12.000 — làm tiền hàng khó kiểm).
                if (gsel.Any(f => f.Field.Equals("so_luong", StringComparison.OrdinalIgnoreCase))) p.Add(new FboSampleDataService.Pick("so_luong", "1", "số lượng mẫu"));
                return (p, frm, prim, mf, gsel);
            });
            if (IsDisposed) return;
            if (picks.Count == 0 && master.Count + grid.Count > 0) { SetStatus("Không tìm được dữ liệu mẫu cho các ô cần điền (cột không có trong database / bảng chưa có dữ liệu)."); }

            // Master: mã chính trước, rồi mã (FBO tra mã → tự điền tên), rồi ô khác; sau cùng tới lưới chi tiết (theo thứ tự đã chọn).
            var byField = picks.GroupBy(p => p.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            object Item(FormField f, bool isGrid) => new { tag = f.Tag, value = byField[f.Field].Value, grid = isGrid };
            IEnumerable<FormField> Ordered(IEnumerable<FormField> m) => m.Where(f => byField.ContainsKey(f.Field))
                .OrderBy(f => f.Field.Equals(primary, StringComparison.OrdinalIgnoreCase) ? 0 : FboSampleDataService.IsKey(f.Field) ? 1 : 2);
            var fill = Ordered(master).Select(f => Item(f, false)).Concat(grid.Where(f => byField.ContainsKey(f.Field)).Select(f => Item(f, true))).ToList();
            if (fill.Count > 0) await FillAsync(fill);

            // Kiểm lại: FBO chạy sự kiện của ô (tra mã, đổi loại…) có thể xoá ô khác vừa điền → điền lại đúng các ô đó 1 lần.
            var wanted = new HashSet<string>(master.Select(f => f.Field), StringComparer.OrdinalIgnoreCase);
            await Task.Delay(700);
            var after = await ReadFormFieldsAsync();
            var lost = after.Where(f => f.Kind == "text" && !f.Disabled && !f.ReadOnly && string.IsNullOrWhiteSpace(f.Value) && wanted.Contains(f.Field) && byField.ContainsKey(f.Field))
                .GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (lost.Count > 0)
            {
                await FillAsync(Ordered(lost).Select(f => Item(f, false)).ToList());
                await Task.Delay(500);
                after = await ReadFormFieldsAsync();
            }

            // Ô bắt buộc (đọc được controller) vẫn trống sau khi điền → báo rõ để người dùng tự chọn, không để tưởng là đủ.
            var missing = new List<string>();
            if (form is not null)
            {
                foreach (var f in after.Where(f => f.Kind == "text" && !f.Disabled && !f.ReadOnly && string.IsNullOrWhiteSpace(f.Value) && form.Required.Contains(f.Field))
                             .Select(f => f.Field).Distinct(StringComparer.OrdinalIgnoreCase))
                    missing.Add(f + (byField.ContainsKey(f) ? " (FBO xoá lại sau khi điền)" : " (không có giá trị hợp lệ trong database)"));
                foreach (var f in after.Where(f => form.IsVoucher && f.Kind == "grid" && IsBlank(f) && form.GridRequired.Contains(f.Field)).Select(f => f.Field).Distinct(StringComparer.OrdinalIgnoreCase))
                    missing.Add("lưới." + f + (byField.ContainsKey(f) ? " (không gõ được vào ô lưới)" : " (không có giá trị hợp lệ)"));
            }

            var detail = string.Join(", ", picks.Take(8).Select(p => $"{p.Field}={p.Value} ({p.Source})")) + (picks.Count > 8 ? ", …" : "");
            var src = form is null ? "không đọc được controller của menu — điền mọi ô trống, điều kiện tra cứu đoán theo tên cột"
                : $"theo {Path.GetFileName(form.File)}: chỉ ô bắt buộc";
            var gridNote = grid.Count == 0 ? "" : $" · chi tiết: {grid.Count(f => byField.ContainsKey(f.Field))} cột";
            var missNote = missing.Count == 0 ? (form is null ? "" : " · đủ ô bắt buộc") : " · ⚠ CÒN TRỐNG: " + string.Join(", ", missing);
            SetStatus($"Đã điền mẫu {fill.Count} ô{gridNote} ({src}){missNote} — xem lại rồi tự bấm Lưu. {detail}");
        }
        catch (Exception ex)
        {
            SetStatus("Không điền được dữ liệu mẫu: " + ex.Message);
        }
        finally { _filling = false; }
    }
}
