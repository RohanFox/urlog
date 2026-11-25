using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Urlog.Query;

namespace Urlog.Api.Search;

/// <summary>
/// Where the search routes run their statements: the event store as the query account, or a test's stand-in.
/// Every call names the one case of its route, which is the whole allowed-case list of the request.
/// </summary>
public interface ISearchBackend
{
    /// <summary>One statement of a search; reading stops after <paramref name="maxRows"/> rows.</summary>
    Task<QueryRows> RunAsync(SearchStatement statement, ulong caseId, int maxRows, CancellationToken cancellationToken);

    /// <summary>An aggregation as the transpiler wrote it, its cells as text.</summary>
    Task<QueryTable> RunAsync(TranspileResult query, ulong caseId, CancellationToken cancellationToken);

    /// <summary>The raw-SQL tab's text; reading stops after <paramref name="maxRows"/> rows.</summary>
    Task<QueryRows> RunSqlAsync(string sql, ulong caseId, int maxRows, CancellationToken cancellationToken);

    /// <summary>A statement, or when <paramref name="statement"/> is null the raw text <paramref name="sql"/>, a row at a time.</summary>
    Task<long> StreamAsync(SearchStatement? statement, string? sql, ulong caseId, Action<IReadOnlyList<string>> onColumns, Func<object?[], ValueTask<bool>> onRow, CancellationToken cancellationToken);
}

/// <summary>The event store through <see cref="ClickHouseQueryClient"/>: gated, as <c>urlog_query</c>, the route's case alone allowed.</summary>
public sealed class QuerySearchBackend(ClickHouseQueryClient client) : ISearchBackend
{
    public Task<QueryRows> RunAsync(SearchStatement statement, ulong caseId, int maxRows, CancellationToken cancellationToken) =>
        client.RunAsync(statement, [caseId], maxRows, cancellationToken);

    public Task<QueryTable> RunAsync(TranspileResult query, ulong caseId, CancellationToken cancellationToken) =>
        client.RunAsync(query, [caseId], cancellationToken);

    public Task<QueryRows> RunSqlAsync(string sql, ulong caseId, int maxRows, CancellationToken cancellationToken) =>
        client.RunSqlAsync(sql, [caseId], maxRows, cancellationToken);

    public Task<long> StreamAsync(SearchStatement? statement, string? sql, ulong caseId, Action<IReadOnlyList<string>> onColumns, Func<object?[], ValueTask<bool>> onRow, CancellationToken cancellationToken) =>
        statement is not null
            ? client.StreamAsync(statement, [caseId], onColumns, onRow, cancellationToken)
            : client.StreamSqlAsync(sql ?? throw new ArgumentNullException(nameof(sql)), [caseId], onColumns, onRow, cancellationToken);
}

/// <summary>One bucket of a histogram: its start and the events in it by severity.</summary>
public sealed record HistogramBucket(DateTimeOffset Start, IReadOnlyDictionary<string, long> Counts);

/// <summary>
/// The histogram kept for the timeline of a case (design record, "Query language", The search API), so the first
/// screen of a large case does not wait for a read of every row. Asked only for a search that narrows nothing,
/// over a case whose every parsed file has its stored minutes; null means there is none for this request and the
/// search reads the histogram from the events.
/// </summary>
public interface IStoredHistogram
{
    ValueTask<IReadOnlyList<HistogramBucket>?> FindAsync(SearchPlan plan, ulong caseId, HistogramSpan span, CancellationToken cancellationToken);
}

