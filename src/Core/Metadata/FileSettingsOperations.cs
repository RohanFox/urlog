using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Detection;
using Urlog.Core.Intake;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>A member of an edit that is either left alone or set, to null included.</summary>
public readonly record struct Change<T>(bool IsSet, T Value);

/// <summary>Makes a <see cref="Change{T}"/> that is set.</summary>
public static class Change
{
    public static Change<T> To<T>(T value) => new(true, value);
}

/// <summary>What a change costs before it is made: the files that would be parsed again and their stored bytes.</summary>
public sealed record ReparseCost(int Files, long Bytes)
{
    public static ReparseCost None { get; } = new(0, 0);
}

/// <summary>A person's change to one file. <paramref name="ConfirmHost"/> accepts an inferred host as it is.</summary>
public sealed record FileEdit(Change<string?> Host, bool ConfirmHost, Change<string?> Timezone, Change<int?> Year);

/// <summary>What one file's change did: the record after it, the parse it queued, and whether anything changed.</summary>
public sealed record FileChange(EvidenceFile File, Job? Job, bool Changed, ReparseCost Cost);

/// <summary>Which files a bulk change is about. A walk through a large selection passes <paramref name="AfterId"/> and <paramref name="Paged"/>.</summary>
public sealed record FileSelection
{
    /// <summary>A path template over the archive path, or the original name of a file that arrived alone.</summary>
    public string? Pattern { get; init; }

    /// <summary>The extension without its dot, after compression and rotation suffixes are removed: <c>log</c> for <c>access.log.3.gz</c>.</summary>
    public string? Extension { get; init; }

    public string? DetectedSource { get; init; }

    public FileKind? DetectedKind { get; init; }

    public string? DetectedLayout { get; init; }

    public long? HostId { get; init; }

    public IReadOnlyList<long>? FileIds { get; init; }

    /// <summary>
    /// Only files a newer detector could send to a parser: no override, taken for <c>unparsed</c> or sent to the
    /// fallback source, with a detection record made by another detector version or source list.
    /// </summary>
    public bool StaleDetection { get; init; }

    public long AfterId { get; init; }

    public int Limit { get; init; } = FileSettingsOperations.BulkLimit;

    /// <summary>True when the caller walks the selection: a page is taken instead of the call being refused for size.</summary>
    public bool Paged { get; init; }
}

/// <summary>
/// The answer of a bulk change. Without the confirmation nothing was changed and this is what it would do;
/// with it, what it did. <paramref name="Digest"/> names the selected files and their versions: the confirmation
/// repeats it, and a selection that has moved since is refused.
/// </summary>
public sealed record BulkOutcome(bool Applied, int Matched, string Digest, ReparseCost Cost, int Changed, int Jobs, IReadOnlyDictionary<string, int> Skipped, long? NextAfterId);

/// <summary>
/// The changes a person makes to files after intake (design record, "Data model" and "Format detection"): host,
/// zone, year, the override, and the same for many files at once. Each is one transaction that checks the case
/// and the version under the write lock, changes the records, queues the parse the change calls for and writes its
/// audit row. A change that alters what a parse writes raises the file's settings version; a parse that was
/// running under the older one is followed by another when it ends (<see cref="JobOperations"/>).
/// The detection record is never written here: an override is stored beside it.
/// </summary>
public static class FileSettingsOperations
{
    /// <summary>Files one bulk call may touch.</summary>
    public const int BulkLimit = 1000;

    /// <summary>Changes one file's host, zone or year and queues its re-parse when the rows would differ.</summary>
    /// <exception cref="StaleWriteException"><paramref name="expectedVersion"/> is not the file's.</exception>
    /// <exception cref="CaseFrozenException">The case is not active.</exception>
    public static async Task<FileChange> UpdateAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, SourceRules rules, long caseId, long fileId, int expectedVersion, FileEdit edit, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        Guard(db, audit, actor, rules, clock);
        ArgumentNullException.ThrowIfNull(edit);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var @case = await ActiveCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            var file = await LoadFileAsync(db, caseId, fileId, expectedVersion, cancellationToken).ConfigureAwait(false);
            var details = actor.Stamp(new Dictionary<string, object?>(StringComparer.Ordinal));
            var reparse = false;
            var changed = false;

