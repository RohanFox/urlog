using System.Globalization;
using Urlog.Core.Audit;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Intake;

/// <summary>Which of the gate's rules refused the bytes, for a caller that answers each differently.</summary>
public enum IntakeGateRefusal
{
    None,

    /// <summary>The case does not exist or is not active.</summary>
    Case,

    /// <summary>The ingest queue is held by the disk watermark.</summary>
    Paused,

    /// <summary>The intake volume would fall under its mark.</summary>
    Volume,
}

/// <summary>The gate's answer: bytes may come, or they may not and why.</summary>
public sealed record IntakeGateAnswer(bool Accepted, string? Reason, IntakeGateRefusal Refusal = IntakeGateRefusal.None)
{
    public static IntakeGateAnswer Yes { get; } = new(true, null);
}

/// <summary>
/// Asked before new bytes are taken, by a caller that can still say no (an upload that has not started). A file
/// that is already on disk is never turned away here: it is hashed and recorded, and its job waits.
/// <para>
/// Bytes are refused when the case is not active, when the ingest queue is held by the disk watermark (the newest
/// of <c>ingest_paused</c> and <c>ingest_resumed</c> in the audit chain says so, which a process without a
/// connection to the event store can read), and when the intake volume, less the bytes that are about to come,
/// would be under its own mark: the larger of the absolute floor and the share of the volume.
/// </para>
/// </summary>
public sealed class IntakeGate(IDiskProbe disk, WatermarkOptions watermark)
{
    public async Task<IntakeGateAnswer> CheckAsync(MetadataDb db, long caseId, string intakeDirectory, long incomingBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(intakeDirectory);
        var state = await db.Cases.AsNoTracking().Where(c => c.Id == caseId).Select(c => (CaseState?)c.State).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new IntakeGateAnswer(false, $"case {caseId} does not exist", IntakeGateRefusal.Case);
        }

        if (state != CaseState.Active)
        {
            return new IntakeGateAnswer(false, $"case {caseId} is {Names.Of(state.Value)}; only an active case takes files", IntakeGateRefusal.Case);
        }

        var newest = await db.Audit.AsNoTracking()
            .Where(a => a.Action == AuditActions.IngestPaused || a.Action == AuditActions.IngestResumed)
            .OrderByDescending(a => a.Seq).Select(a => a.Action).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (newest == AuditActions.IngestPaused)
        {
            return new IntakeGateAnswer(false, "ingest is paused by the disk watermark; new files are taken again when space is freed", IntakeGateRefusal.Paused);
        }

        var volume = disk.Measure(intakeDirectory);
        var mark = Math.Max(watermark.MinFreeBytes, volume.TotalBytes / 100 * watermark.MinFreePercent);
        var after = volume.FreeBytes - Math.Max(0, incomingBytes);
        return after < mark
            ? new IntakeGateAnswer(false, string.Create(CultureInfo.InvariantCulture,
                $"{volume.Name} has {volume.FreeBytes:N0} bytes free; with the {incomingBytes:N0} that would come it is under the mark of {mark:N0}"), IntakeGateRefusal.Volume)
            : IntakeGateAnswer.Yes;
    }
}
