using Microsoft.Data.SqlClient;

namespace SqlWatch;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Any(arg => arg == "--selftest"))
        {
            Environment.ExitCode = SelfTest.RunAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Any(arg => arg == "--parse-check"))
        {
            Console.WriteLine(ProcCalls.SelfCheck());
            return;
        }

        if (args.Any(arg => arg == "--login-check"))
        {
            Environment.ExitCode = LoginCheckAsync().GetAwaiter().GetResult();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    static async Task<int> LoginCheckAsync()
    {
        var catalog = BcodeCatalog.Load();
        var names = new[] { catalog.LastWorkspace, "DECOFI" };
        var failed = 0;
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!catalog.Workspaces.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("MISSING " + name);
                failed++;
                continue;
            }

            var workspace = catalog.Workspaces.First(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var builder = TraceWatcher.CreateBuilder(workspace.Server, SqlLogin.User, SqlLogin.Password, "master", TraceWatcher.ApplicationName);
            try
            {
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    "SELECT CAST(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') AS int), DB_ID(@a), DB_ID(@b);",
                    connection);
                command.Parameters.AddWithValue("@a", workspace.Databases.ElementAtOrDefault(0) ?? "");
                command.Parameters.AddWithValue("@b", workspace.Databases.ElementAtOrDefault(1) ?? "");
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                Console.WriteLine($"{workspace.Name} OK view={reader.GetInt32(0)} db1={(reader.IsDBNull(1) ? "missing" : "yes")} db2={(reader.IsDBNull(2) ? "missing" : "yes")} dbs={workspace.Databases.Count}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"{workspace.Name} FAIL {ex.Message}");
            }
        }

        return failed == 0 ? 0 : 1;
    }
}
