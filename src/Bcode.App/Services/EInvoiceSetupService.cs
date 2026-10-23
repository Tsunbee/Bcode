using System.Net.Http;
using System.Text;
using System.Text.Json;
using Bcode.App.Secrets;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Các ô nhập của màn Setup eInvoice (dùng chung cho tab WebView2 và form cũ).</summary>
public record EInvoiceInputs(string Project, string Product, string Unit, string ClientCode, string ProxyCode, string PortalKey, string PortalLink,
    string Username, string Password, string MiddleLink, string DbProxy, bool Hsm);

/// <summary>
/// Logic của "Setup eInvoice (FE)": dựng script cấu hình hoá đơn điện tử (Proxy setting + App setting), chạy script vào đúng 2 database, và gọi API UpdateKey
/// của FastBusiness. Tách khỏi giao diện để tab WebView2 (<c>SetupEInvoiceControl</c>) và form cũ (<c>SetupEInvoiceForm</c>) dùng chung một bản — các khoá RSA tĩnh trong
/// script giữ nguyên như template gốc, nhưng giá trị nằm ở Secrets/LocalSecrets.cs (gitignore) chứ không ghi trong source.
/// </summary>
public class EInvoiceSetupService
{
    private readonly DbConnectionService _connections;

    public EInvoiceSetupService(DbConnectionService connections) => _connections = connections;

    /// <summary>null = bản build có khoá RSA / hash mẫu; còn lại là thông báo — thiếu thì KHÔNG được dựng script (sẽ ghi khoá rỗng vào DB khách).</summary>
    public static string? MissingKeysError =>
        LocalSecrets.EInvProxyPrivateKey.Length > 0 && LocalSecrets.EInvAppRk.Length > 0 ? null
            : "Error: bản Bcode này build thiếu file bí mật Secrets\\LocalSecrets.cs (khoá RSA của eInvoice) — chép file đó vào src\\Bcode.App\\Secrets rồi build lại.";

    /// <summary>null = hợp lệ; còn lại là thông báo lỗi (thiếu khoá mẫu / Project / ClientCode).</summary>
    public static string? ValidateRequired(EInvoiceInputs i) =>
        MissingKeysError ?? (string.IsNullOrWhiteSpace(i.Project) || string.IsNullOrWhiteSpace(i.ClientCode) ? "Error: please input fields" : null);

    /// <summary>Step 1: script gộp (Proxy + App) để xem / copy.</summary>
    public static string BuildScript(EInvoiceInputs i) =>
        BuildProxyScript(i.Project, i.Unit, i.ClientCode, i.PortalLink, i.Username, i.Product, i.ProxyCode, i.PortalKey)
        + "\n\n" + BuildAppScript(i.Project, i.Unit, i.ClientCode, i.PortalLink, i.Username, i.PortalKey, i.Hsm);

    /// <summary>"Proxy setting" — tkhddt/edmsp/ekey/edmkh. MUST run against the separate
    /// "CSDL / Database Proxy" database (_dbProxyBox), NOT the workspace's normal App/Sys
    /// Data — this database's own "tkhddt" has a different column set than the app
    /// database's "tkhddt" (see BuildAppScript below), which is exactly why running the
    /// whole combined script against one connection used to fail on one half's INSERT.
    /// </summary>
    public static string BuildProxyScript(string prj, string unit, string client, string portalLink, string user, string prod, string proxy, string portalKey) =>
        MissingKeysError is { } missing ? throw new InvalidOperationException(missing) : $@"/*Script update to Database Proxy*/

/*Proxy setting*/
delete tkhddt
insert into tkhddt(ma_kh, ma_kn, ten_kn, ten_kn2, url_hddt1, url_hddt2, url_hddt3, user_hddt, [pass_hddt], ten_tk_hddt, [mk_tk_hddt], dich_vu_hddt, status, datetime0, datetime2)
	select N'{prj}', N'{unit}', N'{prj}', '.', N'{portalLink}', '.', '.', N'{user}', N'{LocalSecrets.EInvPassHddt}', N'{client}', N'{LocalSecrets.EInvMkTkHddt}', 8, '1', getdate(), getdate()

delete edmsp; insert into edmsp (ma_sp, ten_sp) select N'{prod}', N'{prod}'
delete ekey; insert into ekey (proxy_code, private_key, public_key, pk_portal) select N'{proxy}', N'{LocalSecrets.EInvProxyPrivateKey}', N'{LocalSecrets.EInvProxyPublicKey}', N'{portalKey}'
delete edmkh; insert into edmkh (ma_kh, ten_kh, ma_sp, client_code, public_key, password, status) select N'{prj}', N'{prj}', N'{prod}', N'{client}', N'{LocalSecrets.EInvClientPublicKey}', '{LocalSecrets.EInvClientPassword}', '1'";

