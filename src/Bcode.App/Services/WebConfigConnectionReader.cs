using System.Collections.Concurrent;
using System.Data.Common;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Đọc thông tin kết nối từ Web.config ở thư mục Program Path của dự án (site đang chạy thật): key <c>sysDatabaseName</c> và
/// connection string <c>appConnectionString</c> / <c>sysConnectionString</c> → server, database Sys/App, user, mật khẩu SQL.
/// Đồng bộ FSG (<see cref="FsgProjectLookupService"/>) dùng để sửa những trường QLYC khai lệch — Web.config là cái site dùng nên được ưu tiên.
/// Chỉ ĐỌC file. Không có Program Path / không có file / không đọc được / quá thời gian → null, nơi gọi giữ nguyên dữ liệu QLYC.
/// </summary>
public static class WebConfigConnectionReader
{
    /// <summary>Giá trị rỗng = Web.config không khai (hoặc chỉ là chỗ giữ chỗ kiểu <c>%Database</c>) → giữ giá trị đang có.</summary>
    public sealed record Info(string Server, string SysDatabase, string AppDatabase, string User, string Password, bool IntegratedSecurity);

    /// <summary>Thời gian chờ đọc 1 file. Đọc thật qua LAN ~50 ms; quá hạn thì coi máy chủ đó không tới được và bỏ qua các dự án còn lại trên nó.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Đọc Web.config của nhiều Program Path cùng lúc (tối đa <paramref name="parallel"/> file). Khoá kết quả = đường dẫn đã Trim, không phân biệt hoa thường.
    /// <paramref name="progress"/> nhận số file đã xong. Đo thật (~2900 dự án gần hết trên 1 máy chủ): đọc qua LAN ~40 ms/file, song song 8 mất 13–25 s;
    /// tăng lên 32–64 luồng lại chậm hơn (máy chủ SMB nghẽn) nên để 8.
    /// </summary>
    public static async Task<Dictionary<string, Info?>> ReadManyAsync(IEnumerable<string> programPaths, int parallel = 8, TimeSpan? timeout = null, IProgress<int>? progress = null)
    {
        var paths = programPaths.Select(p => (p ?? "").Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new ConcurrentDictionary<string, Info?>(StringComparer.OrdinalIgnoreCase);
        var deadHosts = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var wait = timeout ?? DefaultTimeout;
        var done = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));

