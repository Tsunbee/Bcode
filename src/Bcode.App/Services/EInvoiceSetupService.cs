using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Các ô nhập của màn Setup eInvoice (dùng chung cho tab WebView2 và form cũ).</summary>
public record EInvoiceInputs(string Project, string Product, string Unit, string ClientCode, string ProxyCode, string PortalKey, string PortalLink,
    string Username, string Password, string MiddleLink, string DbProxy, bool Hsm);

/// <summary>
/// Logic của "Setup eInvoice (FE)": dựng script cấu hình hoá đơn điện tử (Proxy setting + App setting), chạy script vào đúng 2 database, và gọi API UpdateKey
/// của FastBusiness. Tách khỏi giao diện để tab WebView2 (<c>SetupEInvoiceControl</c>) và form cũ (<c>SetupEInvoiceForm</c>) dùng chung một bản — các khoá RSA tĩnh trong
/// script giữ nguyên như template gốc.
/// </summary>
public class EInvoiceSetupService
{
    private readonly DbConnectionService _connections;

    public EInvoiceSetupService(DbConnectionService connections) => _connections = connections;

    /// <summary>null = hợp lệ; còn lại là thông báo lỗi (thiếu Project / ClientCode).</summary>
    public static string? ValidateRequired(EInvoiceInputs i) =>
        string.IsNullOrWhiteSpace(i.Project) || string.IsNullOrWhiteSpace(i.ClientCode) ? "Error: please input fields" : null;

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
        $@"/*Script update to Database Proxy*/

/*Proxy setting*/
delete tkhddt
insert into tkhddt(ma_kh, ma_kn, ten_kn, ten_kn2, url_hddt1, url_hddt2, url_hddt3, user_hddt, [pass_hddt], ten_tk_hddt, [mk_tk_hddt], dich_vu_hddt, status, datetime0, datetime2)
	select N'{prj}', N'{unit}', N'{prj}', '.', N'{portalLink}', '.', '.', N'{user}', N'7266CC5F28AE082CF002DA2ACF0C7B2AF496905ED1E353ED00012F1064DBAABECE046C102D91EAC35F66EE3C0FB297922A0ACEC3DBE309B5312FECA3B28AA8C6536B858010EED6E3427541FD3712AA3F2BF15E5ACB6FCA6D5E9A34B59DE9285F30488FCD9ABC65BCBE95114053D7BD0070B76880FAB5195238CE425792033488', N'{client}', N'75B9DD5031BC28B620E44A06D8466425CCCA84A48DC4C14FC69848AEFC1C61D786726FD42A1A47D2C9650E2BD345FE5D80AB25E1F0272C53A941DEDA1AB0581449DFE40E953A211125287E1431CE4F19ECD95059669A439E4EC89F057BA723E65104E77A03E9A7808703A723ED80C783B19D01C853B909D0B5C201348C5B49B4', 8, '1', getdate(), getdate()

delete edmsp; insert into edmsp (ma_sp, ten_sp) select N'{prod}', N'{prod}'
delete ekey; insert into ekey (proxy_code, private_key, public_key, pk_portal) select N'{proxy}', N'<RSAKeyValue><Modulus>vA4WHJRhbEhh4t0/qsz3yRYJiCbH4Cg+tHIpxtFNp5G/Yl/C8XpeHg24SGg2N2zr/7WoLnz9G5FOd6Elt9Fdxor4S1EiDEWrOupyOWBx5ra0IHiFDer8p3Hx5QBH53QB/HyQGwZD/nwuWj99ITwjeC8L9iowaZgNzL0x+sC6VTE=</Modulus><Exponent>AQAB</Exponent><P>2LtQeONvtqCQFVGNvYj2UWnVLYjKYrGhI1ZCyc/x2MGFcRb/+x5j7DBOELMuNcfBe1Q58goqkDYDzffk0M2Rvw==</P><Q>3iCozm1CeFBH5A4vmKt08qui6yfIDw6yWmszn6ocGayGLlBGBXH4HFFYgUdgq+jbuXnyJa7qxDWNzdBX/o31Dw==</Q><DP>riCgoN+qK4KJAHfLd1IJBJQREEpswCqSmj993YLSfiHNQnUGKQ3bnjGZJtWu9MqO6rVa8Nm2JLMhD2RxVEk1JQ==</DP><DQ>pl6T0Ljo9jA7CEbPw2t4FmITjkmngA+j6jEs40OH9HrRrVKWf3GTQbJztbB+aYPpPoxln2/ZisgJw8NuhMxSZQ==</DQ><InverseQ>XGNuMxHF+kHAJ4FjCMVX1i+ZYFH9MXKCne9EwkKjrXUkrPPh4cwVrIwXhymEYH6UbYw6HpzKgUznyCuLYZ/Qng==</InverseQ><D>GCzTaN8mWw4/DzQUIDfzTrV3ijo6DbX+waG/fyCfFACnktTusa5idQicfSpwddWZzSikMz28KBQY+0YLHENdA5WlAmLit+seEOQe1TVXzAvjG5AeN7VRLRXCOCVI10JSK2+y9RejjSnDtOGEXJ4mD8KFuPmAv6NmMqXL+zARWQU=</D></RSAKeyValue>', N'<RSAKeyValue><Modulus>vA4WHJRhbEhh4t0/qsz3yRYJiCbH4Cg+tHIpxtFNp5G/Yl/C8XpeHg24SGg2N2zr/7WoLnz9G5FOd6Elt9Fdxor4S1EiDEWrOupyOWBx5ra0IHiFDer8p3Hx5QBH53QB/HyQGwZD/nwuWj99ITwjeC8L9iowaZgNzL0x+sC6VTE=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>', N'{portalKey}'
delete edmkh; insert into edmkh (ma_kh, ten_kh, ma_sp, client_code, public_key, password, status) select N'{prj}', N'{prj}', N'{prod}', N'{client}', N'<RSAKeyValue><Modulus>5sMYunQupB+bn2HsF5xVLe8xZLBNNBe/neWLF/75HRtaAAtIW2qR0YEzfi7FP22SHZY7j4hZHhGESNZce1km4zR1+FCAcZWNlu3cwGw2mFsvIi8hCfXX4RUPaGVSIH5/v5FL3FPLuTNG8Q4+Jmy50SQ9s3lyMxOx5wc/jgQmZVk=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>', '62e51ff343af5bc0c89a06a8640cd01c', '1'";

