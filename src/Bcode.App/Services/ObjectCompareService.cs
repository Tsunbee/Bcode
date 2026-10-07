using System.Text;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

public enum ObjectDiffStatus { Same, Different, WhitespaceOnly, OnlyLeft, OnlyRight, Encrypted }

/// <summary>Một procedure / function / view / trigger đọc từ <c>sys.objects + sys.sql_modules</c> (chưa kèm nội dung — chỉ checksum &amp; độ dài để so nhanh).</summary>
public sealed record ModuleInfo(string Schema, string Name, string Type, DateTime Modified, int? Checksum, long Length, bool Encrypted, bool AnsiNulls, bool QuotedIdentifier)
{
    public string Key => (Schema + "." + Name).ToLowerInvariant();
    public string Kind => Type.Trim() switch { "P" => "Procedure", "V" => "View", "TR" => "Trigger", _ => "Function" };
}

public sealed record ObjectDiffItem(string Key, string Schema, string Name, string Kind, ObjectDiffStatus Status, DateTime? LeftModified, DateTime? RightModified);

public sealed record ObjectDiffLine(string K, int? N, string T);

/// <summary>
/// So sánh phần thân procedure / function / view / trigger giữa hai database (thường: bản dev và bản của khách) rồi sinh script ALTER để đưa bên phải
/// về giống bên trái. Chỉ ĐỌC hai database và chỉ SINH script — không bao giờ chạy script lên database nào (người dùng xem, chép, tự chạy).
/// Cách làm nhanh với database hàng nghìn object: (1) mỗi bên một câu truy vấn lấy danh sách + checksum + độ dài (không kéo nội dung); (2) chỉ những object
/// có checksum khác nhau mới tải nội dung, theo từng nhóm; (3) so lại sau khi chuẩn hoá (CRLF, khoảng trắng cuối dòng, CREATE/ALTER) để tách
/// "chỉ khác khoảng trắng" khỏi "khác thật".
/// </summary>
public sealed class ObjectCompareService
{
    private const int FetchChunk = 80;
    private const int MaxDiffLines = 1500;

