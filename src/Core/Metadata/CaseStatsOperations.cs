using Urlog.Core.Audit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>A case whose field statistics are due: what the runner needs to compute them and to record the result.</summary>
/// <param name="DirtySince">The mark the run starts from; the result clears the mark only if it is still this one.</param>
/// <param name="LastComputedAt">The <c>computed_at</c> of the set the event store holds; the new set's is later.</param>
/// <param name="Rows">Rows of the case's committed parses, from the file records.</param>
public sealed record StatsClaim(long CaseId, DateTime DirtySince, DateTime? LastComputedAt, long Rows);

/// <summary>The span of a case: the default range of a search, and the events that lie outside it.</summary>
public sealed record CaseSpan(DateTime? From, DateTime? To, long UndatedEvents, long FutureEvents)
{
    /// <summary>
    /// The default range of a search over the case, half-open: from the first event to one millisecond past the
    /// last. Null while the case has no event with a usable time.
    /// </summary>
    public (DateTimeOffset From, DateTimeOffset To)? DefaultRange =>
        From is { } from && To is { } to
            ? (new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)), new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)).AddMilliseconds(1))
            : null;
}

/// <summary>
/// The case's span and the state of its field statistics in SQLite. Both follow the commit of a parse: the span is
/// taken from the file records in the commit's own transaction, and the statistics are marked out of date there and
/// computed later by the job runner, once for however many files were committed in a row.
/// </summary>
public static class CaseStatsOperations
{
    private const int MaxMessageLength = 4000;

    /// <summary>
    /// Inside the transaction that commits a parse: the case's span is the least <c>ts_min</c> and the greatest
    /// <c>ts_max</c> of its files, recomputed and never only widened, since a re-parse can narrow it; the two
    /// counts are sums; the statistics are marked out of date as of <paramref name="nowUtc"/>.
    /// </summary>
    public static async Task OnCommitAsync(MetadataDb db, long caseId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var files = db.Files.Where(f => f.CaseId == caseId);
        var min = await files.Where(f => f.TsMin != null).OrderBy(f => f.TsMin).Select(f => f.TsMin).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var max = await files.Where(f => f.TsMax != null).OrderByDescending(f => f.TsMax).Select(f => f.TsMax).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var undated = await files.SumAsync(f => f.UndatedRows, cancellationToken).ConfigureAwait(false);
        var future = await files.SumAsync(f => f.FutureRows, cancellationToken).ConfigureAwait(false);
        await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(
            s => s.SetProperty(c => c.TsMin, min).SetProperty(c => c.TsMax, max).SetProperty(c => c.UndatedRows, undated).SetProperty(c => c.FutureRows, future),
            cancellationToken).ConfigureAwait(false);

        var updated = await db.CaseStats.Where(s => s.CaseId == caseId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DirtySince, nowUtc), cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            db.CaseStats.Add(new CaseStats { CaseId = caseId, DirtySince = nowUtc });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The recorded span of a case; null for an unknown case.</summary>
    public static async Task<CaseSpan?> SpanAsync(MetadataDb db, long caseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.Cases.AsNoTracking().Where(c => c.Id == caseId).Select(c => new { c.TsMin, c.TsMax, c.UndatedRows, c.FutureRows }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : new CaseSpan(row.TsMin, row.TsMax, row.UndatedRows, row.FutureRows);
    }

    /// <summary>
    /// The recorded span of a case, read from a metadata file without opening it for writing: for a command that
    /// only reads (<c>urlog query --db</c>). Null for a file or a case that is not there, or a file from before the
    /// span was recorded.
    /// </summary>
    public static async Task<CaseSpan?> ReadSpanAsync(string path, long caseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ts_min, ts_max, undated_rows, future_rows FROM cases WHERE id = $id";
            command.Parameters.AddWithValue("$id", caseId);
            try
            {
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new CaseSpan(Utc(reader, 0), Utc(reader, 1), reader.GetInt64(2), reader.GetInt64(3));
                }
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 1)
            {
                return null;
            }
        }

        static DateTime? Utc(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
    }

    /// <summary>The statistics state of a case; null when no parse of it was ever committed.</summary>
    public static Task<CaseStats?> GetAsync(MetadataDb db, long caseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.CaseStats.AsNoTracking().FirstOrDefaultAsync(s => s.CaseId == caseId, cancellationToken);
    }

    /// <summary>
    /// The case whose statistics are due, longest out of date first, or null. A case is due when it is marked, is
    /// <c>active</c> or <c>closed</c>, has no job running and none it could still run, has no lifecycle work under
    /// way, and its last commit is at least <paramref name="quiet"/> old. Read only: the runner is the one process
    /// that computes, and it runs one case at a time.
    /// </summary>
    public static async Task<StatsClaim?> NextDueAsync(MetadataDb db, DateTime nowUtc, TimeSpan quiet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var cutoff = nowUtc - quiet;
        var due = await (
            from s in db.CaseStats.AsNoTracking()
            join c in db.Cases.AsNoTracking() on s.CaseId equals c.Id
            where s.DirtySince != null && s.DirtySince <= cutoff
                && (c.State == CaseState.Active || c.State == CaseState.Closed)
                && !db.Jobs.Any(j => j.CaseId == c.Id && j.State == JobState.Parsing)
                // Queued jobs of a closed case are held and may wait for months; those of an active case run next.
                && !(c.State == CaseState.Active && db.Jobs.Any(j => j.CaseId == c.Id && j.State == JobState.Queued))
                && !db.CaseTasks.Any(t => t.CaseId == c.Id && (t.State == CaseTaskState.Requested || t.State == CaseTaskState.Running || t.State == CaseTaskState.Committed))
            orderby s.DirtySince, s.CaseId
            select new { s.CaseId, s.DirtySince, s.ComputedAt }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (due is null)
        {
            return null;
        }

        var rows = await db.Files.AsNoTracking().Where(f => f.CaseId == due.CaseId).SumAsync(f => f.RowCount, cancellationToken).ConfigureAwait(false);
        return new StatsClaim(due.CaseId, due.DirtySince!.Value, due.ComputedAt, rows);
    }

    /// <summary>
    /// Records how a run ended. <paramref name="computedAt"/> is the set the event store now holds for the case:
    /// the new one after <c>ok</c>, none after <c>skipped</c> (an older set was removed), unchanged (pass the
    /// claim's value) after a run that wrote nothing. The mark is cleared only if no parse was committed since the
    /// run began.
    /// </summary>
    public static async Task RecordAsync(MetadataDb db, StatsClaim claim, CaseStatsState state, string? message, DateTime? computedAt, long events, TimeSpan duration, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var row = await db.CaseStats.FirstOrDefaultAsync(s => s.CaseId == claim.CaseId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                // The case was purged while the run was under way.
                return;
            }

            await db.Entry(row).ReloadAsync(cancellationToken).ConfigureAwait(false);
            row.State = state;
            row.Message = message is { Length: > MaxMessageLength } ? message[..MaxMessageLength] : message;
            row.ComputedAt = computedAt;
            if (state == CaseStatsState.Ok)
            {
                row.Events = events;
            }

            row.DurationMs = (long)duration.TotalMilliseconds;
            row.FinishedAt = clock.GetUtcNow().UtcDateTime;
            if (row.DirtySince == claim.DirtySince)
            {
                row.DirtySince = null;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
