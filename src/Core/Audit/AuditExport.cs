using System.Buffers;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Audit;

/// <summary>
/// The per-case audit trail as JSONL, for the archive manifest. One line per row, the line being the canonical JSON
/// of the whole row with <c>hash</c> included (eleven keys), so a line verifies with the same rule as a table row: drop
/// <c>hash</c>, serialise canonically, prepend <c>prev_hash</c>, SHA-256. Rows selected: target <c>case</c> or <c>query</c>
/// with the case id, and target <c>file</c> with any of the case's file ids, from the <b>latest</b> <c>case_create</c> row
/// of that id onward (a reused SQLite id starts a new trail). No header.
/// </summary>
public static class AuditExport
{
    /// <summary>Writes the trail of <paramref name="caseId"/> to <paramref name="output"/>; returns the number of lines.</summary>
    public static async Task<long> WriteCaseAsync(MetadataDb db, long caseId, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(output);
        var start = await db.Audit.AsNoTracking()
            .Where(a => a.TargetType == AuditTargets.Case && a.TargetId == caseId && a.Action == AuditActions.CaseCreate)
            .OrderByDescending(a => a.Seq)
            .Select(a => (long?)a.Seq)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 1;
        var fileIds = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId).Select(f => (long?)f.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rows = db.Audit.AsNoTracking()
            .Where(a => a.Seq >= start && (
                ((a.TargetType == AuditTargets.Case || a.TargetType == AuditTargets.Query) && a.TargetId == caseId)
                || (a.TargetType == AuditTargets.File && fileIds.Contains(a.TargetId))))
            .OrderBy(a => a.Seq)
            .AsAsyncEnumerable();

        var count = 0L;
        var buffer = new ArrayBufferWriter<byte>(1024);
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            buffer.ResetWrittenCount();
            WriteLine(row, buffer);
            await output.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            count++;
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>One JSONL line: the canonical JSON of the full row, then <c>\n</c>.</summary>
    public static void WriteLine(AuditRow row, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(output);
        var members = new Dictionary<string, object?>(11, StringComparer.Ordinal)
        {
            ["seq"] = row.Seq,
            ["ts_utc"] = row.TsUtc,
            ["actor"] = row.Actor,
            ["actor_id"] = row.ActorId,
            ["action"] = row.Action,
            ["target_type"] = row.TargetType,
            ["target_id"] = row.TargetId,
            ["details_json"] = row.DetailsJson,
            ["prev_hash"] = row.PrevHash,
            ["hash"] = row.Hash,
            ["schema_version"] = row.SchemaVersion,
        };
        CanonicalJson.Write(members, output);
        output.Write("\n"u8);
    }
}
