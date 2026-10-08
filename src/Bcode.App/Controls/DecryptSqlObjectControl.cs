using System.Text;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Engine = SqlDecryptor.Core;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Decrypt SQL Object" — khôi phục source của stored procedure / view / function / trigger tạo bằng <c>WITH ENCRYPTION</c> trên SQL Server
/// của CHÍNH bạn (known-plaintext qua DAC + sys.sysobjvalues, do engine <c>SqlDecryptor.Core</c> đảm nhiệm; mọi ALTER giả chạy trong transaction và luôn rollback).
/// Giao diện là trang WebView2 (Web/Shell/decryptsql.html — ăn theo Template giao diện, tự co giãn); control này giữ kết nối + kết quả và gọi engine.
///
/// Thông tin kết nối: ưu tiên registry khai báo sẵn (<see cref="DecryptSqlCredentialStore"/>, user/pass mã hoá AES) — DAC cần login sysadmin; không có thì lấy theo workspace đang chọn.
/// </summary>
public class DecryptSqlObjectControl : UserControl
{
    private readonly WebBarHost _web = new("decryptsql.html") { Dock = DockStyle.Fill };
    private readonly Func<Workspace?> _workspace;
    private readonly SqlFormatterService _formatter = new();

    private readonly List<Engine.EncryptedObject> _allObjects = new();
    // Kết quả thô (chưa format) của lần giải mã gần nhất — để render lại khi bật/tắt Format.
    private readonly List<ResultEntry> _lastResults = new();
    private sealed record ResultEntry(string Banner, string? Sql, string? Warning, string? Error);
    private bool _busy;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    public DecryptSqlObjectControl(Func<Workspace?> workspace)
    {
        _workspace = workspace;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => { var raw = root.GetRawText(); _ = HandleAsync(raw); };
        _web.Ready += SendInit;
    }

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    /// <summary>Kết nối ban đầu: registry trước (nguồn = "registry"), không có thì workspace đang chọn (nguồn = "workspace").</summary>
    private void SendInit()
    {
        var ws = _workspace();
        var dbs = new List<string>();
        if (ws is not null)
        {
            if (!string.IsNullOrWhiteSpace(ws.AppDatabase)) dbs.Add(ws.AppDatabase);
            if (!string.IsNullOrWhiteSpace(ws.SysDatabase)) dbs.Add(ws.SysDatabase);
        }
        var reg = DecryptSqlCredentialStore.Load();
        object conn = reg is not null
            ? new { server = reg.Server, windows = reg.WindowsAuth, user = reg.User, pass = reg.Password, source = "registry" }
            : new { server = ws?.Server ?? "", windows = ws?.IntegratedSecurity ?? true, user = ws?.User ?? "", pass = ws?.Password ?? "", source = ws is null ? "" : "workspace" };
        Js($"decryptSql.init({J(new { conn, databases = dbs })})");
    }

    private static Engine.SqlObjectDecryptor BuildEngine(JsonElement conn)
    {
        var server = (conn.GetProperty("server").GetString() ?? "").Trim();
        if (server.Length == 0) throw new InvalidOperationException("Chưa nhập Server\\Instance.");
        var windows = conn.GetProperty("windows").GetBoolean();
        var user = (conn.GetProperty("user").GetString() ?? "").Trim();
        if (!windows && user.Length == 0) throw new InvalidOperationException("SQL Auth: cần nhập User (và Password).");
        return new Engine.SqlObjectDecryptor(new Engine.SqlConnectionInfo
        {
            InstanceName = server,
            UseWindowsAuth = windows,
            Username = user,
            Password = conn.GetProperty("pass").GetString() ?? "",
        });
    }

