using System.Net.Http;
using System.Text;
using System.Text.Json;
using Bcode.App.Secrets;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Các ô nhập của màn Setup eInvoice (dùng chung cho tab WebView2 và form cũ). <paramref name="Unit"/> nhận nhiều mã đơn vị (ma_dvcs) cách nhau dấu phẩy /
/// chấm phẩy / khoảng trắng — mã đầu là mã khai báo (ma_kn). <paramref name="OwnProxy"/> = "Nhóm dịch vụ riêng": tắt thì dùng nhóm dịch vụ chung của Fast
/// (<see cref="EInvoiceSetupService.FastProxyCode"/>), bỏ qua ô ProxyCode và không cần Proxy setting.</summary>
public record EInvoiceInputs(string Project, string Product, string Unit, string ClientCode, string ProxyCode, string PortalKey, string PortalLink,
    string Username, string Password, string MiddleLink, string DbProxy, bool Hsm, bool OwnProxy = false);

/// <summary>
/// Logic của "Setup eInvoice (FE)", viết lại theo tool Setup HDDT (VB, src/Setup HDDT/fSetupFE.vb): dựng script App setting (dmstthddt, tkhddt, tkhddtdv,
/// options) + Proxy setting (tkhddt, edmsp, ekey, edmkh của database Proxy — chỉ khi có nhóm dịch vụ riêng), chạy script vào đúng database, và gọi API
/// UpdateProxy của Fast. pass_hddt / mk_tk_hddt = <c>Crypto.RSAEncrypt</c> (Libs/FastBusiness.Crypto.dll — chính là CryptoEx.jsAESEncrypt của bản VB) của
/// mật khẩu portal / ".", tính lại mỗi lần (RSA có đệm ngẫu nhiên nên chuỗi mỗi lần khác nhau nhưng giải mã ra như nhau). Cặp khoá RSA Proxy / App cố định
/// như bản VB, giá trị nằm ở Secrets/LocalSecrets.cs (gitignore) chứ không ghi trong source.
/// </summary>
public class EInvoiceSetupService
{
    /// <summary>Nhóm dịch vụ chung của Fast — bản VB tự điền khi không tick "Nhóm dịch vụ riêng".</summary>
    public const string FastProxyCode = "005500";

    /// <summary>Link API mặc định (urlService của bản VB + "UpdateProxy").</summary>
    public const string DefaultMiddleLink = "http://dev.fast.com.vn/Fast-EInvoice-Crypto/Fast-Api.asmx/UpdateProxy";

    public const string DefaultPortalLink = "https://tportal.fast.com.vn/AppService/FastEInvoice.PortalService.asmx";

    /// <summary>Khoá công khai của Portal Fast (mặc định ô "Portal Key" của bản VB).</summary>
    public const string FastPortalPublicKey = "<RSAKeyValue><Modulus>rjUs5YF+GcqdXUoCh6dmZVL0TqFxL3klsSkhbQIMvden8MUGsY8R8+4jUNJ0RbXXSu5KYQyy1n2Qhlvbz7QWAsrN7GaULdKM4RIgubFoOXANoUxOQDiJVyg/vDV+disyyzmtjIuBID4NA9vdBU2ejIvXaTmRO+NzUlNOUGuxCjU=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    private readonly DbConnectionService _connections;

    public EInvoiceSetupService(DbConnectionService connections) => _connections = connections;

    /// <summary>null = bản build có khoá RSA mẫu; còn lại là thông báo — thiếu thì KHÔNG được dựng script (sẽ ghi khoá rỗng vào DB khách).</summary>
    public static string? MissingKeysError =>
        LocalSecrets.EInvProxyPrivateKey.Length > 0 && LocalSecrets.EInvProxyPublicKey.Length > 0 && LocalSecrets.EInvAppPk.Length > 0 && LocalSecrets.EInvAppRk.Length > 0 ? null
            : "Error: bản Bcode này build thiếu file bí mật Secrets\\LocalSecrets.cs (khoá RSA của eInvoice) — chép file đó vào src\\Bcode.App\\Secrets rồi build lại.";

    /// <summary>Các mã đơn vị trong ô Đơn vị (bỏ trùng, giữ thứ tự). Mã đầu = mã khai báo ma_kn.</summary>
    public static IReadOnlyList<string> Units(string unit) =>
        unit.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Nhóm dịch vụ thực dùng: riêng = ô ProxyCode, không thì <see cref="FastProxyCode"/>.</summary>
    public static string EffectiveProxyCode(EInvoiceInputs i) => i.OwnProxy ? i.ProxyCode.Trim() : FastProxyCode;

