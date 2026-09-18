using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Backs the "Change Owner" tool. Modern SQL Server doesn't have a separate
/// per-object "owner" outside of schema (that's a SQL Server 2000-era
/// concept) — the schema IS the owner boundary now, so this runs
/// ALTER SCHEMA ... TRANSFER ..., which is the direct equivalent of the old
/// sp_changeobjectowner for moving an object to a different schema/owner.
/// </summary>
public class ChangeOwnerService
{
    private readonly DbConnectionService _connections;

    public ChangeOwnerService(DbConnectionService connections)
    {
        _connections = connections;
    }

    public async Task ChangeOwnerAsync(bool useSysDatabase, string currentSchema, string objectName, string newSchema)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();

        // Target schema must already exist — create it if missing (matches what
        // sp_changeobjectowner used to do implicitly for a "new owner" that was really a login/user).
        await using (var ensure = new SqlCommand(
            "IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schema) EXEC('CREATE SCHEMA [' + @schema + ']');", conn))
        {
            ensure.Parameters.AddWithValue("@schema", newSchema);
            await ensure.ExecuteNonQueryAsync();
        }

        var sql = $"ALTER SCHEMA [{newSchema}] TRANSFER [{currentSchema}].[{objectName}];";
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
