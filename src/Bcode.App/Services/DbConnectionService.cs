using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Owns the "current" workspace/connection, mirroring the WS combobox at the
/// top of FCode's main window. Every other service asks this for a connection
/// instead of holding its own connection string. A workspace has two databases
/// (Sys Data / App Data — see Workspace), so callers say which one they need.
/// </summary>
public class DbConnectionService
{
    public Workspace? Current { get; private set; }

    public event Action? WorkspaceChanged;

    public void SetWorkspace(Workspace ws)
    {
        Current = ws;
        WorkspaceChanged?.Invoke();
    }

    /// <param name="useSysDatabase">true = connect to Sys Data (menu/wcommand/users), false = App Data (business/transaction tables).</param>
    public SqlConnection CreateConnection(bool useSysDatabase = false)
    {
        if (Current is null)
            throw new InvalidOperationException("Chưa chọn Workspace (WS). Vào File > Choose Server để thêm kết nối.");

        return new SqlConnection(Current.BuildConnectionString(useSysDatabase));
    }

    /// <summary>Opens a connection to an arbitrary database name on the current
    /// workspace's server (same credentials) — for a tool that needs a database outside
    /// the usual Sys/App pair, such as Setup eInvoice's separate "Database Proxy".</summary>
    public SqlConnection CreateConnectionToDatabase(string database)
    {
        if (Current is null)
            throw new InvalidOperationException("Chưa chọn Workspace (WS). Vào File > Choose Server để thêm kết nối.");

        return new SqlConnection(Current.BuildConnectionString(database));
    }

    public async Task<(bool ok, string message)> TestConnectionAsync(Workspace ws, bool useSysDatabase = false)
    {
        try
        {
            await using var conn = new SqlConnection(ws.BuildConnectionString(useSysDatabase));
            await conn.OpenAsync();
            var dbLabel = useSysDatabase ? ws.SysDatabase : ws.AppDatabase;
            return (true, $"Kết nối thành công tới {ws.Server}\\{dbLabel}.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