    /// <summary>App setting block — targets the App Database (the workspace's normal
    /// "App Data"), NOT the Proxy database. "tkhddt" here has a different column set
    /// than the Proxy database's own "tkhddt" above (no ma_kh/ten_kn2/pass_hddt/
    /// mk_tk_hddt; has user_id0/user_id2/serial_cert instead) — see ExecuteScriptsAsync.
    /// </summary>
    public static string BuildAppScript(string prj, string unit, string client, string portalLink, string user, string portalKey, bool isHsm) =>
        $@"/*App setting*/
delete dmstthddt
insert into dmstthddt (ma_kh, pk, rk, pk_service, password) Select N'{prj}', N'<RSAKeyValue><Modulus>5sMYunQupB+bn2HsF5xVLe8xZLBNNBe/neWLF/75HRtaAAtIW2qR0YEzfi7FP22SHZY7j4hZHhGESNZce1km4zR1+FCAcZWNlu3cwGw2mFsvIi8hCfXX4RUPaGVSIH5/v5FL3FPLuTNG8Q4+Jmy50SQ9s3lyMxOx5wc/jgQmZVk=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>', N'<RSAKeyValue><Modulus>5sMYunQupB+bn2HsF5xVLe8xZLBNNBe/neWLF/75HRtaAAtIW2qR0YEzfi7FP22SHZY7j4hZHhGESNZce1km4zR1+FCAcZWNlu3cwGw2mFsvIi8hCfXX4RUPaGVSIH5/v5FL3FPLuTNG8Q4+Jmy50SQ9s3lyMxOx5wc/jgQmZVk=</Modulus><Exponent>AQAB</Exponent><P>7NBacsi79GzFgwqyRpOY4ZzY2PZ/rZAbidATFtAag6+QfEqEutRoSZy1TNt1zb+CW665pbnEP7NhQ906hFGH4w==</P><Q>+XU53bHLpmmxgWngSy2h8p4oWY4KuzZCm2hziZCF4WgaBEPiNamUIEkEmr3f+oZcxzsrp1HKXUUraPJOm6eKkw==</Q><DP>Ee75WoXvDeSK1JCjzYpx4mwBU/Te2GL4YuhZ+blKuLw74d22zXs2ZpSyeh6IfktJcO37axx1SymnbP885jZSZw==</DP><DQ>7LjIa8+fsNCVqHg/ZzfreZ+KLMm090kbVfx9v2pNEcTHA4sjq8a7kROZcfqDBGriuhE1cLcV8QKFmjZuUBliTw==</DQ><InverseQ>Ye+lyhOs6eamWYkfEYcBrFEk6uaOVdcdYfy8a2A1nZO4Bl3PkIJZHBLIqlZJ2qHdqT4UQwUWp7vXkzEyDiA+2A==</InverseQ><D>WTbKA6PROGCD8N2RwhsNj2GvLec/IcmgqjHJUbCgrNEbPXMfOUB9OYsC1mDMn1YELG4dfsNO+OH6y5IcVQ/FiUuKR87+elQUokDBpyCTSWJYVbxz4JgwUffZkpVqbITmCWbcaFrI/hi4/fJxu7wom0JKHSZrVGAWDslyD2IL58U=</D></RSAKeyValue>', N'{portalKey}', '62e51ff343af5bc0c89a06a8640cd01c'

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
