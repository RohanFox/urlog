using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Detection;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Intake;

/// <summary>One file that is on disk and is to become evidence of a case.</summary>
public sealed record IntakeRequest
{
    public required long CaseId { get; init; }

    /// <summary>Where the bytes are now. Under the evidence root the file is made read-only; elsewhere it is recorded where it lies.</summary>
    public required string Path { get; init; }

    public required FileSource Source { get; init; }

    /// <summary>The account name (web) or the OS user (SFTP drop, CLI).</summary>
    public required string Uploader { get; init; }

    public long? UploaderUserId { get; init; }

    /// <summary>The name the uploader gave the file; null takes it from <see cref="Path"/>.</summary>
    public string? OriginalName { get; init; }

    /// <summary>The host a person assigned. Null lets the name and, for an archive, the member paths propose one.</summary>
    public string? Host { get; init; }

    /// <summary>Per-file zone for naive timestamps: an IANA id, <c>UTC</c>, or an offset like <c>+02:00</c>. Null uses the host's.</summary>
    public string? Timezone { get; init; }

    /// <summary>The year of the first record, for formats that carry none. Null derives it from the file's own time.</summary>
    public int? Year { get; init; }

    /// <summary>A parser a person chose; the detector's record is kept beside it.</summary>
    public string? OverrideSource { get; init; }

    /// <summary>Per-file source options (<c>nginx.log_format</c>, ...).</summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>Called once when the record finds the metadata file locked and the pipeline goes on trying; a caller logs it.</summary>
    public Action? RecordWaiting { get; init; }
}

/// <summary>What became of one <see cref="IntakeRequest"/>.</summary>
/// <param name="File">The record: new, or the existing one for bytes the case already holds. Null when the file was refused.</param>
/// <param name="Job">The parse or the expansion queued with a new record; null for a duplicate and for what is only kept.</param>
/// <param name="Rejected">The file was refused and nothing of it is recorded (a packet capture).</param>
/// <param name="Host">The host the file was given or inferred to belong to, lowercased; null when unassigned.</param>
public sealed record IntakeOutcome(EvidenceFile? File, Job? Job, bool Duplicate, bool Rejected, DetectionResult Detection, string Sha256, long Size, string? Host, bool HostInferred);

/// <summary>The request cannot be taken as it is: an unknown zone, an unknown source, a path that is not a file.</summary>
public sealed class IntakeRefusedException : Exception
{
    public IntakeRefusedException()
    {
    }

    public IntakeRefusedException(string message)
        : base(message)
    {
    }

    public IntakeRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The one pipeline every delivery path calls once the bytes are on disk (design record, "Intake"). In order:
/// <list type="number">
/// <item>SHA-256 over the raw bytes, with the size from the same read. Nothing opens the file before this.</item>
/// <item>The file is made read-only when it lies under the evidence root.</item>
/// <item>Detection from the first 64 KB and the last 4 KB. A packet capture is refused: an audit row on the case, no record.</item>
/// <item>The hints: host (given, else inferred from the name), zone, year, the file's own time.</item>
/// <item>One transaction: the record, its <c>file_intake</c> row and the job it needs (a parse for a log, an
/// expansion for a container), or for bytes the case already holds a <c>file_duplicate</c> row and nothing else.</item>
/// </list>
/// Everything that decodes is the job's work, not intake's. The file is only ever opened for reading.
/// <para>
/// With a <see cref="BusyRetry"/> the last step is tried again while another writer holds the metadata file. Each
/// attempt opens its own context and its own transaction, so the active-case check, the duplicate check and the
/// audit chain head are read again every time; the hash is taken once. Without one a locked file ends the call
/// at the store's busy timeout.
/// </para>
/// </summary>
public sealed class IntakePipeline
{
    private readonly MetadataStore _store;
    private readonly SourceCatalog _catalog;
    private readonly IReadOnlyList<ILogSource> _sources;
    private readonly string? _evidenceRoot;
    private readonly HostInference _hosts;
    private readonly ArchiveLimits _limits;
    private readonly BusyRetry? _recordRetry;
    private long _hashPasses;