    /// <summary>null = hợp lệ; còn lại là thông báo lỗi — cùng danh sách ô bắt buộc như bản VB (dự án, mã DN, đơn vị, nhóm dịch vụ, sản phẩm, tài khoản, mật khẩu, link portal).</summary>
    public static string? ValidateRequired(EInvoiceInputs i)
    {
        if (MissingKeysError is { } missing) return missing;
        var empty = new List<string>();
        if (string.IsNullOrWhiteSpace(i.Project)) empty.Add("Mã dự án");
        if (string.IsNullOrWhiteSpace(i.ClientCode)) empty.Add("Mã doanh nghiệp");
        if (Units(i.Unit).Count == 0) empty.Add("Đơn vị");
        if (string.IsNullOrWhiteSpace(EffectiveProxyCode(i))) empty.Add("Nhóm dịch vụ");
        if (string.IsNullOrWhiteSpace(i.Product)) empty.Add("Sản phẩm");
        if (string.IsNullOrWhiteSpace(i.Username)) empty.Add("Tài khoản portal");
        if (string.IsNullOrEmpty(i.Password)) empty.Add("Mật khẩu");
        if (string.IsNullOrWhiteSpace(i.PortalLink)) empty.Add("Link Portal");
        return empty.Count == 0 ? null : "Error: hãy nhập đủ thông tin — còn trống: " + string.Join(", ", empty) + ".";
    }

    /// <summary>pass_hddt = mã hoá mật khẩu portal, mk_tk_hddt = mã hoá "." (như bản VB). Lỗi DLL mã hoá → ném ngoại lệ có thông báo.</summary>
    public static (string PassHddt, string MkTkHddt) ComputeKeys(EInvoiceInputs i) => (Encrypt(i.Password), Encrypt("."));

    private static string Encrypt(string text)
    {
        string? s;
        try { s = global::Crypto.RSAEncrypt(text); }
        catch (Exception ex) { throw new InvalidOperationException("Không mã hoá được bằng FastBusiness.Crypto.dll: " + ex.Message, ex); }
        return string.IsNullOrEmpty(s) ? throw new InvalidOperationException("Không mã hoá được bằng FastBusiness.Crypto.dll (kết quả rỗng).") : s;
    }

    /// <summary>Chuỗi SQL kiểu Fox.ConvertVS2SQLType của bản VB: N'...' với ' nhân đôi.</summary>
    private static string Q(string s) => "N'" + s.Replace("'", "''") + "'";

    /// <summary>Step 1: script để xem / copy — App setting luôn có; Proxy setting chỉ khi "Nhóm dịch vụ riêng" (như bản VB).</summary>
    public static string BuildScript(EInvoiceInputs i)
    {
        var app = BuildAppScript(i);
        if (!i.OwnProxy) return app + "\n\n/* Proxy setting: không cần — dùng nhóm dịch vụ chung của Fast (" + FastProxyCode + ") */";
        var (passHddt, mkTkHddt) = ComputeKeys(i);
        return app + "\n\n/* Script update to Database Proxy" + (i.DbProxy.Trim().Length > 0 ? " \"" + i.DbProxy.Trim() + "\"" : "") + " */\n" + BuildProxyScript(i, passHddt, mkTkHddt);
    }

    /// <summary>"Proxy setting" — tkhddt/edmsp/ekey/edmkh. Chạy vào database Proxy riêng ("CSDL / Database Proxy"), KHÔNG phải App Data: "tkhddt" của database này
    /// có bộ cột khác "tkhddt" của database app (xem <see cref="BuildAppScript"/>).</summary>
    public static string BuildProxyScript(EInvoiceInputs i, string passHddt, string mkTkHddt)
    {
        if (MissingKeysError is { } missing) throw new InvalidOperationException(missing);
        string prj = i.Project.Trim(), prod = i.Product.Trim(), client = i.ClientCode.Trim(), unit = Units(i.Unit)[0];
        return $@"/*Proxy setting*/
delete tkhddt;
insert into tkhddt(ma_kh, ma_kn, ten_kn, ten_kn2, url_hddt1, url_hddt2, url_hddt3, user_hddt, [pass_hddt], ten_tk_hddt, [mk_tk_hddt], dich_vu_hddt, status, datetime0, datetime2)
select {Q(prj)}, {Q(unit)} , {Q(prj)}, '.', {Q(i.PortalLink.Trim())}, '.', '.', {Q(i.Username.Trim())}, {Q(passHddt)}, {Q(client)}, {Q(mkTkHddt)}, 8, '1', getdate(), getdate()
delete edmsp; insert into edmsp (ma_sp, ten_sp) select {Q(prod)}, {Q(prod)}
delete ekey; insert into ekey (proxy_code, private_key, public_key, pk_portal) select {Q(EffectiveProxyCode(i))}, {Q(LocalSecrets.EInvProxyPrivateKey)}, {Q(LocalSecrets.EInvProxyPublicKey)}, {Q(i.PortalKey.Trim())}
delete edmkh; insert into edmkh (ma_kh, ten_kh, ma_sp, client_code, public_key, password, status) select {Q(prj)}, {Q(prj)}, {Q(prod)}, {Q(client)}, {Q(LocalSecrets.EInvAppPk)}, '{LocalSecrets.EInvAppPassword}', '1'
";
    }

