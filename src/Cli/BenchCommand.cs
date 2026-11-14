using System.Diagnostics;
using Urlog.Query;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog bench query --case &lt;id&gt; [--runs 20] [--cold] '&lt;spl&gt;'</c>: the query latency measurement. Runs the
/// transpiled query N times through the real query path and prints min / p50 / p95 / max of the client-side wall
/// time. <c>--cold</c> drops the server's mark, uncompressed and index caches (as the admin user) before every run,
/// which is as cold as a client can make it: the OS page cache stays. The throughput benches live in
/// <c>bench/</c> (BenchmarkDotNet); <c>urlog ingest</c> prints its own MB/s.
/// </summary>
internal static class BenchCommand
{
    private static readonly string[] CacheDrops =
    [
        "SYSTEM DROP MARK CACHE", "SYSTEM DROP UNCOMPRESSED CACHE", "SYSTEM DROP INDEX MARK CACHE", "SYSTEM DROP INDEX UNCOMPRESSED CACHE",
        "SYSTEM DROP SKIPPING INDEX CACHE", "SYSTEM DROP PRIMARY INDEX CACHE", "SYSTEM DROP QUERY CACHE",
    ];

    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        if (args.Positional.Count == 0 || !args.Positional[0].Equals("query", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("usage: urlog bench query --case <id> [--runs 20] [--cold] '<spl>'  (throughput benches: dotnet run --project bench)");
        }

        args.Positional.RemoveAt(0);
        var runs = args.Int("runs", 20);
        if (runs < 1)
        {
            throw new CliException("--runs must be at least 1");
        }

        var cold = args.Flag("cold");
        var (result, client) = await QueryCommand.PrepareAsync(args, ct).ConfigureAwait(false);
        var connection = new Connection(args);
        if (cold)
        {
            Console.Error.WriteLine(connection.AnnounceAdmin());
        }

        Console.WriteLine(result.Sql);
        Console.WriteLine($"{runs} runs, {(cold ? "cold (server caches dropped before each run)" : "warm")}, case {result.CaseId}, range {Transpiler.Fmt(result.RangeFrom)} .. {Transpiler.Fmt(result.RangeTo)}");

        var dropped = new List<string>();
        var times = new List<double>(runs);
        long rows = -1;
        for (var i = 0; i < runs; i++)
        {
            if (cold)
            {
                await using var admin = connection.Admin();
                dropped.Clear();
                foreach (var statement in CacheDrops)
                {
                    try
                    {
                        await admin.ExecuteAsync(statement, cancellationToken: ct).ConfigureAwait(false);
                        dropped.Add(statement["SYSTEM DROP ".Length..]);
                    }
                    catch (ClickHouse.Driver.Tcp.ClickHouseTcpServerException)
                    {
                        // a cache this server version does not have
                    }
                }
            }

            var sw = Stopwatch.StartNew();
            var table = await QueryCommand.GuardAsync(() => client.RunAsync(result, cancellationToken: ct)).ConfigureAwait(false);
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (rows >= 0 && rows != table.Rows.Count)
            {
                throw new CliException($"run {i + 1} returned {table.Rows.Count} rows, earlier runs {rows}");
            }

            rows = table.Rows.Count;
        }

        times.Sort();
        if (cold)
        {
            Console.WriteLine($"dropped before each run: {string.Join(", ", dropped)}");
        }

        Console.WriteLine($"rows {rows:N0}; ms: min {times[0]:N0}, p50 {Percentile(times, 0.50):N0}, p95 {Percentile(times, 0.95):N0}, max {times[^1]:N0} (client wall time, HTTP, {runs} runs)");
        return 0;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        var rank = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[rank];
    }
}
