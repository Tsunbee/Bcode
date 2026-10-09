using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Backs the "SQL Query" tool: a free-form SQL script runner (SSMS-style — any
/// number of statements, not just one SELECT), as opposed to "Command"
/// (the structured SELECT/FROM/WHERE/ORDER BY builder, SqlQueryService —
/// naming note: an earlier push had "SQL Query" and "Command" swapped; see
/// MainForm.OpenSelectBuilderTab/OpenFreeScriptTab for the corrected mapping).
///
/// Splits on "GO" batch separators (ADO.NET/SqlCommand has no native concept
/// of GO — that's a client-side batch separator SSMS/sqlcmd handle, so we do
/// the same: split the script on lines that are just "GO" and run each batch
/// as its own SqlCommand). Each batch's EVERY result set is captured (see
/// BatchResult.Tables and RunBatchesAsync) — a single EXEC of a stored
/// procedure with several SELECTs inside it, or several bare SELECTs in one
/// batch, all come back as separate tables, not just the first one.
/// </summary>
public class RawSqlService
{
    private readonly DbConnectionService _connections;

    public RawSqlService(DbConnectionService connections)
    {
        _connections = connections;
    }

    public DbConnectionService Connections => _connections;

    /// <summary>Định nghĩa hiện tại của procedure/function/view/trigger trong database (null nếu không có hoặc bị mã hoá).</summary>
    public async Task<string?> GetObjectDefinitionAsync(string qualifiedName, bool useSysDatabase)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(@n));", conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@n", qualifiedName);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static readonly Regex GoSeparator = new(@"^[ \t]*GO[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// .NET's ^/$ trong RegexOptions.Multiline CHỈ coi "\n" là ranh giới dòng — khác với
    /// JavaScript/ECMAScript (nơi \r đứng một mình cũng được coi là xuống dòng). Nếu script
    /// dán/mở vào có dòng chỉ ngăn cách bằng "\r" trơ (kiểu file text cũ — hay gặp khi copy
    /// nguyên văn 1 đoạn SQL từ 1 ô nhập liệu/report cũ của FastBusiness), GoSeparator không
    /// nhận ra ranh giới dòng nào cả, Split() trả nguyên cả script làm 1 "batch" duy nhất —
    /// chữ "GO" lọt vào giữa batch đó như text thường, SQL Server báo "Incorrect syntax near
    /// 'GO'" (và thường kéo theo 1 lỗi ăn theo ở chỗ khác nữa vì parser bị lạc sau đó). Chuẩn
    /// hoá mọi kiểu xuống dòng về "\n" trước khi tách là cách chắc ăn nhất, không phụ thuộc
    /// nguồn gốc file/kiểu xuống dòng gốc.
    /// </summary>
    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>Tách script theo GO, kèm DÒNG bắt đầu (1-based, trong toàn script) của từng batch — SQL Server báo số dòng TƯƠNG ĐỐI trong batch nên phải cộng thêm dòng bắt đầu mới ra đúng dòng trong editor.</summary>
    private static List<(string Text, int StartLine)> SplitBatches(string script)
    {
        var norm = NormalizeLineEndings(script);
        var res = new List<(string, int)>();
        var pos = 0; var lineAtPos = 1; var counted = 0;
        void Add(int from, int to)
        {
            var seg = norm.Substring(from, to - from);
            var text = seg.Trim(); if (text.Length == 0) return;
            var first = from + (seg.Length - seg.TrimStart().Length);
            for (; counted < first; counted++) if (norm[counted] == '\n') lineAtPos++;
            res.Add((text, lineAtPos));
        }
        foreach (Match m in GoSeparator.Matches(norm)) { Add(pos, m.Index); pos = m.Index + m.Length; }
        Add(pos, norm.Length);
        return res;
    }

    /// <summary>"Lỗi ở dòng 9 của script (câu lệnh 3/5 giữa các GO, dòng 2 trong câu lệnh): …" + trích đúng dòng gây lỗi. Lỗi bên trong procedure / trigger thì nói rõ tên và dòng trong chính nó.</summary>
    private static string DescribeError(SqlError err, string batch, int startLine, int index, int total, List<(string Text, int StartLine)> allBatches)
    {
        if (!string.IsNullOrEmpty(err.Procedure))
        {
            // Procedure/trigger được tạo-sửa NGAY TRONG script này: đổi số dòng của procedure thành số dòng của script, để bấm vào lỗi (tab Message)
            // nhảy tới đúng chỗ trong editor — "Lỗi ở dòng N" là mẫu trang Message nhận ra để cho bấm.
            if (TryMapProcedureLine(allBatches, err.Procedure, err.LineNumber, out var scriptLine, out var scriptText))
                return $"Lỗi ở dòng {scriptLine} của script (trong {err.Procedure}, dòng {err.LineNumber} của chính procedure / trigger đó): {err.Message}"
                       + (scriptText.Length > 0 ? Environment.NewLine + "    → " + scriptText : "");
            return $"Lỗi trong {err.Procedure} (dòng {err.LineNumber} của chính procedure / trigger đó): {err.Message}";
        }
        var lines = batch.Split('\n');
        var rel = Math.Max(1, err.LineNumber);
        var abs = startLine + rel - 1;
        var snippet = rel <= lines.Length ? lines[rel - 1].Trim() : "";
        if (snippet.Length > 170) snippet = snippet[..170] + "…";
        var where = total > 1 ? $"dòng {abs} của script (câu lệnh {index}/{total} giữa các GO, dòng {rel} trong câu lệnh)" : $"dòng {abs}";
        return $"Lỗi ở {where}: {err.Message}" + (snippet.Length > 0 ? Environment.NewLine + "    → " + snippet : "") + DynamicSqlHint(batch);
    }

    /// <summary>Tìm câu CREATE/ALTER của <paramref name="procName"/> trong các batch của script; số dòng lỗi (tính từ dòng CREATE/ALTER = dòng 1) → số dòng trong script.
    /// Không tìm thấy (procedure không nằm trong script) hoặc dòng nằm ngoài batch thì trả false.</summary>
    private static bool TryMapProcedureLine(List<(string Text, int StartLine)> batches, string procName, int procLine, out int scriptLine, out string scriptText)
    {
        scriptLine = 0; scriptText = "";
        if (procLine < 1) return false;
        var header = new Regex(@"(?:^|\n)[ \t]*(?:CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(?:PROCEDURE|PROC|FUNCTION|TRIGGER|VIEW)\s+(?:\[?[\w$#@]+\]?\s*\.\s*)?\[?" + Regex.Escape(procName) + @"\]?(?![\w$#@])",
            RegexOptions.IgnoreCase);
        foreach (var (text, startLine) in batches)
        {
            var m = header.Match(text);
            if (!m.Success) continue;
            var kwIndex = m.Index + (text[m.Index] == '\n' ? 1 : 0);
            var headerIdx = 0;                                   // số dòng (0-based) trong batch của dòng chứa CREATE/ALTER
            for (var i = 0; i < kwIndex; i++) if (text[i] == '\n') headerIdx++;
            var lines = text.Split('\n');
            var idx = headerIdx + procLine - 1;
            if (idx >= lines.Length) return false;
            scriptLine = startLine + idx;
            scriptText = lines[idx].Trim();
            if (scriptText.Length > 170) scriptText = scriptText[..170] + "…";
            return true;
        }
        return false;
    }

    private static readonly Regex ExecProcRegex = new(@"\bEXEC(?:UTE)?\s+(?:\[?\w+\]?\s*\.\s*)?\[?([A-Za-z_][\w$#]*)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Lỗi không kèm tên procedure mà batch lại EXEC một procedure: nhiều khả năng lỗi nằm trong SQL động (sp_executesql / EXEC(@q)) bên trong procedure đó.
    /// SQL Server tính "dòng" theo chuỗi SQL động (thường là dòng 1) nên không trỏ về được dòng nguồn của procedure — nói rõ để khỏi hiểu nhầm dòng đã báo.</summary>
    private static string DynamicSqlHint(string batch)
    {
        var names = ExecProcRegex.Matches(batch).Select(m => m.Groups[1].Value)
            .Where(n => !n.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase) && !n.StartsWith('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        if (names.Count == 0) return "";
        return Environment.NewLine + $"    ⚠ Batch này gọi procedure ({string.Join(", ", names)}). Nếu dòng trên không chứa chỗ lỗi thì đây thường là lỗi trong SQL động " +
               "(sp_executesql / EXEC(@q)) bên trong procedure đó: SQL Server tính \"dòng\" theo chuỗi SQL động nên không trỏ về dòng của procedure. " +
               "Kiểm tra chỗ ghép chuỗi (dấu phẩy, khoảng trắng trong chuỗi '...') hoặc PRINT @q trước khi EXEC để xem câu lệnh thực tế.";
    }

    // "$000000" FROM/JOIN placeholders used to get expanded into a UNION ALL over every real
    // period table here too (the same "$000000 = mọi kỳ" convenience SQL Query's own builder
    // has) — removed per Bee: "ở sql query thì ko cần xử lý select bảng $000000 ... vì làm v
    // sẽ lỗi khi đọc procedure". The expansion regex scanned the WHOLE batch text, including
    // inside string literals — so loading a real FastBusiness procedure body (e.g. via "Debug
    // store/function") that builds its OWN dynamic SQL string containing literal "...$000000"
    // text (handled at runtime by FastBusiness$Partition$Execute, not by Bcode) made this
    // service try to expand that placeholder too, fail to find any matching physical tables,
    // and throw "Không tìm thấy bảng ký nào khớp mẫu ...$000000" — even though nothing was
    // actually wrong with the script. "SQL Query" (this service) now always sends the script
    // through exactly as typed/loaded. "Command" (SqlQueryService) and "Table"
    // (TableDataService) keep doing their own "$000000" expansion as before — Bee only asked
    // to remove it here, in "SQL Query" — since those two only ever build a real FROM clause
    // themselves and never have arbitrary dynamic-SQL string literals to misread the way a
    // free-form script (a whole procedure body, say) can.

    /// <summary>Tables holds EVERY result set the batch produced, in order — a batch can be a
    /// single EXEC of a stored procedure that itself contains several SELECTs (or several bare
    /// SELECTs typed directly, not separated by GO), and SQL Server returns each of those as
    /// its own result set on the same reader. Empty when the batch was pure DML/DDL with no
    /// SELECT at all.
    ///
    /// <para>Messages holds whatever this batch sent through PRINT/RAISERROR(không có mức độ
    /// nghiêm trọng cao)/session-level thông báo (vd "5 row(s) affected" không tính, đó là
    /// RecordsAffected riêng) — ĐÃ SỬA: trước đây không có gì đọc <see cref="SqlConnection.InfoMessage"/>
    /// nên PRINT @q Bee gõ để soi câu SQL động ngay trước dòng gây lỗi hoàn toàn bị bỏ qua,
    /// không có cách nào xem lại nó khi batch báo lỗi. Null khi batch không PRINT gì.</para></summary>
    /// <summary><c>ElapsedMs</c>: từ lúc gửi batch tới lúc đọc xong kết quả (máy chủ chạy + tải về). Mỗi bảng kết quả còn có thời gian riêng trong <c>DataTable.ExtendedProperties["ms"]</c> (long).</summary>
    public record BatchResult(string Batch, List<DataTable> Tables, int RowsAffected, string? Error, string? Messages = null, long ElapsedMs = 0);

    /// <summary>
    /// Runs the script on a brand-new connection that's closed again right after — the
    /// "Reset Connection" behavior: any #temp tables the script created die with the
    /// connection, so re-running the same "CREATE TABLE #tmp ..." script never collides
    /// with a leftover #temp table from the previous run (no explicit DROP needed).
    /// </summary>
    public Task<List<BatchResult>> ExecuteScriptAsync(string script, bool useSysDatabase = false) =>
        // Toàn bộ việc gọi SQL (mở kết nối, gửi lệnh, chờ, đọc kết quả) chạy ở luồng nền — như mỗi cửa sổ query trong SSMS: tab đang chạy lâu
        // không được chặn luồng giao diện, để vẫn chuyển tab / thao tác mục khác. (Chạy thẳng trên luồng giao diện từng làm Bcode đứng hàng chục giây
        // khi chạy procedure nặng, đo được 15,7 giây.)
        Task.Run(() => ExecuteScriptOnConnectionAsync(script, () => _connections.CreateConnection(useSysDatabase), ownsConnection: true));

    /// <summary>
    /// Runs the script on an existing, already-open connection instead ("Reset Connection"
    /// unchecked) — #temp tables and any SET/session state persist across calls, matching
    /// what a real SSMS query window does when you keep the same connection open.
    /// </summary>
    public Task<List<BatchResult>> ExecuteScriptOnAsync(string script, SqlConnection conn) =>
        Task.Run(() => ExecuteScriptOnConnectionAsync(script, () => conn, ownsConnection: false));   // luồng nền, lý do như ExecuteScriptAsync

    /// <summary>Opens a new, caller-owned connection (used to hold a persistent connection across
    /// several ExecuteScriptOnAsync calls when "Reset Connection" is unchecked in RawSqlControl —
    /// the caller is responsible for opening/disposing it).</summary>
    public SqlConnection CreateConnection(bool useSysDatabase = false) => _connections.CreateConnection(useSysDatabase);

    public string CurrentStamp(bool useSysDatabase) => _connections.CurrentStamp(useSysDatabase);

    private async Task<List<BatchResult>> ExecuteScriptOnConnectionAsync(string script, Func<SqlConnection> connFactory, bool ownsConnection)
    {
        var batches = SplitBatches(script);   // đã ở luồng nền (xem ExecuteScriptAsync)

        var results = new List<BatchResult>();
        if (batches.Count == 0) return results;

        var conn = connFactory();
        try
        {
            if (conn.State != ConnectionState.Open) await conn.OpenAsync();
            return await RunBatchesAsync(batches, conn, results);
        }
        finally
        {
            if (ownsConnection) await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// Compile-only validation (SET NOEXEC ON) — SQL Server parses and binds
    /// (resolves table/column names) each batch without running it, so a typo'd
    /// column or table surfaces the same error it would on Execute, without
    /// actually running INSERT/UPDATE/DELETE statements. Backs "Check Fields".
    /// </summary>
    public async Task<string?> CheckFieldsAsync(string script, bool useSysDatabase = false)
    {
        var batches = GoSeparator.Split(NormalizeLineEndings(script)).Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        if (batches.Count == 0) return null;

        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();

        await using (var on = new SqlCommand("SET NOEXEC ON;", conn)) await on.ExecuteNonQueryAsync();
        try
        {
            foreach (var batch in batches)
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 30 };
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            // NOEXEC must always be turned back off on this connection/session, even on error.
            await using var off = new SqlCommand("SET NOEXEC OFF;", conn);
            await off.ExecuteNonQueryAsync();
        }
        return null; // no error => every batch compiled/bound cleanly
    }

    /// <summary>
    /// PRINT/RAISERROR (mức thấp) trong 1 batch không đi qua reader — SQL Server gửi chúng như
    /// các "info message" riêng trong luồng TDS, ADO.NET chỉ đưa ra được qua sự kiện
    /// <see cref="SqlConnection.InfoMessage"/>. Gắn 1 lần cho cả script, gom theo TỪNG batch
    /// (Clear() trước mỗi batch) — quan trọng là các PRINT xảy ra TRƯỚC 1 câu lỗi trong CÙNG
    /// batch vẫn được server gửi trước khi lỗi xảy ra, nên vẫn có mặt trong "pending" kịp lúc
    /// bắt exception ở dưới, đúng thứ tự SQL Server thực thi.
    /// </summary>
    private static async Task<List<BatchResult>> RunBatchesAsync(List<(string Text, int StartLine)> batches, SqlConnection conn, List<BatchResult> results)
    {
        var pending = new List<string>();
        void OnInfoMessage(object? _, SqlInfoMessageEventArgs e) => pending.Add(e.Message);
        conn.InfoMessage += OnInfoMessage;

        try
        {
          var batchNo = 0;
          foreach (var (batch, startLine) in batches)
          {
            batchNo++;
            pending.Clear();
            var batchWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
                await using var reader = await cmd.ExecuteReaderAsync();

                // A single batch can produce more than one result set — most commonly an EXEC
                // of a stored procedure that itself runs several SELECTs (see the "P.xxx"
                // screenshot: 3 result sets from one EXEC), but also just several bare SELECTs
                // typed one after another without GO between them.
                //
                // Two earlier attempts here both turned out wrong — confirmed with a small
                // standalone repro (no SQL Server needed: a hand-rolled multi-result-set
                // IDataReader run through a throwaway console app), since neither failure mode
                // is easy to catch just from reading the code:
                //   1. "DataTable.Load(reader) then reader.NextResultAsync()" — Load() itself
                //      CLOSES the reader once it's read that one result set, so the very next
                //      NextResultAsync() call throws "Invalid attempt to call NextResultAsync
                //      when reader is closed" the moment a batch has more than one result set.
                //   2. "DataSet.Load(reader, loadOption, "Table")" — the commonly-cited fix for
                //      exactly this (given one base name it's *documented* to auto-walk every
                //      result set, naming extras Table1/Table2/...). Repro said otherwise: 4
                //      result sets in, only 1 table out — it silently reads just the first one
                //      and stops, same end result as the original bug, just without an error.
                // What actually works (repro'd: 4 result sets in, incl. a zero-column "rows
                // affected" one, 3 real tables out in order, correct columns/rows each): build
                // each DataTable by hand from the reader's own schema (GetName/GetFieldType)
                // and rows (Read/GetValues) — never call Load() on it at all — then advance
                // with NextResultAsync() ourselves.
                var tables = new List<DataTable>();
                var segStartMs = 0L;   // mốc bắt đầu của bảng kết quả hiện tại (đầu batch, hoặc lúc bảng trước đọc xong)
                do
                {
                    if (reader.FieldCount > 0)
                    {
                        var table = new DataTable();
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            var name = string.IsNullOrEmpty(reader.GetName(i)) ? $"Column{i}" : reader.GetName(i);
                            var unique = name;
                            for (var n = 1; table.Columns.Contains(unique); n++) unique = $"{name}{n}"; // SQL allows duplicate column names; DataTable doesn't
                            table.Columns.Add(unique, reader.GetFieldType(i));
                        }

                        // Task.Run moves the CPU-bound row-by-row read off the UI thread — same
                        // reasoning as the table.Load(reader) call this replaces: a big/
                        // unbounded result (this runner has no row cap, unlike SQL Query/Table)
                        // would otherwise freeze the whole window until it finished loading.
                        await Task.Run(() =>
                        {
                            var values = new object[reader.FieldCount];
                            while (reader.Read())
                            {
                                reader.GetValues(values);
                                table.Rows.Add(values);   // DataRowCollection.Add(object[]) tự chép giá trị vào bảng nên dùng lại mảng `values` được (không cần Clone mỗi dòng)
                            }
                        });
                        // Thời gian của riêng SELECT này: máy chủ tạo ra kết quả + tải về, tính từ khi bảng trước đọc xong (hoặc từ lúc gửi batch).
                        table.ExtendedProperties["ms"] = batchWatch.ElapsedMilliseconds - segStartMs;
                        segStartMs = batchWatch.ElapsedMilliseconds;
                        tables.Add(table);
                    }
                } while (await reader.NextResultAsync());

                // RecordsAffected is cumulative across every statement in the batch and is only
                // reliable once the reader has been fully drained (the loop above just did
                // that) — -1 means "not applicable" (e.g. a batch that was pure SELECT(s)).
                var rowsAffected = Math.Max(0, reader.RecordsAffected);
                results.Add(new BatchResult(batch, tables, rowsAffected, null, JoinMessages(pending), batchWatch.ElapsedMilliseconds));
            }
                catch (SqlException ex)
                {
                    var errorLines = new List<string>();
                    foreach (SqlError err in ex.Errors)
                    {
                        errorLines.Add(DescribeError(err, batch, startLine, batchNo, batches.Count, batches));
                    }
                    var fullErrorText = string.Join(Environment.NewLine, errorLines);
                    
                    // Gói lỗi đã kèm số dòng vào BatchResult, giữ nguyên cơ chế lấy PRINT (pending)
                    results.Add(new BatchResult(batch, new List<DataTable>(), 0, fullErrorText, JoinMessages(pending), batchWatch.ElapsedMilliseconds));
                    break; // stop at the first failing batch, same as SSMS default behavior
                }
                catch (Exception ex)
                {
                    // Bắt các lỗi hệ thống không thuộc SQL Server
                    results.Add(new BatchResult(batch, new List<DataTable>(), 0, ex.Message, JoinMessages(pending), batchWatch.ElapsedMilliseconds));
                    break; 
                }
          }
        }
        finally
        {
            conn.InfoMessage -= OnInfoMessage;
        }

        return results;
    }

    private static string? JoinMessages(List<string> pending) =>
        pending.Count > 0 ? string.Join("\r\n", pending) : null;
}