        await Task.WhenAll(paths.Select(async path =>
        {
            var host = UncHost(path);
            await gate.WaitAsync();
            try
            {
                if (deadHosts.ContainsKey(host)) { result[path] = null; return; }
                // Đọc trên đường UNC không tới được có thể treo hàng chục giây → chạy ở thread pool và chỉ chờ tới hạn. Không gọi File.Exists
                // trước (thêm 1 lượt mạng mỗi file) — thiếu file / thư mục thì bắt lỗi.
                var read = Task.Run(() =>
                {
                    try { return File.ReadAllText(Path.Combine(path, "Web.config")); }
                    catch (FileNotFoundException) { return null; }
                    catch (DirectoryNotFoundException) { return null; }
                });
                string? xml;
                try { xml = await read.WaitAsync(wait); }
                catch (TimeoutException) { deadHosts[host] = true; xml = null; }
                result[path] = xml is null ? null : Parse(xml, host);
            }
            catch { result[path] = null; }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref done));
            }
        }));
        return new Dictionary<string, Info?>(result, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Tách thông tin kết nối từ nội dung Web.config; null khi không phải XML hoặc không có key/connection string nào.</summary>
    /// <param name="siteHost">Máy chủ chứa site (host của đường UNC Program Path). Web.config khai server cục bộ (<c>.\SQL2019</c>, <c>(local)</c>,
    /// <c>localhost</c>) là tính từ máy chủ đó → đổi thành <c>siteHost\SQL2019</c>, không thì từ máy người dùng sẽ kết nối nhầm vào máy mình.</param>
    public static Info? Parse(string xml, string siteHost = "")
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return null; }

        string? sysDbName = null, appCs = null, sysCs = null;
        foreach (var add in doc.Descendants().Where(e => e.Name.LocalName.Equals("add", StringComparison.OrdinalIgnoreCase)))
        {
            string A(string name) => add.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            if (A("key").Equals("sysDatabaseName", StringComparison.OrdinalIgnoreCase)) sysDbName ??= A("value");
            else if (A("name").Equals("appConnectionString", StringComparison.OrdinalIgnoreCase)) appCs ??= A("connectionString");
            else if (A("name").Equals("sysConnectionString", StringComparison.OrdinalIgnoreCase)) sysCs ??= A("connectionString");
        }

        var sys = ParseConnectionString(sysCs);
        var app = ParseConnectionString(appCs);
        var info = new Info(
            Server: ResolveLocalServer(First(sys?.Server, app?.Server), siteHost),
            SysDatabase: First(sysDbName, sys?.Database),
            AppDatabase: Clean(app?.Database),
            User: First(sys?.User, app?.User),
            Password: sys is { Password.Length: > 0 } ? sys.Password : app?.Password ?? "",
            IntegratedSecurity: sys?.Integrated ?? app?.Integrated ?? false);
        return info.Server.Length + info.SysDatabase.Length + info.AppDatabase.Length + info.User.Length + info.Password.Length == 0 && !info.IntegratedSecurity
            ? null : info;
    }

    private sealed record Cs(string Server, string Database, string User, string Password, bool Integrated);

    /// <summary>SqlConnectionStringBuilder trước; chuỗi có từ khoá lạ (vd Provider=) làm nó báo lỗi thì đọc bằng DbConnectionStringBuilder theo các tên đồng nghĩa.</summary>
    private static Cs? ParseConnectionString(string? cs)
    {
        if (string.IsNullOrWhiteSpace(cs)) return null;
        try
        {
            var b = new SqlConnectionStringBuilder(cs);
            return new Cs(Clean(b.DataSource), Clean(b.InitialCatalog), Clean(b.UserID), CleanPassword(b.Password), b.IntegratedSecurity);
        }
        catch
        {
            try
            {
                var b = new DbConnectionStringBuilder { ConnectionString = cs };
                string G(params string[] keys) => keys.Select(k => b.TryGetValue(k, out var v) ? Convert.ToString(v) : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
                var integrated = G("Integrated Security", "Trusted_Connection");
                return new Cs(Clean(G("Data Source", "Server", "Address", "Addr", "Network Address")), Clean(G("Initial Catalog", "Database")),
                    Clean(G("User ID", "UID", "User")), CleanPassword(G("Password", "PWD")),
                    integrated.Equals("true", StringComparison.OrdinalIgnoreCase) || integrated.Equals("sspi", StringComparison.OrdinalIgnoreCase) || integrated.Equals("yes", StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }
    }

    /// <summary>Bỏ chỗ giữ chỗ FBO thay lúc chạy (<c>%Database</c>, <c>%UserID</c>).</summary>
    private static string Clean(string? s) => string.IsNullOrWhiteSpace(s) || s.Trim().StartsWith('%') ? "" : s.Trim();
    private static string CleanPassword(string? s) => string.IsNullOrEmpty(s) || s.StartsWith('%') ? "" : s;
    private static string First(string? a, string? b) => Clean(a) is { Length: > 0 } x ? x : Clean(b);

    private static readonly string[] LocalNames = { ".", "(local)", "localhost", "127.0.0.1" };

    /// <summary>".\sql2019" + site trên \\172.168.5.14 → "172.168.5.14\sql2019". Site nằm trên ổ cục bộ (không có host) thì giữ nguyên.</summary>
    private static string ResolveLocalServer(string server, string siteHost)
    {
        if (server.Length == 0 || siteHost.Length == 0) return server;
        var cut = server.IndexOfAny(new[] { '\\', ',' });
        var name = cut < 0 ? server : server[..cut];
        return LocalNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase) ? siteHost + (cut < 0 ? "" : server[cut..]) : server;
    }

    /// <summary>"\\172.168.5.14\CustomerPro\..." → "172.168.5.14"; đường dẫn ổ đĩa cục bộ → "".</summary>
    private static string UncHost(string path) =>
        path.StartsWith(@"\\") ? path[2..].Split('\\', 2)[0] : "";
}
