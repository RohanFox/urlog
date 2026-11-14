using System.Globalization;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Jobs;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog sweep --db &lt;metadata.db&gt; [--evidence-root dir] [--cache-root dir]</c>: the integrity sweep by hand.
/// Every stored file is read and compared with its intake record, each result goes to the audit chain, and a
/// file that changed or is gone is quarantined. It takes the runner lock, so it never runs beside an ingest; a
/// runner that is up does its own sweep on its schedule.
/// </summary>
internal static class SweepCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var path = VerifyAuditCommand.MetadataPath(args, mustExist: true);
        await StoreCheck.LocalAsync(path, ct).ConfigureAwait(false);
        var store = new MetadataStore(path);
        using var runnerLock = RunnerLock.TryAcquire(store.Path)
            ?? throw new CliException($"a job runner holds {RunnerLock.PathFor(store.Path)}; it runs the sweep on its own schedule, and a second reader would compete with its ingest");
        await store.MigrateAsync(ct).ConfigureAwait(false);
        var paths = CaseCommand.Paths(args, store);
        var sweep = new IntegritySweep(store, new MemberStore(paths.EvidenceRoot, paths.CacheRoot), paths.EvidenceRoot, new LifecycleOptions(), NullLogger.Instance);
        var report = await sweep.RunAsync(store.Clock.GetUtcNow().UtcDateTime, () => ct.IsCancellationRequested ? SweepSignal.Stop : SweepSignal.Go, ct).ConfigureAwait(false);
        var megabytes = report.Bytes / 1e6;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"sweep     {report.Ok} match, {report.Mismatched} changed, {report.Missing} missing, {report.Unreadable} could not be read; {megabytes:N1} MB in {report.Elapsed.TotalSeconds:N1} s = {megabytes / Math.Max(report.Elapsed.TotalSeconds, 0.001):N1} MB/s{(report.Complete ? "" : "; stopped before the end")}"));
        await using var db = store.CreateDbContext();
        await UserCommand.WriteHeadAsync(db, ct).ConfigureAwait(false);
        return report.Mismatched + report.Missing > 0 ? 1 : 0;
    }
}
