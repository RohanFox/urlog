using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Urlog.Core.Tests;

/// <summary>
/// The runner's store identity check against an in-memory event store that records every call: a metadata file
/// and a store that do not belong together end the start before a migration, a reconciler or a claim, with not one
/// write on either side. Both write paths that once overwrote another deployment's <c>file_generation</c> are here:
/// the commit of a queued job, and the startup sync of a committed generation.
/// </summary>
public sealed class StoreIdentityRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AForeignFileWithAQueuedJobIsRefusedBeforeTheClaim()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("scratch");
        var (file, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(2_000, seed: 1).Bytes, "a.log");
        var mine = h.Events.StoreId!;

        // The store belongs to another metadata file and holds that file's id 1: generation 1, committed, with rows.
        var theirs = StoreIdentity.NewId();
        h.Events.StoreId = theirs;
        h.Events.Seed((ulong)file.Id, generation: 1, rows: 4_809_713, current: 1);
        SqliteConnection.ClearAllPools();
        var before = await MetadataDump.TakeAsync(h.Store.Path, Ct);

        var refused = await Assert.ThrowsAsync<StoreIdentityException>(() => h.Runner().RunAsync(Ct));

        Assert.Contains(mine, refused.Message, StringComparison.Ordinal);
        Assert.Contains(theirs, refused.Message, StringComparison.Ordinal);
        Assert.Contains(h.Store.Path, refused.Message, StringComparison.Ordinal);
        Assert.Contains(h.Events.Endpoint, refused.Message, StringComparison.Ordinal);
        Assert.Equal((mine, theirs), (refused.MetadataId, refused.StoreId));

        // The store: not one call beyond the identity read. What it shows for the other file 1 is what it showed.
        Assert.Empty(h.Events.Calls);
        Assert.Equal(1u, h.Events.Current((ulong)file.Id));
        Assert.Equal(4_809_713, h.Events.Visible((ulong)file.Id));
        // The metadata file: the job was not claimed, nothing was recorded.
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await MetadataDump.TakeAsync(h.Store.Path, Ct));
        Assert.Equal(JobState.Queued, (await h.JobAsync(job.Id)).State);
    }

    [Fact]
    public async Task AForeignFileWithACommittedGenerationIsRefusedBeforeTheStartupSync()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("scratch");
        var (file, _, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(2_000, seed: 2).Bytes, "a.log");
        await h.Runner().RunAsync(Ct);
        await using (var db = h.Open())
        {
            await JobOperations.EnqueueAsync(db, file.Id, DateTime.UtcNow, Ct);
        }

        await h.Runner().RunAsync(Ct);
        // The file says "file 1, committed generation 2" and has nothing queued.
        Assert.Equal(2, (await h.FileAsync(file.Id)).ParseGeneration);
        await using (var db = h.Open())
        {
            Assert.Equal(new Dictionary<long, int> { [file.Id] = 2 }, await JobOperations.CommittedGenerationsAsync(db, Ct));
            Assert.False(await db.Jobs.AnyAsync(j => j.State == JobState.Queued, Ct));
        }

        // Another store, which holds its own file 1 at generation 1: the sync would write 2 over it.
        var other = new FakeEventStore { StoreId = StoreIdentity.NewId() };
        other.Seed((ulong)file.Id, generation: 1, rows: 4_809_713, current: 1);
        SqliteConnection.ClearAllPools();
        var before = await MetadataDump.TakeAsync(h.Store.Path, Ct);
        var runner = new JobRunner(h.Store, other, new Urlog.Core.Sources.SourceCatalog(), RunnerHarness.Options() with { CacheRoot = h.CacheRoot }, h.Disk);

        var refused = await Assert.ThrowsAsync<StoreIdentityException>(() => runner.RunAsync(Ct));

        Assert.Contains(h.Events.StoreId!, refused.Message, StringComparison.Ordinal);
        Assert.Contains(other.StoreId, refused.Message, StringComparison.Ordinal);
        Assert.Null(runner.LastReconcile); // the reconcilers did not run
        Assert.Empty(other.Calls);
        Assert.Equal(1u, other.Current((ulong)file.Id));
        Assert.Equal(4_809_713, other.Visible((ulong)file.Id));
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, await MetadataDump.TakeAsync(h.Store.Path, Ct));
    }

    [Fact]
    public async Task OneSideWithoutAnIdentityIsRefusedAndTheMessageSaysNone()
    {
        // The store has none.
        await using (var h = await RunnerHarness.CreateAsync())
        {
            var mine = h.Events.StoreId!;
            h.Events.StoreId = null;
            var refused = await Assert.ThrowsAsync<StoreIdentityException>(() => h.Runner().RunAsync(Ct));
            Assert.Contains($"has store identity {mine}", refused.Message, StringComparison.Ordinal);
            Assert.Contains($"event store {h.Events.Endpoint} has none", refused.Message, StringComparison.Ordinal);
            Assert.Contains("urlog pair", refused.Message, StringComparison.Ordinal);
            Assert.Empty(h.Events.Calls);
        }

        // The metadata file has none.
        await using (var h = await RunnerHarness.CreateAsync())
        {
            await using (var db = h.Open())
            {
                await db.StoreIdentity.ExecuteDeleteAsync(Ct);
            }

            var refused = await Assert.ThrowsAsync<StoreIdentityException>(() => h.Runner().RunAsync(Ct));
            Assert.Contains("has store identity none", refused.Message, StringComparison.Ordinal);
            Assert.Contains($"has {h.Events.StoreId}", refused.Message, StringComparison.Ordinal);
            Assert.Empty(h.Events.Calls);
        }

        // Neither has one: a new deployment is told where its identity comes from.
        await using (var h = await RunnerHarness.CreateAsync())
        {
            await using (var db = h.Open())
            {
                await db.StoreIdentity.ExecuteDeleteAsync(Ct);
            }

            h.Events.StoreId = null;
            var refused = await Assert.ThrowsAsync<StoreIdentityException>(() => h.Runner().RunAsync(Ct));
            Assert.Contains("urlog migrate --db", refused.Message, StringComparison.Ordinal);
            Assert.Empty(h.Events.Calls);
        }
    }

    [Fact]
    public async Task TheCheckRunsBeforeTheMetadataMigrations()
    {
        // A metadata file from before the identity existed: the runner must not even bring its schema forward.
        var directory = Directory.CreateTempSubdirectory("urlog-identity-");
        try
        {
            var store = new MetadataStore(Path.Combine(directory.FullName, "metadata.db"));
            await using (var db = store.CreateDbContext())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261113061516_IntakeMembers", Ct);
            }

            SqliteConnection.ClearAllPools();
            var before = await MetadataDump.TakeAsync(store.Path, Ct);
            var events = new FakeEventStore { StoreId = StoreIdentity.NewId() };
            var runner = new JobRunner(store, events, new Urlog.Core.Sources.SourceCatalog(), RunnerHarness.Options() with { CacheRoot = Path.Combine(directory.FullName, "cache") });

            var refused = await Assert.ThrowsAsync<StoreIdentityException>(() => runner.RunAsync(Ct));

            Assert.Contains("has store identity none", refused.Message, StringComparison.Ordinal);
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, await MetadataDump.TakeAsync(store.Path, Ct));
            await using var after = store.CreateDbContext();
            Assert.Equal("20261113061516_IntakeMembers", (await after.Database.GetAppliedMigrationsAsync(Ct)).Last());
            Assert.Empty(events.Calls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task AMatchingPairRunsAndReadsTheIdentityBeforeAnythingElse()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (file, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(2_000, seed: 3).Bytes, "a.log");

        await h.Runner().RunAsync(Ct);

        Assert.Equal(JobState.Done, (await h.JobAsync(job.Id)).State);
        Assert.True(h.Events.IdentityReads >= 1);
        Assert.Equal(["commit " + file.Id + " 1"], h.Events.Calls);
    }

    [Fact]
    public async Task AStoreReplacedUnderARunningJobStopsTheRunnerBeforeTheCommit()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (file, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(20_000, seed: 4).Bytes, "a.log", HookedSource.Id);
        var mine = h.Events.StoreId!;
        var theirs = StoreIdentity.NewId();
        var flipped = 0;
        h.BeforeChunk = _ =>
        {
            // Mid-parse the store at this address becomes another one; the parse goes on until the runner has noticed.
            if (Interlocked.Exchange(ref flipped, 1) == 0)
            {
                h.Events.StoreId = theirs;
                var seen = h.Events.IdentityReads;
                SpinWait.SpinUntil(() => h.Events.IdentityReads > seen + 1, TimeSpan.FromSeconds(20));
            }
        };

        var lost = await Assert.ThrowsAsync<StoreIdentityException>(() => h.Runner(RunnerHarness.Options(once: false)).RunAsync(Ct));

        Assert.Contains(mine, lost.Message, StringComparison.Ordinal);
        Assert.Contains(theirs, lost.Message, StringComparison.Ordinal);
        // Nothing was committed and nothing was deleted in a store that is not this file's; the job waits in parsing
        // for a start against the right store.
        Assert.Empty(h.Events.Calls);
        Assert.Equal(0u, h.Events.Current((ulong)file.Id));
        var left = await h.JobAsync(job.Id);
        Assert.Equal(JobState.Parsing, left.State);
        Assert.Equal(1, (await h.FileAsync(file.Id)).ParseGeneration);
        Assert.Equal(FileState.Received, (await h.FileAsync(file.Id)).State);

        // Against the right store again, the next start recovers the job and finishes it.
        h.Events.StoreId = mine;
        h.BeforeChunk = _ => { };
        await h.Runner().RunAsync(Ct);
        Assert.Contains(await h.Open().Jobs.AsNoTracking().Where(j => j.FileId == file.Id).Select(j => j.State).ToListAsync(Ct), s => s == JobState.Done);
    }
}
