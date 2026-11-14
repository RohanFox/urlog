using System.Globalization;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Metadata;

namespace Urlog.Core.Ingest;

/// <summary>
/// Control-plane operations on the event store over the driver's TCP client: the re-parse protocol's delete and
/// generation commit, and the purge's partition drop. Never the insert data plane (that is <see cref="NativeInserter"/>).
/// <para>
/// Re-parse order (design record, "Consistency"): every attempt writes its own generation. Rows left by earlier
/// attempts go first (<see cref="DeleteFileGenerationsExceptAsync"/>, which keeps the committed generation, so the old
/// parse stays visible), then the insert, then <c>current</c> in SQLite, then <see cref="CommitFileGenerationAsync"/>,
/// then the generations below the new one (<see cref="DeleteFileGenerationsBelowAsync"/>). The lightweight delete is
/// a mask; what decides visibility is the generation filter in <c>events_v</c>, a set the view builds once per
/// query from <c>file_generation</c>: a commit is visible to every query that starts after the insert here returns,
/// and a query already running keeps the state it started with. A file with no committed generation shows nothing.
/// </para>
/// </summary>
public sealed class EventStoreAdmin(ClickHouseTcpClient client)
{
    /// <summary>Lightweight delete of every row of a file (all generations). Idempotent; the mutation applies in the background.</summary>
    public ValueTask DeleteFileRowsAsync(ulong fileId, CancellationToken cancellationToken = default) =>
        client.ExecuteAsync($"DELETE FROM events WHERE file_id = {fileId.ToString(CultureInfo.InvariantCulture)}", cancellationToken: cancellationToken);

    /// <summary>
    /// Lightweight delete of the file's rows of every generation but <paramref name="keep"/> (0 keeps none): what
    /// crashed or failed attempts left behind, never the committed parse. The case predicate prunes to the case's
    /// partitions. Idempotent.
    /// </summary>
    public ValueTask DeleteFileGenerationsExceptAsync(ulong caseId, ulong fileId, uint keep, CancellationToken cancellationToken = default) =>
        client.ExecuteAsync($"DELETE FROM events WHERE case_id = {Text(caseId)} AND file_id = {Text(fileId)} AND parse_generation != {Text(keep)}", cancellationToken: cancellationToken);

    /// <summary>Lightweight delete of the file's generations below <paramref name="generation"/>, after that one was committed. Idempotent.</summary>
    public ValueTask DeleteFileGenerationsBelowAsync(ulong caseId, ulong fileId, uint generation, CancellationToken cancellationToken = default) =>
        client.ExecuteAsync($"DELETE FROM events WHERE case_id = {Text(caseId)} AND file_id = {Text(fileId)} AND parse_generation < {Text(generation)}", cancellationToken: cancellationToken);

    /// <summary>Records that <paramref name="generation"/> is the visible parse of the file. Visible to the next query.</summary>
    public ValueTask CommitFileGenerationAsync(ulong fileId, uint generation, CancellationToken cancellationToken = default) =>
        CommitFileGenerationsAsync([new(fileId, generation)], cancellationToken);

    /// <summary>Several commits in one insert (the startup sync from SQLite).</summary>
    public async ValueTask CommitFileGenerationsAsync(IReadOnlyList<KeyValuePair<ulong, uint>> generations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generations);
        if (generations.Count == 0)
        {
            return;
        }