    /// <summary>"App setting" — chạy vào App Data của workspace. tkhddt = 1 tài khoản HĐĐT (ma_kn = đơn vị đầu); tkhddtdv = mỗi đơn vị trong ô Đơn vị một dòng trỏ
    /// về tài khoản đó (1 đơn vị thì đúng y script bản VB). pk_service = khoá công khai của Proxy.</summary>
    public static string BuildAppScript(EInvoiceInputs i)
    {
        if (MissingKeysError is { } missing) throw new InvalidOperationException(missing);
        string prj = i.Project.Trim();
        var units = Units(i.Unit);
        var kn = units[0];
        var dv = string.Join("\nunion all ", units.Select(u => $"select {Q(u)} , {Q(kn)}, getdate(), getdate(), 1, 1, cast(getdate() as date), '1'"));
        return $@"/* App setting */
delete dmstthddt;
insert into dmstthddt (ma_kh, pk, rk, pk_service, password) Select {Q(prj)}, {Q(LocalSecrets.EInvAppPk)}, {Q(LocalSecrets.EInvAppRk)}, {Q(LocalSecrets.EInvProxyPublicKey)}, '{LocalSecrets.EInvAppPassword}'
delete tkhddt;
insert into tkhddt(ma_kn, ten_kn, url_hddt1, url_hddt2, url_hddt3, user_hddt, ten_tk_hddt, dich_vu_hddt, status, datetime0, datetime2, user_id0, user_id2, serial_cert)
select {Q(kn)} , {Q(prj)}, {Q(i.PortalLink.Trim())}, '.', '.', {Q(i.Username.Trim())}, {Q(i.ClientCode.Trim())}, 8, '1', getdate(), getdate(), 1, 1, ''
delete tkhddtdv;
insert into tkhddtdv(ma_dvcs, ma_kn, datetime0, datetime2, user_id0, user_id2, ngay_hl, status)
{dv}
update options set val = 1 where rtrim(name) = 'm_sd_hddt'" + (i.Hsm ? "\nupdate options set val = 1 where rtrim(name) = 'm_ky_hddt';" : "");
    }

    /// <summary>
    /// "Step 2: Exec Insert in Server (SQL)": chạy thẳng script — App setting vào App Data của workspace; Proxy setting (chỉ khi nhóm dịch vụ riêng) vào
    /// "CSDL / Database Proxy" — vì bảng "tkhddt" có ở cả hai nơi với cột khác nhau. Khác nút "Cập nhật / Update Key" (gọi API, không đụng SQL trực tiếp).
    /// <paramref name="log"/> nhận từng dòng tiến trình (kind: info | ok | err).
    /// </summary>
    public async Task<bool> ExecuteScriptsAsync(EInvoiceInputs i, Action<string, string> log)
    {
        if (ValidateRequired(i) is { } missing) { log(missing, "err"); return false; }
        var dbProxy = i.DbProxy.Trim();
        if (i.OwnProxy && string.IsNullOrWhiteSpace(dbProxy))
        {
            log("Error: chưa khai \"CSDL / Database Proxy\" — nhóm dịch vụ riêng cần biết chạy phần Proxy setting vào database nào.", "err");
            return false;
        }

        var ok = true;
        if (i.OwnProxy)
        {
            try
            {
                var (passHddt, mkTkHddt) = ComputeKeys(i);
                log($"Đang chạy Proxy setting vào database \"{dbProxy}\"...", "info");
                await using var proxyConn = _connections.CreateConnectionToDatabase(dbProxy);
                await proxyConn.OpenAsync();
                await using var proxyCmd = new SqlCommand(BuildProxyScript(i, passHddt, mkTkHddt), proxyConn) { CommandTimeout = 60 };
                await proxyCmd.ExecuteNonQueryAsync();
                log($"✓ Proxy setting: OK (database \"{dbProxy}\").", "ok");
            }
            catch (Exception ex) { ok = false; log($"✗ Proxy setting LỖI (database \"{dbProxy}\"): {ex.Message}", "err"); }
        }
        else log($"Proxy setting: bỏ qua — nhóm dịch vụ chung của Fast ({FastProxyCode}).", "info");

        try
        {
            log("Đang chạy App setting vào App Data (database app hiện tại)...", "info");
            await using var appConn = _connections.CreateConnection(useSysDatabase: false);
            await appConn.OpenAsync();
            await using var appCmd = new SqlCommand(BuildAppScript(i), appConn) { CommandTimeout = 60 };
            await appCmd.ExecuteNonQueryAsync();
            log($"✓ App setting: OK (đơn vị {string.Join(", ", Units(i.Unit))}).", "ok");
        }
        catch (Exception ex) { ok = false; log($"✗ App setting LỖI: {ex.Message}", "err"); }

        return ok;
    }