            if (edit.Host.IsSet)
            {
                var from = file.HostId;
                Host? host = null;
                if (!string.IsNullOrWhiteSpace(edit.Host.Value))
                {
                    host = await HostOperations.ResolveAsync(db, audit, actor, @case, edit.Host.Value, nowUtc, cancellationToken).ConfigureAwait(false);
                }

                if (from != host?.Id)
                {
                    var fromName = from is { } id ? await db.Hosts.Where(h => h.Id == id).Select(h => h.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) : null;
                    details["host"] = FromTo(fromName, host?.Name);
                    details["host_was_inferred"] = file.HostInferred;
                    file.HostId = host?.Id;
                    file.HostInferred = false;
                    reparse = changed = true;
                }
                else if (file.HostInferred)
                {
                    file.HostInferred = false;
                    details["host_confirmed"] = host?.Name;
                    changed = true;
                }
            }
            else if (edit.ConfirmHost && file.HostInferred)
            {
                file.HostInferred = false;
                details["host_confirmed"] = file.HostId is { } id ? await db.Hosts.Where(h => h.Id == id).Select(h => h.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) : null;
                changed = true;
            }

            var readsZone = rules.ReadsZone(SourceRules.EffectiveSource(file));
            if (edit.Timezone.IsSet)
            {
                var zone = string.IsNullOrWhiteSpace(edit.Timezone.Value) ? null : edit.Timezone.Value.Trim();
                CheckZone(zone);
                if (!string.Equals(zone, file.Timezone, StringComparison.Ordinal))
                {
                    details["timezone"] = FromTo(file.Timezone, zone);
                    file.Timezone = zone;
                    changed = true;
                    reparse |= readsZone;
                }
            }