    public IntakePipeline(MetadataStore store, SourceCatalog catalog, string? evidenceRoot = null, HostInference? hosts = null, ArchiveLimits? limits = null, BusyRetry? recordRetry = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        _store = store;
        _catalog = catalog;
        _sources = FormatDetector.Instantiate(catalog);
        _evidenceRoot = string.IsNullOrEmpty(evidenceRoot) ? null : Path.GetFullPath(evidenceRoot);
        _hosts = hosts ?? HostInference.Default;
        _limits = limits ?? ArchiveLimits.Default;
        _recordRetry = recordRetry;
    }

    /// <summary>How many files this pipeline has read from end to end for their hash: one per accepted call, however often the record was tried.</summary>
    public long HashPasses => Interlocked.Read(ref _hashPasses);

    /// <exception cref="IntakeRefusedException">The request names an unknown zone or source, or no file.</exception>
    /// <exception cref="CaseNotFoundException">No such case.</exception>
    /// <exception cref="CaseNotActiveException">The case is not <c>active</c>.</exception>
    /// <exception cref="MetadataBusyException">The metadata file stayed locked for the whole of the record's retry; nothing was written.</exception>
    public async Task<IntakeOutcome> AcceptAsync(IntakeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = Path.GetFullPath(request.Path);
        if (!File.Exists(path))
        {
            throw new IntakeRefusedException($"no such file: {path}");
        }

        if (!IntakeHints.IsKnownZone(request.Timezone))
        {
            throw new IntakeRefusedException($"unknown timezone '{request.Timezone}'; use an IANA id, UTC, or an offset like +02:00");
        }

        if (request.OverrideSource is { } wanted && !_catalog.TryGet(wanted, out _))
        {
            throw new IntakeRefusedException($"no log source called '{wanted}'; known: {string.Join(", ", _catalog.All.Select(r => r.Id))}");
        }

        // 1. The hash, before anything else reads the bytes.
        Interlocked.Increment(ref _hashPasses);
        var (sha256, size) = await HashAsync(path, cancellationToken).ConfigureAwait(false);

        // 2. Read-only from here on, where the file is ours to keep.
        var relative = RelativeToCase(path, request.CaseId);
        if (relative is not null)
        {
            EvidencePermissions.MakeReadOnly(path);
        }

        // 3. What it is. The host comes first, because a host may carry default source options (its nginx
        // log format): a new file of that host is scored against them unless it brings its own.
        var name = request.OriginalName ?? Path.GetFileName(path);
        string? hostName = null;
        var inferred = false;
        string? rule = null;
        if (!string.IsNullOrWhiteSpace(request.Host))
        {
            hostName = HostOperations.Normalise(request.Host);
        }
        else if (_hosts.Infer(name) is { } proposal)
        {
            (hostName, inferred, rule) = (proposal.Host, true, proposal.Rule);
        }

        var detection = DetectionSample.DetectFile(path, name, _sources, await EffectiveOptionsAsync(request, hostName, cancellationToken).ConfigureAwait(false), out var content);
        var now = _store.Clock.GetUtcNow().UtcDateTime;
        var actor = new AuditActor(request.Uploader, request.UploaderUserId);
        if (detection.Kind == FileKind.Rejected && request.OverrideSource is null)
        {
            var refusal = new Dictionary<string, object?>
            {
                ["sha256"] = sha256,
                ["original_name"] = name,
                ["size"] = size,
                ["format"] = detection.Format,
                ["source"] = Names.Of(request.Source),
                ["message"] = detection.Message,
            };
            await RecordAsync(
                async db =>
                {
                    await FileOperations.RejectAsync(db, _store.Audit, actor, request.CaseId, refusal, cancellationToken).ConfigureAwait(false);
                    return true;
                },
                request.RecordWaiting,
                cancellationToken).ConfigureAwait(false);
            return new IntakeOutcome(null, null, Duplicate: false, Rejected: true, detection, sha256, size, null, HostInferred: false);
        }

        // 4. The hints.
        var fileTime = File.GetLastWriteTimeUtc(path);
        var year = IntakeHints.Year(request.Year, fileTime, content, now);

        // A container that cannot be opened here is kept like any other file that is not a log.
        var parsed = request.OverrideSource is not null || detection.Kind == FileKind.Log;
        var expanded = !parsed && detection.Kind == FileKind.Container && _limits.MaxDepth > 0;

        // A new entity for every attempt at the record: a failed one leaves nothing behind for the next.
        EvidenceFile NewFile() => new()
        {
            CaseId = request.CaseId,
            Sha256 = sha256,
            Size = size,
            OriginalName = name,
            StoredPath = relative ?? path,
            Source = request.Source,
            Uploader = request.Uploader,
            UploaderUserId = request.UploaderUserId,
            ReceivedAt = now,
            Timezone = request.Timezone,
            YearHint = year.Year,
            YearAssumed = year.Assumed,
            YearGiven = request.Year is not null,
            HostInferred = inferred,
            HostRule = rule,
            Encoding = Names.Of(detection.Encoding),
            Compression = detection.Compression == CompressionKind.None ? null : Names.Of(detection.Compression),
            DetectedKind = detection.Kind,
            DetectedSource = detection.SourceId,
            DetectedConfidence = detection.Confidence,
            DetectedLayout = detection.Layout,
            DetectedFormat = detection.Format,
            DetectedControlBytes = detection.Kind == Urlog.Core.Detection.FileKind.Log && detection.Flags.Contains(Urlog.Core.Detection.FormatDetector.FlagBinaryBytes),
            Detection = detection.ToJson(),
            DetectionRegistry = detection.RegistryKey,
            FileTime = fileTime,
            OverrideSource = request.OverrideSource,
            OverrideOptions = request.Options is { Count: > 0 } ? JsonSerializer.Serialize(request.Options) : null,
            State = parsed || expanded ? FileState.Received : FileState.Artifact,
        };

        // 5. The record, its audit row and its job, together.
        var result = await RecordAsync(
            async db =>
            {
                var file = NewFile();
                if (hostName is not null)
                {
                    file.HostId = (await HostOperations.GetOrCreateAsync(db, request.CaseId, hostName, now, cancellationToken).ConfigureAwait(false)).Id;
                }

                return await FileOperations.AddAsync(db, _store.Audit, actor, file, queue: true, hostName, cancellationToken).ConfigureAwait(false);
            },
            request.RecordWaiting,
            cancellationToken).ConfigureAwait(false);
        return new IntakeOutcome(result.File, result.Job, result.Duplicate, Rejected: false, detection, sha256, size, hostName, inferred);
    }

