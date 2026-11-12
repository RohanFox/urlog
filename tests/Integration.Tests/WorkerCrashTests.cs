using Urlog.Bench.Fixtures;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Urlog.Integration.Tests;

/// <summary>
/// A real <c>urlog worker</c> process, killed from outside with <c>Process.Kill</c> during a 1 GiB ingest, at three
/// points: while chunks are being read, while INSERTs are being acknowledged, and after the last acknowledgement but
/// before the commit. Nothing inside the worker cooperates with the kill. After each one a second worker starts:
/// the file is parsed again from the beginning and the case's users see exactly one generation with the fixture's
/// row count, and at no moment a partial count.
/// <para>
/// The third point is reached without a hook in the worker. The test takes the SQLite write lock once the last
/// chunk has been read; the worker finishes its INSERTs and then waits in its commit, which retries a busy store for
/// <c>--commit-timeout</c> seconds and says so on its output. The test kills on that line, inside that window, with
/// every row of the file already in the table.
/// </para>
/// </summary>
public sealed class WorkerCrashTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private const string CommitWaiting = "commit is waiting for the metadata lock";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("reading")]
    [InlineData("inserting")]
    [InlineData("before-commit")]
    public async Task AWorkerKilledFromOutsideLeavesOneGenerationAfterTheRestart(string point)
    {
        var fixture = FixtureStore.Nginx1G();
        var chunks = (int)((fixture.Bytes + (64L << 20) - 1) / (64L << 20));
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("crash " + point);
        var (file, job) = await h.AddFileAsync(@case.Id, fixture.Path, fixture.Sha256);

        var first = h.StartWorker("--progress-interval", "50", "--commit-timeout", "300");
        Job seen;
        switch (point)
        {
            case "reading":
                await WorkerHarness.EventuallyAsync(async () => (await h.JobAsync(job.Id)).ChunksRead >= 1, "the first chunk read", pollMilliseconds: 5);
                seen = await h.JobAsync(job.Id);
                await first.KillAsync();
                Assert.True(seen.Rows == 0 && seen.ChunksRead < chunks, $"the kill was meant to land while reading: {seen.ChunksRead} of {chunks} chunks read, {seen.Rows} rows acknowledged");
                break;

            case "inserting":
                await WorkerHarness.EventuallyAsync(async () => (await h.JobAsync(job.Id)).Rows > 0, "the first acknowledged INSERT", pollMilliseconds: 5);
                seen = await h.JobAsync(job.Id);
                await first.KillAsync();
                Assert.True(seen.Rows > 0 && seen.Rows < fixture.Lines, $"the kill was meant to land between acknowledgements: {seen.Rows} of {fixture.Lines} rows acknowledged");
                break;

            default:
                await WorkerHarness.EventuallyAsync(async () => (await h.JobAsync(job.Id)).ChunksRead >= chunks, "the last chunk read", pollMilliseconds: 5);
                await using (var blocker = new SqliteConnection(MetadataStore.ConnectionString(h.Store.Path)))
                {
                    await blocker.OpenAsync(Ct);
                    await using (var begin = blocker.CreateCommand())
                    {
                        begin.CommandText = "BEGIN IMMEDIATE";
                        await begin.ExecuteNonQueryAsync(Ct);
                    }

                    // The worker cannot commit now. It says so once every INSERT is acknowledged and the hash agrees.
                    await WorkerHarness.EventuallyAsync(() => Task.FromResult(first.Printed(CommitWaiting) || first.Process.HasExited), "the worker waiting in its commit", seconds: 280);
                    Assert.False(first.Process.HasExited, "the worker ended before its commit: " + first.Output);
                    Assert.Equal(fixture.Lines, await h.StoredRowsAsync(@case.Id)); // every row is in the table
                    await first.KillAsync();
                    await using var rollback = blocker.CreateCommand();
                    rollback.CommandText = "ROLLBACK";
                    await rollback.ExecuteNonQueryAsync(Ct);
                }

                seen = await h.JobAsync(job.Id);
                break;
        }

        // What the kill left: the job still in parsing, rows in the table, none of them visible.
        var left = await h.JobAsync(job.Id);
        var storedAfterKill = await h.StoredRowsAsync(@case.Id);
        var visibleAfterKill = await h.VisibleAsync(@case.Id);
        output.WriteLine($"{point}: killed with {seen.ChunksRead}/{chunks} chunks read and {seen.Rows:N0} rows acknowledged; {storedAfterKill:N0} rows in the table, {visibleAfterKill.Rows} visible");
        Assert.Equal((JobState.Parsing, 1, 1), (left.State, left.Attempt, left.Generation));
        Assert.Equal(0, visibleAfterKill.Rows);
        Assert.Equal(FileState.Received, (await h.FileAsync(file.Id)).State);

        // The restart. While it runs, a user of the case reads either nothing or the whole file.
        var second = h.StartWorker("--once", "--progress-interval", "50");
        var observed = new SortedSet<long>();
        while (!second.Process.HasExited)
        {
            observed.Add((await h.VisibleAsync(@case.Id)).Rows);
            await Task.Delay(100, Ct);
        }

        var exit = await second.WaitForExitAsync(300);
        output.WriteLine(second.Output);
        Assert.Equal(0, exit);
        Assert.Contains("was left in parsing", second.Output, StringComparison.Ordinal);

        var done = await h.JobAsync(job.Id);
        Assert.Equal((JobState.Done, 2, 2, fixture.Lines), (done.State, done.Attempt, done.Generation, done.Rows));
        var visible = await h.VisibleAsync(@case.Id);
        Assert.Equal((fixture.Lines, 1L, 2L), visible);
        observed.Add(visible.Rows);
        Assert.Subset(new SortedSet<long> { 0, fixture.Lines }, observed);
        var parsed = await h.FileAsync(file.Id);
        Assert.Equal((FileState.Parsed, 2, fixture.Lines), (parsed.State, parsed.ParseGeneration, parsed.RowCount));
        Assert.Equal(2u, await h.MirroredGenerationAsync(file.Id));

        // The killed attempt's rows are gone from the table, not only hidden.
        await WorkerHarness.EventuallyAsync(async () => (await h.StoredGenerationsAsync(file.Id)).Keys.SequenceEqual([2u]), "the first attempt's rows deleted", seconds: 60, pollMilliseconds: 250);
        Assert.Equal(fixture.Lines, (await h.StoredGenerationsAsync(file.Id))[2]);

        var chain = await AuditVerifier.VerifyAsync(h.Store.Path, Ct);
        Assert.True(chain.Ok, chain.Detail);
    }
}
