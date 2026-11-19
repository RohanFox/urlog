using System.Text.Json;
using Urlog.Api.Contracts;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Host = Urlog.Core.Metadata.Host;

namespace Urlog.Api.Endpoints;

/// <summary>The records of a case as the API shows them, built in one place so a list and a single read agree.</summary>
internal static class CaseViews
{
    public static DateTimeOffset? Utc(DateTime? value) => UserResponse.Utc(value);

    public static CaseTaskResponse? Task(CaseTask? task) => task is null
        ? null
        : new(task.Id, task.Kind, task.State, task.Step, task.RequestedBy, task.ConfirmedBy, task.Message, Utc(task.CreatedAt)!.Value, Utc(task.UpdatedAt)!.Value);

    /// <summary>The lifecycle work of the case that has not ended, oldest first; null when there is none.</summary>
    public static async Task<CaseTaskResponse?> UnfinishedTaskAsync(MetadataDb db, long caseId, CancellationToken cancellationToken)
    {
        var task = await db.CaseTasks.AsNoTracking()
            .Where(t => t.CaseId == caseId && (t.State == CaseTaskState.Requested || t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed))
            .OrderBy(t => t.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return Task(task);
    }

    public static StatisticsStateResponse Statistics(CaseStats? stats) => stats is null
        ? new(CaseStatsState.None, false, null, null)
        : new(stats.State, stats.DirtySince is not null, Utc(stats.ComputedAt), stats.State is CaseStatsState.Ok or CaseStatsState.None ? null : stats.Message);

    public static async Task<CaseDetailResponse> CaseAsync(MetadataDb db, CaseRecord @case, MemberRole role, CancellationToken cancellationToken)
    {
        var members = await db.CaseMembers.AsNoTracking().CountAsync(m => m.CaseId == @case.Id, cancellationToken).ConfigureAwait(false);
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == @case.OwnerId).Select(u => u.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "?";
        var stats = await CaseStatsOperations.GetAsync(db, @case.Id, cancellationToken).ConfigureAwait(false);
        var task = await UnfinishedTaskAsync(db, @case.Id, cancellationToken).ConfigureAwait(false);
        return new(@case.Id, @case.Name, @case.State, owner, Utc(@case.CreatedAt)!.Value, Utc(@case.ClosedAt), Utc(@case.ArchivedAt), members, role, SpanResponse.Of(@case), Statistics(stats), task);
    }

    public static HostResponse Host(Host host, HostTotals? totals) => new(
        host.Id, host.Name, host.Timezone, host.TimezoneAssumed, Options(host.SourceOptions), host.MergedIntoId,
        totals?.Files ?? 0, totals?.Rows ?? 0, totals?.Rejects ?? 0, Utc(totals?.TsMin), Utc(totals?.TsMax), host.Version);

    public static IReadOnlyDictionary<string, string>? Options(string? json)
    {
        var options = SourceOptionSet.Parse(json);
        return options.Count == 0 ? null : options;
    }

    public static JobRefResponse? JobRef(Job? job) => job is null ? null : new(job.Id, job.State, job.Kind);

    public static JobResponse Job(Job job, long? bytesTotal) => new(
        job.Id, job.FileId, job.Kind, job.State, job.Generation, job.Attempt, JobOperations.IsRedetect(job),
        Utc(job.CreatedAt)!.Value, Utc(job.StartedAt), Utc(job.FinishedAt),
        job.ChunksRead, job.ChunksParsed, job.ChunksAcked, job.Rows, job.Rejects, job.BytesRead, bytesTotal, job.Message);

    /// <param name="hosts">Every host of the case by id.</param>
    /// <param name="active">The waiting or running job of each file that has one.</param>
    public static FileResponse File(EvidenceFile file, IReadOnlyDictionary<long, Host> hosts, IReadOnlyDictionary<long, Job> active, SourceRules rules)
    {
        var host = file.HostId is { } hostId && hosts.TryGetValue(hostId, out var found) ? found : null;
        var parsed = file.State == FileState.Parsed;
        var own = SourceOptionSet.Parse(file.OverrideOptions);
        var effective = SourceOptionSet.Effective(file.OverrideOptions, host?.SourceOptions);
        var from = effective.Keys.ToDictionary(k => k, k => own.ContainsKey(k) ? "file" : "host", StringComparer.Ordinal);
        var stale = CaseOverview.IsStale(file.DetectedKind, file.DetectedSource, file.OverrideSource, file.DetectionRegistry, rules.RegistryKey);
        var applied = !parsed || (file.ParsedSettingsVersion ?? 1) == file.SettingsVersion;
        active.TryGetValue(file.Id, out var job);
        var isLog = file.State is FileState.Received or FileState.Parsed or FileState.Failed && !(file.DetectedKind == FileKind.Container && file.OverrideSource is null);

        var flags = new List<string>();
        Flag(flags, "unassigned", host is null && isLog);
        Flag(flags, "host_inferred", file.HostInferred);
        Flag(flags, "timezone_assumed", isLog && file.Timezone is null && (host?.TimezoneAssumed ?? true));
        Flag(flags, "year_assumed", isLog && file.YearAssumed);
        Flag(flags, "input_truncated", file.InputTruncated);
        Flag(flags, "container_cut", file.ContainerCut);
        Flag(flags, "trailing_bytes", file.TrailingBytes > 0);
        Flag(flags, "spans_dst", file.SpansDst);
        Flag(flags, "undated", parsed && file.UndatedRows > 0);
        Flag(flags, "future_dated", parsed && file.FutureRows > 0);
        Flag(flags, "detection_stale", stale);
        Flag(flags, "reparse_pending", !applied);
        Flag(flags, "seen_elsewhere", file.SeenPaths is { Length: > 0 });

        return new FileResponse(
            file.Id, file.ParentFileId, file.RootFileId, file.ArchivePath, file.OriginalName, file.Sha256, file.Size, file.HashOfStoredBytes,
            file.Source, file.Uploader, Utc(file.ReceivedAt)!.Value, file.State, file.State == FileState.Quarantined,
            host is null ? null : new FileHostResponse(host.Id, host.Name, file.HostInferred, file.HostRule),
            file.Timezone, new FileYearResponse(file.YearHint, file.YearGiven, file.YearAssumed), file.Encoding, file.Compression,
            new FileDetectionResponse(file.DetectedKind, file.DetectedSource, file.DetectedConfidence, file.DetectedLayout, stale),
            file.OverrideSource is null && own.Count == 0 ? null : new FileOverrideResponse(file.OverrideSource, own.Count == 0 ? null : own),
            new FileEffectiveResponse(SourceRules.EffectiveSource(file), effective, from),
            parsed ? file.ParseGeneration : 0, parsed ? file.RowCount : 0, parsed ? file.RejectCount : 0,
            new FileSpanResponse(Utc(file.TsMin), Utc(file.TsMax), file.UndatedRows, file.FutureRows),
            parsed && file.HistogramGeneration == file.ParseGeneration,
            flags, JobRef(job), applied, file.SettingsVersion);
    }

    public static async Task<Dictionary<long, Host>> HostsAsync(MetadataDb db, long caseId, CancellationToken cancellationToken) =>
        await db.Hosts.AsNoTracking().Where(h => h.CaseId == caseId).ToDictionaryAsync(h => h.Id, cancellationToken).ConfigureAwait(false);

    /// <summary>The waiting or running job of each of the files; a running one wins over a waiting one.</summary>
    public static async Task<Dictionary<long, Job>> ActiveJobsAsync(MetadataDb db, IReadOnlyCollection<long> fileIds, CancellationToken cancellationToken)
    {
        var active = new Dictionary<long, Job>();
        foreach (var chunk in fileIds.Chunk(500))
        {
            var jobs = await db.Jobs.AsNoTracking().Where(j => chunk.Contains(j.FileId) && (j.State == JobState.Queued || j.State == JobState.Parsing)).OrderBy(j => j.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var job in jobs)
            {
                if (!active.TryGetValue(job.FileId, out var known) || known.State != JobState.Parsing)
                {
                    active[job.FileId] = job;
                }
            }
        }

        return active;
    }

    public static async Task<FileResponse> FileAsync(MetadataDb db, EvidenceFile file, SourceRules rules, CancellationToken cancellationToken)
    {
        var hosts = await HostsAsync(db, file.CaseId, cancellationToken).ConfigureAwait(false);
        var active = await ActiveJobsAsync(db, [file.Id], cancellationToken).ConfigureAwait(false);
        return File(file, hosts, active, rules);
    }

    /// <summary>A stored JSON text as a value of the response, or null when it is absent or not JSON.</summary>
    public static JsonElement? Json(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Flag(List<string> flags, string name, bool set)
    {
        if (set)
        {
            flags.Add(name);
        }
    }
}
