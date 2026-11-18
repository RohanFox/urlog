using System.Security.Cryptography;
using Urlog.Bench.Fixtures;
using Urlog.Core.Audit;
using Urlog.Core.Ingest;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Integration.Tests;

/// <summary>
/// The job runner in this process against the real event store as <c>urlog_ingest</c>: a parse is visible through
/// <c>events_v</c> only after its commit, a file changed after intake leaves no row behind, a re-parse replaces the
/// first generation, and the startup sync writes <c>file_generation</c> from SQLite.
/// </summary>
public sealed class JobRunnerStoreTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobRunnerOptions Options => new()
    {
        Pipeline = new PipelineOptions { ChunkBytes = 1 << 20, MaxRecordBytes = 1 << 20, BatchRows = 5_000, Parallelism = 2, InFlightChunks = 4, InFlightBatches = 2, Writers = 2 },
        ExitWhenIdle = true,
        IdlePoll = TimeSpan.FromMilliseconds(20),
        ProgressInterval = TimeSpan.FromMilliseconds(50),
        WorkerName = "test",
        Watermark = new WatermarkOptions { MinFreeBytes = 0, MinFreePercent = 0 }, // the test box's disks are not under test here
    };

    [Fact]
    public async Task AParseIsCommittedAReparseReplacesItAndTheSyncRepairsTheMirror()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("one");
        var (path, lines, sha256) = await SliceAsync(h, "access.log", 8 << 20);
        var (file, job) = await h.AddFileAsync(@case.Id, path, sha256);

        await Runner(h).RunAsync(Ct);

        var done = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Done, 1, (long)lines), (done.State, done.Generation, done.Rows));
        Assert.Equal((lines, 1L, 1L), await h.VisibleAsync(@case.Id));
        Assert.Equal(1u, await h.MirroredGenerationAsync(file.Id));
        output.WriteLine($"{lines:N0} rows of generation 1 visible");
        // The runner wrote the file's stored histogram before that commit: every minute and severity as the events have them.
        var stored = await h.HistogramAsync(@case.Id, file.Id);
        Assert.Equal((0L, (long)lines), (stored.Differing, stored.Events));
        Assert.Equal([1u], stored.FileGenerations);
        Assert.Equal(1, (await h.FileAsync(file.Id)).HistogramGeneration);

        // Re-parse: generation 2 replaces generation 1, in the view and in the table.
        await using (var db = h.Store.CreateDbContext())
        {
            await JobOperations.EnqueueAsync(db, file.Id, DateTime.UtcNow, Ct);
        }

        await Runner(h).RunAsync(Ct);
        Assert.Equal((lines, 1L, 2L), await h.VisibleAsync(@case.Id));
        await WorkerHarness.EventuallyAsync(async () => (await h.StoredGenerationsAsync(file.Id)).Keys.SequenceEqual([2u]), "generation 1 deleted after the commit of 2", seconds: 60, pollMilliseconds: 250);
        Assert.Equal(2, (await h.FileAsync(file.Id)).ParseGeneration);
        // The clean-up took the first generation's buckets with its rows: the table holds the second's alone.
        await WorkerHarness.EventuallyAsync(async () => (await h.HistogramAsync(@case.Id, file.Id)).FileGenerations.SequenceEqual([2u]), "the buckets of generation 1 deleted after the commit of 2", seconds: 60, pollMilliseconds: 250);
        Assert.Equal((0L, (long)lines), ((await h.HistogramAsync(@case.Id, file.Id)).Differing, (await h.HistogramAsync(@case.Id, file.Id)).Events));
        Assert.Equal(2, (await h.FileAsync(file.Id)).HistogramGeneration);

        // The mirror drifts (a commit the event store never got, or a row written by mistake): the next start puts SQLite's value back.
        await using (var ingest = new ClickHouse.Driver.Tcp.ClickHouseTcpClient(ch.NativeConnectionString(WorkerHarness.IngestUser, WorkerHarness.IngestPassword)))
        {
            await new EventStoreAdmin(ingest).CommitFileGenerationAsync((ulong)file.Id, 1, Ct);
        }

        Assert.Equal(0, (await h.VisibleAsync(@case.Id)).Rows); // generation 1 has no rows any more
        var runner = Runner(h);
        await runner.RunAsync(Ct);
        Assert.Equal(1, runner.LastReconcile!.GenerationsWritten);
        Assert.Equal((lines, 1L, 2L), await h.VisibleAsync(@case.Id));

        var chain = await AuditVerifier.VerifyAsync(h.Store.Path, Ct);
        Assert.True(chain.Ok, chain.Detail);
    }

    [Fact]
    public async Task AFileChangedAfterIntakeIsQuarantinedWithNoRowLeftInTheTable()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("one");
        var (path, _, sha256) = await SliceAsync(h, "access.log", 8 << 20);
        var (file, job) = await h.AddFileAsync(@case.Id, path, sha256);

        // One byte, after intake. The line still parses; only the hash can tell.
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        var at = Array.FindLastIndex(bytes, b => b is >= (byte)'a' and <= (byte)'y');
        bytes[at]++;
        await File.WriteAllBytesAsync(path, bytes, Ct);

        await Runner(h).RunAsync(Ct);

        var failed = await h.JobAsync(job.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.StartsWith("integrity mismatch", failed.Message, StringComparison.Ordinal);
        Assert.Equal(FileState.Quarantined, (await h.FileAsync(file.Id)).State);
        Assert.Equal(0, (await h.VisibleAsync(@case.Id)).Rows);
        Assert.Equal(0u, await h.MirroredGenerationAsync(file.Id));
        await WorkerHarness.EventuallyAsync(async () => (await h.StoredGenerationsAsync(file.Id)).Count == 0, "the unverified rows deleted", seconds: 60, pollMilliseconds: 250);
        Assert.Equal(0, await h.HistogramRowsAsync(@case.Id)); // a file that fails its hash gets no stored histogram

        await using var db = h.Store.CreateDbContext();
        var row = Assert.Single(db.Audit.Where(a => a.Action == AuditActions.IntegrityMismatch).ToList());
        Assert.Equal((AuditTargets.File, (long?)file.Id), (row.TargetType, row.TargetId));
        var chain = await AuditVerifier.VerifyAsync(h.Store.Path, Ct);
        Assert.True(chain.Ok, chain.Detail);
    }

    [Fact]
    public async Task TheIngestAccountReadsTheStoresDisks()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var disks = await h.EventStore().DiskSpaceAsync(Ct);
        output.WriteLine(string.Join("; ", disks.Select(d => $"{d.Name}: {d.FreeBytes:N0} free of {d.TotalBytes:N0}")));
        Assert.Contains(disks, d => d.Name == "clickhouse:default" && d.TotalBytes > 0);
        await h.EventStore().RequireSchemaAsync(Ct);
    }

    private static JobRunner Runner(WorkerHarness h)
    {
        var catalog = new SourceCatalog();
        catalog.Add(Urlog.Sources.LogSourceRegistry.Registrations);
        return new JobRunner(h.Store, h.EventStore(Options.Pipeline.Writers), catalog, Options);
    }

    /// <summary>The first <paramref name="bytes"/> of the 1 GiB nginx fixture, cut at a line end, as a file of its own.</summary>
    private static async Task<(string Path, int Lines, string Sha256)> SliceAsync(WorkerHarness h, string name, int bytes)
    {
        var fixture = FixtureStore.Nginx1G();
        var buffer = new byte[bytes];
        await using (var source = File.OpenRead(fixture.Path))
        {
            await source.ReadExactlyAsync(buffer, Ct);
        }

        var end = Array.LastIndexOf(buffer, (byte)'\n') + 1;
        var slice = buffer.AsMemory(0, end);
        var path = Path.Combine(h.Folder, name);
        await File.WriteAllBytesAsync(path, slice, Ct);
        return (path, slice.Span.Count((byte)'\n'), Convert.ToHexStringLower(SHA256.HashData(slice.Span)));
    }
}
