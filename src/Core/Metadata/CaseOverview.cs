using Urlog.Core.Detection;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>What the files of one host add up to. <paramref name="HostId"/> is null for the files without a host.</summary>
public sealed record HostTotals(long? HostId, int Files, long Rows, long Rejects, DateTime? TsMin, DateTime? TsMax);

/// <summary>The job a case is parsing right now, with the size of its file for a progress bar.</summary>
public sealed record RunningJob(long JobId, long FileId, JobKind Kind, long BytesRead, long BytesTotal, long Rows, long Rejects, DateTime? StartedAt);

/// <summary>
/// The counts of a case's files and jobs, from the metadata file alone. <see cref="FileRows"/> and
/// <see cref="FileRejects"/> are the sums of the file records, which is what the committed parses hold.
/// </summary>
public sealed record CaseOverviewData
{
    public int Files { get; init; }

    /// <summary>Files that arrived by themselves; the rest are members of archives.</summary>
    public int Arrived { get; init; }

    public long BytesArrived { get; init; }

    public long FileRows { get; init; }

    public long FileRejects { get; init; }

    public IReadOnlyDictionary<FileState, int> ByState { get; init; } = new Dictionary<FileState, int>();

    public IReadOnlyDictionary<FileKind, int> ByKind { get; init; } = new Dictionary<FileKind, int>();

    /// <summary>How many files carry each flag a person may have to look at, by the flag's name.</summary>
    public IReadOnlyDictionary<string, int> Flags { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<HostTotals> Hosts { get; init; } = [];

    public IReadOnlyDictionary<JobState, int> Jobs { get; init; } = new Dictionary<JobState, int>();

    public IReadOnlyList<RunningJob> Running { get; init; } = [];

    /// <summary>Why the queue is held by the disk watermark, when it is.</summary>
    public string? Paused { get; init; }

    /// <summary>Stored bytes of every file that is or will be parsed, and of those the bytes whose parse is committed or read so far.</summary>
    public long ParseBytesTotal { get; init; }

    public long ParseBytesDone { get; init; }
}

/// <summary>
/// The case dashboard's reads (design record, "Cases, hosts, files and jobs API"): one grouped statement over
/// the case's file records, the case's jobs by state, and the jobs that run. No file list: that is paged by the
/// files route. Nothing here reads the event store.
/// </summary>
public static class CaseOverview
{
    /// <summary>The names under <see cref="CaseOverviewData.Flags"/>, in the order a response lists them.</summary>
    public static IReadOnlyList<string> FlagNames { get; } =
    [
        "unassigned", "host_inferred", "quarantined", "input_truncated", "spans_dst", "undated", "future_dated",
        "year_assumed", "detection_stale", "reparse_pending", "no_stored_histogram", "control_bytes",
    ];

