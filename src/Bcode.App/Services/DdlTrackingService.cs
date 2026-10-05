using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

public sealed record DdlTrackingStatus(bool TableExists, bool TriggerExists, bool TriggerEnabled, long Rows, string? Error);

/// <summary>Một sự kiện CREATE/ALTER/DROP đã được DDL trigger ghi lại.</summary>
public sealed record DdlLogEntry(long Id, DateTime At, string EventType, string Schema, string Name, string ObjectType,
    string Login, string Host, string App, string Ip, int Length);

/// <summary>Một object có sự kiện trong bảng log (kể cả object chưa từng sửa qua Bcode).</summary>
public sealed record DdlLogObject(string Schema, string Name, string ObjectType, int Count, DateTime Last);

/// <summary>
/// Theo dõi thay đổi procedure / function / view / trigger làm NGOÀI Bcode (SSMS, tool deploy, script...) bằng một DDL trigger mức database.
/// Người dùng bấm "Cài" cho từng dự án (màn hình Lịch sử) thì Bcode tạo trong database đó:
///   • bảng <c>dbo.bcode_ddl_history</c> (thời gian, loại sự kiện, object, login, tên máy, ứng dụng, IP, câu lệnh đầy đủ);
///   • DDL trigger <c>bcode_trg_ddl_history</c> ghi mỗi CREATE/ALTER/DROP PROCEDURE|FUNCTION|VIEW|TRIGGER vào bảng đó.
/// Trigger bọc TRY/CATCH nên lỗi ghi log KHÔNG BAO GIỜ làm hỏng câu DDL của người khác. Tương thích SQL Server 2008 trở lên
/// (không dùng CREATE OR ALTER). Gỡ = bỏ trigger (giữ bảng để không mất lịch sử đã ghi).
/// Bcode tự bỏ qua các dòng do chính Bcode sửa (ApplicationName = "Bcode") vì đã có bản lưu ở thư mục History.
/// </summary>
public static class DdlTrackingService
{
    public const string TableName = "bcode_ddl_history";
    public const string TriggerName = "bcode_trg_ddl_history";