    /// <summary>App setting block — targets the App Database (the workspace's normal
    /// "App Data"), NOT the Proxy database. "tkhddt" here has a different column set
    /// than the Proxy database's own "tkhddt" above (no ma_kh/ten_kn2/pass_hddt/
    /// mk_tk_hddt; has user_id0/user_id2/serial_cert instead) — see ExecuteScriptsAsync.
    /// </summary>
    public static string BuildAppScript(string prj, string unit, string client, string portalLink, string user, string portalKey, bool isHsm) =>
        MissingKeysError is { } missing ? throw new InvalidOperationException(missing) : $@"/*App setting*/
delete dmstthddt
insert into dmstthddt (ma_kh, pk, rk, pk_service, password) Select N'{prj}', N'{LocalSecrets.EInvAppPk}', N'{LocalSecrets.EInvAppRk}', N'{portalKey}', '{LocalSecrets.EInvAppPassword}'

delete tkhddt; 
insert into tkhddt(ma_kn, ten_kn, url_hddt1, url_hddt2, url_hddt3, user_hddt, ten_tk_hddt, dich_vu_hddt, status, datetime0, datetime2, user_id0, user_id2, serial_cert)
	select N'{unit}', N'{prj}', N'{portalLink}', '.', '.', N'{user}', N'{client}', 8, '1', getdate(), getdate(), 1, 1, ''
	
update options set val = 1 where rtrim(name) = 'm_sd_hddt'
{(isHsm ? "update options set val = 1 where rtrim(name) = 'm_ky_hddt'" : "")}";

    /// <summary>
    /// "Step 2: Exec Insert in Server (SQL)": chạy thẳng script xuống 2 database RIÊNG — Proxy setting vào "CSDL / Database Proxy", App setting vào App Data của
    /// workspace — vì bảng "tkhddt" tồn tại ở cả hai nơi với cột khác nhau (chạy script gộp trên 1 connection luôn lỗi ở 1 nửa). Khác nút "Cập nhật / Update Key"
    /// (không đụng SQL trực tiếp mà gọi API UpdateKey). <paramref name="log"/> nhận từng dòng tiến trình (kind: info | ok | err).
    /// </summary>
    public async Task<bool> ExecuteScriptsAsync(EInvoiceInputs i, Action<string, string> log)
    {
        if (ValidateRequired(i) is { } missing) { log(missing, "err"); return false; }
        var dbProxy = i.DbProxy.Trim();
        if (string.IsNullOrWhiteSpace(dbProxy))
        {
            log("Error: chưa khai \"CSDL / Database Proxy\" — cần biết chạy phần Proxy setting vào database nào.", "err");
            return false;
        }

        var proxyScript = BuildProxyScript(i.Project, i.Unit, i.ClientCode, i.PortalLink, i.Username, i.Product, i.ProxyCode, i.PortalKey);
        var appScript = BuildAppScript(i.Project, i.Unit, i.ClientCode, i.PortalLink, i.Username, i.PortalKey, i.Hsm);
        var ok = true;

        try
        {
            log($"Đang chạy Proxy setting vào database \"{dbProxy}\"...", "info");
            await using var proxyConn = _connections.CreateConnectionToDatabase(dbProxy);
            await proxyConn.OpenAsync();
            await using var proxyCmd = new SqlCommand(proxyScript, proxyConn) { CommandTimeout = 60 };
            await proxyCmd.ExecuteNonQueryAsync();
            log($"✓ Proxy setting: OK (database \"{dbProxy}\").", "ok");
        }
        catch (Exception ex) { ok = false; log($"✗ Proxy setting LỖI (database \"{dbProxy}\"): {ex.Message}", "err"); }

        try
        {
            log("Đang chạy App setting vào App Data (database app hiện tại)...", "info");
            await using var appConn = _connections.CreateConnection(useSysDatabase: false);
            await appConn.OpenAsync();
            await using var appCmd = new SqlCommand(appScript, appConn) { CommandTimeout = 60 };
            await appCmd.ExecuteNonQueryAsync();
            log("✓ App setting: OK.", "ok");
        }
        catch (Exception ex) { ok = false; log($"✗ App setting LỖI: {ex.Message}", "err"); }

        return ok;
    }

