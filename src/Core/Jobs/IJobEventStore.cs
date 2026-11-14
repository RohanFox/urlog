using ClickHouse.Driver.Tcp;
using Urlog.Core.Ingest;
using Urlog.Core.Pipeline;

namespace Urlog.Core.Jobs;

/// <summary>The sink of one job: the pipeline's <see cref="IBatchSink"/> plus the rows the store has acknowledged so far.</summary>
public interface IJobSink : IBatchSink, IAsyncDisposable
{
    /// <summary>Rows in INSERTs the server has acknowledged.</summary>
    long AckedRows { get; }
}

/// <summary>The event store's side of the store identity check (<see cref="StoreIdentityGuard"/>).</summary>
public interface IStoreIdentitySource
{
    /// <summary>Where the store is, as <c>host:port</c>, for messages that have to name it.</summary>
    string Endpoint { get; }

    /// <summary>The identity the store carries, null when it has none.</summary>
    Task<string?> ReadStoreIdentityAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What the job runner needs from the event store, as the ingest account: the schema check, the generation-scoped
/// deletes, the generation commit and its read-back, the disk figures and a sink per job. ClickHouse behind
/// <see cref="ClickHouseJobEventStore"/>; a fake in the tests that run without Docker.
/// </summary>
public interface IJobEventStore : IStoreIdentitySource
{
    Task RequireSchemaAsync(CancellationToken cancellationToken);

    /// <summary>Removes the file's rows of every generation but <paramref name="keep"/> (0 keeps none).</summary>
    Task DeleteFileGenerationsExceptAsync(ulong caseId, ulong fileId, uint keep, CancellationToken cancellationToken);

    /// <summary>Removes the file's rows of the generations below <paramref name="generation"/>.</summary>
    Task DeleteFileGenerationsBelowAsync(ulong caseId, ulong fileId, uint generation, CancellationToken cancellationToken);

    Task CommitFileGenerationsAsync(IReadOnlyList<KeyValuePair<ulong, uint>> generations, CancellationToken cancellationToken);

    Task<Dictionary<ulong, uint>> ReadFileGenerationsAsync(CancellationToken cancellationToken);

    /// <summary>The visible generation the store holds for one file id, 0 when it has none.</summary>
    Task<uint> ReadFileGenerationAsync(ulong fileId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiskSpace>> DiskSpaceAsync(CancellationToken cancellationToken);

    /// <summary>The partitions of <c>events</c> the case has rows in, with rows and bytes on disk.</summary>
    Task<IReadOnlyList<CasePartition>> CasePartitionsAsync(ulong caseId, CancellationToken cancellationToken);

    /// <summary>Drops one month of a case. A partition that is not there is not an error.</summary>
    Task DropCasePartitionAsync(ulong caseId, int yyyymm, CancellationToken cancellationToken);

    /// <summary>Removes the case's field statistics. Returns when they are gone.</summary>
    Task DeleteCaseFieldStatsAsync(ulong caseId, CancellationToken cancellationToken);

    /// <summary>Removes the generation rows of the file ids. Returns when the delete is applied.</summary>
    Task ClearFileGenerationsAsync(IReadOnlyList<ulong> fileIds, CancellationToken cancellationToken);

    /// <summary>Generation rows the store still holds for the file ids.</summary>
    Task<long> CountFileGenerationsAsync(IReadOnlyList<ulong> fileIds, CancellationToken cancellationToken);

    IJobSink OpenSink(PipelineOptions pipeline);
}

/// <summary>
/// The event store over ClickHouse as <c>urlog_ingest</c>: <see cref="EventStoreAdmin"/> on a short-lived control
/// connection per call, <see cref="NativeInserter"/> as the sink.
/// </summary>
public sealed class ClickHouseJobEventStore(Func<ClickHouseTcpClient> control, NativeInserterOptions inserter) : IJobEventStore
{
    /// <summary>The store at <paramref name="host"/> as the ingest account: the control plane and the inserter on the native port.</summary>
    public static ClickHouseJobEventStore Create(string host, int nativePort, string user, string password, int writers)
    {
        var port = nativePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ClickHouseJobEventStore(
            () => new ClickHouseTcpClient($"Host={host};Port={port};Username={user};Password={password};Database=default;Compression=lz4"),
            new NativeInserterOptions { Host = host, Port = nativePort, User = user, Password = password, Database = "default", Table = "events", Writers = writers });
    }

