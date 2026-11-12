using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Reporting;

namespace Urlog.Bench;

/// <summary>
/// Entry point for every benchmark in this assembly. Arguments are BenchmarkDotNet's own (<c>--filter</c>,
/// <c>--job short</c>, <c>--list flat</c>, ...) plus one of ours:
/// <list type="bullet">
/// <item><c>-Slo</c>: pin the benchmark process to 8 threads (<c>--affinity 0xFF</c>) so EndToEnd and Insert
/// numbers are comparable to the 8-core reference box; results go to <c>bench/BenchmarkDotNet.Artifacts-slo/</c>
/// so they never mix with the unpinned run the gate reads.</item>
/// </list>
/// Fixtures are generated into <c>bench/data/</c> before anything runs.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var slo = args.Any(IsSloFlag);
        var bdnArgs = args.Where(a => !IsSloFlag(a)).ToList();
        if (slo)
        {
            bdnArgs.AddRange(["--affinity", "255"]);
            Console.WriteLine("bench: -Slo: process affinity 0xFF (8 threads), artifacts in bench/BenchmarkDotNet.Artifacts-slo");
        }

        FixtureStore.EnsureAll();

        var config = DefaultConfig.Instance
            .WithArtifactsPath(RepoPaths.Artifacts(slo ? "-slo" : ""))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(JsonExporter.Full)
            .AddColumn(
                new ThroughputColumn(ThroughputKind.MegabytesPerSecond),
                new ThroughputColumn(ThroughputKind.RowsPerSecond),
                new ThroughputColumn(ThroughputKind.AllocatedBytesPerRow));

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run([.. bdnArgs], config).ToList();

        if (summaries.Count == 0)
        {
            // A filter that matched nothing is not a failure of the gate; the regression check decides what an
            // absent result means.
            Console.WriteLine("bench: no benchmarks matched; nothing was measured.");
            return 0;
        }

        // A benchmark that failed to build or run shows as NA in the table; that must not look like a green run.
        var failed = summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => !r.Success));
        return failed ? 1 : 0;
    }

    private static bool IsSloFlag(string arg) =>
        arg.Equals("-Slo", StringComparison.OrdinalIgnoreCase) || arg.Equals("--slo", StringComparison.OrdinalIgnoreCase);
}