    public static async Task<CaseOverviewData> ReadAsync(MetadataDb db, long caseId, string registryKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        // One statement over the file records of the case, grouped by what the counts are split by. The store
        // adds up; nothing of a record crosses into this process but the sums of its group.
        var generic = FormatDetector.GenericSourceId;
        var unknownBinary = FormatDetector.UnknownBinary;
        var groups = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId)
            .GroupBy(f => new { f.HostId, f.State, f.DetectedKind })
            .Select(g => new
            {
                g.Key.HostId,
                g.Key.State,
                g.Key.DetectedKind,
                Files = g.Count(),
                Bytes = g.Sum(f => f.Size),
                Arrived = g.Sum(f => f.ParentFileId == null ? 1 : 0),
                BytesArrived = g.Sum(f => f.ParentFileId == null ? f.Size : 0),
                Rows = g.Sum(f => f.RowCount),
                Rejects = g.Sum(f => f.RejectCount),
                Min = g.Min(f => f.TsMin),
                Max = g.Max(f => f.TsMax),
                NoOverride = g.Sum(f => f.OverrideSource == null ? 1 : 0),
                NoOverrideBytes = g.Sum(f => f.OverrideSource == null ? f.Size : 0),
                HostInferred = g.Sum(f => f.HostInferred ? 1 : 0),
                InputTruncated = g.Sum(f => f.InputTruncated ? 1 : 0),
                SpansDst = g.Sum(f => f.SpansDst ? 1 : 0),
                Undated = g.Sum(f => f.UndatedRows > 0 ? 1 : 0),
                Future = g.Sum(f => f.FutureRows > 0 ? 1 : 0),
                YearAssumed = g.Sum(f => f.YearAssumed ? 1 : 0),
                YearAssumedNoOverride = g.Sum(f => f.YearAssumed && f.OverrideSource == null ? 1 : 0),
                Stale = g.Sum(f => f.OverrideSource == null && (f.DetectionRegistry == null || f.DetectionRegistry != registryKey) && (f.DetectedKind == FileKind.Unparsed || (f.DetectedKind == FileKind.Log && f.DetectedSource == generic) || (f.DetectedKind == FileKind.Artifact && f.DetectedFormat == unknownBinary)) ? 1 : 0),
                ControlBytes = g.Sum(f => f.DetectedControlBytes ? 1 : 0),
                Pending = g.Sum(f => (f.ParsedSettingsVersion ?? 1) != f.SettingsVersion ? 1 : 0),
                NoHistogram = g.Sum(f => f.HistogramGeneration == null || f.HistogramGeneration != f.ParseGeneration ? 1 : 0),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var byState = new Dictionary<FileState, int>();
        var byKind = new Dictionary<FileKind, int>();
        var flags = FlagNames.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        var hosts = new Dictionary<long, (int Files, long Rows, long Rejects, DateTime? Min, DateTime? Max)>();
        (int Files, long Rows, long Rejects, DateTime? Min, DateTime? Max) unassigned = default;
        long rows = 0, rejects = 0, bytesArrived = 0, parseTotal = 0, parseDone = 0;
        int arrived = 0, total = 0;
        foreach (var g in groups)
        {
            total += g.Files;
            byState[g.State] = byState.GetValueOrDefault(g.State) + g.Files;
            if (g.DetectedKind is { } kind)
            {
                byKind[kind] = byKind.GetValueOrDefault(kind) + g.Files;
            }

            arrived += g.Arrived;
            bytesArrived += g.BytesArrived;
            var parsed = g.State == FileState.Parsed;
            // A container without an override is expanded, never parsed; with one it is a log like any other.
            var container = g.DetectedKind == FileKind.Container;
            var parses = g.State is FileState.Received or FileState.Parsed or FileState.Failed;
            var parsedFiles = !parses ? 0 : container ? g.Files - g.NoOverride : g.Files;
            var parsedBytes = !parses ? 0 : container ? g.Bytes - g.NoOverrideBytes : g.Bytes;
            parseTotal += parsedBytes;
            parseDone += parsed ? parsedBytes : 0;

            var totals = g.HostId is { } id ? hosts.GetValueOrDefault(id) : unassigned;
            totals.Files += g.Files;
            if (parsed)
            {
                rows += g.Rows;
                rejects += g.Rejects;
                totals.Rows += g.Rows;
                totals.Rejects += g.Rejects;
                totals.Min = Least(totals.Min, g.Min);
                totals.Max = Greatest(totals.Max, g.Max);
            }

            if (g.HostId is { } hostId)
            {
                hosts[hostId] = totals;
            }
            else
            {
                unassigned = totals;
            }

            flags["unassigned"] += g.HostId is null ? parsedFiles : 0;
            flags["host_inferred"] += g.HostInferred;
            flags["quarantined"] += g.State == FileState.Quarantined ? g.Files : 0;
            flags["input_truncated"] += g.InputTruncated;
            flags["spans_dst"] += g.SpansDst;
            flags["undated"] += parsed ? g.Undated : 0;
            flags["future_dated"] += parsed ? g.Future : 0;
            flags["year_assumed"] += !parses ? 0 : container ? g.YearAssumed - g.YearAssumedNoOverride : g.YearAssumed;
            flags["detection_stale"] += g.Stale;
            flags["reparse_pending"] += parsed ? g.Pending : 0;
            flags["no_stored_histogram"] += parsed ? g.NoHistogram : 0;
            flags["control_bytes"] += parses ? g.ControlBytes : 0;
        }

        var jobs = await db.Jobs.AsNoTracking().Where(j => j.CaseId == caseId).GroupBy(j => j.State).Select(g => new { State = g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var running = await db.Jobs.AsNoTracking().Where(j => j.CaseId == caseId && j.State == JobState.Parsing).OrderBy(j => j.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var paused = await db.Jobs.AsNoTracking().Where(j => j.CaseId == caseId && j.State == JobState.Queued && j.Message != null && j.Message.StartsWith(JobOperations.PausedPrefix)).Select(j => j.Message).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var runningFiles = running.Select(j => j.FileId).ToList();
        var sizes = runningFiles.Count == 0
            ? []
            : await db.Files.AsNoTracking().Where(f => runningFiles.Contains(f.Id)).Select(f => new { f.Id, f.Size }).ToDictionaryAsync(f => f.Id, f => f.Size, cancellationToken).ConfigureAwait(false);
        var runningJobs = running.Select(j => new RunningJob(j.Id, j.FileId, j.Kind, j.BytesRead, sizes.GetValueOrDefault(j.FileId), j.Rows, j.Rejects, j.StartedAt)).ToList();
        // A re-parse reads a file whose bytes are already counted as done; a first parse adds what it has read so far.
        parseDone += running.Where(j => j.Kind == JobKind.Parse).Sum(j => Math.Min(j.BytesRead, sizes.GetValueOrDefault(j.FileId)));

        var perHost = hosts.OrderBy(p => p.Key).Select(p => new HostTotals(p.Key, p.Value.Files, p.Value.Rows, p.Value.Rejects, p.Value.Min, p.Value.Max)).ToList();
        if (unassigned.Files > 0)
        {
            perHost.Add(new HostTotals(null, unassigned.Files, unassigned.Rows, unassigned.Rejects, unassigned.Min, unassigned.Max));
        }

        return new CaseOverviewData
        {
            Files = total,
            Arrived = arrived,
            BytesArrived = bytesArrived,
            FileRows = rows,
            FileRejects = rejects,
            ByState = byState,
            ByKind = byKind,
            Flags = flags,
            Hosts = perHost,
            Jobs = jobs.ToDictionary(j => j.State, j => j.Count),
            Running = runningJobs,
            Paused = paused,
            ParseBytesTotal = parseTotal,
            ParseBytesDone = Math.Min(parseDone, parseTotal),
        };
    }

    /// <summary>
    /// True for a file that could reach a parser it has not got: no override, taken for <c>unparsed</c>, sent to
    /// the fallback source, or kept as an unknown binary (which another build's rule may read as text), by a
    /// detector that is not this build's.
    /// </summary>
    public static bool IsStale(FileKind? kind, string? detectedSource, string? detectedFormat, string? overrideSource, string? registry, string registryKey) =>
        overrideSource is null
        && (kind == FileKind.Unparsed
            || (kind == FileKind.Log && string.Equals(detectedSource, FormatDetector.GenericSourceId, StringComparison.Ordinal))
            || (kind == FileKind.Artifact && string.Equals(detectedFormat, FormatDetector.UnknownBinary, StringComparison.Ordinal)))
        && !string.Equals(registry, registryKey, StringComparison.Ordinal);

    private static DateTime? Least(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static DateTime? Greatest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