    /// <summary>Link API thực gọi: bản VB ghép urlService + "UpdateProxy". Link cũ lưu trong einvoice.json kết thúc bằng /UpdateKey (API không có trong bản VB)
    /// hoặc chỉ tới .asmx thì đổi / thêm thành /UpdateProxy; link khác giữ nguyên.</summary>
    public static string NormalizeMiddleLink(string link)
    {
        var s = link.Trim().TrimEnd('/', '\\');
        if (s.Length == 0) return DefaultMiddleLink;
        if (s.EndsWith("/UpdateKey", StringComparison.OrdinalIgnoreCase)) return s[..^"UpdateKey".Length] + "UpdateProxy";
        if (s.EndsWith(".asmx", StringComparison.OrdinalIgnoreCase)) return s + "/UpdateProxy";
        return s;
    }

    /// <summary>Thân JSON gửi API UpdateProxy — đúng tên / thứ tự trường của reqClient bản VB. isProxyFast: bản VB truyền Boolean "Nhóm dịch vụ riêng" vào tham số
    /// Byte nên True thành 255 (CByte(True)), False thành 0 — giữ nguyên giá trị đó.</summary>
    public static string BuildUpdatePayload(EInvoiceInputs i, string passHddt) => JsonSerializer.Serialize<object>(new
    {
        ProjectCode = i.Project.Trim(),
        ClientCode = i.ClientCode.Trim(),
        ProxyCode = EffectiveProxyCode(i),
        UnitCode = Units(i.Unit)[0],
        LinkPotal = i.PortalLink.Trim(),
        LinkServiceMTT = "",
        UserName = i.Username.Trim(),
        Pass = i.Password,
        PortalPublicKey = i.PortalKey.Trim(),
        ProxyPublicKey = LocalSecrets.EInvProxyPublicKey,
        ProxyPrivateKey = LocalSecrets.EInvProxyPrivateKey,
        pass_hddt = passHddt,
        isProxyFast = (byte)(i.OwnProxy ? 255 : 0),
    }, PayloadJson);

    /// <summary>Không thoát &lt; &gt; + và chữ có dấu thành \uXXXX — thân JSON giống Newtonsoft của bản VB.</summary>
    private static readonly JsonSerializerOptions PayloadJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>"Cập nhật / Update Key" (= nút tạo script của bản VB): tính pass_hddt từ mật khẩu rồi POST lên API UpdateProxy. Kết quả {"d": "..."}: có "lỗi" /
    /// "thất bại" là lỗi.</summary>
    public async Task<(bool Ok, string Text)> UpdateKeyAsync(EInvoiceInputs i, Action<string> progress)
    {
        if (ValidateRequired(i) is { } missing) return (false, missing);
        try
        {
            progress("Đang tính pass_hddt từ mật khẩu portal...");
            var (passHddt, _) = ComputeKeys(i);
            var url = NormalizeMiddleLink(i.MiddleLink);
            progress($"Đang gọi API {url} (nhóm dịch vụ {EffectiveProxyCode(i)}, đơn vị {Units(i.Unit)[0]})...");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var content = new StringContent(BuildUpdatePayload(i, passHddt), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return (false, $"Error UpdateKey: thất bại, xin kiểm tra lại thông tin khai báo ! (HTTP {(int)response.StatusCode}{(body.Length > 0 ? ": " + (body.Length > 500 ? body[..500] + "…" : body) : "")})");

            string d;
            try { d = JsonDocument.Parse(body).RootElement.GetProperty("d").GetString() ?? ""; }
            catch { d = body; }
            return d.Contains("lỗi") || d.Contains("thất bại") ? (false, "Error UpdateKey: " + d) : (true, "UpdateKey : " + d);
        }
        catch (Exception ex) { return (false, $"Error UpdateKey: {ex.Message}"); }
    }
}
