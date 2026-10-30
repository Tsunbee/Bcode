using System.Security.Cryptography;
using System.Text;
using Bcode.App.Models;
using Bcode.App.Secrets;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Đồng bộ thông tin project từ database FSG_A (bảng nbdmda + nbdmserver) vào danh sách Workspace của Bcode — thay cho bản cũ
/// cào web FSG bằng WebView2 ẩn (chậm, dễ vỡ khi giao diện FSG đổi). 3 chế độ (<see cref="SyncMode"/>):
///  1. <see cref="SyncMode.AddOne"/>      — nhập 1 mã dự án (ma_da), lấy và THÊM project đó (đã có thì bỏ qua, không ghi đè).
///  2. <see cref="SyncMode.NewOnly"/>     — chỉ THÊM những project chưa có trong danh sách.
///  3. <see cref="SyncMode.OverwriteAll"/> — project đã có thì CẬP NHẬT, chưa có thì THÊM.
/// "INSERT/UPDATE" ở đây là trên danh sách Workspace của Bcode (AppSettings.Workspaces) — FSG_A chỉ được ĐỌC. Việc lưu xuống
/// settings.json do nơi gọi làm (AppSettings.Save) sau khi xem kết quả.
///
/// Cập nhật chỉ ghi đè bằng những giá trị FSG có (không rỗng); các thứ FSG không có — Mobile Path, tài khoản web đã lưu, cấu hình
/// Profiler — luôn được giữ nguyên. Tên WS của project đã có cũng giữ nguyên (Last Access / LastWorkspace tham chiếu theo tên).
///
/// BẢO MẬT — đọc kỹ trước khi dựa vào: thông tin kết nối FSG_A (đã mã hoá AES-256-GCM) cùng khoá giải nằm ở Secrets/LocalSecrets.cs —
/// file gitignore, mỗi máy build tự chép vào, nên không còn trong source/git. Trong .exe đã build thì vẫn LÀM RỐI (ai có file .exe đều lấy
/// lại được khoá và mật khẩu) — KHÔNG phải bảo vệ thật. Tài khoản FASTAD nên chỉ có quyền
/// SELECT trên FSG_A; muốn an toàn thật cần đưa bí mật ra khỏi máy client (vd dịch vụ trung gian hoặc DPAPI theo từng người dùng).
/// </summary>
public sealed class FsgProjectLookupService
{
    // ---- Khoá + dữ liệu mã hoá (AES-256-GCM; khoá = SHA-256 của chuỗi dưới; blob = nonce 12B | tag 16B | ciphertext, base64) ----
    private static string KeyPassphrase => LocalSecrets.FsgKeyPassphrase;
    private static string EncServer => LocalSecrets.FsgEncServer;
    private static string EncDatabase => LocalSecrets.FsgEncDatabase;
    private static string EncUser => LocalSecrets.FsgEncUser;
    private static string EncPassword => LocalSecrets.FsgEncPassword;

    public enum SyncMode { AddOne, NewOnly, OverwriteAll }

    /// <summary>Kết quả tra 1 dự án (<see cref="LookupAsync"/>) — giữ nguyên dạng cũ để MainForm (Ctrl+F5) dùng tiếp.</summary>
    public sealed class Result
    {
        public bool Found;
        public string? Error;
        public Workspace? Workspace;
        /// <summary>Tóm tắt các giá trị lấy được, để người dùng rà lại.</summary>
        public string? Summary;
    }

    public sealed class SyncResult
    {
        public int Inserted, Updated, Skipped;
        public string? Error;
        public List<string> Notes { get; } = new();
        /// <summary>Mỗi dòng 1 dự án: "MÃ: các trường đã thay theo Web.config" (mật khẩu chỉ ghi tên trường, không ghi giá trị).</summary>
        public List<string> WebConfigFixes { get; } = new();
        public bool Success => Error is null;
        public string Summary => Error ?? $"Thêm {Inserted}, cập nhật {Updated}, bỏ qua {Skipped}.";
    }

    private sealed record FsgRow(string MaDa, string DirProApp, string DirSrcApp, string TenServer, string XUser, string XPass,
        string DbSys, string DirProWeb, string DirSrcWeb, string WebHost2, string DirUpdate, string MaPbsp);