    private static readonly string[] InstallBatches =
    {
        $@"IF OBJECT_ID(N'dbo.{TableName}', N'U') IS NULL
CREATE TABLE dbo.{TableName} (
    id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
    event_time datetime2(3) NOT NULL CONSTRAINT DF_{TableName}_time DEFAULT SYSDATETIME(),
    event_type nvarchar(64) NOT NULL,
    schema_name nvarchar(128) NULL,
    object_name nvarchar(128) NOT NULL,
    object_type nvarchar(64) NULL,
    login_name nvarchar(256) NULL,
    host_name nvarchar(256) NULL,
    app_name nvarchar(256) NULL,
    client_ip nvarchar(64) NULL,
    command_text nvarchar(max) NULL
)",
        $@"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_{TableName}_obj' AND object_id = OBJECT_ID(N'dbo.{TableName}'))
CREATE INDEX IX_{TableName}_obj ON dbo.{TableName} (object_name, schema_name)",
        // người sửa không phải chủ database vẫn ghi được dòng log của mình (trigger chạy theo quyền người sửa)
        $"GRANT INSERT ON dbo.{TableName} TO PUBLIC",
        $@"IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'{TriggerName}' AND parent_class = 0)
DROP TRIGGER [{TriggerName}] ON DATABASE",
        // CREATE TRIGGER phải đứng riêng trong 1 batch
        $@"CREATE TRIGGER [{TriggerName}] ON DATABASE
FOR CREATE_PROCEDURE, ALTER_PROCEDURE, DROP_PROCEDURE,
    CREATE_FUNCTION, ALTER_FUNCTION, DROP_FUNCTION,
    CREATE_VIEW, ALTER_VIEW, DROP_VIEW,
    CREATE_TRIGGER, ALTER_TRIGGER, DROP_TRIGGER
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        DECLARE @e xml = EVENTDATA();
        DECLARE @obj nvarchar(128) = @e.value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(128)');
        IF @obj LIKE N'bcode[_]%' RETURN;
        INSERT INTO dbo.{TableName} (event_type, schema_name, object_name, object_type, login_name, host_name, app_name, client_ip, command_text)
        VALUES (
            @e.value('(/EVENT_INSTANCE/EventType)[1]', 'nvarchar(64)'),
            @e.value('(/EVENT_INSTANCE/SchemaName)[1]', 'nvarchar(128)'),
            @obj,
            @e.value('(/EVENT_INSTANCE/ObjectType)[1]', 'nvarchar(64)'),
            ORIGINAL_LOGIN(), HOST_NAME(), APP_NAME(),
            CONVERT(nvarchar(64), CONNECTIONPROPERTY('client_net_address')),
            @e.value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'nvarchar(max)'));
    END TRY
    BEGIN CATCH
        -- ghi log lỗi thì bỏ qua: không bao giờ chặn câu DDL của người dùng
    END CATCH
END",
    };

    private static async Task<SqlConnection> OpenAsync(DbConnectionService c, bool useSys)
    {
        var conn = c.CreateConnection(useSys);
        await conn.OpenAsync();
        return conn;
    }

    // ------------------------------------------------------------------ trạng thái / cài / gỡ

    public static async Task<DdlTrackingStatus> GetStatusAsync(DbConnectionService c, bool useSys)
    {
        try
        {
            await using var conn = await OpenAsync(c, useSys);
            await using var cmd = new SqlCommand($@"
SELECT
  CASE WHEN OBJECT_ID(N'dbo.{TableName}', N'U') IS NULL THEN 0 ELSE 1 END,
  CASE WHEN EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'{TriggerName}' AND parent_class = 0) THEN 1 ELSE 0 END,
  CASE WHEN EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'{TriggerName}' AND parent_class = 0 AND is_disabled = 0) THEN 1 ELSE 0 END", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            bool table = r.GetInt32(0) == 1, trig = r.GetInt32(1) == 1, enabled = r.GetInt32(2) == 1;
            await r.CloseAsync();

            long rows = 0;
            if (table)
            {
                await using var cnt = new SqlCommand($"SELECT COUNT_BIG(*) FROM dbo.{TableName}", conn);
                rows = Convert.ToInt64(await cnt.ExecuteScalarAsync());
            }
            return new DdlTrackingStatus(table, trig, enabled, rows, null);
        }
        catch (Exception ex) { return new DdlTrackingStatus(false, false, false, 0, ex.Message); }
    }

    public static async Task InstallAsync(DbConnectionService c, bool useSys)
    {
        await using var conn = await OpenAsync(c, useSys);
        foreach (var sql in InstallBatches)
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Gỡ trigger (giữ bảng log để không mất lịch sử đã ghi).</summary>
    public static async Task RemoveAsync(DbConnectionService c, bool useSys)
    {
        await using var conn = await OpenAsync(c, useSys);
        await using var cmd = new SqlCommand($@"IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'{TriggerName}' AND parent_class = 0)
DROP TRIGGER [{TriggerName}] ON DATABASE", conn) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------ đọc log

    private const string NotFromBcode = "(app_name IS NULL OR app_name NOT LIKE N'Bcode%')";

    /// <summary>Các object có sự kiện do công cụ khác ghi lại (bỏ qua sự kiện của chính Bcode). Danh sách rỗng nếu chưa cài.</summary>
    public static async Task<List<DdlLogObject>> ListObjectsAsync(DbConnectionService c, bool useSys)
    {
        var list = new List<DdlLogObject>();
        try
        {
            await using var conn = await OpenAsync(c, useSys);
            await using var cmd = new SqlCommand($@"
IF OBJECT_ID(N'dbo.{TableName}', N'U') IS NULL SELECT TOP 0 CAST(NULL AS nvarchar(128)), CAST(NULL AS nvarchar(128)), CAST(NULL AS nvarchar(64)), 0, CAST(NULL AS datetime2)
ELSE
SELECT ISNULL(schema_name, N'dbo'), object_name, MAX(object_type), COUNT(*), MAX(event_time)
FROM dbo.{TableName} WHERE {NotFromBcode} GROUP BY ISNULL(schema_name, N'dbo'), object_name", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new DdlLogObject(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetInt32(3), r.GetDateTime(4)));
        }
        catch { /* chưa cài / không có quyền đọc — bỏ qua */ }
        return list;
    }

    /// <summary>Các sự kiện của một object do công cụ khác ghi lại, mới nhất trước.</summary>
    public static async Task<List<DdlLogEntry>> ListAsync(DbConnectionService c, bool useSys, string schema, string name)
    {
        var list = new List<DdlLogEntry>();
        try
        {
            await using var conn = await OpenAsync(c, useSys);
            await using var cmd = new SqlCommand($@"
IF OBJECT_ID(N'dbo.{TableName}', N'U') IS NOT NULL
SELECT id, event_time, event_type, ISNULL(schema_name, N'dbo'), object_name, object_type, login_name, host_name, app_name, client_ip, ISNULL(LEN(command_text), 0)
FROM dbo.{TableName}
WHERE object_name = @n AND ISNULL(schema_name, N'dbo') = @s AND {NotFromBcode}
ORDER BY id DESC", conn);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@s", schema);
            await using var r = await cmd.ExecuteReaderAsync();
            string S(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            while (await r.ReadAsync())
                list.Add(new DdlLogEntry(r.GetInt64(0), r.GetDateTime(1), S(2), S(3), S(4), S(5), S(6), S(7), S(8), S(9), Convert.ToInt32(r.GetValue(10))));
        }
        catch { }
        return list;
    }

    public static async Task<string?> ReadCommandAsync(DbConnectionService c, bool useSys, long id)
    {
        await using var conn = await OpenAsync(c, useSys);
        await using var cmd = new SqlCommand($"SELECT command_text FROM dbo.{TableName} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteScalarAsync() as string;
    }
}