        var now = ClickHouseMigrator.UtcNowMilliseconds();
        var ids = ClickHouseTcpColumn.Create("file_id", generations.Select(g => g.Key).ToArray());
        var current = ClickHouseTcpColumn.Create("current", generations.Select(g => g.Value).ToArray());
        var times = ClickHouseTcpColumn.Create("updated_at", Enumerable.Repeat(now, generations.Count).ToArray());
        await client.InsertAsync("INSERT INTO file_generation (file_id, current, updated_at) VALUES", [ids, current, times], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The visible generation the store holds for one file id, 0 when it has no row. The id may have belonged to
    /// another file (SQLite reuses the highest id after a delete), or the store may be ahead of a restored metadata
    /// file: the job runner allocates every attempt above this value.
    /// </summary>
    public async Task<uint> ReadFileGenerationAsync(ulong fileId, CancellationToken cancellationToken = default)
    {
        await foreach (var row in client.QueryAsync($"SELECT argMax(current, updated_at) FROM file_generation WHERE file_id = {Text(fileId)}", cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return Convert.ToUInt32(row[0], CultureInfo.InvariantCulture);
        }

        return 0;
    }

    /// <summary>The visible generation of every file ClickHouse has a row for (the newest row per file).</summary>
    public async Task<Dictionary<ulong, uint>> ReadFileGenerationsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<ulong, uint>();
        await foreach (var row in client.QueryAsync("SELECT file_id, argMax(current, updated_at) FROM file_generation GROUP BY file_id", cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            result[Convert.ToUInt64(row[0], CultureInfo.InvariantCulture)] = Convert.ToUInt32(row[1], CultureInfo.InvariantCulture);
        }

        return result;
    }

    /// <summary>
    /// The two tables the write path needs answer this account. The ingest account cannot read
    /// <c>schema_migrations</c>, so this says "missing" or "not granted", not "out of date": a schema older than the
    /// binary fails in the insert with the server's own message.
    /// </summary>
    /// <exception cref="EventStoreSchemaException">No schema, or the account lacks the write path's grants.</exception>
    public async Task RequireSchemaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await client.ExecuteScalarAsync(CaseScopedEventsQuery.Build(1, "count()", tail: "LIMIT 0"), cancellationToken: cancellationToken).ConfigureAwait(false);
            await client.ExecuteScalarAsync("SELECT count() FROM file_generation LIMIT 0", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ClickHouseTcpServerException e) when (e.RawCode is 60 or 81)
        {
            throw new EventStoreSchemaException($"the event store has no schema (server code {e.RawCode}); run 'urlog migrate' as the admin account first", e);
        }
        catch (ClickHouseTcpServerException e) when (e.RawCode == 497)
        {
            throw new EventStoreSchemaException("the account lacks the write path's grants (server code 497); the write path runs as urlog_ingest (deploy/clickhouse/users.d/urlog.xml)", e);
        }
    }

    /// <summary>
    /// The identity this event store carries, or null when it has none yet. The ingest account may read it and
    /// nobody but the admin account may write it.
    /// </summary>
    /// <exception cref="EventStoreSchemaException">The schema is older than the identity table, or the account lacks the grant.</exception>
    /// <exception cref="StoreIdentityException">The table holds more than one row.</exception>
    public async Task<string?> ReadStoreIdentityAsync(CancellationToken cancellationToken = default)
    {
        var ids = new List<string>(2);
        try
        {
            await foreach (var row in client.QueryAsync("SELECT store_id FROM store_identity LIMIT 2", cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                ids.Add((string)row[0]!);
            }
        }
        catch (ClickHouseTcpServerException e) when (e.RawCode is 60 or 81)
        {
            throw new EventStoreSchemaException($"the event store's schema has no store_identity table (server code {e.RawCode}); run 'urlog migrate' as the admin account first", e);
        }
        catch (ClickHouseTcpServerException e) when (e.RawCode == 497)
        {
            throw new EventStoreSchemaException("the account may not read store_identity (server code 497); the ingest account needs GRANT SELECT ON default.store_identity (deploy/clickhouse/users.d/urlog.xml)", e);
        }

        return ids.Count switch
        {
            0 => null,
            1 => ids[0],
            _ => throw new StoreIdentityException("the event store's store_identity table holds more than one row; a store has one identity, and this one has to be repaired by hand before anything writes to it"),
        };
    }

    /// <summary>Writes the store's identity. The admin account only, and only into a store that has none.</summary>
    public async ValueTask WriteStoreIdentityAsync(string storeId, string createdBy, CancellationToken cancellationToken = default)
    {
        if (!StoreIdentity.IsValid(storeId))
        {
            throw new ArgumentException("a store identity is 32 lowercase hex characters", nameof(storeId));
        }

        if (await ReadStoreIdentityAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            throw new StoreIdentityException($"the event store already has store identity {existing}; it is never replaced", null, existing);
        }

        var ids = ClickHouseTcpColumn.Create("store_id", new[] { storeId });
        var times = ClickHouseTcpColumn.Create("created_at", new[] { ClickHouseMigrator.UtcNowMilliseconds() });
        var authors = ClickHouseTcpColumn.Create("created_by", new[] { createdBy });
        await client.InsertAsync("INSERT INTO store_identity (store_id, created_at, created_by) VALUES", [ids, times, authors], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rows in the active parts of <c>events</c>, from <c>system.parts</c>: what the table holds without reading it.</summary>
    public async Task<long> StoredEventRowsAsync(CancellationToken cancellationToken = default) =>
        Convert.ToInt64(await client.ExecuteScalarAsync("SELECT sum(rows) FROM system.parts WHERE database = currentDatabase() AND table = 'events' AND active", cancellationToken: cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

    /// <summary>Free and total bytes of every disk the server stores data on (<c>system.disks</c>), for the ingest watermark.</summary>
    public async Task<IReadOnlyList<DiskSpace>> DiskSpaceAsync(CancellationToken cancellationToken = default)
    {
        var disks = new List<DiskSpace>();
        await foreach (var row in client.QueryAsync("SELECT name, free_space, total_space FROM system.disks ORDER BY name", cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            disks.Add(new DiskSpace("clickhouse:" + (string)row[0]!, Convert.ToInt64(row[1], CultureInfo.InvariantCulture), Convert.ToInt64(row[2], CultureInfo.InvariantCulture)));
        }

        return disks;
    }

    /// <summary>
    /// Drops one month's partition of a case (purge and archive). Idempotent: a missing partition is not an error.
    /// The statement lifts the server's size limit on a partition drop for itself (50 GB by default; a case month
    /// can be larger): the limit is there against a mistyped statement, and this one takes its partition from
    /// <see cref="CasePartitionsAsync"/>.
    /// </summary>
    public ValueTask DropCasePartitionAsync(ulong caseId, int yyyymm, CancellationToken cancellationToken = default) =>
        client.ExecuteAsync($"ALTER TABLE events DROP PARTITION ({Text(caseId)}, {yyyymm.ToString(CultureInfo.InvariantCulture)}) SETTINGS max_partition_size_to_drop = 0", cancellationToken: cancellationToken);

    /// <summary>The partitions a case has active parts in, with their rows and bytes on disk (<c>system.parts</c>), by month.</summary>
    public async Task<IReadOnlyList<CasePartition>> CasePartitionsAsync(ulong caseId, CancellationToken cancellationToken = default)
    {
        var partitions = new List<CasePartition>();
        var prefix = "(" + Text(caseId) + ",";
        await foreach (var row in client.QueryAsync(
            "SELECT partition, sum(rows), sum(bytes_on_disk) FROM system.parts WHERE database = currentDatabase() AND table = 'events' AND active " +
            $"AND startsWith(partition, '{prefix}') GROUP BY partition ORDER BY partition",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            // The partition key is (case_id, toYYYYMM(ts)); system.parts shows it as "(7,202601)".
            var text = (string)row[0]!;
            var month = int.Parse(text.AsSpan(prefix.Length).TrimEnd(')').Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
            partitions.Add(new CasePartition(month, Convert.ToInt64(row[1], CultureInfo.InvariantCulture), Convert.ToInt64(row[2], CultureInfo.InvariantCulture)));
        }

        return partitions;
    }

    /// <summary>Removes a case's field statistics (purge and archive). A lightweight delete; idempotent.</summary>
    public ValueTask DeleteCaseFieldStatsAsync(ulong caseId, CancellationToken cancellationToken = default) =>
        client.ExecuteAsync($"DELETE FROM field_stats WHERE case_id = {Text(caseId)}", cancellationToken: cancellationToken);

    /// <summary>
    /// Removes the generation rows of the given file ids (purge), so an id the metadata file hands out again starts
    /// from no row. A lightweight delete, which returns when its mask is applied; idempotent.
    /// </summary>
    public async ValueTask ClearFileGenerationsAsync(IReadOnlyList<ulong> fileIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        foreach (var batch in fileIds.Chunk(IdBatch))
        {
            await client.ExecuteAsync($"DELETE FROM file_generation WHERE file_id IN ({string.Join(',', batch.Select(Text))})", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How many generation rows the store still holds for the given file ids.</summary>
    public async Task<long> CountFileGenerationsAsync(IReadOnlyList<ulong> fileIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        long total = 0;
        foreach (var batch in fileIds.Chunk(IdBatch))
        {
            total += Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT count() FROM file_generation WHERE file_id IN ({string.Join(',', batch.Select(Text))})", cancellationToken: cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        return total;
    }

    private const int IdBatch = 1000;

    private static string Text(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One month of a case in <c>events</c>: the partition <c>(case_id, yyyymm)</c> with what its active parts hold.</summary>
public sealed record CasePartition(int Month, long Rows, long BytesOnDisk);

/// <summary>Free and total bytes of one volume: a ClickHouse disk (<c>clickhouse:&lt;name&gt;</c>) or a local path.</summary>
public sealed record DiskSpace(string Name, long FreeBytes, long TotalBytes);

/// <summary>The event store cannot take writes from this account: the schema is missing or the grants are.</summary>
public sealed class EventStoreSchemaException : Exception
{
    public EventStoreSchemaException()
    {
    }

    public EventStoreSchemaException(string message)
        : base(message)
    {
    }

    public EventStoreSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
