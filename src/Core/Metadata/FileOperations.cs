using System.Text.Json.Nodes;
using Urlog.Core.Audit;
using Urlog.Core.Detection;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>The outcome of <see cref="FileOperations.AddAsync"/>: the row, whether it already existed (evidence rule 3), and the job queued with it.</summary>
public sealed record FileIntakeResult(EvidenceFile File, bool Duplicate, Job? Job = null);

/// <summary>One archive member as the expansion found it: the row to write and the host its path suggests.</summary>
/// <param name="File">The member's record, without an id and without a host.</param>
/// <param name="Host">The host name to give it (inferred from its archive path, or its container's); null leaves it unassigned.</param>
public sealed record MemberDraft(EvidenceFile File, string? Host);

/// <summary>What one batch of members became.</summary>
public sealed record MemberBatchResult(int Recorded, int Duplicates, int Rejected, int ParseJobs, int ExpandJobs);

/// <summary>
/// The intake record (evidence rule 6) as a domain rule: every <c>files</c> insert needs its case in <c>active</c>, and
/// the same bytes in the same case are one row. Intake calls this after the hash exists and the bytes are in place.
/// Audited like <see cref="CaseOperations"/>: one BEGIN IMMEDIATE transaction holds the check, the insert, the
/// <c>file_intake</c> row and the queued job (or, for bytes the case already holds, the <c>file_duplicate</c> row
/// and the note on the surviving record of where else the bytes were seen).
/// </summary>
public static class FileOperations
{
    /// <summary>
    /// Adds <paramref name="file"/> unless the case already holds its hash, in which case the existing row comes back
    /// flagged <see cref="FileIntakeResult.Duplicate"/> and the second arrival is audited as <c>file_duplicate</c>. The
    /// transaction takes the write lock at BEGIN, so the active check and the insert cannot straddle a close.
    /// With <paramref name="queue"/> the job the file needs (a parse for a log, an expansion for a container) is
    /// written in the same transaction: a file is never recorded as received with nothing to act on it.
    /// </summary>
    /// <exception cref="CaseNotFoundException">No such case.</exception>
    /// <exception cref="CaseNotActiveException">The case is not <c>active</c>.</exception>
    public static Task<FileIntakeResult> AddAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, EvidenceFile file, CancellationToken cancellationToken = default) =>
        AddAsync(db, audit, actor, file, queue: false, arrivalHost: null, cancellationToken);

    /// <summary>
    /// As <see cref="AddAsync(MetadataDb, IAuditWriter, AuditActor, EvidenceFile, CancellationToken)"/>, and with
    /// <paramref name="queue"/> the job the file needs is written in the same transaction.
    /// </summary>
    /// <param name="arrivalHost">The host this arrival was given or inferred to belong to; kept when the bytes turn out to be a duplicate.</param>
    public static async Task<FileIntakeResult> AddAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, EvidenceFile file, bool queue, string? arrivalHost, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(file);
        if (!IsSha256(file.Sha256))
        {
            throw new ArgumentException("sha256 must be 64 lowercase hex characters", nameof(file));
        }

        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var @case = await ActiveCaseAsync(db, file.CaseId, cancellationToken).ConfigureAwait(false);
            var (stored, duplicate, job) = await AddOneAsync(db, audit, actor, @case, file, arrivalHost, queue, file.ReceivedAt, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new FileIntakeResult(stored, duplicate, job);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Records a batch of one container's members in one transaction: each member's row with its
    /// <c>file_intake</c> row and its job, or a <c>file_duplicate</c> row for bytes the case already holds, or a
    /// <c>file_rejected</c> row on the case for a member that is refused (a packet capture). The container's
    /// <c>expanded_members</c> becomes <paramref name="expandedThrough"/> in the same transaction, so an expansion
    /// that stops goes on behind the last whole batch and writes no row twice.
    /// </summary>
    public static async Task<MemberBatchResult> AddMembersAsync(MetadataDb db, IAuditWriter audit, long containerId, IReadOnlyList<MemberDraft> members, int expandedThrough, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(members);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var container = await db.Files.FirstAsync(f => f.Id == containerId, cancellationToken).ConfigureAwait(false);
            await db.Entry(container).ReloadAsync(cancellationToken).ConfigureAwait(false);
            var @case = await ActiveCaseAsync(db, container.CaseId, cancellationToken).ConfigureAwait(false);
            var actor = new AuditActor(container.Uploader, container.UploaderUserId);
            int recorded = 0, duplicates = 0, rejected = 0, parseJobs = 0, expandJobs = 0;
            foreach (var (file, hostName) in members)
            {
                if (!IsSha256(file.Sha256))
                {
                    throw new ArgumentException($"member '{file.ArchivePath}': sha256 must be 64 lowercase hex characters", nameof(members));
                }

                if (file.DetectedKind == FileKind.Rejected)
                {
                    var refusal = new Dictionary<string, object?>
                    {
                        ["sha256"] = file.Sha256,
                        ["original_name"] = file.OriginalName,
                        ["size"] = file.Size,
                        ["archive_path"] = file.ArchivePath,
                        ["container_file_id"] = container.Id,
                        ["detection"] = file.Detection,
                    };
                    await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.FileRejected, @case, refusal), cancellationToken).ConfigureAwait(false);
                    rejected++;
                    continue;
                }

                if (hostName is not null && file.HostId is null)
                {
                    file.HostId = (await HostOperations.GetOrCreateAsync(db, container.CaseId, hostName, nowUtc, cancellationToken).ConfigureAwait(false)).Id;
                }

                var (_, duplicate, job) = await AddOneAsync(db, audit, actor, @case, file, hostName, queue: true, nowUtc, cancellationToken).ConfigureAwait(false);
                duplicates += duplicate ? 1 : 0;
                recorded += duplicate ? 0 : 1;
                parseJobs += job?.Kind == JobKind.Parse ? 1 : 0;
                expandJobs += job?.Kind == JobKind.Expand ? 1 : 0;
            }

            container.ExpandedMembers = expandedThrough;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MemberBatchResult(recorded, duplicates, rejected, parseJobs, expandJobs);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// A file that is refused and not stored (a packet capture): the arrival is still on the case's trail, as
    /// <c>file_rejected</c> with the name, the size and the hash in <paramref name="details"/>.
    /// </summary>
    /// <exception cref="CaseNotFoundException">No such case.</exception>
    /// <exception cref="CaseNotActiveException">The case is not <c>active</c>.</exception>
    public static async Task RejectAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var @case = await ActiveCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.FileRejected, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>64 lowercase hex characters, the form <c>urlog ingest</c> prints and the intake record stores.</summary>
    public static bool IsSha256(string? text)
    {
        if (text is null || text.Length != 64)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The job a newly recorded file needs, or null: a parse for a log, an expansion for a container, nothing for what is only kept.</summary>
    public static JobKind? JobFor(EvidenceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.State != FileState.Received)
        {
            return null;
        }

        return file.DetectedKind == FileKind.Container && file.OverrideSource is null ? JobKind.Expand : JobKind.Parse;
    }

    private static async Task<CaseRecord> ActiveCaseAsync(MetadataDb db, long caseId, CancellationToken cancellationToken)
    {
        var @case = await db.Cases.FindAsync([caseId], cancellationToken).ConfigureAwait(false) ?? throw new CaseNotFoundException(caseId);
        await db.Entry(@case).ReloadAsync(cancellationToken).ConfigureAwait(false);
        return @case.State == CaseState.Active ? @case : throw new CaseNotActiveException(@case.Id, @case.State);
    }

    /// <summary>Inside the caller's transaction: the row with its audit row and job, or the duplicate's audit row and note.</summary>
    private static async Task<(EvidenceFile File, bool Duplicate, Job? Job)> AddOneAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, CaseRecord @case, EvidenceFile file, string? arrivalHost, bool queue, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var existing = await FindAsync(db, file.CaseId, file.Sha256, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.Files.Add(file);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // A provider without the immediate lock (Postgres) can lose the race on the unique index: take the winner's row.
                db.Entry(file).State = EntityState.Detached;
                existing = await FindAsync(db, file.CaseId, file.Sha256, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    throw;
                }
            }
        }

        if (existing is not null)
        {
            // The arrival is the event: where it came from is what the existing record does not already say. The
            // surviving record keeps the list of every other place its bytes were seen at.
            var path = file.ArchivePath ?? file.OriginalName;
            var seen = existing.SeenPaths is { Length: > 0 } json ? JsonNode.Parse(json)!.AsArray() : [];
            seen.Add(new JsonObject { ["path"] = path, ["host"] = arrivalHost, ["at"] = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", System.Globalization.CultureInfo.InvariantCulture) });
            existing.SeenPaths = seen.ToJsonString();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var arrival = new Dictionary<string, object?>
            {
                ["arrival_name"] = file.OriginalName,
                ["arrival_source"] = Names.Of(file.Source),
                ["arrival_archive_path"] = file.ArchivePath,
                ["arrival_host"] = arrivalHost,
            };
            await audit.AppendAsync(db, AuditEntry.ForFile(actor.Name, actor.UserId, AuditActions.FileDuplicate, existing, @case.Name, arrival), cancellationToken).ConfigureAwait(false);
            return (existing, true, null);
        }

        var details = new Dictionary<string, object?> { ["source"] = Names.Of(file.Source), ["archive_path"] = file.ArchivePath };
        if (file.UnreadableReason is { } reason)
        {
            // The hash in this row is of the stored bytes, not of content: the row says so and why.
            details["unreadable"] = reason;
            details["hash_of"] = "stored_bytes";
            details["stated_size"] = file.StatedSize;
        }

        await audit.AppendAsync(db, AuditEntry.ForFile(actor.Name, actor.UserId, AuditActions.FileIntake, file, @case.Name, details), cancellationToken).ConfigureAwait(false);
        if (file.InputTruncated)
        {
            // A member its container ends inside: known to be cut before anything parses it.
            var cut = new Dictionary<string, object?> { ["stored_offset"] = file.TruncatedStoredOffset, ["archive_path"] = file.ArchivePath, ["cut_by"] = "container" };
            await audit.AppendAsync(db, AuditEntry.ForFile(AuditActions.SystemActor, null, AuditActions.InputTruncated, file, @case.Name, cut), cancellationToken).ConfigureAwait(false);
        }

        Job? job = null;
        if (queue && JobFor(file) is { } kind)
        {
            job = new Job { CaseId = file.CaseId, FileId = file.Id, Kind = kind, CreatedAt = nowUtc };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return (file, false, job);
    }

    private static Task<EvidenceFile?> FindAsync(MetadataDb db, long caseId, string sha256, CancellationToken cancellationToken) =>
        db.Files.FirstOrDefaultAsync(f => f.CaseId == caseId && f.Sha256 == sha256, cancellationToken);
}
