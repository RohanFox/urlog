using System.Collections.Concurrent;
using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Api.Uploads;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Jobs;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Names = Urlog.Core.Metadata.Names;

namespace Urlog.Api.Endpoints;

/// <summary>Where the dashboard gets a case's disk use: the directories and the event store, or a test's stand-in.</summary>
public interface ICaseUsageSource
{
    /// <summary>The usage and when it was measured; null when the case is gone.</summary>
    Task<(CaseUsage Usage, DateTimeOffset MeasuredAt)?> MeasureAsync(long caseId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="CaseAccounting"/> as the dashboard asks it: the case's directories are walked and the event store
/// is asked for its partitions as the ingest account, at most once in <see cref="MaxAge"/> for a case. The figure
/// is kept in memory with the time it was taken; nothing about who may see the case is kept.
/// </summary>
public sealed class CaseUsageSource(MetadataStore store, IJobEventStore events, UploadFolders folders, IOptions<UrlogOptions> options, IHostEnvironment environment, TimeProvider clock) : ICaseUsageSource
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, (CaseUsage Usage, DateTimeOffset At)> _recent = new();

    public async Task<(CaseUsage Usage, DateTimeOffset MeasuredAt)?> MeasureAsync(long caseId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (_recent.TryGetValue(caseId, out var known) && now - known.At < MaxAge && now >= known.At)
        {
            return known;
        }

        var archive = options.Value.Lifecycle.ArchiveRoot;
        var paths = new LifecyclePaths(
            folders.Root,
            new MemberStore(folders.Root, null).CacheRoot,
            string.Empty,
            string.IsNullOrWhiteSpace(archive) ? null : Path.IsPathRooted(archive) ? archive : Path.Combine(environment.ContentRootPath, archive));
        await using var db = store.CreateDbContext();
        var usage = await CaseAccounting.MeasureAsync(db, events, paths, caseId, cancellationToken).ConfigureAwait(false);
        if (usage is null)
        {
            _recent.TryRemove(caseId, out _);
            return null;
        }

        _recent[caseId] = (usage, now);
        return (usage, now);
    }
}

/// <summary>
/// <c>GET /api/v1/cases/{caseId}/dashboard</c>: what the case page shows above the file list. Counts of files by
/// state and kind, the flags that want a person's eye, the queue and the job that runs, the hosts with their
/// totals, the span, the state of the statistics and disk use. The counts are read from the metadata file in one
/// pass over the case's file records; the file list itself is the files route's, in pages. Disk use walks the
/// case's directories and asks the event store, so it is measured at most every 30 s and says when.
/// </summary>
public static class DashboardEndpoints
{
    public static void Map(RouteGroupBuilder oneCase)
    {
        ArgumentNullException.ThrowIfNull(oneCase);
        oneCase.MapGet("/dashboard", GetAsync).RequireCaseRole(MemberRole.Ro).WithName("GetCaseDashboard");
    }

    private static async Task<Ok<DashboardResponse>> GetAsync(HttpContext http, MetadataDb db, SourceRules rules, ICaseUsageSource usageSource, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var context = http.Features.GetRequiredFeature<CaseContext>();
        var @case = context.Case;
        var body = await BuildAsync(db, @case, context.Role, rules, cancellationToken).ConfigureAwait(false);
        DashboardUsageResponse? usage = null;
        string? unavailable = null;
        try
        {
            if (await usageSource.MeasureAsync(@case.Id, cancellationToken).ConfigureAwait(false) is { } measured)
            {
                var u = measured.Usage;
                usage = new(u.RecordedBytes, u.IntakeBytes, u.UploadBytes, u.CacheBytes, u.ArchiveBytes, u.StoreRows, u.StoreBytes, measured.MeasuredAt);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The counts need no event store; the page shows them and says the disk figures are missing.
            unavailable = "disk use could not be measured: the event store did not answer";
            Log.UsageUnavailable(loggers.CreateLogger(typeof(DashboardEndpoints)), @case.Id, e.Message);
        }

        return TypedResults.Ok(body with { Usage = usage, UsageUnavailable = unavailable });
    }

    /// <summary>Everything but disk use, from the metadata file alone.</summary>
    internal static async Task<DashboardResponse> BuildAsync(MetadataDb db, CaseRecord @case, MemberRole role, SourceRules rules, CancellationToken cancellationToken)
    {
        var data = await CaseOverview.ReadAsync(db, @case.Id, rules.RegistryKey, cancellationToken).ConfigureAwait(false);
        var hosts = await db.Hosts.AsNoTracking().Where(h => h.CaseId == @case.Id).OrderBy(h => h.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == @case.OwnerId).Select(u => u.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "?";
        var stats = await CaseStatsOperations.GetAsync(db, @case.Id, cancellationToken).ConfigureAwait(false);
        var task = await CaseViews.UnfinishedTaskAsync(db, @case.Id, cancellationToken).ConfigureAwait(false);
        var totals = data.Hosts.Where(t => t.HostId is not null).ToDictionary(t => t.HostId!.Value);
        var loose = data.Hosts.FirstOrDefault(t => t.HostId is null);

        var files = new DashboardFilesResponse(
            data.Files, data.Arrived, data.BytesArrived,
            data.ByState.OrderBy(p => p.Key).ToDictionary(p => Names.Of(p.Key), p => p.Value),
            data.ByKind.OrderBy(p => p.Key).ToDictionary(p => Names.Of(p.Key), p => p.Value),
            data.FileRows, data.FileRejects,
            CaseOverview.FlagNames.ToDictionary(n => n, n => data.Flags.GetValueOrDefault(n)));
        var ingest = new DashboardIngestResponse(
            data.Jobs.GetValueOrDefault(JobState.Queued), data.Jobs.GetValueOrDefault(JobState.Parsing), data.Jobs.GetValueOrDefault(JobState.Failed),
            data.Jobs.GetValueOrDefault(JobState.Cancelled), data.Jobs.GetValueOrDefault(JobState.Done), data.Paused,
            [.. data.Running.Select(j => new DashboardJobResponse(j.JobId, j.FileId, j.Kind, j.BytesRead, j.BytesTotal, j.Rows, j.Rejects, CaseViews.Utc(j.StartedAt)))],
            data.ParseBytesDone, data.ParseBytesTotal);
        return new DashboardResponse(
            new DashboardCaseResponse(@case.Id, @case.Name, @case.State, role, owner, task),
            SpanResponse.Of(@case), CaseViews.Statistics(stats), files, ingest,
            [.. hosts.Select(h => CaseViews.Host(h, totals.GetValueOrDefault(h.Id)))],
            loose is null ? null : new HostResponse(0, string.Empty, "UTC", true, null, null, loose.Files, loose.Rows, loose.Rejects, CaseViews.Utc(loose.TsMin), CaseViews.Utc(loose.TsMax), 0),
            null, null);
    }
}