    public string Endpoint { get; } = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{inserter.Host}:{inserter.Port}");

    public Task RequireSchemaAsync(CancellationToken cancellationToken) =>
        WithAsync(admin => admin.RequireSchemaAsync(cancellationToken));

    public async Task<string?> ReadStoreIdentityAsync(CancellationToken cancellationToken)
    {
        await using var client = control();
        return await new EventStoreAdmin(client).ReadStoreIdentityAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteFileGenerationsExceptAsync(ulong caseId, ulong fileId, uint keep, CancellationToken cancellationToken) =>
        WithAsync(admin => admin.DeleteFileGenerationsExceptAsync(caseId, fileId, keep, cancellationToken).AsTask());

    public Task DeleteFileGenerationsBelowAsync(ulong caseId, ulong fileId, uint generation, CancellationToken cancellationToken) =>
        WithAsync(admin => admin.DeleteFileGenerationsBelowAsync(caseId, fileId, generation, cancellationToken).AsTask());

    public Task CommitFileGenerationsAsync(IReadOnlyList<KeyValuePair<ulong, uint>> generations, CancellationToken cancellationToken) =>
        WithAsync(admin => admin.CommitFileGenerationsAsync(generations, cancellationToken).AsTask());

    public async Task<Dictionary<ulong, uint>> ReadFileGenerationsAsync(CancellationToken cancellationToken)
    {
        await using var client = control();
        return await new EventStoreAdmin(client).ReadFileGenerationsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<uint> ReadFileGenerationAsync(ulong fileId, CancellationToken cancellationToken)
    {
        await using var client = control();
        return await new EventStoreAdmin(client).ReadFileGenerationAsync(fileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiskSpace>> DiskSpaceAsync(CancellationToken cancellationToken)
    {
        await using var client = control();
        return await new EventStoreAdmin(client).DiskSpaceAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CasePartition>> CasePartitionsAsync(ulong caseId, CancellationToken cancellationToken)
    {
        await using var client = control();
        return await new EventStoreAdmin(client).CasePartitionsAsync(caseId, cancellationToken).ConfigureAwait(false);
    }

    public Task DropCasePartitionAsync(ulong caseId, int yyyymm, CancellationToken cancellationToken) =>
        WithAsync(admin => admin.DropCasePartitionAsync(caseId, yyyymm, cancellationToken).AsTask());

    public Task DeleteCaseFieldStatsAsync(ulong caseId, CancellationToken cancellationToken) =>
        WithAsync(admin => admin.DeleteCaseFieldStatsAsync(caseId, cancellationToken).AsTask());

    public Task ClearFileGenerationsAsync(IReadOnlyList<ulong> fileIds, CancellationToken cancellationToken) =>
        WithAsync(admin => admin.ClearFileGenerationsAsync(fileIds, cancellationToken).AsTask());

    public async Task<long> CountFileGenerationsAsync(IReadOnlyList<ulong> fileIds, CancellationToken cancellationToken)
    {
        await using var client = control();
        return await new EventStoreAdmin(client).CountFileGenerationsAsync(fileIds, cancellationToken).ConfigureAwait(false);
    }

    public IJobSink OpenSink(PipelineOptions pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return new Sink(new NativeInserter(inserter with { Writers = pipeline.Writers }, pipeline));
    }

    private async Task WithAsync(Func<EventStoreAdmin, Task> action)
    {
        await using var client = control();
        await action(new EventStoreAdmin(client)).ConfigureAwait(false);
    }

    private sealed class Sink(NativeInserter inner) : IJobSink
    {
        public long AckedRows => inner.Rows;

        public void Submit(EventBatch batch) => inner.Submit(batch);

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