    /// <summary>
    /// The options detection reads: the request's own, over the defaults of the host the file goes to when that
    /// host exists already. The record keeps the request's own options only; a parse merges the host's again.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> EffectiveOptionsAsync(IntakeRequest request, string? hostName, CancellationToken cancellationToken)
    {
        if (hostName is null)
        {
            return request.Options;
        }

        await using var db = _store.CreateDbContext();
        var host = await HostOperations.FindLiveAsync(db, request.CaseId, hostName, cancellationToken).ConfigureAwait(false);
        if (host?.SourceOptions is not { Length: > 0 } defaults)
        {
            return request.Options;
        }

        var merged = SourceOptionSet.Effective(request.Options is { Count: > 0 } ? JsonSerializer.Serialize(request.Options) : null, defaults);
        return merged.Count == 0 ? null : merged;
    }

    /// <summary>One attempt is one context, so one connection and one transaction of its own.</summary>
    private Task<T> RecordAsync<T>(Func<MetadataDb, Task<T>> write, Action? waiting, CancellationToken cancellationToken)
    {
        async Task<T> AttemptAsync()
        {
            await using var db = _store.CreateDbContext();
            return await write(db).ConfigureAwait(false);
        }

        return _recordRetry is null ? AttemptAsync() : _recordRetry.RunAsync(AttemptAsync, waiting, cancellationToken);
    }

    /// <summary>SHA-256 of the bytes as they are on disk, streamed (evidence rule 1): 64 lowercase hex characters, and the byte count of the same read.</summary>
    public static async Task<(string Sha256, long Size)> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var raw = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        long size = 0;
        int n;
        while ((n = await raw.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            size += n;
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), size);
    }

    /// <summary>The path below <c>&lt;evidence root&gt;/&lt;case id&gt;/</c>, or null when the file lies elsewhere.</summary>
    private string? RelativeToCase(string path, long caseId)
    {
        if (_evidenceRoot is null)
        {
            return null;
        }

        var directory = Path.Combine(_evidenceRoot, caseId.ToString(CultureInfo.InvariantCulture)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(directory, comparison) ? path[directory.Length..] : null;
    }
}