    // Truy vấn đúng như yêu cầu, thêm bộ lọc @ma_da (NULL = lấy tất cả).
    private const string Sql = @"
SELECT a.ma_da, a.dir_pro_app, a.dir_src_app, b.ten_server, a.xuser, a.xpass, a.db_sys,
       a.dir_pro_web, a.dir_src_web, a.web_host2, a.dir_update, a.ma_pbsp
INTO #data
FROM nbdmda a
JOIN nbdmserver b ON a.server = b.ma_server
WHERE a.ma_pbsp <> '';

UPDATE #data SET xpass = @pw_lan WHERE @pw_lan IS NOT NULL AND ten_server LIKE '%172.168.5.14%';
UPDATE #data SET xpass = @pw_ltud1 WHERE @pw_ltud1 IS NOT NULL AND xuser LIKE '%LTUD1%';

SELECT * FROM #data WHERE (@ma_da IS NULL OR ma_da = @ma_da);";

    // ---- Công khai ----------------------------------------------------------------------------

    /// <summary>Tra 1 dự án theo ma_da và dựng sẵn Workspace (chưa thêm vào đâu cả).</summary>
    public async Task<Result> LookupAsync(string maDa)
    {
        maDa = (maDa ?? "").Trim();
        if (maDa.Length == 0) return new Result { Error = "Chưa nhập mã dự án." };
        try
        {
            var rows = await FetchAsync(maDa);
            var row = rows.FirstOrDefault(r => r.MaDa.Equals(maDa, StringComparison.OrdinalIgnoreCase));
            if (row is null) return new Result { Error = $"FSG không có dự án \"{maDa}\" (hoặc dự án chưa gán phân bổ sản phẩm)." };

            var ws = ToWorkspace(row);
            var configs = await WebConfigConnectionReader.ReadManyAsync(new[] { ws.ProgramPath });
            var info = configs.GetValueOrDefault(ws.ProgramPath.Trim());
            var fixes = info is null ? new List<string>() : OverrideFromWebConfig(ws, info);
            return new Result
            {
                Found = true,
                Workspace = ws,
                Summary = $"Server: {ws.Server}\nUser: {ws.User}\nSys Data: {ws.SysDatabase}\nApp Data: {ws.AppDatabase}\n" +
                          $"Login WLink: {ws.LoginWLink}\nProgram Path: {ws.ProgramPath}\nSource Path: {ws.SourcePath}\nWorking Path: {ws.WorkingPath}" +
                          (info is null ? "\n\n(Không đọc được Web.config ở Program Path — giữ theo QLYC.)"
                           : fixes.Count > 0 ? "\n\nĐã sửa theo Web.config: " + string.Join("; ", fixes) : "\n\nWeb.config khớp với QLYC."),
            };
        }
        catch (Exception ex)
        {
            return new Result { Error = ex.Message };
        }
    }

    /// <summary>Đồng bộ vào <paramref name="target"/> (danh sách Workspace đang dùng) theo <paramref name="mode"/>. Không tự lưu file.</summary>
    /// <param name="maDa">Bắt buộc với <see cref="SyncMode.AddOne"/>; bỏ qua ở 2 chế độ còn lại.</param>
    /// <param name="progress">Nhận câu tiến độ lúc đối chiếu Web.config (chế độ hàng loạt đọc vài nghìn file qua mạng, mất cỡ chục giây).</param>
    public async Task<SyncResult> SyncAsync(SyncMode mode, string? maDa, List<Workspace> target, IProgress<string>? progress = null)
    {
        var result = new SyncResult();
        maDa = maDa?.Trim();
        if (mode == SyncMode.AddOne && string.IsNullOrEmpty(maDa))
        {
            result.Error = "Chưa nhập mã dự án.";
            return result;
        }

        List<FsgRow> rows;
        try { rows = await FetchAsync(mode == SyncMode.AddOne ? maDa : null); }
        catch (Exception ex)
        {
            _ = ex; // không đưa chi tiết lỗi (có thể chứa tên server/database nội bộ) ra giao diện
            result.Error = Available ? "Không đồng bộ được — kiểm tra kết nối mạng hoặc quyền rồi thử lại." : MissingSecretMessage;
            return result;
        }

        if (mode == SyncMode.AddOne && rows.Count == 0)
        {
            result.Error = $"FSG không có dự án \"{maDa}\" (hoặc dự án chưa gán phân bổ sản phẩm).";
            return result;
        }

        // Có thể có nhiều dòng trùng ma_da trong FSG — lấy dòng đầu, không tạo project trùng.
        var byCode = new Dictionary<string, Workspace>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in target)
        {
            if (!string.IsNullOrWhiteSpace(w.ProjectId)) byCode.TryAdd(w.ProjectId, w);
            if (!string.IsNullOrWhiteSpace(w.Name)) byCode.TryAdd(w.Name, w);
        }