            if (edit.Year.IsSet)
            {
                var year = edit.Year.Value;
                if (year is { } y && (y < 1970 || y > nowUtc.Year + 1))
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"year: {y} is outside 1970 to {nowUtc.Year + 1}"));
                }

                if (year is null)
                {
                    throw new ArgumentException("year: a year is required; the one derived at intake cannot be derived again here");
                }

                if (year != file.YearHint || !file.YearGiven)
                {
                    details["year"] = new Dictionary<string, object?> { ["from"] = file.YearHint, ["to"] = year, ["was_given"] = file.YearGiven };
                    reparse |= readsZone && year != file.YearHint;
                    file.YearHint = year;
                    file.YearGiven = true;
                    file.YearAssumed = false;
                    changed = true;
                }
            }

            if (!changed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return new FileChange(file, null, Changed: false, ReparseCost.None);
            }

            var planner = await ReparsePlanner.LoadAsync(db, [file.Id], cancellationToken).ConfigureAwait(false);
            Job? job = null;
            if (reparse)
            {
                job = planner.Apply(db, file, nowUtc);
            }
            else
            {
                // Still a change a second writer must not overwrite unseen.
                file.SettingsVersion++;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Describe(details, planner.Cost, job);
            await audit.AppendAsync(db, AuditEntry.ForFile(actor.Name, actor.UserId, AuditActions.FileUpdate, file, @case.Name, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new FileChange(file, job, Changed: true, planner.Cost);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Sets or clears (<paramref name="source"/> and <paramref name="options"/> both null) the override of one file
    /// and queues its re-parse. The detection record stays as it is.
    /// </summary>
    /// <exception cref="SettingsRefusedException">The file is quarantined, unreadable or a container, or the override that is cleared is what makes it a log.</exception>
    public static async Task<FileChange> OverrideAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, SourceRules rules, long caseId, long fileId, int expectedVersion, string? source, IReadOnlyDictionary<string, string>? options, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        Guard(db, audit, actor, rules, clock);
        var (sourceId, optionsJson) = CheckOverride(rules, source, options);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var @case = await ActiveCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            var file = await LoadFileAsync(db, caseId, fileId, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (WhyNoOverride(file, sourceId) is { } reason)
            {
                throw new SettingsRefusedException($"file {fileId}: {reason}");
            }

            if (string.Equals(file.OverrideSource, sourceId, StringComparison.Ordinal) && string.Equals(file.OverrideOptions, optionsJson, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return new FileChange(file, null, Changed: false, ReparseCost.None);
            }

            var details = actor.Stamp(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["from"] = OverrideOf(file.OverrideSource, file.OverrideOptions),
                ["to"] = OverrideOf(sourceId, optionsJson),
                ["detected_kind"] = file.DetectedKind is { } kind ? Names.Of(kind) : null,
                ["detected_source"] = file.DetectedSource,
            });
            var planner = await ReparsePlanner.LoadAsync(db, [file.Id], cancellationToken).ConfigureAwait(false);
            SetOverride(file, sourceId, optionsJson);
            var job = planner.Apply(db, file, nowUtc);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Describe(details, planner.Cost, job);
            await audit.AppendAsync(db, AuditEntry.ForFile(actor.Name, actor.UserId, AuditActions.FileOverride, file, @case.Name, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new FileChange(file, job, Changed: true, planner.Cost);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Gives the selected files a host. Without <paramref name="confirm"/> nothing changes and the answer says what
    /// would: how many files, how many of them would be parsed again and their bytes, and the digest to confirm with.
    /// </summary>
    /// <exception cref="StaleWriteException">The selection is not the one <paramref name="expectedDigest"/> was taken from.</exception>
    /// <exception cref="SelectionTooLargeException">More than the limit, and the caller does not walk the selection.</exception>
    public static Task<BulkOutcome> AssignHostAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, SourceRules rules, long caseId, FileSelection selection, string hostName, bool confirm, string? expectedDigest, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        Guard(db, audit, actor, rules, clock);
        ArgumentNullException.ThrowIfNull(selection);
        var name = HostOperations.CheckName(hostName);
        if (string.IsNullOrWhiteSpace(selection.Pattern))
        {
            throw new ArgumentException("pattern: a path pattern is required");
        }

        return BulkAsync(db, audit, actor, caseId, selection, rules, confirm, expectedDigest, clock, AuditActions.FileBulkHost,
            new Dictionary<string, object?> { ["pattern"] = selection.Pattern, ["host"] = name },
            async (@case, nowUtc) =>
            {
                // Looked up for the answer, created only when the change is made.
                var host = confirm
                    ? await HostOperations.ResolveAsync(db, audit, actor, @case, name, nowUtc, cancellationToken).ConfigureAwait(false)
                    : await HostOperations.FindLiveAsync(db, caseId, name, cancellationToken).ConfigureAwait(false);
                return (file, planner, apply) =>
                {
                    if (host is not null && file.HostId == host.Id)
                    {
                        if (!file.HostInferred)
                        {
                            return "unchanged";
                        }

                        if (apply)
                        {
                            file.HostInferred = false;
                            file.SettingsVersion++;
                        }

                        return "confirmed";
                    }

                    if (apply)
                    {
                        file.HostId = host!.Id;
                        file.HostInferred = false;
                        planner.Apply(db, file, nowUtc);
                    }
                    else
                    {
                        planner.Count(file);
                    }

                    return null;
                };
            },
            cancellationToken);
    }

    /// <summary>Sets the override of the selected files (or clears it, with both values null). The preview and the confirmation are as for <see cref="AssignHostAsync"/>.</summary>
    public static Task<BulkOutcome> BulkOverrideAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, SourceRules rules, long caseId, FileSelection selection, string? source, IReadOnlyDictionary<string, string>? options, bool confirm, string? expectedDigest, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        Guard(db, audit, actor, rules, clock);
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(selection.Extension) && string.IsNullOrWhiteSpace(selection.DetectedSource) && selection.DetectedKind is null)
        {
            throw new ArgumentException("match: an extension, a detected source or a detected kind is required");
        }

        var (sourceId, optionsJson) = CheckOverride(rules, source, options);
        var details = new Dictionary<string, object?>
        {
            ["match"] = MatchOf(selection),
            ["to"] = OverrideOf(sourceId, optionsJson),
        };
        return BulkAsync(db, audit, actor, caseId, selection, rules, confirm, expectedDigest, clock, AuditActions.FileBulkOverride, details,
            (_, nowUtc) => Task.FromResult<Func<EvidenceFile, ReparsePlanner, bool, string?>>((file, planner, apply) =>
            {
                if (WhyNoOverride(file, sourceId) is not null)
                {
                    return file.DetectedKind == FileKind.Container ? "container" : file.State is FileState.Received or FileState.Parsed or FileState.Failed or FileState.Artifact ? "not_a_log" : Names.Of(file.State);
                }

                if (string.Equals(file.OverrideSource, sourceId, StringComparison.Ordinal) && string.Equals(file.OverrideOptions, optionsJson, StringComparison.Ordinal))
                {
                    return "unchanged";
                }

                if (apply)
                {
                    SetOverride(file, sourceId, optionsJson);
                    planner.Apply(db, file, nowUtc);
                }
                else
                {
                    planner.Count(file, becomesLog: sourceId is not null);
                }

                return null;
            }),
            cancellationToken);
    }

    /// <summary>
    /// Queues the selected files to be detected again by the job runner, which reads their bytes. The candidates
    /// are files without an override that are <c>unparsed</c> or went to the fallback source. A file whose result
    /// does not change is not parsed again, so the cost is an upper bound.
    /// </summary>
    public static Task<BulkOutcome> RedetectAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, SourceRules rules, long caseId, FileSelection selection, bool confirm, string? expectedDigest, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        Guard(db, audit, actor, rules, clock);
        ArgumentNullException.ThrowIfNull(selection);
        if (!selection.StaleDetection && selection.FileIds is not { Count: > 0 })
        {
            throw new ArgumentException("stale or file_ids: say which files");
        }

        var details = new Dictionary<string, object?> { ["stale"] = selection.StaleDetection, ["registry"] = rules.RegistryKey };
        return BulkAsync(db, audit, actor, caseId, selection, rules, confirm, expectedDigest, clock, AuditActions.FileBulkRedetect, details,
            (_, nowUtc) => Task.FromResult<Func<EvidenceFile, ReparsePlanner, bool, string?>>((file, planner, apply) =>
            {
                if (file.OverrideSource is not null)
                {
                    return "has_override";
                }

                if (file.DetectedKind == FileKind.Container || file.State is FileState.Quarantined or FileState.Unreadable or FileState.Expanded or FileState.Archived)
                {
                    return file.DetectedKind == FileKind.Container ? "container" : Names.Of(file.State);
                }

                if (!(file.DetectedKind == FileKind.Unparsed
                    || (file.DetectedKind == FileKind.Log && string.Equals(file.DetectedSource, FormatDetector.GenericSourceId, StringComparison.Ordinal))
                    || (file.DetectedKind == FileKind.Artifact && string.Equals(file.DetectedFormat, FormatDetector.UnknownBinary, StringComparison.Ordinal))))
                {
                    return "has_a_parser";
                }

                if (planner.HasJob(file.Id))
                {
                    return "job_waiting";
                }

                if (apply)
                {
                    planner.Queue(db, file, nowUtc, JobOperations.RedetectOptions);
                }
                else
                {
                    planner.Count(file, becomesLog: true);
                }

                return null;
            }),
            cancellationToken);
    }

    /// <summary>The extension a selection compares: without the dot, lowercased, behind compression and rotation suffixes.</summary>
    public static string ExtensionOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var slash = name.LastIndexOfAny(['/', '\\', '!']);
        var rest = (slash >= 0 ? name[(slash + 1)..] : name).ToLowerInvariant();
        while (true)
        {
            var dot = rest.LastIndexOf('.');
            if (dot <= 0)
            {
                return string.Empty;
            }

            var last = rest[(dot + 1)..];
            if (last is "gz" or "zst" or "zstd" or "xz" or "bz2" || (last.Length > 0 && last.All(c => char.IsAsciiDigit(c) || c == '-' || c == '_')))
            {
                rest = rest[..dot];
                continue;
            }

            // A date that logrotate puts behind the extension: access.log-20261001.
            var cut = last.IndexOfAny(['-', '_']);
            return cut > 0 && last.AsSpan(cut + 1).IndexOfAnyExceptInRange('0', '9') < 0 ? last[..cut] : last;
        }
    }

    /// <summary>The files a selection takes from a case, in id order, as the listing and the bulk calls share it.</summary>
    public static Func<EvidenceFile, bool> Compile(FileSelection selection, SourceRules rules)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(rules);
        var path = string.IsNullOrWhiteSpace(selection.Pattern) ? null : HostInference.CompileSelector(selection.Pattern);
        var extension = string.IsNullOrWhiteSpace(selection.Extension) ? null : selection.Extension.Trim().TrimStart('.').ToLowerInvariant();
        var ids = selection.FileIds is { Count: > 0 } list ? list.ToHashSet() : null;
        return file =>
            (ids is null || ids.Contains(file.Id))
            && (path is null || path(file.ArchivePath ?? file.OriginalName))
            && (extension is null || ExtensionOf(file.OriginalName) == extension)
            && (selection.DetectedSource is null || string.Equals(file.DetectedSource, selection.DetectedSource, StringComparison.OrdinalIgnoreCase))
            && (selection.DetectedKind is null || file.DetectedKind == selection.DetectedKind)
            && (selection.DetectedLayout is null || string.Equals(file.DetectedLayout, selection.DetectedLayout, StringComparison.Ordinal))
            && (selection.HostId is null || file.HostId == selection.HostId)
            && (!selection.StaleDetection || CaseOverview.IsStale(file.DetectedKind, file.DetectedSource, file.DetectedFormat, file.OverrideSource, file.DetectionRegistry, rules.RegistryKey));
    }

    /// <summary>SHA-256 over one line per file, <c>id:settings_version</c>, in id order.</summary>
    public static string Digest(IEnumerable<EvidenceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{file.Id}:{file.SettingsVersion}\n")));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<BulkOutcome> BulkAsync(
        MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, FileSelection selection, SourceRules rules, bool confirm, string? expectedDigest, TimeProvider clock,
        string action, Dictionary<string, object?> details, Func<CaseRecord, DateTime, Task<Func<EvidenceFile, ReparsePlanner, bool, string?>>> prepare, CancellationToken cancellationToken)
    {
        var limit = selection.Limit;
        if (limit is < 1 or > BulkLimit)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"limit: 1 to {BulkLimit}"));
        }

        var matches = Compile(selection, rules);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var @case = await ActiveCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            var selected = new List<EvidenceFile>();
            var matched = 0;
            await foreach (var file in db.Files.Where(f => f.CaseId == caseId && f.Id > selection.AfterId).OrderBy(f => f.Id).AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!matches(file))
                {
                    db.Entry(file).State = EntityState.Detached;
                    continue;
                }

                matched++;
                if (selected.Count < limit)
                {
                    selected.Add(file);
                }
                else
                {
                    db.Entry(file).State = EntityState.Detached;
                }
            }

            if (matched > limit && !selection.Paged)
            {
                throw new SelectionTooLargeException(matched, limit);
            }

            var digest = Digest(selected);
            if (confirm && !string.Equals(expectedDigest, digest, StringComparison.Ordinal))
            {
                throw new StaleWriteException("the selection", expectedDigest ?? "none", digest);
            }

            var change = await prepare(@case, nowUtc).ConfigureAwait(false);
            var planner = await ReparsePlanner.LoadAsync(db, [.. selected.Select(f => f.Id)], cancellationToken).ConfigureAwait(false);
            var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var changedFiles = new List<EvidenceFile>();
            foreach (var file in selected)
            {
                if (change(file, planner, confirm) is { } reason)
                {
                    skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
                }
                else
                {
                    changedFiles.Add(file);
                }
            }

            long? next = matched > selected.Count && selected.Count > 0 ? selected[^1].Id : null;
            if (!confirm || (changedFiles.Count == 0 && !skipped.ContainsKey("confirmed")))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return new BulkOutcome(Applied: false, selected.Count, digest, planner.Cost, changedFiles.Count, 0, skipped, next);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            actor.Stamp(details);
            details["count"] = changedFiles.Count;
            details["selected"] = selected.Count;
            details["digest"] = digest;
            details["files"] = changedFiles.Select(f => (object?)new Dictionary<string, object?> { ["id"] = f.Id, ["sha256"] = f.Sha256 }).ToList();
            details["skipped"] = skipped.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
            details["reparse_files"] = planner.Cost.Files;
            details["reparse_bytes"] = planner.Cost.Bytes;
            details["jobs_queued"] = planner.Jobs.Count;
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, action, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BulkOutcome(Applied: true, selected.Count, digest, planner.Cost, changedFiles.Count, planner.Jobs.Count, skipped, next);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    internal static async Task<CaseRecord> ActiveCaseAsync(MetadataDb db, long caseId, CancellationToken cancellationToken)
    {
        var @case = await db.Cases.FindAsync([caseId], cancellationToken).ConfigureAwait(false) ?? throw new CaseNotFoundException(caseId);
        await db.Entry(@case).ReloadAsync(cancellationToken).ConfigureAwait(false);
        return @case.State == CaseState.Active ? @case : throw new CaseFrozenException(@case.Id, @case.State);
    }

    internal static void CheckZone(string? zone)
    {
        if (zone is not null && (zone.Length > 64 || !IntakeHints.IsKnownZone(zone)))
        {
            throw new ArgumentException($"timezone: unknown zone '{zone}'; use an IANA id, UTC, or an offset like +02:00");
        }
    }

    internal static Dictionary<string, object?> FromTo(object? from, object? to) => new(StringComparer.Ordinal) { ["from"] = from, ["to"] = to };

    internal static void Describe(Dictionary<string, object?> details, ReparseCost cost, Job? job)
    {
        details["reparse_files"] = cost.Files;
        details["reparse_bytes"] = cost.Bytes;
        details["job_id"] = job?.Id;
    }

    private static async Task<EvidenceFile> LoadFileAsync(MetadataDb db, long caseId, long fileId, int expectedVersion, CancellationToken cancellationToken)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId && f.CaseId == caseId, cancellationToken).ConfigureAwait(false) ?? throw new RecordNotFoundException("file", fileId, caseId);
        await db.Entry(file).ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (file.SettingsVersion != expectedVersion)
        {
            throw new StaleWriteException($"file {fileId}", expectedVersion.ToString(CultureInfo.InvariantCulture), file.SettingsVersion.ToString(CultureInfo.InvariantCulture));
        }

        return file;
    }

    private static (string? Source, string? OptionsJson) CheckOverride(SourceRules rules, string? source, IReadOnlyDictionary<string, string>? options)
    {
        string? sourceId = null;
        if (!string.IsNullOrWhiteSpace(source) && !rules.TryResolve(source, out sourceId!))
        {
            throw new ArgumentException($"source: no log source called '{source}'; known: {string.Join(", ", rules.Ids)}");
        }

        SourceOptionSet.Check(options, rules);
        return (sourceId, SourceOptionSet.Serialize(options));
    }

    /// <summary>Why this file takes no override, or null. A file the detector did not take for a log needs a source named.</summary>
    private static string? WhyNoOverride(EvidenceFile file, string? sourceId)
    {
        if (file.State is FileState.Quarantined or FileState.Unreadable or FileState.Expanded or FileState.Archived)
        {
            return $"it is {Names.Of(file.State)} and is never parsed";
        }

        if (file.DetectedKind == FileKind.Container)
        {
            return "it is an archive; its members are the files that are parsed";
        }

        if (sourceId is null && file.DetectedKind is not (FileKind.Log or null))
        {
            return $"the detector took it for {Names.Of(file.DetectedKind.Value)}, so it is parsed only by a source that is named; name one";
        }

        return null;
    }

    private static void SetOverride(EvidenceFile file, string? sourceId, string? optionsJson)
    {
        file.OverrideSource = sourceId;
        file.OverrideOptions = optionsJson;
        if (sourceId is not null && file.State == FileState.Artifact)
        {
            // Recorded and kept until now; from here a file that waits for its first parse.
            file.State = FileState.Received;
        }
    }

    private static Dictionary<string, object?> OverrideOf(string? source, string? optionsJson) => new(StringComparer.Ordinal)
    {
        ["source"] = source,
        ["options"] = optionsJson is null ? null : SourceOptionSet.Parse(optionsJson).ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal),
    };

    private static Dictionary<string, object?> MatchOf(FileSelection selection) => new(StringComparer.Ordinal)
    {
        ["extension"] = selection.Extension,
        ["detected_source"] = selection.DetectedSource,
        ["detected_kind"] = selection.DetectedKind is { } kind ? Names.Of(kind) : null,
        ["detected_layout"] = selection.DetectedLayout,
        ["host_id"] = selection.HostId,
        ["pattern"] = selection.Pattern,
    };

    private static void Guard(MetadataDb db, IAuditWriter audit, AuditActor actor, SourceRules rules, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(clock);
    }
}

