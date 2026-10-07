using System.Security.Cryptography;
using System.Text;
using Bcode.App.Models;
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
/// BẢO MẬT — đọc kỹ trước khi dựa vào: thông tin kết nối FSG_A không nằm dạng chữ thường trong source mà được mã hoá AES-256-GCM
/// bằng khoá cố định (hard-code bên dưới) rồi giải mã lúc chạy. Đây là LÀM RỐI (ai có file .exe/source đều lấy lại được khoá và
/// mật khẩu), chỉ tránh việc lộ mật khẩu khi lướt source/git hoặc grep — KHÔNG phải bảo vệ thật. Tài khoản FASTAD nên chỉ có quyền
/// SELECT trên FSG_A; muốn an toàn thật cần đưa bí mật ra khỏi máy client (vd dịch vụ trung gian hoặc DPAPI theo từng người dùng).
/// </summary>
public sealed class FsgProjectLookupService
{
    // ---- Khoá + dữ liệu mã hoá (AES-256-GCM; khoá = SHA-256 của chuỗi dưới; blob = nonce 12B | tag 16B | ciphertext, base64) ----
    private const string KeyPassphrase = "Q5xyatEtaBVq5JxnfBDKE0RYQiDGvNCx";
    private const string EncServer = "YpH0yzSQlchloWXdPUfX0O856NJbHXkRJDtJLOYfotAeJfvTsl1l9FfZkxBKloFSfuE=";
    private const string EncDatabase = "X6bXLHstcWOCpnNEX5KFKPD+tY8p6HHLJO/TnHxZp/tW";
    private const string EncUser = "oW9B9le6jjLyqtIujXP8s9WCrJO5gfAMoI/U6uW53nsC3A==";
    private const string EncPassword = "t4Rl6SC5P/LUPQJzMwoPBuSrioF5dOvT+lvFP6AkfLLG3NFOMY3ZVM6tDw==";

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

UPDATE #data SET xpass = 'fsd' WHERE ten_server LIKE '%172.168.5.14%';
UPDATE #data SET xpass = 'fts' WHERE xuser LIKE '%LTUD1%';

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
            return new Result
            {
                Found = true,
                Workspace = ws,
                Summary = $"Server: {ws.Server}\nUser: {ws.User}\nSys Data: {ws.SysDatabase}\nApp Data: {ws.AppDatabase}\n" +
                          $"Login WLink: {ws.LoginWLink}\nProgram Path: {ws.ProgramPath}\nSource Path: {ws.SourcePath}\nWorking Path: {ws.WorkingPath}",
            };
        }
        catch (Exception ex)
        {
            return new Result { Error = ex.Message };
        }
    }

    /// <summary>Đồng bộ vào <paramref name="target"/> (danh sách Workspace đang dùng) theo <paramref name="mode"/>. Không tự lưu file.</summary>
    /// <param name="maDa">Bắt buộc với <see cref="SyncMode.AddOne"/>; bỏ qua ở 2 chế độ còn lại.</param>
    public async Task<SyncResult> SyncAsync(SyncMode mode, string? maDa, List<Workspace> target)
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
            result.Error = "Không đồng bộ được — kiểm tra kết nối mạng hoặc quyền rồi thử lại.";
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

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.MaDa) || !seen.Add(row.MaDa)) continue;

            if (byCode.TryGetValue(row.MaDa, out var existing))
            {
                if (mode == SyncMode.OverwriteAll) { ApplyTo(existing, row); result.Updated++; }
                else
                {
                    result.Skipped++;
                    if (mode == SyncMode.AddOne) result.Notes.Add($"\"{row.MaDa}\" đã có trong danh sách — không ghi đè (dùng chế độ \"Overwrite tất cả\" nếu muốn cập nhật).");
                }
                continue;
            }

            var ws = ToWorkspace(row);
            target.Add(ws);
            byCode[row.MaDa] = ws;
            result.Inserted++;
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
            return (false, "Không tạo được menu — kiểm tra kết nối mạng hoặc quyền rồi thử lại.");
        }
    }

    /// <summary>1 dòng yêu cầu lấy từ bảng nvphyc: mã dự án, bộ phận lập trình, mã nhân viên, tên lập trình, mã yêu cầu.</summary>
    public sealed record RequestRow(string MaDa, string BpLt, string MaNv1, string MaLt1, string Fcode1, string NoiDung);

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
                "SELECT ma_da, bp_lt, ma_nv1, ma_lt1, fcode1, noi_dung FROM nvphyc " +
                "WHERE ma_da = @ma_da AND ma_lt1 = @lt AND ISNULL(fcode1, '') <> '' ORDER BY xorder, stt_rec", conn)
            { CommandTimeout = 60 };
            cmd.Parameters.Add("@ma_da", System.Data.SqlDbType.NVarChar, 100).Value = maDa;
            cmd.Parameters.Add("@lt", System.Data.SqlDbType.NVarChar, 100).Value = programmer;

            var rows = new List<RequestRow>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string S(string c) => r.IsDBNull(r.GetOrdinal(c)) ? "" : Convert.ToString(r.GetValue(r.GetOrdinal(c)))?.Trim() ?? "";
                rows.Add(new RequestRow(S("ma_da"), S("bp_lt"), S("ma_nv1"), S("ma_lt1"), S("fcode1"), S("noi_dung")));
            }
            return (rows, null);
        }
        catch
        {
            return (new(), "Không đồng bộ được yêu cầu — kiểm tra kết nối mạng hoặc quyền rồi thử lại.");
        }
    }

    // ---- Nội bộ -------------------------------------------------------------------------------

    private static async Task<List<FsgRow>> FetchAsync(string? maDa)
    {
        await using var conn = new SqlConnection(BuildConnectionString());
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(Sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@ma_da", (object?)maDa ?? DBNull.Value);

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

    private static string BuildConnectionString()
    {
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

    /// <summary>Ghi đè bằng giá trị FSG có (không rỗng); giá trị FSG rỗng thì giữ cái đang có.</summary>
    private static void ApplyTo(Workspace w, FsgRow r)
    {
        static string Keep(string fresh, string old) => string.IsNullOrWhiteSpace(fresh) ? old : fresh;
        var fresh = ToWorkspace(r);
        w.ProjectId = Keep(fresh.ProjectId, w.ProjectId);
        w.Server = Keep(fresh.Server, w.Server);
        if (!string.IsNullOrWhiteSpace(fresh.User)) { w.IntegratedSecurity = false; w.User = fresh.User; }
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