/// <summary>No stored histogram: every histogram is read from the events.</summary>
public sealed class NoStoredHistogram : IStoredHistogram
{
    public ValueTask<IReadOnlyList<HistogramBucket>?> FindAsync(SearchPlan plan, ulong caseId, HistogramSpan span, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<HistogramBucket>?>(null);
}

/// <summary>
/// The stored histogram through the search backend: as the query account, the route's case alone allowed, read
/// through <c>case_histogram_v</c>. A read that fails is logged and answered with null, so the search falls back
/// to the events and the analyst gets a histogram either way.
/// </summary>
public sealed partial class StoredHistogram(ISearchBackend backend, ILogger<StoredHistogram> logger) : IStoredHistogram
{
    public async ValueTask<IReadOnlyList<HistogramBucket>?> FindAsync(SearchPlan plan, ulong caseId, HistogramSpan span, CancellationToken cancellationToken)
    {
        try
        {
            return await new SearchRunner(backend, caseId).StoredHistogramAsync(plan, span, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Unavailable(logger, caseId, e.Message);
            return null;
        }
    }

    [LoggerMessage(EventId = 310, Level = LogLevel.Warning, Message = "the stored histogram of case {CaseId} could not be read, the events are read instead: {Reason}")]
    private static partial void Unavailable(ILogger logger, ulong caseId, string reason);
}

/// <summary>How a request that carried a query text ended.</summary>
public static class QueryOutcome
{
    public const string Ok = "ok";
    public const string Invalid = "invalid";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Abandoned = "abandoned";
    public const string Busy = "busy";
}

/// <summary>
/// The audit row of a query (design record, "Security", Audit log): one for every request that carries a query
/// text, written when the request ends, whatever its end was. It holds the text, the SHA-256 of the text's UTF-8
/// bytes, the case, the range and the zone, the outcome with its reason, the duration and the number of rows
/// sent. An export's row also says what left the server: the bytes, their SHA-256, whether the file was cut at
/// the row limit and whether it was sent whole. It never holds a result: no cell, no value, no bucket. The row is written with no cancellation, so a
/// client that left is recorded as having left.
/// </summary>
public sealed class QueryAudit(MetadataStore store)
{
    public async Task WriteAsync(AuditActor actor, CaseRecord @case, QueryRecord record)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(@case);
        ArgumentNullException.ThrowIfNull(record);
        var details = actor.Stamp(new Dictionary<string, object?>
        {
            ["case_name"] = @case.Name,
            ["language"] = record.Language,
            ["request"] = record.Request,
            ["text"] = record.Text,
            ["sha256"] = Sha256(record.Text),
            ["from"] = record.From?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["to"] = record.To?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["tz"] = record.Zone,
            ["first_row"] = record.FirstRow,
            ["outcome"] = record.Outcome,
            ["reason"] = record.Reason,
            ["duration_ms"] = record.DurationMs,
            ["rows"] = record.Rows,
            ["bytes"] = record.Bytes,
            ["sent_sha256"] = record.SentSha256,
            ["cut"] = record.Cut,
            ["complete"] = record.Complete,
        });
        // Members only some requests have are written only for those, so every older row keeps its shape.
        if (record.FileId is { } fileId)
        {
            details["file_id"] = fileId;
            details["file_offset"] = record.FileOffset;
        }

        if (record.Columns is { } columns)
        {
            details["columns"] = columns;
        }

        await using var db = store.CreateDbContext();
        await using var transaction = await AuditChain.BeginTransactionAsync(db, CancellationToken.None).ConfigureAwait(false);
        await store.Audit.AppendAsync(db, new AuditEntry(actor.Name, actor.UserId, AuditActions.Query, AuditTargets.Query, @case.Id, details), CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public static string Sha256(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

/// <summary>What the audit row of one query request says.</summary>
/// <param name="Language"><c>spl</c>, <c>sql</c>, or <c>bytes</c> for the bytes of one line.</param>
/// <param name="Request"><c>search</c>, <c>page</c>, <c>export</c>, <c>export_files</c> or <c>raw</c>.</param>
/// <param name="Text">The query as the caller sent it.</param>
public sealed record QueryRecord(string Language, string Request, string Text)
{
    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }

    public string? Zone { get; set; }

    /// <summary>For a page: how many rows of the listing came before it.</summary>
    public long? FirstRow { get; set; }

    public string Outcome { get; set; } = QueryOutcome.Ok;

    public string? Reason { get; set; }

    public long DurationMs { get; set; }

    /// <summary>Rows sent to the caller. A count, never a value.</summary>
    public long Rows { get; set; }

    /// <summary>For an export: the bytes of the file as far as it was written.</summary>
    public long? Bytes { get; set; }

    /// <summary>For an export: the SHA-256 of those bytes, so a file in someone's hands can be matched to its row.</summary>
    public string? SentSha256 { get; set; }

    /// <summary>For an export: true when the result had more rows than the export limit and the file stops there.</summary>
    public bool? Cut { get; set; }

    /// <summary>For an export: false when the file stopped before its end (the client left, or the read failed).</summary>
    public bool? Complete { get; set; }

    /// <summary>For the bytes of one line: the file of the event.</summary>
    public long? FileId { get; set; }

    /// <summary>For the bytes of one line: the offset of the event in its file.</summary>
    public long? FileOffset { get; set; }

    /// <summary>For an export of a listing: which columns it carried (<c>evidence</c>, <c>brief</c>, or <c>table</c> for the query's own).</summary>
    public string? Columns { get; set; }
}