/// <summary>
/// Decides, for files whose settings change inside one transaction, what parses again and queues it. A file with a
/// job waiting gets nothing: that job reads the settings when it is claimed. A file with a job running gets no
/// second job either; the running one is followed by another when it ends, because the file's settings version
/// has moved past the one it was claimed under. Everything else that can be parsed gets a job now.
/// </summary>
internal sealed class ReparsePlanner
{
    private readonly Dictionary<long, JobState> _active;
    private int _files;
    private long _bytes;

    private ReparsePlanner(Dictionary<long, JobState> active) => _active = active;

    public List<Job> Jobs { get; } = [];

    public ReparseCost Cost => new(_files, _bytes);

    public static async Task<ReparsePlanner> LoadAsync(MetadataDb db, IReadOnlyList<long> fileIds, CancellationToken cancellationToken)
    {
        var active = new Dictionary<long, JobState>();
        foreach (var chunk in fileIds.Chunk(500))
        {
            var rows = await db.Jobs.AsNoTracking().Where(j => chunk.Contains(j.FileId) && (j.State == JobState.Queued || j.State == JobState.Parsing)).Select(j => new { j.FileId, j.State }).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                // A running job decides over a waiting one: it is the one that reads old settings.
                if (!active.TryGetValue(row.FileId, out var known) || row.State == JobState.Parsing)
                {
                    active[row.FileId] = known == JobState.Parsing ? known : row.State;
                }
            }
        }