    /// <summary>"Cập nhật / Update Key": lấy pass_hddt từ Portal rồi gọi API UpdateKey ở link trung gian (không chạy SQL trực tiếp).</summary>
    public async Task<(bool Ok, string Text)> UpdateKeyAsync(EInvoiceInputs i, Action<string> progress)
    {
        if (ValidateRequired(i) is { } missing) return (false, missing);
        progress("Đang lấy pass_hddt từ Portal...");
        try
        {
            // Giữ nguyên hành vi cũ: lấy pass_hddt từ Portal Service mặc định của Fast (không dùng ô "Link Portal").
            string? passHddt = await GetPassHddtFromPortalAsync("https://tportal.fast.com.vn/AppService/FastEInvoice.PortalService.asmx", i.ProxyCode, i.ClientCode, i.Username);
            if (string.IsNullOrEmpty(passHddt))
                return (false, "Error: Không thể lấy được pass_hddt từ Link Portal (Kiểm tra lại tài khoản/mật khẩu portal).");

            progress("Đang gọi API UpdateKey tới Server...");
            using var client = new HttpClient();
            var payload = new
            {
                ProjectCode = i.Project.Trim(),
                ClientCode = i.ClientCode,
                ProxyCode = i.ProxyCode,
                UnitCode = i.Unit.Trim(),
                LinkPotal = i.PortalLink,
                LinkServiceMTT = string.Empty,
                UserName = i.Username,
                Pass = i.Password.Trim(),
                PortalPublicKey = i.PortalKey.Trim(),
                ProxyPublicKey = string.Empty,
                ProxyPrivateKey = string.Empty,
                pass_hddt = passHddt,
                isProxyFast = 1
            };
            var httpContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            httpContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var response = await client.PostAsync(i.MiddleLink.Trim(), httpContent);
            var responseStr = await response.Content.ReadAsStringAsync();
            return response.IsSuccessStatusCode
                ? (true, "Thành công!\n" + responseStr)
                : (false, $"HTTP Error {(int)response.StatusCode}: {responseStr}");
        }
        catch (Exception ex) { return (false, $"Exception: {ex.Message}"); }
    }

    private static async Task<string?> GetPassHddtFromPortalAsync(string portalUrl, string proxyCode, string clientCode, string username)
    {
        try
        {
            using var client = new HttpClient();
            string soapXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                <soap:Body>
                    <CheckKey xmlns=""http://tempuri.org/"">
                    <proxyCode>{proxyCode}</proxyCode>
                    <clientCode>{clientCode}</clientCode>
                    <user>{username}</user>
                    <data></data>
                    </CheckKey>
                </soap:Body>
                </soap:Envelope>";
            var content = new StringContent(soapXml, Encoding.UTF8, "text/xml");
            client.DefaultRequestHeaders.Add("SOAPAction", "http://tempuri.org/CheckKey");   // header đặc trưng của ASMX service
            var response = await client.PostAsync(portalUrl, content);
            if (!response.IsSuccessStatusCode) return null;
            var xDoc = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());
            System.Xml.Linq.XName name = "{http://tempuri.org/}CheckKeyResult";
            return xDoc.Descendants(name).FirstOrDefault()?.Value;
        }
        catch { return null; }
    }
}
