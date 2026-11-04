using System.Net.Http;
using System.Text;
using System.Text.Json;
using Bcode.App.Secrets;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Các ô nhập của màn Setup eInvoice (dùng chung cho tab WebView2 và form cũ). <paramref name="Unit"/> = đơn vị đang khai (ma_kn); mã thêm sau dấu phẩy /
/// chấm phẩy / khoảng trắng (nếu có) = đơn vị (ma_dvcs) dùng chung tài khoản đó. <paramref name="OwnProxy"/> = "Nhóm dịch vụ riêng": tắt thì dùng nhóm dịch vụ chung của Fast
/// (<see cref="EInvoiceSetupService.FastProxyCode"/>), bỏ qua ô ProxyCode và không cần Proxy setting.</summary>
public record EInvoiceInputs(string Project, string Product, string Unit, string ClientCode, string ProxyCode, string PortalKey, string PortalLink,
    string Username, string Password, string MiddleLink, string DbProxy, bool Hsm, bool OwnProxy = false);

/// <summary>
/// Logic của "Setup eInvoice (FE)", viết lại theo tool Setup HDDT (VB, src/Setup HDDT/fSetupFE.vb): dựng script App setting (dmstthddt, tkhddt, tkhddtdv,
/// options) + Proxy setting (tkhddt, edmsp, ekey, edmkh của database Proxy — chỉ khi có nhóm dịch vụ riêng), chạy script vào đúng database, và gọi API
/// UpdateProxy của Fast. pass_hddt / mk_tk_hddt = <c>Crypto.RSAEncrypt</c> (Libs/FastBusiness.Crypto.dll — chính là CryptoEx.jsAESEncrypt của bản VB) của
/// mật khẩu portal / ".", tính lại mỗi lần (RSA có đệm ngẫu nhiên nên chuỗi mỗi lần khác nhau nhưng giải mã ra như nhau). Nhiều đơn vị: khai lần lượt từng đơn vị
/// (mỗi đơn vị mã DN / tài khoản portal riêng), script chỉ xoá / ghi dòng của đơn vị đó. Khoá RSA: cặp khoá cố định của bản VB (<see cref="VbKeys"/>,
/// Secrets/LocalSecrets.cs, gitignore) — giống hệt bản VB cả ở script lẫn thân request gửi service UpdateProxy.
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

    /// <summary>Database Proxy khai trong "DB Access" của project: ưu tiên {mã_dự_án}_eInv, rồi database đầu tiên có đuôi _eInv, rồi có chữ "eInv";
    /// không có thì null (giữ giá trị người dùng tự gõ).</summary>
    public static string? ProxyDatabaseOf(Models.Workspace? ws)
    {
        if (ws is null) return null;
        // Bỏ database Sys / App của project (tên App có chữ "einv" thì khỏi bị nhận nhầm là database Proxy).
        var dbs = ws.AccessDatabases().Where(d => !d.Equals(ws.AppDatabase, StringComparison.OrdinalIgnoreCase) && !d.Equals(ws.SysDatabase, StringComparison.OrdinalIgnoreCase)).ToList();
        return dbs.FirstOrDefault(d => d.Equals(ws.ProjectId + "_eInv", StringComparison.OrdinalIgnoreCase))
            ?? dbs.FirstOrDefault(d => d.EndsWith("_eInv", StringComparison.OrdinalIgnoreCase))
            ?? dbs.FirstOrDefault(d => d.Contains("eInv", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Danh mục đơn vị cơ sở (dmdvcs) của App Data đang chọn — gợi ý cho ô Đơn vị. Không có bảng / không kết nối được thì danh sách rỗng.</summary>
    public async Task<List<(string Code, string Name)>> LoadUnitsAsync()
    {
        var list = new List<(string, string)>();
        if (_connections.Current is null) return list;
        try
        {
            await using var conn = _connections.CreateConnection(useSysDatabase: false);
            await conn.OpenAsync();
            // ten_dvcs có ở FBO; DB khác thiếu cột thì chỉ lấy mã. Nhánh có ten_dvcs chạy bằng exec: SQL Server biên dịch cả batch, cột thiếu là lỗi dù nhánh không chạy.
            var sql = "if object_id('dmdvcs') is not null if col_length('dmdvcs', 'ten_dvcs') is not null "
                + "exec('select rtrim(ma_dvcs), rtrim(isnull(ten_dvcs, '''')) from dmdvcs order by ma_dvcs') else select rtrim(ma_dvcs), '' from dmdvcs order by ma_dvcs";
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                if (!r.IsDBNull(0) && r.GetString(0).Length > 0) list.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        }
        catch { /* không có bảng / mất kết nối: ô Đơn vị vẫn gõ tay được */ }
        return list;
    }

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

    /// <summary>Cặp khoá RSA dùng khi setup. <paramref name="Note"/> = nguồn khoá (để log / ghi chú trong script).</summary>
    public record Keys(string ProxyPk, string ProxyRk, string AppPk, string AppRk, string Note);

    /// <summary>Cặp khoá cố định của tool VB (LocalSecrets) — như bản VB: luôn dùng bộ này cho ekey / dmstthddt / edmkh VÀ gửi lên service UpdateProxy (service chỉ
    /// biết khoá Proxy được gửi lên, nhóm dịch vụ chung Fast chỉ biết khoá cố định). Không dùng lại khoá khác đang có trong DB — lệch với service.</summary>
    public static Keys VbKeys => new(LocalSecrets.EInvProxyPublicKey, LocalSecrets.EInvProxyPrivateKey, LocalSecrets.EInvAppPk, LocalSecrets.EInvAppRk, "khoá mặc định (tool VB)");

    /// <summary>Step 1: script để xem / copy — App setting luôn có; Proxy setting chỉ khi "Nhóm dịch vụ riêng" (như bản VB).</summary>
    public Task<string> BuildScriptAsync(EInvoiceInputs i) => Task.FromResult(BuildScript(i));

    public static string BuildScript(EInvoiceInputs i)
    {
        var keys = VbKeys;
        var app = BuildAppScript(i, keys);
        if (!i.OwnProxy) return app + "\n\n/* Proxy setting: không cần — dùng nhóm dịch vụ chung của Fast (" + FastProxyCode + ") */";
        var (passHddt, mkTkHddt) = ComputeKeys(i);
        return app + "\n\n/* Script update to Database Proxy" + (i.DbProxy.Trim().Length > 0 ? " \"" + i.DbProxy.Trim() + "\"" : "") + " */\n" + BuildProxyScript(i, keys, passHddt, mkTkHddt);
    }

    /// <summary>"Proxy setting" — chạy vào database Proxy riêng ("CSDL / Database Proxy"), KHÔNG phải App Data: "tkhddt" của database này có bộ cột khác "tkhddt"
    /// của database app (xem <see cref="BuildAppScript"/>). Khai nhiều đơn vị như dự án đang chạy (vd KOG_PRO): mỗi đơn vị 1 dòng tkhddt (mã DN / tài khoản portal /
    /// pass_hddt riêng) — chỉ xoá dòng của đơn vị đang khai, đơn vị khác giữ nguyên; edmsp / ekey / edmkh là 1 dòng chung cho cả project (edmkh.client_code = '*'). edmkh KHÔNG xoá / sửa: đã
    /// có dòng của dự án thì bỏ qua (giữ nguyên network_key / last_access proxy đang dùng), chưa có mới insert. Cài lại từ đầu: nút Clear (<see cref="ClearAsync"/>).</summary>
    public static string BuildProxyScript(EInvoiceInputs i, Keys keys, string passHddt, string mkTkHddt)
    {
        if (MissingKeysError is { } missing) throw new InvalidOperationException(missing);
        string prj = i.Project.Trim(), prod = i.Product.Trim(), client = i.ClientCode.Trim(), unit = Units(i.Unit)[0];
        return $@"/*Proxy setting*/
delete tkhddt where rtrim(ma_kh) = {Q(prj)} and rtrim(ma_kn) = {Q(unit)};
insert into tkhddt(ma_kh, ma_kn, ten_kn, ten_kn2, url_hddt1, url_hddt2, url_hddt3, user_hddt, [pass_hddt], ten_tk_hddt, [mk_tk_hddt], dich_vu_hddt, status, datetime0, datetime2)
select {Q(prj)}, {Q(unit)} , {Q(prj)}, '.', {Q(i.PortalLink.Trim())}, '.', '.', {Q(i.Username.Trim())}, {Q(passHddt)}, {Q(client)}, {Q(mkTkHddt)}, 8, '1', getdate(), getdate()
delete edmsp; insert into edmsp (ma_sp, ten_sp) select {Q(prod)}, {Q(prod)}
delete ekey; insert into ekey (proxy_code, private_key, public_key, pk_portal) select {Q(EffectiveProxyCode(i))}, {Q(keys.ProxyRk)}, {Q(keys.ProxyPk)}, {Q(i.PortalKey.Trim())}
if not exists (select 1 from edmkh where rtrim(ma_kh) = {Q(prj)})
  insert into edmkh (ma_kh, ten_kh, ma_sp, client_code, public_key, password, status) select {Q(prj)}, {Q(prj)}, {Q(prod)}, '*', {Q(keys.AppPk)}, '{LocalSecrets.EInvAppPassword}', '1'
";
    }

    /// <summary>"App setting" — chạy vào App Data của workspace. dmstthddt: 1 dòng chung cho project (pk_service = khoá công khai của Proxy). tkhddt: chỉ xoá / ghi
    /// tài khoản của đơn vị đang khai (giữ serial_cert đã có), đơn vị khác giữ nguyên. tkhddtdv: đơn vị → chính nó (mã thêm sau dấu phẩy, nếu có, = đơn vị dùng chung
    /// tài khoản này). dmkysohddt (hình thức ký số theo kết nối): kieu_ky_so = 1 khi ký bằng token (không tick Ký HSM), 0 khi HSM; dmkieuphhddt (kiểu phát hành theo
    /// kết nối): kieu_ph = 0 — cả hai chỉ đơn vị đang khai, bỏ qua nếu DB chưa có bảng (bản FBO cũ).</summary>
    public static string BuildAppScript(EInvoiceInputs i, Keys keys)
    {
        if (MissingKeysError is { } missing) throw new InvalidOperationException(missing);
        string prj = i.Project.Trim();
        var units = Units(i.Unit);
        var kn = units[0];
        var dv = string.Join("\nunion all ", units.Select(u => $"select {Q(u)} , {Q(kn)}, getdate(), getdate(), 1, 1, cast(getdate() as date), '1'"));
        return $@"/* App setting */
delete dmstthddt;
insert into dmstthddt (ma_kh, pk, rk, pk_service, password) Select {Q(prj)}, {Q(keys.AppPk)}, {Q(keys.AppRk)}, {Q(keys.ProxyPk)}, '{LocalSecrets.EInvAppPassword}'
declare @serial_cert varchar(64) = isnull((select top 1 serial_cert from tkhddt where rtrim(ma_kn) = {Q(kn)}), '')
delete tkhddt where rtrim(ma_kn) = {Q(kn)};
insert into tkhddt(ma_kn, ten_kn, url_hddt1, url_hddt2, url_hddt3, user_hddt, ten_tk_hddt, dich_vu_hddt, status, datetime0, datetime2, user_id0, user_id2, serial_cert)
select {Q(kn)} , {Q(prj)}, {Q(i.PortalLink.Trim())}, '.', '.', {Q(i.Username.Trim())}, {Q(i.ClientCode.Trim())}, 8, '1', getdate(), getdate(), 1, 1, @serial_cert
delete tkhddtdv where rtrim(ma_kn) = {Q(kn)} or rtrim(ma_dvcs) in ({string.Join(", ", units.Select(Q))});
insert into tkhddtdv(ma_dvcs, ma_kn, datetime0, datetime2, user_id0, user_id2, ngay_hl, status)
{dv}
if object_id('dmkysohddt') is not null begin
  delete dmkysohddt where rtrim(ma_kn) = {Q(kn)};
  insert into dmkysohddt(ma_kn, kieu_ky_so, datetime0, datetime2, user_id0, user_id2) select {Q(kn)}, {(i.Hsm ? 0 : 1)}, getdate(), getdate(), 1, 1
end
if object_id('dmkieuphhddt') is not null begin
  delete dmkieuphhddt where rtrim(ma_kn) = {Q(kn)};
  insert into dmkieuphhddt(ma_kn, kieu_ph, datetime0, datetime2, user_id0, user_id2) select {Q(kn)}, 0, getdate(), getdate(), 1, 1
end
update options set val = 1 where rtrim(name) = 'm_sd_hddt'
update options set val = {(i.Hsm ? 0 : 1)} where rtrim(name) = 'm_ky_hddt'";   // Ký HSM = 0, không ký HSM = 1 (theo khai báo của Bee — bản VB chỉ ghi khi tick HSM)
    }

    /// <summary>Kiểm tra trước khi ghi database (dùng chung Step 2 và Update Key — Update Key kiểm trước khi gọi API để không gọi API xong mới báo thiếu).
    /// null = được chạy; còn lại là thông báo lỗi.</summary>
    public string? PrecheckExecute(EInvoiceInputs i)
    {
        if (ValidateRequired(i) is { } missing) return missing;
        var dbProxy = i.DbProxy.Trim();
        if (i.OwnProxy && dbProxy.Length == 0)
            return "Error: chưa khai \"CSDL / Database Proxy\" — nhóm dịch vụ riêng cần biết chạy phần Proxy setting vào database nào.";
        if (_connections.Current is null) return "Error: chưa chọn Workspace (WS).";
        // App setting chạy vào database "App" đang chọn ở thanh trên — nếu đang chọn chính database Proxy (… (DB) trong ô WS) thì dừng, kẻo ghi bảng app vào nhầm DB.
        var appDb = _connections.Current.EffectiveAppDatabase;
        if (appDb.Equals(dbProxy, StringComparison.OrdinalIgnoreCase) || appDb.Equals(ProxyDatabaseOf(_connections.Current), StringComparison.OrdinalIgnoreCase))
            return $"Error: database App đang chọn ở thanh trên là \"{appDb}\" (database Proxy) — chọn lại database App của project (… (App)) rồi chạy lại.";
        return null;
    }

    /// <summary>Chạy 1 script trong transaction (xact_abort): lỗi giữa chừng thì không để bảng đã delete mà chưa insert.</summary>
    private static async Task RunInTransactionAsync(SqlConnection conn, string script)
    {
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("set xact_abort on\nbegin tran\n" + script + "\ncommit tran", conn) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// "Step 2: Exec Insert in Server (SQL)": chạy thẳng script — App setting vào App Data của workspace; Proxy setting (chỉ khi nhóm dịch vụ riêng) vào
    /// "CSDL / Database Proxy" — vì bảng "tkhddt" có ở cả hai nơi với cột khác nhau. Mỗi script chạy trong 1 transaction. Nút "Cập nhật / Update Key" gọi API
    /// UpdateProxy rồi chạy tiếp hàm này (<see cref="UpdateKeyAndRunAsync"/>). <paramref name="log"/> nhận từng dòng tiến trình (kind: info | ok | err).
    /// </summary>
    public async Task<bool> ExecuteScriptsAsync(EInvoiceInputs i, Action<string, string> log, Keys? keys = null)
    {
        if (PrecheckExecute(i) is { } err) { log(err, "err"); return false; }
        keys ??= VbKeys;
        var dbProxy = i.DbProxy.Trim();
        var appDb = _connections.Current!.EffectiveAppDatabase;

        var ok = true;
        if (i.OwnProxy)
        {
            try
            {
                var (passHddt, mkTkHddt) = ComputeKeys(i);
                log($"Đang chạy Proxy setting vào database \"{dbProxy}\"...", "info");
                await using var proxyConn = _connections.CreateConnectionToDatabase(dbProxy);
                await RunInTransactionAsync(proxyConn, BuildProxyScript(i, keys, passHddt, mkTkHddt));
                log($"✓ Proxy setting: OK (database \"{dbProxy}\").", "ok");
            }
            catch (Exception ex) { ok = false; log($"✗ Proxy setting LỖI (database \"{dbProxy}\"): {ex.Message}", "err"); }
        }
        else log($"Proxy setting: bỏ qua — nhóm dịch vụ chung của Fast ({FastProxyCode}).", "info");

        try
        {
            log($"Đang chạy App setting vào App Data \"{appDb}\"...", "info");
            await using var appConn = _connections.CreateConnection(useSysDatabase: false);
            await RunInTransactionAsync(appConn, BuildAppScript(i, keys));
            log($"✓ App setting: OK (đơn vị {string.Join(", ", Units(i.Unit))}).", "ok");
        }
        catch (Exception ex) { ok = false; log($"✗ App setting LỖI: {ex.Message}", "err"); }

        return ok;
    }

    /// <summary>Bảng cấu hình HĐĐT mà nút Clear xoá sạch — App Data / database Proxy (cùng các bảng script setup ghi vào).</summary>
    public static readonly string[] ClearAppTables = { "dmstthddt", "tkhddt", "tkhddtdv", "dmkysohddt", "dmkieuphhddt" };
    public static readonly string[] ClearProxyTables = { "tkhddt", "edmsp", "ekey", "edmkh" };

    private static string BuildClearScript(IEnumerable<string> tables) =>
        string.Join("\n", tables.Select(t => $"if object_id('{t}') is not null delete {t}"));

    /// <summary>
    /// Nút "Clear": xoá HẾT dữ liệu các bảng cấu hình HĐĐT để cài lại từ đầu — App Data đang chọn (<see cref="ClearAppTables"/>) và database Proxy khi tick nhóm dịch vụ
    /// riêng (<see cref="ClearProxyTables"/>). Không gọi API, không đụng options. Mỗi database 1 transaction; bảng không có thì bỏ qua.
    /// </summary>
    public async Task<bool> ClearAsync(EInvoiceInputs i, Action<string, string> log)
    {
        if (_connections.Current is null) { log("Error: chưa chọn Workspace (WS).", "err"); return false; }
        var dbProxy = i.DbProxy.Trim();
        var appDb = _connections.Current.EffectiveAppDatabase;
        if (appDb.Equals(dbProxy, StringComparison.OrdinalIgnoreCase) || appDb.Equals(ProxyDatabaseOf(_connections.Current), StringComparison.OrdinalIgnoreCase))
        {
            log($"Error: database App đang chọn ở thanh trên là \"{appDb}\" (database Proxy) — chọn lại database App của project (… (App)) rồi chạy lại.", "err");
            return false;
        }
        if (i.OwnProxy && dbProxy.Length == 0) { log("Error: chưa khai \"CSDL / Database Proxy\" — không biết xoá cấu hình Proxy ở database nào.", "err"); return false; }

        var ok = true;
        try
        {
            log($"Đang xoá {string.Join(", ", ClearAppTables)} trong App Data \"{appDb}\"...", "info");
            await using var appConn = _connections.CreateConnection(useSysDatabase: false);
            await RunInTransactionAsync(appConn, BuildClearScript(ClearAppTables));
            log($"✓ Đã xoá cấu hình HĐĐT trong App Data \"{appDb}\".", "ok");
        }
        catch (Exception ex) { ok = false; log($"✗ Xoá App Data LỖI: {ex.Message}", "err"); }

        if (i.OwnProxy)
        {
            try
            {
                log($"Đang xoá {string.Join(", ", ClearProxyTables)} trong database Proxy \"{dbProxy}\"...", "info");
                await using var proxyConn = _connections.CreateConnectionToDatabase(dbProxy);
                await RunInTransactionAsync(proxyConn, BuildClearScript(ClearProxyTables));
                log($"✓ Đã xoá cấu hình HĐĐT trong database Proxy \"{dbProxy}\".", "ok");
            }
            catch (Exception ex) { ok = false; log($"✗ Xoá database Proxy \"{dbProxy}\" LỖI: {ex.Message}", "err"); }
        }
        else log($"Database Proxy: bỏ qua — nhóm dịch vụ chung của Fast ({FastProxyCode}).", "info");
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
    public static string BuildUpdatePayload(EInvoiceInputs i, Keys keys, string passHddt) => JsonSerializer.Serialize<object>(new
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
        ProxyPublicKey = keys.ProxyPk,
        ProxyPrivateKey = keys.ProxyRk,
        pass_hddt = passHddt,
        isProxyFast = (byte)(i.OwnProxy ? 255 : 0),
    }, PayloadJson);

    /// <summary>Không thoát &lt; &gt; + và chữ có dấu thành \uXXXX — thân JSON giống Newtonsoft của bản VB.</summary>
    private static readonly JsonSerializerOptions PayloadJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>"Cập nhật / Update Key": kiểm tra trước (<see cref="PrecheckExecute"/>) → gọi API UpdateProxy → API thành công thì chạy luôn script App (+ Proxy khi
    /// nhóm dịch vụ riêng) vào database như Step 2. Bản VB chỉ gọi API rồi hiện script để copy chạy tay; Bcode làm luôn bước chạy.</summary>
    public async Task<bool> UpdateKeyAndRunAsync(EInvoiceInputs i, Action<string, string> log)
    {
        if (PrecheckExecute(i) is { } err) { log(err, "err"); return false; }
        var keys = VbKeys;   // cùng 1 bộ khoá cho API và script, như bản VB
        var (ok, text) = await UpdateKeyAsync(i, keys, line => log(line, "info"));
        log(text, ok ? "ok" : "err");
        if (!ok) { log("Chưa chạy script vào database vì gọi API lỗi.", "err"); return false; }
        return await ExecuteScriptsAsync(i, log, keys);
    }

    /// <summary>Gọi API UpdateProxy (= nút tạo script của bản VB): tính pass_hddt từ mật khẩu rồi POST. Kết quả {"d": "..."}: có "lỗi" / "thất bại" là lỗi.</summary>
    public async Task<(bool Ok, string Text)> UpdateKeyAsync(EInvoiceInputs i, Keys keys, Action<string> progress)
    {
        if (ValidateRequired(i) is { } missing) return (false, missing);
        try
        {
            progress("Đang tính pass_hddt từ mật khẩu portal...");
            var (passHddt, _) = ComputeKeys(i);
            var url = NormalizeMiddleLink(i.MiddleLink);
            progress($"Đang gọi API {url} (nhóm dịch vụ {EffectiveProxyCode(i)}, đơn vị {Units(i.Unit)[0]})...");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            // Header như RestSharp của bản VB: Content-Type "application/json;charset=UTF-8" (không dấu cách) + Accept của RestSharp.
            var content = new StringContent(BuildUpdatePayload(i, keys, passHddt), Encoding.UTF8);
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=UTF-8");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/json, text/x-json, text/javascript, application/xml, text/xml");
            var response = await client.SendAsync(req);
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