    private async Task HandleAsync(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();
            // Lấy hết giá trị ra khỏi JsonElement trước khi await (doc bị Dispose khi rời using).
            var conn = root.TryGetProperty("conn", out var c) ? c.Clone() : default;
            var db = root.TryGetProperty("db", out var d) ? d.GetString() ?? "" : "";
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.True;

            switch (action)
            {
                case "ready": SendInit(); break;
                case "loadDbs": await RunBusyAsync("Đang tải danh sách database...", () => LoadDatabasesAsync(conn)); break;
                case "loadObjs": await RunBusyAsync($"Đang liệt kê object mã hóa trong [{db}]...", () => LoadObjectsAsync(conn, db)); break;
                case "decrypt": await RunBusyAsync($"Đang giải mã {name} qua DAC...", () => DecryptOneAsync(conn, db, name, format)); break;
                case "decryptAll": await RunBusyAsync($"Đang giải mã toàn bộ [{db}] (tuần tự qua DAC)...", () => DecryptAllAsync(conn, db, format)); break;
                case "render": Render(format); break;
                case "saveReg": SaveRegistry(conn); break;
                case "clearReg": DecryptSqlCredentialStore.Clear(); Js($"decryptSql.onStatus({J("Đã xóa khai báo trong registry.")}, 'ok'); decryptSql.onRegistry(false)"); break;
                case "copy": try { Clipboard.SetText(text); } catch { /* clipboard bận */ } break;
                case "saveSql": SaveSql(text, name); break;
            }
        }
        catch (Exception ex)
        {
            Js($"decryptSql.onStatus({J("Lỗi: " + FriendlyError(ex))}, 'err'); decryptSql.onBusy(false)");
        }
    }

    // ---------------------------------------------------------------------------------------------

    private async Task LoadDatabasesAsync(JsonElement conn)
    {
        var dbs = await BuildEngine(conn).ListDatabasesAsync();
        Js($"decryptSql.onDatabases({J(dbs)})");
        Js($"decryptSql.onStatus({J($"Đã tải {dbs.Count} database.")}, 'ok')");
    }

    private async Task LoadObjectsAsync(JsonElement conn, string db)
    {
        if (db.Length == 0) { Js($"decryptSql.onStatus({J("Chưa chọn database.")}, 'err')"); return; }
        var objs = await BuildEngine(conn).ListEncryptedObjectsAsync(db);
        _allObjects.Clear();
        _allObjects.AddRange(objs);
        Js($"decryptSql.onObjects({J(objs.Select(o => new { name = o.FullName, type = o.Type }))})");
        Js($"decryptSql.onStatus({J(objs.Count == 0 ? "Không có object WITH ENCRYPTION nào trong database này." : $"Đã tìm thấy {objs.Count} object mã hóa. Gõ ô lọc để tìm nhanh, chọn rồi Giải mã.")}, {J(objs.Count > 0 ? "ok" : "err")})");
    }

    private async Task DecryptOneAsync(JsonElement conn, string db, string name, bool format)
    {
        if (name.Length == 0) return;
        var r = await BuildEngine(conn).DecryptAsync(db, name);
        _lastResults.Clear();
        _lastResults.Add(new ResultEntry(Banner(r.Name, r.Type, r.WasEncrypted), r.Sql, r.Warning, null));
        Render(format);
        Js($"decryptSql.onWarning({J(r.Warning ?? "")})");
        Js($"decryptSql.onStatus({J($"Xong: {r.Name}." + (r.Warning is null ? "" : "  (có cảnh báo)"))}, {J(r.Warning is null ? "ok" : "warn")})");
    }

    private async Task DecryptAllAsync(JsonElement conn, string db, bool format)
    {
        if (_allObjects.Count == 0) { Js($"decryptSql.onStatus({J("Chưa có object nào để giải mã.")}, 'err')"); return; }
        var progress = new Progress<string>(msg => Js($"decryptSql.onStatus({J(msg)}, '')"));
        var all = await BuildEngine(conn).DecryptAllAsync(db, progress);

        _lastResults.Clear();
        int ok = 0, fail = 0;
        foreach (var item in all)
        {
            if (item.Ok && item.Result is { } r)
            {
                ok++;
                _lastResults.Add(new ResultEntry(Banner(r.Name, r.Type, r.WasEncrypted), r.Sql, r.Warning, null));
            }
            else
            {
                fail++;
                _lastResults.Add(new ResultEntry(item.ObjectName, null, null, item.Error));
            }
        }
        Render(format);
        Js($"decryptSql.onWarning({J(fail > 0 ? $"{fail} object lỗi (xem chi tiết trong kết quả)." : "")})");
        Js($"decryptSql.onStatus({J($"Hoàn tất: {ok} thành công, {fail} lỗi / tổng {all.Count}.")}, {J(fail == 0 ? "ok" : "err")})");
    }

    private void SaveRegistry(JsonElement conn)
    {
        var server = (conn.GetProperty("server").GetString() ?? "").Trim();
        if (server.Length == 0) { Js($"decryptSql.onStatus({J("Chưa nhập Server\\Instance để lưu.")}, 'err')"); return; }
        DecryptSqlCredentialStore.Save(new DecryptSqlCredential(server, conn.GetProperty("windows").GetBoolean(),
            (conn.GetProperty("user").GetString() ?? "").Trim(), conn.GetProperty("pass").GetString() ?? ""));
        Js($"decryptSql.onStatus({J("Đã lưu server / user / pass (mã hóa AES) vào registry HKCU\\SOFTWARE\\Bcode\\DecryptSql — lần sau mở tab sẽ ưu tiên dùng.")}, 'ok'); decryptSql.onRegistry(true)");
    }

    private void SaveSql(string text, string name)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "SQL script (*.sql)|*.sql|Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = name.Length > 0 ? name.Replace('.', '_') + ".sql" : "decrypted.sql",
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
        Js($"decryptSql.onStatus({J("Đã lưu: " + dlg.FileName)}, 'ok')");
    }

    // ---------------------------------------------------------------------------------------------

    private static string Banner(string name, string type, bool wasEncrypted) =>
        $"-- {name}  ({type})  {(wasEncrypted ? "[decrypted]" : "[not encrypted]")}" + Environment.NewLine;

    /// <summary>Render lại output từ kết quả thô, áp dụng Format nếu bật.</summary>
    private void Render(bool format)
    {
        var sb = new StringBuilder();
        var many = _lastResults.Count > 1;
        foreach (var e in _lastResults)
        {
            if (e.Error is not null) { sb.AppendLine($"-- [LỖI] {e.Banner}: {e.Error}").AppendLine(); continue; }
            sb.Append(e.Banner);
            if (e.Warning is not null) sb.AppendLine($"-- [!] {e.Warning}");
            sb.AppendLine(format ? SafeFormat(e.Sql ?? "") : e.Sql ?? "");
            if (many) sb.AppendLine().AppendLine("GO").AppendLine();
        }
        Js($"decryptSql.onOutput({J(sb.ToString())})");
    }

    private string SafeFormat(string sql)
    {
        try { return _formatter.Format(sql); }
        catch { return sql; } // formatter lỗi thì giữ nguyên bản thô
    }

    private async Task RunBusyAsync(string busyText, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Js($"decryptSql.onBusy(true); decryptSql.onStatus({J(busyText)}, '')");
        try { await action(); }
        catch (Exception ex) { Js($"decryptSql.onStatus({J("Lỗi: " + FriendlyError(ex))}, 'err')"); }
        finally { _busy = false; Js("decryptSql.onBusy(false)"); }
    }

    private static string FriendlyError(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("dedicated administrator", StringComparison.OrdinalIgnoreCase) || msg.Contains("DAC", StringComparison.OrdinalIgnoreCase))
            return msg + "  (DAC chỉ cho 1 kết nối/instance — đóng kết nối DAC khác, hoặc bật 'remote admin connections' nếu chạy remote.)";
        if (msg.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
            return msg + "  (DAC yêu cầu quyền sysadmin — dùng user admin, có thể khai báo vào registry bằng nút “Lưu registry”.)";
        return msg;
    }
}