        // Lượt 1: chọn dòng nào sẽ thêm / cập nhật. Chưa đụng vào target để còn sửa theo Web.config trước.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(Workspace Fresh, Workspace? Existing)>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.MaDa) || !seen.Add(row.MaDa)) continue;

            if (byCode.TryGetValue(row.MaDa, out var existing))
            {
                if (mode == SyncMode.OverwriteAll) plan.Add((ToWorkspace(row), existing));
                else
                {
                    result.Skipped++;
                    if (mode == SyncMode.AddOne) result.Notes.Add($"\"{row.MaDa}\" đã có trong danh sách — không ghi đè (dùng chế độ \"Overwrite tất cả\" nếu muốn cập nhật).");
                }
                continue;
            }
            plan.Add((ToWorkspace(row), null));
        }

        // Lượt 2: Web.config ở Program Path là cái site thật dùng → lệch với QLYC thì lấy theo Web.config. Đọc song song, có hạn chờ;
        // dự án không đọc được thì giữ dữ liệu QLYC.
        if (plan.Count > 0)
        {
            static string PathOf((Workspace Fresh, Workspace? Existing) p) =>
                (string.IsNullOrWhiteSpace(p.Fresh.ProgramPath) ? p.Existing?.ProgramPath ?? "" : p.Fresh.ProgramPath).Trim();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var total = plan.Select(PathOf).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var lastShown = -1;
            var fileProgress = progress is null ? null : new Progress<int>(n =>
            {
                // Progress<T> gửi về luồng UI; chỉ đẩy câu mới mỗi ~5% cho đỡ gọi giao diện vài nghìn lần
                var pct = total == 0 ? 100 : n * 100 / total;
                if (pct / 5 == lastShown / 5 && n < total) return;
                lastShown = pct;
                progress.Report($"Đang đối chiếu Web.config ở Program Path: {n}/{total}...");
            });
            var configs = await WebConfigConnectionReader.ReadManyAsync(plan.Select(PathOf), progress: fileProgress);
            var read = 0;
            foreach (var p in plan)
            {
                if (configs.GetValueOrDefault(PathOf(p)) is not { } info) continue;
                read++;
                var fixes = OverrideFromWebConfig(p.Fresh, info);
                if (fixes.Count > 0) result.WebConfigFixes.Add($"{p.Fresh.ProjectId}: {string.Join("; ", fixes)}");
            }
            result.Notes.Add($"Web.config: đọc được {read}/{plan.Count} dự án trong {sw.Elapsed.TotalSeconds:0.0} s" +
                             (result.WebConfigFixes.Count > 0 ? $", sửa theo Web.config {result.WebConfigFixes.Count} dự án." : ", không lệch với QLYC."));
        }

        foreach (var (fresh, existing) in plan)
        {
            if (existing is not null) { ApplyTo(existing, fresh); result.Updated++; }
            else { target.Add(fresh); result.Inserted++; }
        }
        return result;
    }

    /// <summary>
    /// Sync menu: chạy <c>EXEC ns_createCommand N'&lt;mã dự án&gt;'</c> trên FSG_A (cùng kết nối đã mã hoá ở trên) để FSG tạo/đồng bộ
    /// menu cho dự án đó. Đây là lệnh GHI trên FSG_A — nơi gọi phải xác nhận với người dùng trước. Mã dự án truyền bằng tham số
    /// (nvarchar → N'...'), không ghép chuỗi. Trả về các thông báo PRINT/INFO của procedure và số dòng nó trả ra (nếu có).
    /// </summary>
    public async Task<(bool Ok, string Message)> CreateMenuAsync(string maDa)
    {
        maDa = (maDa ?? "").Trim();
        if (maDa.Length == 0) return (false, "Chưa nhập mã dự án.");

        try
        {
            await using var conn = new SqlConnection(BuildConnectionString());
            await conn.OpenAsync();

            // Tên tham số của procedure không biết trước → gọi theo vị trí qua biến, đúng như EXEC ns_createCommand N'...'.
            await using var cmd = new SqlCommand("EXEC ns_createCommand @ma_da_arg", conn) { CommandTimeout = 300 };
            cmd.Parameters.Add("@ma_da_arg", System.Data.SqlDbType.NVarChar, 200).Value = maDa;

            // Đọc hết kết quả cho procedure chạy xong, nhưng KHÔNG trả nội dung/thông báo của nó về giao diện (tránh lộ script, tên bảng).
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                do { while (await r.ReadAsync()) { } } while (await r.NextResultAsync());
            }
            return (true, "");
        }
        catch
        {
            // Chi tiết lỗi SQL có thể chứa tên server/đối tượng nội bộ → chỉ báo chung.
            return (false, Available ? "Không tạo được menu — kiểm tra kết nối mạng hoặc quyền rồi thử lại." : MissingSecretMessage);
        }
    }

    /// <summary>1 dòng yêu cầu lấy từ bảng nvphyc: mã dự án, bộ phận lập trình, mã nhân viên, tên lập trình, mã yêu cầu.</summary>
    public sealed record RequestRow(string MaDa, string BpLt, string MaNv1, string MaLt1, string Fcode1, string NoiDung, string GhiChu = "", string TenMenu = "", string MaXlyc = "");

    /// <summary>Chữ thuần từ chuỗi HTML của ns_getContentInfo: &lt;br/&gt; → xuống dòng, bỏ thẻ (span, style...), giải mã &amp;nbsp; &amp;lt;...</summary>
    internal static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = System.Text.RegularExpressions.Regex.Replace(html, @"<\s*br\s*/?\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s).Replace("\u00A0", " ").Replace("\r", "");
        var lines = s.Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// Note (New) → "Sync yêu cầu": kéo danh sách yêu cầu của <paramref name="programmer"/> (cột ma_lt1) trong dự án
    /// <paramref name="maDa"/> từ bảng nvphyc, theo thứ tự xorder, stt_rec. fcode1 = mã yêu cầu, noi_dung = nội dung yêu cầu. Chỉ ĐỌC. Lỗi chỉ trả câu chung.
    /// </summary>
    public async Task<(List<RequestRow> Rows, string? Error)> FetchRequestsAsync(string maDa, string programmer)
    {
        maDa = (maDa ?? "").Trim();
        programmer = (programmer ?? "").Trim();
        if (maDa.Length == 0) return (new(), "Chưa chọn project.");
        if (programmer.Length == 0) return (new(), "Chưa khai tên lập trình.");

        try
        {
            await using var conn = new SqlConnection(BuildConnectionString());
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT stt_rec, ma_da, bp_lt, ma_nv1, ma_lt1, fcode1, noi_dung FROM nvphyc " +
                "WHERE ma_da = @ma_da AND ma_lt1 = @lt AND ISNULL(fcode1, '') <> '' ORDER BY xorder, stt_rec", conn)
            { CommandTimeout = 60 };
            cmd.Parameters.Add("@ma_da", System.Data.SqlDbType.NVarChar, 100).Value = maDa;
            cmd.Parameters.Add("@lt", System.Data.SqlDbType.NVarChar, 100).Value = programmer;

            var basics = new List<(string Stt, RequestRow Row)>();
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    string S(string c) => r.IsDBNull(r.GetOrdinal(c)) ? "" : Convert.ToString(r.GetValue(r.GetOrdinal(c)))?.Trim() ?? "";
                    basics.Add((S("stt_rec"), new RequestRow(S("ma_da"), S("bp_lt"), S("ma_nv1"), S("ma_lt1"), S("fcode1"), S("noi_dung"))));
                }

            // Tên menu + ghi chú (tester / lỗi) lấy từ thủ tục ns_getContentInfo(@cID = stt_rec) — cùng nguồn với màn hình xem yêu cầu. Thủ tục lỗi / không có
            // thì bỏ trống 2 phần này chứ không làm hỏng cả lần Sync. Gọi song song tối đa 4 kết nối.
            var extra = new (string Menu, string Note, string Content, string Status)[basics.Count];
            Array.Fill(extra, ("", "", "", ""));
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(basics.Select(async (b0, i) =>
            {
                if (b0.Stt.Length == 0) return;
                await gate.WaitAsync();
                try
                {
                    await using var c2 = new SqlConnection(BuildConnectionString());
                    await c2.OpenAsync();
                    await using var pc = new SqlCommand("ns_getContentInfo", c2) { CommandType = System.Data.CommandType.StoredProcedure, CommandTimeout = 30 };
                    pc.Parameters.Add("@cID", System.Data.SqlDbType.Char, 13).Value = b0.Stt;
                    await using var pr = await pc.ExecuteReaderAsync();
                    if (await pr.ReadAsync())
                    {
                        string G(string col) { try { var o = pr.GetOrdinal(col); return pr.IsDBNull(o) ? "" : Convert.ToString(pr.GetValue(o)) ?? ""; } catch { return ""; } }
                        extra[i] = (HtmlToText(G("ten_menu")), HtmlToText(G("ghi_chu")), HtmlToText(G("noi_dung")), HtmlToText(G("ma_xlyc")).Replace("\r\n", " "));
                    }
                }
                catch { /* thủ tục không chạy được cho y/c này — bỏ trống tên menu / ghi chú */ }
                finally { gate.Release(); }
            }));

            var rows = new List<RequestRow>();
            for (var i = 0; i < basics.Count; i++)
            {
                var row = basics[i].Row;
                rows.Add(row with { NoiDung = string.IsNullOrWhiteSpace(row.NoiDung) ? extra[i].Content : row.NoiDung, GhiChu = extra[i].Note, TenMenu = extra[i].Menu, MaXlyc = extra[i].Status });
            }
            return (rows, null);
        }
        catch
        {
            return (new(), Available ? "Không đồng bộ được yêu cầu — kiểm tra kết nối mạng hoặc quyền rồi thử lại." : MissingSecretMessage);
        }
    }

    /// <summary>1 dòng của "Báo cáo yêu cầu FSG" (bảng nvphyc + hạn hoàn thành từ vnbcnhanhtda).</summary>
    public sealed record RequirementRow(string MaDa, string MenuId, string TenMenuNgan, string TrangTlks, string TlksYn, string BpLt, string Fcode1,
        string NoiDung, string MaLt1, string MaNv1, DateTime? NgayHt, string TrangThai, string GhiChu = "");

    /// <summary>
    /// Báo cáo yêu cầu FSG: đọc nvphyc (ma_da, menu_id, ten_menu_ngan, trang_tlks, tlks_yn, bp_lt, fcode1, noi_dung, ma_lt1, ma_nv1, trang_thai) và JOIN
    /// vnbcnhanhtda theo ma_da + giai_doan_da lấy ngay_ht (hạn hoàn thành). Lọc theo mã dự án / tên lập trình / tên triển khai (rỗng = không lọc); lọc trạng thái làm ở trang.
    /// Chỉ ĐỌC. Khác các hàm trên: lỗi SQL được báo kèm nội dung (tên cột/đối tượng) để dễ chỉnh khi cấu trúc bảng FSG khác dự kiến.
    /// </summary>
    public async Task<(List<RequirementRow> Rows, string? Error)> FetchRequirementReportAsync(string maDa, string bpLt, string programmer, string deployer, string person, string fcode = "", string content = "")
    {
        try
        {
            await using var conn = new SqlConnection(BuildConnectionString());
            await conn.OpenAsync();
            await using var cmd = new SqlCommand { Connection = conn, CommandTimeout = 90 };
            // Mỗi ô lọc nhận NHIỀU giá trị (cách nhau dấu phẩy / chấm phẩy / khoảng trắng) → a.cột IN (@p0, @p1…). Ô trống = không lọc.
            var where = new List<string> { "ISNULL(a.fcode1, '') <> ''" };
            var n = 0;
            string In(string col, string raw)
            {
                var vals = new string(((raw ?? "").Select(ch => ch == ',' || ch == ';' || char.IsWhiteSpace(ch) ? ' ' : ch)).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();
                if (vals.Count == 0) return "";
                var names = vals.Select(v => { var p = "@p" + n++; cmd.Parameters.Add(p, System.Data.SqlDbType.NVarChar, 100).Value = v; return p; }).ToList();
                return $"{col} IN ({string.Join(", ", names)})";
            }
            foreach (var cond in new[] { In("a.ma_da", maDa), In("a.bp_lt", bpLt), In("a.ma_lt1", programmer), In("a.ma_nv1", deployer) }) if (cond.Length > 0) where.Add(cond);
            // Mã yêu cầu: chứa một trong các mã đã nhập (LIKE %mã%); Nội dung: chứa TẤT CẢ các từ đã nhập (mỗi từ một LIKE)
            string Like(string col, string raw, string join)
            {
                var vals = new string((raw ?? "").Select(ch => ch == ',' || ch == ';' ? ' ' : ch).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
                if (vals.Count == 0) return "";
                var parts = vals.Select(v => { var p = "@p" + n++; cmd.Parameters.Add(p, System.Data.SqlDbType.NVarChar, 200).Value = "%" + v.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%"; return $"{col} LIKE {p}"; });
                return "(" + string.Join(join, parts) + ")";
            }
            var fc = Like("a.fcode1", fcode, " OR "); if (fc.Length > 0) where.Add(fc);
            var ct = Like("a.noi_dung", content, " AND "); if (ct.Length > 0) where.Add(ct);
            // "Người" = lập trình HOẶC triển khai (một ô để kéo theo người, không cần biết họ đứng ở cột nào)
            var pv = In("a.ma_lt1", person); if (pv.Length > 0) { var pn = In("a.ma_nv1", person); where.Add("(" + pv + " OR " + pn + ")"); }
            cmd.CommandText = $@"
SELECT a.stt_rec, a.ma_da, a.menu_id, a.ten_menu_ngan, a.trang_tlks, a.tlks_yn, a.bp_lt, a.fcode1, a.noi_dung, a.ma_lt1, a.ma_nv1, b.ngay_ht, a.trang_thai
FROM nvphyc a
	-- vnbcnhanhtda có thể có NHIỀU dòng cho cùng (ma_da, giai_doan_da) → JOIN thường làm 1 yêu cầu hiện lặp nhiều lần; chỉ lấy 1 dòng (hạn muộn nhất).
	OUTER APPLY (SELECT TOP 1 x.ngay_ht FROM vnbcnhanhtda x WHERE x.ma_da = a.ma_da AND x.giai_doan_da = a.giai_doan_da ORDER BY x.ngay_ht DESC) b
WHERE {string.Join(" AND ", where)}
ORDER BY a.ma_da, a.xorder, a.stt_rec";

            var rows = new List<RequirementRow>();
            var stts = new List<string>();
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    string S(string c) => r.IsDBNull(r.GetOrdinal(c)) ? "" : Convert.ToString(r.GetValue(r.GetOrdinal(c)))?.Trim() ?? "";
                    DateTime? d = r.IsDBNull(r.GetOrdinal("ngay_ht")) ? null : Convert.ToDateTime(r.GetValue(r.GetOrdinal("ngay_ht")));
                    stts.Add(S("stt_rec"));
                    rows.Add(new RequirementRow(S("ma_da"), S("menu_id"), S("ten_menu_ngan"), S("trang_tlks"), S("tlks_yn"), S("bp_lt"), S("fcode1"),
                        S("noi_dung"), S("ma_lt1"), S("ma_nv1"), d, S("trang_thai")));
                }

            // Ghi chú của y/c: dựng giống ns_getContentInfo (bảng nbctyc: xtype 1 = ghi chú tester, xtype 2 = lỗi tester ghi nhận). Đọc theo lô cho cả báo cáo (không gọi thủ tục từng dòng).
            // Lỗi / thiếu bảng thì bỏ trống cột ghi chú, không làm hỏng báo cáo.
            try
            {
                var notes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var keys = stts.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (var off = 0; off < keys.Count; off += 800)
                {
                    var batch = keys.Skip(off).Take(800).ToList();
                    await using var nc = new SqlCommand { Connection = conn, CommandTimeout = 60 };
                    var names = batch.Select((k, i) => { nc.Parameters.Add("@s" + i, System.Data.SqlDbType.Char, 13).Value = k; return "@s" + i; });
                    nc.CommandText = "SELECT stt_rec, xtype, RTRIM(ISNULL(ma_tester, '')) AS ma_tester, RTRIM(ISNULL(noi_dung, '')) AS noi_dung, RTRIM(ISNULL(ghi_chu_lt, '')) AS ghi_chu_lt, RTRIM(ISNULL(trang_thai, '')) AS trang_thai " +
                                     "FROM nbctyc WHERE noi_dung <> '' AND stt_rec IN (" + string.Join(", ", names) + ") ORDER BY stt_rec";
                    await using var nr = await nc.ExecuteReaderAsync();
                    while (await nr.ReadAsync())
                    {
                        string N(int i) => nr.IsDBNull(i) ? "" : Convert.ToString(nr.GetValue(i))?.Trim() ?? "";
                        var key = N(0); var xtype = N(1); var tester = N(2); var text = N(3); var lt = N(4); var tt = N(5);
                        string line;
                        if (xtype == "1") line = "[" + tester + "]:" + text;
                        else if (xtype == "2") line = "[" + tester + "][" + tt + "]:" + text + (lt.Length > 0 ? " [LT:" + lt + "]" : "");
                        else continue;
                        if (!notes.TryGetValue(key, out var l)) notes[key] = l = new List<string>();
                        l.Add(line);
                    }
                }
                for (var i = 0; i < rows.Count; i++)
                    if (stts[i].Length > 0 && notes.TryGetValue(stts[i], out var l)) rows[i] = rows[i] with { GhiChu = string.Join("\r\n", l) };
            }
            catch { /* không đọc được ghi chú — báo cáo vẫn đủ các cột còn lại */ }
            return (rows, null);
        }
        catch (Exception ex)
        {
            var msg = ex.Message.Split((char)10)[0].Trim();
            return (new(), "Không kéo được yêu cầu từ FSG: " + (msg.Length > 300 ? msg[..300] : msg));
        }
    }

    // ---- Nội bộ -------------------------------------------------------------------------------

    private static async Task<List<FsgRow>> FetchAsync(string? maDa)
    {
        await using var conn = new SqlConnection(BuildConnectionString());
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(Sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@ma_da", (object?)maDa ?? DBNull.Value);
        // Mật khẩu mặc định thay cho xpass của các server nội bộ — ở Secrets/LocalSecrets.cs; rỗng (NULL) = giữ xpass của FSG.
        cmd.Parameters.Add("@pw_lan", System.Data.SqlDbType.NVarChar, 200).Value = LocalSecrets.FsgDefaultPassLan is { Length: > 0 } lan ? lan : DBNull.Value;
        cmd.Parameters.Add("@pw_ltud1", System.Data.SqlDbType.NVarChar, 200).Value = LocalSecrets.FsgDefaultPassLtud1 is { Length: > 0 } lt ? lt : DBNull.Value;

        var rows = new List<FsgRow>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            string S(string col) => r.IsDBNull(r.GetOrdinal(col)) ? "" : Convert.ToString(r.GetValue(r.GetOrdinal(col)))?.Trim() ?? "";
            rows.Add(new FsgRow(S("ma_da"), S("dir_pro_app"), S("dir_src_app"), S("ten_server"), S("xuser"), S("xpass"),
                S("db_sys"), S("dir_pro_web"), S("dir_src_web"), S("web_host2"), S("dir_update"), S("ma_pbsp")));
        }
        return rows;
    }

    /// <summary>false = bản build không có Secrets/LocalSecrets.cs (máy chưa chép file bí mật) → không có thông tin kết nối FSG_A.</summary>
    public static bool Available => LocalSecrets.FsgEncServer.Length > 0;

    public const string MissingSecretMessage =
        "Bản Bcode này build thiếu file bí mật Secrets\\LocalSecrets.cs nên không có thông tin kết nối FSG — chép file đó vào src\\Bcode.App\\Secrets rồi build lại.";

    private static string BuildConnectionString()
    {
        if (!Available) throw new InvalidOperationException(MissingSecretMessage);
        return new SqlConnectionStringBuilder
        {
            DataSource = Decrypt(EncServer),
            InitialCatalog = Decrypt(EncDatabase),
            UserID = Decrypt(EncUser),
            Password = Decrypt(EncPassword),
            TrustServerCertificate = true,
            ConnectTimeout = 10,
        }.ConnectionString;
    }

    private static string Decrypt(string blob)
    {
        var raw = Convert.FromBase64String(blob);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(KeyPassphrase));
        var plain = new byte[raw.Length - 28];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>FSG chỉ có db_sys ("THAICHAU_FBISP242_S"); App Data theo quy ước đặt tên là cùng tiền tố với hậu tố "_A".</summary>
    private static string InferAppDatabase(string dbSys) =>
        dbSys.EndsWith("_S", StringComparison.OrdinalIgnoreCase) ? dbSys[..^2] + "_A" : "";

    /// <summary>ten_server trong FSG có dòng kèm tuỳ chọn kết nối ("172.168.5.14\SQL2008; Connect Timeout = 6000") — chỉ lấy phần
    /// tên server trước dấu ';', không thì Workspace.Server hỏng khi ghép connection string.</summary>
    private static string CleanServer(string tenServer) => tenServer.Split(';')[0].Trim();

    /// <summary>Web trước, không có thì app: dự án dạng app (vd FBFF) không có dir_*_web.</summary>
    private static string Pick(string web, string app) => !string.IsNullOrWhiteSpace(web) ? web : app;

    private static Workspace ToWorkspace(FsgRow r) => new()
    {
        Name = r.MaDa,
        ProjectId = r.MaDa,
        Server = CleanServer(r.TenServer),
        IntegratedSecurity = false,
        User = r.XUser,
        Password = r.XPass,
        SysDatabase = r.DbSys,
        AppDatabase = InferAppDatabase(r.DbSys),
        LoginWLink = r.WebHost2,
        ProgramPath = Pick(r.DirProWeb, r.DirProApp),
        SourcePath = Pick(r.DirSrcWeb, r.DirSrcApp),
        WorkingPath = r.DirUpdate,
        RegistryName = @"Software\Fast",
        VersionCode = r.MaPbsp,
        DbAccess = BuildDbAccess(r.DbSys, InferAppDatabase(r.DbSys), r.MaDa),
    };

    /// <summary>Hậu tố các database Proxy của dự án ({mã_dự_án}{hậu tố}); thêm hậu tố mới ở đây khi cần.</summary>
    private static readonly string[] ProxySuffixes = { "_eInv" };

    /// <summary>DB Access mặc định khi đồng bộ: Sys, App và các database Proxy {mã_dự_án}_eInv...</summary>
    private static string BuildDbAccess(string sys, string app, string maDa)
    {
        var list = new List<string>();
        void Add(string? n) { if (!string.IsNullOrWhiteSpace(n) && !list.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase)) list.Add(n.Trim()); }
        Add(sys); Add(app);
        foreach (var suffix in ProxySuffixes) Add(maDa + suffix);
        return string.Join(", ", list);
    }

    /// <summary>Giữ các database người dùng đã khai, thêm những database mới từ FSG (Proxy...) chưa có.</summary>
    private static string MergeDbAccess(string existing, string fresh)
    {
        var list = (existing ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var n in fresh.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
        return string.Join(", ", list);
    }

    /// <summary>
    /// Sửa Workspace vừa dựng từ QLYC theo Web.config: Server, Sys Data, App Data, User, Password (và Windows Auth nếu Web.config dùng).
    /// Web.config không khai trường nào thì giữ giá trị QLYC. appConnectionString của FBO thường để <c>Initial Catalog=%Database</c> (chọn
    /// lúc đăng nhập) → khi đó App Data suy lại từ Sys Data mới theo quy ước "_S" → "_A". Trả về danh sách trường đã thay để báo người dùng.
    /// </summary>
    private static List<string> OverrideFromWebConfig(Workspace w, WebConfigConnectionReader.Info info)
    {
        var changes = new List<string>();
        void Set(string label, string value, string old, Action<string> set, bool secret = false)
        {
            if (value.Length == 0 || (secret ? value == old : value.Equals(old.Trim(), StringComparison.OrdinalIgnoreCase))) return;
            set(value);
            changes.Add(secret ? label : $"{label} \"{old}\" → \"{value}\"");
        }

        Set("Server", info.Server, w.Server, v => w.Server = v);
        if (info.IntegratedSecurity)
        {
            if (!w.IntegratedSecurity) { w.IntegratedSecurity = true; changes.Add("Windows Auth"); }
        }
        else
        {
            Set("User", info.User, w.User, v => w.User = v);
            Set("Password", info.Password, w.Password, v => w.Password = v, secret: true);
        }
        var oldSys = w.SysDatabase;
        Set("Sys Data", info.SysDatabase, w.SysDatabase, v => w.SysDatabase = v);
        var sysChanged = !w.SysDatabase.Equals(oldSys, StringComparison.OrdinalIgnoreCase);
        var app = info.AppDatabase.Length > 0 ? info.AppDatabase : sysChanged ? InferAppDatabase(w.SysDatabase) : "";
        Set("App Data", app, w.AppDatabase, v => w.AppDatabase = v);
        if (sysChanged && app.Length == 0 && w.AppDatabase.Length > 0) changes.Add($"App Data \"{w.AppDatabase}\" (theo QLYC) cần kiểm tra lại");
        if (changes.Count > 0) w.DbAccess = BuildDbAccess(w.SysDatabase, w.AppDatabase, w.ProjectId);
        return changes;
    }

    /// <summary>Ghi đè bằng giá trị FSG có (không rỗng, đã sửa theo Web.config); giá trị FSG rỗng thì giữ cái đang có.</summary>
    private static void ApplyTo(Workspace w, Workspace fresh)
    {
        static string Keep(string fresh, string old) => string.IsNullOrWhiteSpace(fresh) ? old : fresh;
        w.ProjectId = Keep(fresh.ProjectId, w.ProjectId);
        w.Server = Keep(fresh.Server, w.Server);
        if (fresh.IntegratedSecurity) w.IntegratedSecurity = true;
        else if (!string.IsNullOrWhiteSpace(fresh.User)) { w.IntegratedSecurity = false; w.User = fresh.User; }
        w.Password = Keep(fresh.Password, w.Password);
        w.SysDatabase = Keep(fresh.SysDatabase, w.SysDatabase);
        w.AppDatabase = Keep(fresh.AppDatabase, w.AppDatabase);
        w.LoginWLink = Keep(fresh.LoginWLink, w.LoginWLink);
        w.ProgramPath = Keep(fresh.ProgramPath, w.ProgramPath);
        w.SourcePath = Keep(fresh.SourcePath, w.SourcePath);
        w.WorkingPath = Keep(fresh.WorkingPath, w.WorkingPath);
        if (string.IsNullOrWhiteSpace(w.RegistryName)) w.RegistryName = fresh.RegistryName;
        w.VersionCode = Keep(fresh.VersionCode, w.VersionCode);
        w.DbAccess = MergeDbAccess(w.DbAccess, fresh.DbAccess);
    }
}
