using System.Globalization;
using System.Reflection;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Parameters;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Urlog.Bench.Reporting;

/// <summary>Bytes and rows processed by one benchmark operation; the denominator for MB/s, rows/s and B/row.</summary>
public sealed record Workload(long Bytes, long Rows);

/// <summary>
/// Benchmarks run in a child process, so the host cannot read their fields. Each benchmark class instead
/// exposes <c>public static Workload WorkloadFor(ParameterInstances)</c>, computed from fixture metadata
/// and parameters, which the columns resolve by reflection in the host.
/// </summary>
public static class Workloads
{
    public static Workload? Resolve(BenchmarkCase benchmarkCase)
    {
        var method = benchmarkCase.Descriptor.Type.GetMethod("WorkloadFor", BindingFlags.Public | BindingFlags.Static, [typeof(ParameterInstances)]);
        return method?.Invoke(null, [benchmarkCase.Parameters]) as Workload;
    }
}

public enum ThroughputKind
{
    MegabytesPerSecond,
    RowsPerSecond,
    AllocatedBytesPerRow,
}

/// <summary>Derived column: throughput from the mean and the workload, allocation per row from the memory diagnoser.</summary>
public sealed class ThroughputColumn(ThroughputKind kind) : IColumn
{
    public string Id => "Urlog." + kind;

    public string ColumnName => kind switch
    {
        ThroughputKind.MegabytesPerSecond => "MB/s",
        ThroughputKind.RowsPerSecond => "rows/s",
        _ => "B/row",
    };

    public bool AlwaysShow => true;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => (int)kind;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend => kind switch
    {
        ThroughputKind.MegabytesPerSecond => "Input bytes per second (decimal MB) derived from Mean",
        ThroughputKind.RowsPerSecond => "Rows (lines) per second derived from Mean",
        _ => "Managed bytes allocated per row; tokenisers must be 0",
    };

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) => GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var workload = Workloads.Resolve(benchmarkCase);
        var report = summary[benchmarkCase];
        var stats = report?.ResultStatistics;
        if (workload is null || report is null || stats is null || stats.Mean <= 0)
        {
            return "-";
        }

        var seconds = stats.Mean / 1e9;
        switch (kind)
        {
            case ThroughputKind.MegabytesPerSecond:
                return (workload.Bytes / 1e6 / seconds).ToString("N1", CultureInfo.InvariantCulture);
            case ThroughputKind.RowsPerSecond:
                return (workload.Rows / seconds).ToString("N0", CultureInfo.InvariantCulture);
            default:
                var allocated = report.GcStats.GetBytesAllocatedPerOperation(benchmarkCase);
                return allocated is null || workload.Rows == 0
                    ? "-"
                    : (allocated.Value / (double)workload.Rows).ToString("N2", CultureInfo.InvariantCulture);
        }
    }

    public bool IsAvailable(Summary summary) => true;

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
}
