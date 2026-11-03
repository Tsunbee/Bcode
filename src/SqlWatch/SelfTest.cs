using Microsoft.Data.SqlClient;

namespace SqlWatch;

internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var password = Environment.GetEnvironmentVariable("SQLWATCH_PWD");
        if (string.IsNullOrEmpty(password))
        {
            Console.WriteLine("NO_PWD");
            return 2;
        }

        const string server = @"172.168.5.14\SQL2016";
        const string database = "BMT_FBO_FBISP2422_A";
        var watcherBuilder = TraceWatcher.CreateBuilder(server, "BMT_FBO", password, "master", TraceWatcher.ApplicationName);
        await using var watcher = new TraceWatcher(watcherBuilder.ConnectionString, [database]);
        await watcher.OpenAsync(CancellationToken.None);

        var canaryBuilder = TraceWatcher.CreateBuilder(server, "BMT_FBO", password, database, "SqlWatchSelfTest");
        await using var canary = new SqlConnection(canaryBuilder.ConnectionString);
        await canary.OpenAsync();

        await using (var batch = new SqlCommand("SELECT 1 AS sqlwatch_canary_batch;", canary))
            await batch.ExecuteNonQueryAsync();

        var hitBatch = await WaitForAsync(watcher, ev =>
            ev.ApplicationName == "SqlWatchSelfTest"
            && ev.EventName == "SQL:BatchStarting"
            && ev.Text.Contains("sqlwatch_canary_batch", StringComparison.OrdinalIgnoreCase));

        await using (var rpc = new SqlCommand("sp_who", canary) { CommandType = System.Data.CommandType.StoredProcedure })
            await rpc.ExecuteNonQueryAsync();

        var hitRpc = await WaitForAsync(watcher, ev =>
            ev.ApplicationName == "SqlWatchSelfTest" && ev.EventName == "RPC:Starting");

        var hitSp = false;
        var waiting = Task.Run(async () =>
        {
            var waitBuilder = TraceWatcher.CreateBuilder(server, "BMT_FBO", password, database, "SqlWatchSelfTest");
            await using var waitConn = new SqlConnection(waitBuilder.ConnectionString);
            await waitConn.OpenAsync();
            await using (var create = new SqlCommand("""
                CREATE PROCEDURE #sqlwatch_wait AS
                BEGIN
                    WAITFOR DELAY '00:00:02';
                END
                """, waitConn))
                await create.ExecuteNonQueryAsync();
            await using var exec = new SqlCommand("EXEC #sqlwatch_wait;", waitConn);
            await exec.ExecuteNonQueryAsync();
        });

        var until = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < until && !waiting.IsCompleted)
        {
            foreach (var ev in await watcher.PollAsync(CancellationToken.None))
            {
                if (ev.ApplicationName == "SqlWatchSelfTest" && ev.EventName == "SP:Starting")
                    hitSp = true;
            }
            await Task.Delay(100);
        }

        await waiting;
        Console.WriteLine($"BATCH={(hitBatch ? "PASS" : "FAIL")} RPC={(hitRpc ? "PASS" : "FAIL")} SP={(hitSp ? "PASS" : "FAIL")}");
        return hitBatch && hitRpc && hitSp ? 0 : 1;
    }

    private static async Task<bool> WaitForAsync(TraceWatcher watcher, Func<CapturedEvent, bool> match)
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(150);
            foreach (var ev in await watcher.PollAsync(CancellationToken.None))
            {
                if (match(ev))
                    return true;
            }
        }
        return false;
    }
}