    private static readonly Regex Header = new(@"\b(?:CREATE\s+OR\s+ALTER|CREATE|ALTER)(\s+(?:PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Kết quả của lần so sánh gần nhất — giữ nội dung các object khác nhau để xem diff / sinh script không phải tải lại.</summary>
    public sealed class Session
    {
        public Workspace Left = null!, Right = null!;
        public bool LeftSys, RightSys;
        public Dictionary<string, ModuleInfo> L = new(), R = new();
        public Dictionary<string, string> LDef = new(), RDef = new();
        public List<ObjectDiffItem> Items = new();
    }

    public static string DbName(Workspace ws, bool sys) => sys ? ws.SysDatabase : ws.EffectiveAppDatabase;

    public async Task<List<ModuleInfo>> ListAsync(Workspace ws, bool sys, CancellationToken ct)
    {
        const string sql = @"
SELECT s.name, o.name, o.type, o.modify_date, CHECKSUM(m.definition), ISNULL(DATALENGTH(m.definition), 0),
       CASE WHEN m.definition IS NULL THEN 1 ELSE 0 END, ISNULL(m.uses_ansi_nulls, 1), ISNULL(m.uses_quoted_identifier, 1)
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.type IN ('P','FN','IF','TF','V','TR') AND o.is_ms_shipped = 0;";
        var list = new List<ModuleInfo>();
        await using var conn = new SqlConnection(ws.BuildConnectionString(sys));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ModuleInfo(r.GetString(0), r.GetString(1), r.GetString(2).Trim(), r.GetDateTime(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.GetInt64(5), r.GetInt32(6) == 1, r.GetBoolean(7), r.GetBoolean(8)));
        return list;
    }

    private static async Task FetchDefinitionsAsync(Workspace ws, bool sys, List<ModuleInfo> modules, Dictionary<string, string> into, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ws.BuildConnectionString(sys));
        await conn.OpenAsync(ct);
        foreach (var chunk in modules.Chunk(FetchChunk))
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder("SELECT s.name, o.name, m.definition FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id JOIN sys.sql_modules m ON m.object_id = o.object_id WHERE ");
            await using var cmd = new SqlCommand { Connection = conn, CommandTimeout = 120 };
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0) sb.Append(" OR ");
                sb.Append($"(s.name = @s{i} AND o.name = @n{i})");
                cmd.Parameters.AddWithValue("@s" + i, chunk[i].Schema);
                cmd.Parameters.AddWithValue("@n" + i, chunk[i].Name);
            }
            cmd.CommandText = sb.ToString();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(2)) into[(r.GetString(0) + "." + r.GetString(1)).ToLowerInvariant()] = r.GetString(2);
        }
    }

    /// <summary>CRLF → LF, bỏ khoảng trắng cuối dòng và dòng trống đầu/cuối, đổi CREATE đầu tiên thành ALTER — để hai bản chỉ khác kiểu viết không bị tính là khác.</summary>
    public static string Normalize(string def)
    {
        var s = def.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Header.Replace(s, "ALTER$1", 1);
        var lines = s.Split('\n').Select(l => l.TrimEnd());
        return string.Join("\n", lines).Trim('\n', ' ', '\t');
    }

    public async Task<Session> CompareAsync(Workspace left, bool leftSys, Workspace right, bool rightSys, Action<string> progress, CancellationToken ct)
    {
        var ses = new Session { Left = left, Right = right, LeftSys = leftSys, RightSys = rightSys };
        progress("Đang đọc danh sách object hai bên…");
        var lt = ListAsync(left, leftSys, ct);
        var rt = ListAsync(right, rightSys, ct);
        await Task.WhenAll(lt, rt);
        foreach (var m in lt.Result) ses.L[m.Key] = m;
        foreach (var m in rt.Result) ses.R[m.Key] = m;

        var items = new List<ObjectDiffItem>();
        var candidates = new List<string>();
        foreach (var key in ses.L.Keys.Union(ses.R.Keys))
        {
            ses.L.TryGetValue(key, out var l);
            ses.R.TryGetValue(key, out var r);
            var any = l ?? r!;
            ObjectDiffStatus st;
            if (l is null) st = ObjectDiffStatus.OnlyRight;
            else if (r is null) st = ObjectDiffStatus.OnlyLeft;
            else if (l.Encrypted || r.Encrypted) st = ObjectDiffStatus.Encrypted;
            else if (l.Checksum == r.Checksum && l.Length == r.Length) st = ObjectDiffStatus.Same;
            else { st = ObjectDiffStatus.Different; candidates.Add(key); }
            items.Add(new ObjectDiffItem(key, any.Schema, any.Name, any.Kind, st, l?.Modified, r?.Modified));
        }

        if (candidates.Count > 0)
        {
            progress($"{candidates.Count} object có checksum khác nhau — đang tải nội dung để so chi tiết…");
            var lf = FetchDefinitionsAsync(left, leftSys, candidates.Select(k => ses.L[k]).ToList(), ses.LDef, ct);
            var rf = FetchDefinitionsAsync(right, rightSys, candidates.Select(k => ses.R[k]).ToList(), ses.RDef, ct);
            await Task.WhenAll(lf, rf);
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Status != ObjectDiffStatus.Different) continue;
                var k = items[i].Key;
                if (ses.LDef.TryGetValue(k, out var ld) && ses.RDef.TryGetValue(k, out var rd) && Normalize(ld) == Normalize(rd))
                    items[i] = items[i] with { Status = ObjectDiffStatus.WhitespaceOnly };
            }
        }

        ses.Items = items.OrderBy(i => i.Status).ThenBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        progress("Xong.");
        return ses;
    }

    /// <summary>Tải nội dung một object chỉ có ở một bên (cần để sinh script / xem) — object khác nhau đã được tải sẵn khi so sánh.</summary>
    public async Task<string?> GetDefinitionAsync(Session ses, bool leftSide, string key, CancellationToken ct)
    {
        var defs = leftSide ? ses.LDef : ses.RDef;
        if (defs.TryGetValue(key, out var have)) return have;
        var map = leftSide ? ses.L : ses.R;
        if (!map.TryGetValue(key, out var m) || m.Encrypted) return null;
        await FetchDefinitionsAsync(leftSide ? ses.Left : ses.Right, leftSide ? ses.LeftSys : ses.RightSys, new List<ModuleInfo> { m }, defs, ct);
        return defs.GetValueOrDefault(key);
    }

    /// <summary>Diff dạng gọn (bên phải → bên trái, tức là những gì cần làm để bên phải giống bên trái) với 3 dòng ngữ cảnh.</summary>
    public List<ObjectDiffLine> BuildDiff(string leftDef, string rightDef)
    {
        var diff = new DiffService().Diff(Normalize(rightDef), Normalize(leftDef));      // trái của DiffService = bản đang có ở khách; phải = bản dev sẽ đưa vào
        var keep = new bool[diff.Count];
        for (var i = 0; i < diff.Count; i++)
            if (diff[i].Kind != DiffKind.Equal)
                for (var k = Math.Max(0, i - 3); k <= Math.Min(diff.Count - 1, i + 3); k++) keep[k] = true;
        var lines = new List<ObjectDiffLine>();
        var skipped = false;
        for (var i = 0; i < diff.Count && lines.Count < MaxDiffLines; i++)
        {
            if (!keep[i]) { if (!skipped && lines.Count > 0) lines.Add(new("…", null, "")); skipped = true; continue; }
            skipped = false;
            var d = diff[i];
            lines.Add(new(d.Kind == DiffKind.Added ? "+" : d.Kind == DiffKind.Removed ? "-" : " ", d.Kind == DiffKind.Added ? d.RightLineNo : d.LeftLineNo, d.Text));
        }
        return lines;
    }

    /// <summary>
    /// Script đưa bên phải về giống bên trái: object khác nhau → ALTER bằng nội dung bên trái; chỉ có bên trái → CREATE; chỉ có bên phải → không động tới
    /// (muốn xoá thì tự thêm DROP). Mỗi object kèm SET ANSI_NULLS / QUOTED_IDENTIFIER đúng như bản gốc rồi GO. Chỉ sinh chữ, không chạy.
    /// </summary>
    public async Task<string> BuildScriptAsync(Session ses, IEnumerable<string> keys, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- Script đưa [" + ses.Right.Name + "] " + DbName(ses.Right, ses.RightSys) + " về giống [" + ses.Left.Name + "] " + DbName(ses.Left, ses.LeftSys));
        sb.AppendLine("-- Sinh bởi Bcode lúc " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + ". XEM KỸ rồi mới chạy — Bcode không tự chạy script này.");
        sb.AppendLine("-- Server đích: " + ses.Right.Server + "   Database đích: " + DbName(ses.Right, ses.RightSys));
        sb.AppendLine();
        var n = 0;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            if (!ses.L.TryGetValue(key, out var lm)) continue;                     // chỉ có bên phải: bỏ qua
            var def = await GetDefinitionAsync(ses, true, key, ct);
            if (def is null) { sb.AppendLine($"-- Bỏ qua {lm.Schema}.{lm.Name}: object mã hoá (WITH ENCRYPTION) hoặc không đọc được nội dung."); sb.AppendLine(); continue; }
            var exists = ses.R.ContainsKey(key);
            def = def.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ', '\t');
            def = exists ? Header.Replace(def, "ALTER$1", 1) : Header.Replace(def, "CREATE$1", 1);
            sb.AppendLine($"-- {(exists ? "ALTER" : "CREATE")} {lm.Kind} {lm.Schema}.{lm.Name}");
            sb.AppendLine("SET ANSI_NULLS " + (lm.AnsiNulls ? "ON" : "OFF"));
            sb.AppendLine("SET QUOTED_IDENTIFIER " + (lm.QuotedIdentifier ? "ON" : "OFF"));
            sb.AppendLine("GO");
            sb.AppendLine(def.Replace("\n", "\r\n"));
            sb.AppendLine("GO");
            sb.AppendLine();
            n++;
        }
        if (n == 0) sb.AppendLine("-- (Không có object nào để sinh script — chỉ chọn được các object khác nhau hoặc chỉ có ở bên trái.)");
        return sb.ToString();
    }
}
