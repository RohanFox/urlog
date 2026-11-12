using System.Globalization;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Ingest;
using Urlog.Core.Ingest.Native;
using Urlog.Core.Pipeline;
using Urlog.Sources.Nginx;

namespace Urlog.Integration.Tests;

/// <summary>The sink end to end: the real pipeline, the real source, the real inserter, into the pinned schema.</summary>
public sealed class NativeInserterTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PipelineThroughTheInserterLandsEveryRowAndBalancesThePools()
    {
        const string table = "native_pipeline";
        await using var client = Admin();
        await client.ExecuteAsync($"DROP TABLE IF EXISTS {table}", cancellationToken: Ct);
        await client.ExecuteAsync(BatchBuilder.Ddl(table), cancellationToken: Ct);

        var options = new PipelineOptions { BatchRows = 7, Parallelism = 2, InFlightChunks = 4, InFlightBatches = 3, ChunkBytes = 4096 };
        var buffers = new PipelineBuffers(options);
        var pipeline = new ParsePipeline(buffers);
        var inserterOptions = BatchBuilder.Options(ch, table) with { Writers = 2, MaxRowsPerInsert = 20, InsertIdleTimeout = TimeSpan.FromMilliseconds(200) };
        await using var inserter = new NativeInserter(inserterOptions, options);

        using var file = File.OpenRead(Path.Combine(RepoRoot(), "fixtures", "nginx", "sample.log"));
        var result = await pipeline.RunAsync(file, new NginxSource(), new FileIdentity(1, 1, 1, 1, "web-01"), inserter, Ct);
        output.WriteLine($"{result.Rows} rows ({result.Rejects} rejects) in {result.Chunks} chunks → {inserter.Blocks} blocks, {inserter.Inserts} INSERTs, {inserter.Rows} acknowledged, {inserter.BytesSent:N0} B on the wire");

        Assert.Equal(result.Rows, inserter.Rows);
        Assert.True(inserter.Inserts >= 2, "20 rows per INSERT must have split the sample into several INSERTs");
        var count = Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT count() FROM {table}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        Assert.Equal(result.Rows, count);
        var rejects = Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT count() FROM {table} WHERE extras['reject_reason'] != ''", cancellationToken: Ct), CultureInfo.InvariantCulture);
        Assert.Equal(result.Rejects, rejects);
        Assert.Equal(0, buffers.Batches.Outstanding);
        Assert.Equal(0, buffers.Chunks.Outstanding);
        await client.ExecuteAsync($"DROP TABLE {table}", cancellationToken: Ct);
    }

    [Fact]
    public async Task IdleTimeoutEndsAnOpenInsertSoTheTailIsNotHeldBack()
    {
        const string table = "native_idle";
        await using var client = Admin();
        await client.ExecuteAsync($"DROP TABLE IF EXISTS {table}", cancellationToken: Ct);
        await client.ExecuteAsync(BatchBuilder.Ddl(table), cancellationToken: Ct);

        using var builder = new BatchBuilder(batchRows: 3, batches: 1);
        for (var i = 0; i < 3; i++)
        {
            builder.Writer.AppendUInt64(Urlog.Core.Sources.EventColumn.FileOffset, (ulong)i);
            builder.Writer.AppendString(Urlog.Core.Sources.EventColumn.Raw, "r"u8);
            builder.Writer.CommitRow();
        }

        var batch = Assert.Single(builder.Flush());
        await using var inserter = new NativeInserter(BatchBuilder.Options(ch, table) with { Writers = 1, MaxRowsPerInsert = 1_000_000, InsertIdleTimeout = TimeSpan.FromMilliseconds(300) }, returnBatches: false);
        inserter.Submit(batch);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (inserter.Inserts == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(1, inserter.Inserts);
        Assert.Equal(3, inserter.Rows);
        var count = Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT count() FROM {table}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        Assert.Equal(3, count);
        await inserter.CompleteAsync(Ct);
        await client.ExecuteAsync($"DROP TABLE {table}", cancellationToken: Ct);
    }

    [Fact]
    public async Task FailureIsTypedAndSubsequentSubmitsThrowWithoutBlocking()
    {
        using var builder = new BatchBuilder(batchRows: 2, batches: 3);
        for (var i = 0; i < 6; i++)
        {
            builder.Writer.AppendString(Urlog.Core.Sources.EventColumn.Raw, "r"u8);
            builder.Writer.CommitRow();
        }

        var batches = builder.Flush();
        Assert.Equal(3, batches.Count);

        await using var inserter = new NativeInserter(BatchBuilder.Options(ch, "native_no_such_table") with { Writers = 1 }, queueCapacity: 1, returnBatches: false);
        inserter.Submit(batches[0]);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (inserter.Failure is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        var failure = Assert.IsType<ClickHouseInsertException>(inserter.Failure);
        output.WriteLine($"{failure.Code} {failure.Name}: {failure.ServerMessage.Split('\n')[0]}");
        Assert.Equal(60, failure.Code);

        var submitTask = Task.Run(() => inserter.Submit(batches[1]), Ct);
        var finished = await Task.WhenAny(submitTask, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        Assert.Same(submitTask, finished);
        await Assert.ThrowsAsync<ClickHouseInsertException>(() => submitTask);
        var completion = await Assert.ThrowsAsync<ClickHouseInsertException>(async () => await inserter.CompleteAsync(Ct));
        Assert.Equal(60, completion.Code);
    }

    private ClickHouseTcpClient Admin() => new(ch.NativeConnectionString(ClickHouseFixture.AdminUser, ClickHouseFixture.AdminPassword));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("urlog.sln not found above the test binary");
    }
}