        return new ReparsePlanner(active);
    }

    public bool HasJob(long fileId) => _active.ContainsKey(fileId);

    public static bool Parseable(EvidenceFile file, bool becomesLog = false) =>
        (file.State is FileState.Received or FileState.Parsed or FileState.Failed || (becomesLog && file.State == FileState.Artifact))
        && !(file.DetectedKind == FileKind.Container && file.OverrideSource is null);

    /// <summary>Adds the file to the cost when the change would have it parsed again. Changes nothing.</summary>
    public void Count(EvidenceFile file, bool becomesLog = false)
    {
        if (Parseable(file, becomesLog) && !(_active.TryGetValue(file.Id, out var state) && state == JobState.Queued))
        {
            _files++;
            _bytes += file.Size;
        }
    }

    /// <summary>Raises the file's settings version and queues its parse where one is due. Returns the new job, if any.</summary>
    public Job? Apply(MetadataDb db, EvidenceFile file, DateTime nowUtc)
    {
        file.SettingsVersion++;
        if (!Parseable(file))
        {
            return null;
        }

        if (_active.TryGetValue(file.Id, out var state))
        {
            if (state == JobState.Parsing)
            {
                _files++;
                _bytes += file.Size;
            }

            return null;
        }

        return Queue(db, file, nowUtc, options: null);
    }

    /// <summary>Queues a parse of the file with the job options given, whatever its settings.</summary>
    public Job Queue(MetadataDb db, EvidenceFile file, DateTime nowUtc, string? options)
    {
        var job = new Job { CaseId = file.CaseId, FileId = file.Id, Kind = file.State == FileState.Parsed ? JobKind.Reparse : JobKind.Parse, CreatedAt = nowUtc, Options = options };
        db.Jobs.Add(job);
        _active[file.Id] = JobState.Queued;
        Jobs.Add(job);
        _files++;
        _bytes += file.Size;
        return job;
    }
}
