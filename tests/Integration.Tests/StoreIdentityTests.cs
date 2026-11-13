using System.Globalization;
using System.Security.Cryptography;
using Urlog.Bench.Fixtures;
using Urlog.Core.Audit;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Tests.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Urlog.Integration.Tests;

/// <summary>
/// A metadata file and an event store that do not belong together refuse to work together, against a real event
/// store and the real <c>urlog</c>. The store holds one deployment's file 1, generation 1, committed, with rows; a
/// second metadata file, whose ids also start at 1, is then pointed at it. Twice in the past that hid the first
/// deployment's rows without an error: once through a job's commit, once through the runner's startup sync. Both
/// are replayed here, in process and through <c>urlog worker</c>, and every replay asserts that the store and the
/// second metadata file are exactly what they were. Then every command, the first identity at
/// <c>urlog migrate --db</c>, the pairing by hand, and the id range of <c>urlog ingest</c>.
/// </summary>
public sealed class StoreIdentityTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private const string Passphrase = "correct horse battery staple 42";

    private static readonly string[] WorkerOnce = ["--once", "--min-free-gb", "0", "--min-free-percent", "0"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobRunnerOptions Options => new()
    {
        Pipeline = new PipelineOptions { ChunkBytes = 1 << 20, MaxRecordBytes = 1 << 20, BatchRows = 5_000, Parallelism = 2, InFlightChunks = 4, InFlightBatches = 2, Writers = 2 },
        ExitWhenIdle = true,
        IdlePoll = TimeSpan.FromMilliseconds(20),
        ProgressInterval = TimeSpan.FromMilliseconds(50),
        WorkerName = "test",
        Watermark = new WatermarkOptions { MinFreeBytes = 0, MinFreePercent = 0 },
    };

    [Fact]
    public async Task TheJobCommitOfAForeignFileIsRefusedAndNothingIsWritten()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var (caseId, path, lines, sha256) = await CommittedFileOneAsync(h);

        // The second metadata file: its own identity, its own case 1 and file 1, a queued parse.
        var foreign = await h.AddForeignMetadataAsync("foreign");
        var (file, job) = await WorkerHarness.AddFileAsync(foreign.Store, foreign.CaseId, path, sha256);
        Assert.Equal((1L, 1L), (foreign.CaseId, file.Id));
        var (store, metadata) = await SnapshotAsync(h, foreign.Store);

        // In this process.
        var refused = await Assert.ThrowsAsync<StoreIdentityException>(() => Runner(foreign.Store, h).RunAsync(Ct));
        output.WriteLine(refused.Message);
        AssertNamesBoth(refused.Message, foreign.StoreId!, h.StoreId, foreign.Store.Path);
        await AssertUnchangedAsync(h, foreign.Store, store, metadata);

        // Through urlog worker.
        var worker = await h.RunCliAsync(null, ["worker", "--db", foreign.Store.Path, .. WorkerOnce]);
        output.WriteLine(worker.ToString());
        Assert.Equal(2, worker.ExitCode);
        AssertNamesBoth(worker.Error, foreign.StoreId!, h.StoreId, foreign.Store.Path);
        Assert.DoesNotContain("job " + job.Id.ToString(CultureInfo.InvariantCulture) + " started", worker.Output, StringComparison.Ordinal);
        await AssertUnchangedAsync(h, foreign.Store, store, metadata);

        // What the first deployment's users see is what they saw.
        Assert.Equal((lines, 1L, 1L), await h.VisibleAsync(caseId));
        Assert.Equal(1u, await h.MirroredGenerationAsync(1));
    }

    [Fact]
    public async Task TheStartupSyncOfAForeignFileIsRefusedAndNothingIsWritten()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var (caseId, path, lines, sha256) = await CommittedFileOneAsync(h);

        // The second metadata file says "file 1, committed generation 2" and has nothing queued: all a start would
        // do with it is the sync, which writes file_generation from the metadata file wherever the two differ.
        var foreign = await h.AddForeignMetadataAsync("foreign");
        var (file, job) = await WorkerHarness.AddFileAsync(foreign.Store, foreign.CaseId, path, sha256);
        await using (var db = foreign.Store.CreateDbContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE jobs SET state = 'done', generation = 2 WHERE id = {job.Id}", Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE files SET state = 'parsed', parse_generation = 2 WHERE id = {file.Id}", Ct);
            Assert.Equal(new Dictionary<long, int> { [1] = 2 }, await JobOperations.CommittedGenerationsAsync(db, Ct));
            Assert.False(await db.Jobs.AnyAsync(j => j.State == JobState.Queued, Ct));
        }

        var (store, metadata) = await SnapshotAsync(h, foreign.Store);

        var runner = Runner(foreign.Store, h);
        var refused = await Assert.ThrowsAsync<StoreIdentityException>(() => runner.RunAsync(Ct));
        AssertNamesBoth(refused.Message, foreign.StoreId!, h.StoreId, foreign.Store.Path);
        Assert.Null(runner.LastReconcile);
        await AssertUnchangedAsync(h, foreign.Store, store, metadata);

        var worker = await h.RunCliAsync(null, ["worker", "--db", foreign.Store.Path, .. WorkerOnce]);
        output.WriteLine(worker.ToString());
        Assert.Equal(2, worker.ExitCode);
        AssertNamesBoth(worker.Error, foreign.StoreId!, h.StoreId, foreign.Store.Path);
        Assert.DoesNotContain("file_generation synced", worker.Output, StringComparison.Ordinal);
        await AssertUnchangedAsync(h, foreign.Store, store, metadata);

        Assert.Equal((lines, 1L, 1L), await h.VisibleAsync(caseId));
        Assert.Equal(1u, await h.MirroredGenerationAsync(1));
    }

    [Fact]
    public async Task OneSideWithoutAnIdentityIsRefusedInBothDirections()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var (_, path, _, sha256) = await CommittedFileOneAsync(h);

        // A metadata file from before the identity existed, against a store that has one.
        var old = new MetadataStore(Path.Combine(Directory.CreateDirectory(Path.Combine(h.Folder, "old")).FullName, "metadata.db"));
        await using (var db = old.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261113061516_IntakeMembers", Ct);
        }

        var (store, metadata) = await SnapshotAsync(h, old);
        var worker = await h.RunCliAsync(null, ["worker", "--db", old.Path, .. WorkerOnce]);
        output.WriteLine(worker.ToString());
        Assert.Equal(2, worker.ExitCode);
        Assert.Contains($"metadata file {old.Path} has store identity none", worker.Error, StringComparison.Ordinal);
        Assert.Contains($"has {h.StoreId}", worker.Error, StringComparison.Ordinal);
        Assert.Contains("urlog pair", worker.Error, StringComparison.Ordinal);
        await AssertUnchangedAsync(h, old, store, metadata); // not even migrated

        // A paired metadata file against a store that has lost its identity (a store put in its place).
        await h.ClearStoreIdentityAsync();
        await using (var db = h.Store.CreateDbContext())
        {
            await JobOperations.EnqueueAsync(db, 1, DateTime.UtcNow, Ct);
        }

        (store, metadata) = await SnapshotAsync(h, h.Store);
        worker = await h.RunCliAsync(null, ["worker", "--db", h.Store.Path, .. WorkerOnce]);
        output.WriteLine(worker.ToString());
        Assert.Equal(2, worker.ExitCode);
        Assert.Contains($"has store identity {h.StoreId}", worker.Error, StringComparison.Ordinal);
        Assert.Contains("has none", worker.Error, StringComparison.Ordinal);
        await AssertUnchangedAsync(h, h.Store, store, metadata);
        _ = (path, sha256);
    }

    [Fact]
    public async Task CommandsThatRecordForOneStoreCompareBothIdentities()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var (caseId, path, _, _) = await CommittedFileOneAsync(h);
        var foreign = await h.AddForeignMetadataAsync("foreign");
        var (store, metadata) = await SnapshotAsync(h, foreign.Store);
        var other = Path.Combine(h.Folder, "other.log");
        await File.WriteAllTextAsync(other, "203.0.113.9 - - [13/Nov/2026:10:00:00 +0000] \"GET /a HTTP/1.1\" 200 12 \"-\" \"curl/8.5.0\"\n", Ct);

        // Refused on the foreign file, each naming both identities, each leaving both sides as they were.
        string[][] commands =
        [
            ["case", "create", "--db", foreign.Store.Path, "--name", "second", "--owner", "mallory"],
            ["enqueue", "--db", foreign.Store.Path, "--case", "1", "--host", "web-01", other],
            ["expand", "--db", foreign.Store.Path, "--file", "1"],
            ["worker", "--db", foreign.Store.Path, .. WorkerOnce],
        ];
        foreach (var command in commands)
        {
            var result = await h.RunCliAsync(null, command);
            output.WriteLine(command[0] + ": " + result.Error.Trim());
            Assert.Equal(2, result.ExitCode);
            AssertNamesBoth(result.Error, foreign.StoreId!, h.StoreId, foreign.Store.Path);
            await AssertUnchangedAsync(h, foreign.Store, store, metadata);
        }

        // The same commands on the pair that belongs together get past the check.
        var created = await h.RunCliAsync(null, "case", "create", "--db", h.Store.Path, "--name", "second", "--owner", "alice");
        Assert.True(created.ExitCode == 0, created.ToString());
        Assert.Contains("case 2 'second' created", created.Output, StringComparison.Ordinal);

        var queued = await h.RunCliAsync(null, "enqueue", "--db", h.Store.Path, "--case", caseId.ToString(CultureInfo.InvariantCulture), "--host", "web-01", other);
        Assert.True(queued.ExitCode == 0, queued.ToString());
        Assert.Contains("queued    job", queued.Output, StringComparison.Ordinal);

        // expand reaches its own rule (file 1 is a log, not an archive): the identity check let it through.
        var expand = await h.RunCliAsync(null, "expand", "--db", h.Store.Path, "--file", "1");
        Assert.DoesNotContain("store identity", expand.Error, StringComparison.Ordinal);

        var worker = await h.RunCliAsync(null, ["worker", "--db", h.Store.Path, .. WorkerOnce]);
        Assert.True(worker.ExitCode == 0, worker.ToString());
        Assert.Contains("done:", worker.Output, StringComparison.Ordinal);

        // With the event store away these commands cannot compare, so they write nothing.
        (_, metadata) = await SnapshotAsync(h, h.Store);
        foreach (var command in new string[][] { ["case", "create", "--db", h.Store.Path, "--name", "third", "--owner", "alice"], ["enqueue", "--db", h.Store.Path, "--case", "1", "--host", "web-01", path] })
        {
            var result = await h.RunCliAsync(1, null, command);
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("did not answer", result.Error, StringComparison.Ordinal);
            Assert.Contains("wrote nothing", result.Error, StringComparison.Ordinal);
            SqliteConnection.ClearAllPools();
            Assert.Equal(metadata, await MetadataDump.TakeAsync(h.Store.Path, Ct));
        }
    }

    [Fact]
    public async Task CommandsThatWriteTheMetadataFileAloneWorkWithTheEventStoreStopped()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        const int nothingListens = 1;

        // user unlock is the break-glass for a locked sole admin: it must not wait for an event store.
        await using (var db = h.Store.CreateDbContext())
        {
            var locked = await db.Users.SingleAsync(u => u.Name == "alice", Ct);
            locked.FailedLogins = 10;
            locked.LockedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        var unlock = await h.RunCliAsync(nothingListens, null, "user", "unlock", "--db", h.Store.Path, "--name", "alice");
        Assert.True(unlock.ExitCode == 0, unlock.ToString());
        Assert.Contains("unlocked (was locked", unlock.Output, StringComparison.Ordinal);
        await using (var db = h.Store.CreateDbContext())
        {
            var alice = await db.Users.SingleAsync(u => u.Name == "alice", Ct);
            Assert.Equal((0, (DateTime?)null), (alice.FailedLogins, alice.LockedAt));
        }

        var create = await h.RunCliAsync(nothingListens, Passphrase + "\n", "user", "create", "--db", h.Store.Path, "--name", "bob", "--password-stdin");
        Assert.True(create.ExitCode == 0, create.ToString());
        var passwd = await h.RunCliAsync(nothingListens, Passphrase + "\n", "user", "passwd", "--db", h.Store.Path, "--name", "bob", "--password-stdin");
        Assert.True(passwd.ExitCode == 0, passwd.ToString());
        var backup = await h.RunCliAsync(nothingListens, null, "backup", "--db", h.Store.Path, "--dir", Path.Combine(h.Folder, "backups"));
        Assert.True(backup.ExitCode == 0, backup.ToString());
        var verify = await h.RunCliAsync(nothingListens, null, "verify-audit", "--db", h.Store.Path);
        Assert.True(verify.ExitCode == 0, verify.ToString());

        // verify-audit reads only: it runs on a file that was never paired and on one from before the identity
        // existed, and leaves each as it was.
        var never = await h.AddForeignMetadataAsync("never-paired", identity: false);
        var old = new MetadataStore(Path.Combine(Directory.CreateDirectory(Path.Combine(h.Folder, "old")).FullName, "metadata.db"));
        await using (var db = old.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261113061516_IntakeMembers", Ct);
            await ((AuditChain)old.Audit).EnsureGenesisAsync(db, Ct);
        }

        foreach (var (file, head) in new[] { (never.Store.Path, "audit head seq 2 hash "), (old.Path, "audit head seq 1 hash ") })
        {
            Assert.Null(await StoreIdentity.ReadAsync(file, Ct));
            SqliteConnection.ClearAllPools();
            var dump = await MetadataDump.TakeAsync(file, Ct);
            var verified = await h.RunCliAsync(nothingListens, null, "verify-audit", "--db", file);
            output.WriteLine(verified.Output.Trim());
            Assert.True(verified.ExitCode == 0, verified.ToString());
            Assert.Contains(head, verified.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("store identity", verified.Output + verified.Error, StringComparison.Ordinal);
            SqliteConnection.ClearAllPools();
            Assert.Equal(dump, await MetadataDump.TakeAsync(file, Ct));
        }

        Assert.DoesNotContain("store_identity", await MetadataDump.TakeAsync(old.Path, Ct), StringComparison.Ordinal);

        // The same commands on a metadata file that was never given an identity: refused, and the file untouched.
        var unpaired = await h.AddForeignMetadataAsync("unpaired", identity: false);
        SqliteConnection.ClearAllPools();
        var before = await MetadataDump.TakeAsync(unpaired.Store.Path, Ct);
        (string? Stdin, string[] Arguments)[] commands =
        [
            (null, ["user", "unlock", "--db", unpaired.Store.Path, "--name", "mallory"]),
            (Passphrase + "\n", ["user", "create", "--db", unpaired.Store.Path, "--name", "bob", "--password-stdin"]),
            (Passphrase + "\n", ["user", "passwd", "--db", unpaired.Store.Path, "--name", "mallory", "--password-stdin"]),
            (null, ["backup", "--db", unpaired.Store.Path, "--dir", Path.Combine(h.Folder, "backups-unpaired")]),
        ];
        foreach (var (stdin, arguments) in commands)
        {
            var result = await h.RunCliAsync(nothingListens, stdin, arguments);
            output.WriteLine(arguments[0] + " " + arguments[1] + ": " + result.Error.Trim());
            Assert.Equal(2, result.ExitCode);
            Assert.Contains($"metadata file {unpaired.Store.Path} has store identity none", result.Error, StringComparison.Ordinal);
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, await MetadataDump.TakeAsync(unpaired.Store.Path, Ct));
        }

        Assert.False(Directory.Exists(Path.Combine(h.Folder, "backups-unpaired")));

        // And a metadata file is not created by a command that only writes accounts.
        var absent = Path.Combine(h.Folder, "absent", "metadata.db");
        var none = await h.RunCliAsync(nothingListens, Passphrase + "\n", "user", "create", "--db", absent, "--name", "bob", "--admin", "--password-stdin");
        Assert.Equal(2, none.ExitCode);
        Assert.Contains("urlog migrate --db", none.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(absent));
    }

    [Fact]
    public async Task IngestWithoutARecordKeepsToItsOwnIdRangesInAStoreThatHasAnIdentity()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var (caseId, path, lines, _) = await CommittedFileOneAsync(h);
        var floor = ((ulong)StoreIdentity.IngestFileIdFloor).ToString(CultureInfo.InvariantCulture);
        var below = ((ulong)StoreIdentity.IngestFileIdFloor - 1).ToString(CultureInfo.InvariantCulture);
        var snapshot = await h.StoreSnapshotAsync();
        Assert.Equal(StoreIdentity.IngestFileIdFloor, StoreIdentity.IngestCaseIdFloor);
        var ingestCase = StoreIdentity.IngestCaseIdFloor;
        string[] ingest = ["ingest", "--case", floor, "--host", "web-09", "--quiet"];

        // Not told the identity: refused, with the store's value and "none" for what was given.
        var untold = await h.RunCliAsync(null, [.. ingest, "--file-id", floor, path]);
        output.WriteLine(untold.Error.Trim());
        Assert.Equal(2, untold.ExitCode);
        Assert.Contains($"has store identity {h.StoreId} and no --store-id was given (none)", untold.Error, StringComparison.Ordinal);
        Assert.Equal(snapshot, await h.StoreSnapshotAsync());

        // Told another store's identity.
        var wrong = StoreIdentity.NewId();
        var mistaken = await h.RunCliAsync(null, [.. ingest, "--store-id", wrong, "--file-id", floor, path]);
        Assert.Equal(2, mistaken.ExitCode);
        Assert.Contains(wrong, mistaken.Error, StringComparison.Ordinal);
        Assert.Contains(h.StoreId, mistaken.Error, StringComparison.Ordinal);
        Assert.Equal(snapshot, await h.StoreSnapshotAsync());

        // The right identity and an id the metadata file could allocate: the default (1), and the last one below the floor.
        foreach (var id in new[] { null, "1", below })
        {
            string[] withId = id is null ? [] : ["--file-id", id];
            var low = await h.RunCliAsync(null, [.. ingest, "--store-id", h.StoreId, .. withId, path]);
            output.WriteLine(low.Error.Trim());
            Assert.Equal(2, low.ExitCode);
            Assert.Contains($"--file-id {id ?? "1"} is below {floor}", low.Error, StringComparison.Ordinal);
            Assert.Equal(snapshot, await h.StoreSnapshotAsync());
        }

        // The right identity and a case id the metadata file could allocate: a recorded case's (1), an unused low
        // one, and the last one below the floor.
        foreach (var id in new[] { "1", "9", below })
        {
            var low = await h.RunCliAsync(null, "ingest", "--case", id, "--host", "web-09", "--quiet", "--store-id", h.StoreId, "--file-id", floor, path);
            output.WriteLine(low.Error.Trim());
            Assert.Equal(2, low.ExitCode);
            Assert.Contains($"--case {id} is below {floor}", low.Error, StringComparison.Ordinal);
            Assert.Equal(snapshot, await h.StoreSnapshotAsync());
        }

        // The right identity and both floors themselves: written, visible in its own case, and case 1 and file 1 are what they were.
        var written = await h.RunCliAsync(null, [.. ingest, "--store-id", h.StoreId, "--file-id", floor, path]);
        Assert.True(written.ExitCode == 0, written.ToString());
        Assert.Equal((lines, 1L, 1L), await h.VisibleAsync(ingestCase));
        Assert.Equal((lines, 1L, 1L), await h.VisibleAsync(caseId));
        Assert.Equal(1u, await h.MirroredGenerationAsync(1));
        Assert.Equal(1u, (await h.EventStoreGenerationsAsync())[(ulong)StoreIdentity.IngestFileIdFloor]);

        // A store without an identity belongs to no metadata file: any id, as before, and no identity to be told.
        await h.ClearStoreIdentityAsync();
        var told = await h.RunCliAsync(null, [.. ingest, "--store-id", h.StoreId, "--file-id", "7", path]);
        Assert.Equal(2, told.ExitCode);
        Assert.Contains("has store identity none", told.Error, StringComparison.Ordinal);
        var free = await h.RunCliAsync(null, ["ingest", "--case", "10", "--host", "web-10", "--quiet", "--file-id", "7", path]);
        Assert.True(free.ExitCode == 0, free.ToString());
        Assert.Equal((lines, 1L, 1L), await h.VisibleAsync(10));
    }

    [Fact]
    public async Task MigrateGivesANewPairItsIdentityOnceAndNothingElse()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        await h.ClearStoreIdentityAsync(); // an event store with its schema and nothing in it

        // Without --db it reports and creates nothing.
        var plain = await h.RunCliAsync(null, "migrate");
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.Contains("identity  the event store has none", plain.Output, StringComparison.Ordinal);
        Assert.Null(await h.StoreIdentityAsync());

        // A new metadata file and the empty store: one identity, in both, with its audit row.
        var fresh = Path.Combine(h.Folder, "fresh", "metadata.db");
        var first = await h.RunCliAsync(null, "migrate", "--db", fresh);
        output.WriteLine(first.Output);
        Assert.True(first.ExitCode == 0, first.ToString());
        var id = await StoreIdentity.ReadAsync(fresh, Ct);
        Assert.True(StoreIdentity.IsValid(id));
        Assert.Equal(id, await h.StoreIdentityAsync());
        Assert.Contains($"identity  {id} created", first.Output, StringComparison.Ordinal);
        await using (var db = new MetadataStore(fresh).CreateDbContext())
        {
            var actions = await db.Audit.OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync(Ct);
            Assert.Equal([AuditActions.Genesis, AuditActions.StoreIdentityCreated], actions);
        }

        Assert.True((await AuditVerifier.VerifyAsync(fresh, Ct)).Ok);

        // Again: nothing to do.
        var second = await h.RunCliAsync(null, "migrate", "--db", fresh);
        Assert.True(second.ExitCode == 0, second.ToString());
        Assert.Contains($"identity  {id}:", second.Output, StringComparison.Ordinal);
        Assert.Contains("agree", second.Output, StringComparison.Ordinal);
        await using (var db = new MetadataStore(fresh).CreateDbContext())
        {
            Assert.Equal(2, await db.Audit.CountAsync(Ct));
        }

        // Another new metadata file against the store that now has an identity: refused, and the file is not created.
        var second2 = Path.Combine(h.Folder, "second", "metadata.db");
        var refused = await h.RunCliAsync(null, "migrate", "--db", second2);
        output.WriteLine(refused.Error.Trim());
        Assert.Equal(2, refused.ExitCode);
        Assert.Contains("has store identity none", refused.Error, StringComparison.Ordinal);
        Assert.Contains($"has {id}", refused.Error, StringComparison.Ordinal);
        Assert.Contains("urlog pair", refused.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(second2));
        Assert.Equal(id, await h.StoreIdentityAsync());

        // A stop between the two writes of a first identity: the metadata file has one, the empty store none. The
        // next run finishes it with the metadata file's value.
        await h.ClearStoreIdentityAsync();
        var finished = await h.RunCliAsync(null, "migrate", "--db", fresh);
        Assert.True(finished.ExitCode == 0, finished.ToString());
        Assert.Equal(id, await h.StoreIdentityAsync());
        Assert.Equal(id, await StoreIdentity.ReadAsync(fresh, Ct));
    }

    [Fact]
    public async Task MigrateNeverJoinsAMetadataFileAndAStoreThatHoldData()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var (caseId, _, lines, _) = await CommittedFileOneAsync(h);

        // A deployment from before the identity existed: data on both sides, an identity on neither.
        await h.ClearStoreIdentityAsync();
        await using (var db = h.Store.CreateDbContext())
        {
            await db.StoreIdentity.ExecuteDeleteAsync(Ct);
        }

        var (store, metadata) = await SnapshotAsync(h, h.Store);
        var upgrade = await h.RunCliAsync(null, "migrate", "--db", h.Store.Path);
        output.WriteLine(upgrade.Error.Trim());
        Assert.Equal(2, upgrade.ExitCode);
        Assert.Contains("has store identity none and 1 file record(s)", upgrade.Error, StringComparison.Ordinal);
        Assert.Contains("has none,", upgrade.Error, StringComparison.Ordinal);
        Assert.Contains("urlog pair", upgrade.Error, StringComparison.Ordinal);
        await AssertUnchangedAsync(h, h.Store, store, metadata);

        // A new, empty metadata file against the store that holds data: refused too, and not created.
        var fresh = Path.Combine(h.Folder, "fresh", "metadata.db");
        var refused = await h.RunCliAsync(null, "migrate", "--db", fresh);
        Assert.Equal(2, refused.ExitCode);
        Assert.False(File.Exists(fresh));
        Assert.Equal(store, await h.StoreSnapshotAsync());
        Assert.Equal((lines, 1L, 1L), await h.VisibleAsync(caseId));
    }

    [Fact]
    public async Task PairingAnUpgradedDeploymentShowsWhatItJoinsAndAsksFirst()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var (caseId, _, lines, _) = await CommittedFileOneAsync(h);
        await h.ClearStoreIdentityAsync();
        await using (var db = h.Store.CreateDbContext())
        {
            await db.StoreIdentity.ExecuteDeleteAsync(Ct);
        }

        var (store, metadata) = await SnapshotAsync(h, h.Store);

        // No terminal and no --confirm: the report, then a refusal, and nothing written.
        var asked = await h.RunCliAsync(null, "pair", "--db", h.Store.Path);
        output.WriteLine(asked.ToString());
        Assert.Equal(2, asked.ExitCode);
        Assert.Contains("not confirmed: nothing was written", asked.Error, StringComparison.Ordinal);
        Assert.Contains($"metadata  {h.Store.Path}", asked.Output, StringComparison.Ordinal);
        Assert.Contains("1 case(s), 1 file record(s) (highest id 1), 1 with a committed generation", asked.Output, StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"{lines:N0} event row(s) in active parts, 1 file_generation row(s)"), asked.Output, StringComparison.Ordinal);
        Assert.Contains("1 with the same generation in the store", asked.Output, StringComparison.Ordinal);
        Assert.Contains("0 with a different generation in the store", asked.Output, StringComparison.Ordinal);
        Assert.Contains("0 with no row in the store", asked.Output, StringComparison.Ordinal);
        Assert.Contains("0 unknown to the metadata file", asked.Output, StringComparison.Ordinal);
        await AssertUnchangedAsync(h, h.Store, store, metadata);

        // A runner on the file: pairing is refused while it is up.
        using (var held = RunnerLock.TryAcquire(h.Store.Path))
        {
            Assert.NotNull(held);
            var locked = await h.RunCliAsync(null, "pair", "--db", h.Store.Path, "--confirm", StoreIdentity.NewId());
            Assert.Equal(2, locked.ExitCode);
            Assert.Contains("a job runner holds", locked.Error, StringComparison.Ordinal);
            await AssertUnchangedAsync(h, h.Store, store, metadata);
        }

        // Neither side has an identity, so --confirm names the one to give them.
        var id = StoreIdentity.NewId();
        var paired = await h.RunCliAsync(null, "pair", "--db", h.Store.Path, "--confirm", id);
        output.WriteLine(paired.ToString());
        Assert.True(paired.ExitCode == 0, paired.ToString());
        Assert.Equal(id, await StoreIdentity.ReadAsync(h.Store.Path, Ct));
        Assert.Equal(id, await h.StoreIdentityAsync());
        await using (var db = h.Store.CreateDbContext())
        {
            var row = await db.Audit.SingleAsync(a => a.Action == AuditActions.StorePaired, Ct);
            Assert.Equal(AuditTargets.Store, row.TargetType);
            Assert.Contains($"\"store_id\":\"{id}\"", row.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("\"previous_store_id\":null", row.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("\"generation_same\":1", row.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("\"generation_different\":0", row.DetailsJson, StringComparison.Ordinal);
            Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"\"store_event_rows\":{lines}"), row.DetailsJson, StringComparison.Ordinal);
            Assert.Equal(StoreIdentityOrigin.Paired, (await db.StoreIdentity.SingleAsync(Ct)).Origin);
        }

        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);

        // The pair works, and nothing of what the store showed has moved.
        var worker = await h.RunCliAsync(null, ["worker", "--db", h.Store.Path, .. WorkerOnce]);
        Assert.True(worker.ExitCode == 0, worker.ToString());
        Assert.Equal((lines, 1L, 1L), await h.VisibleAsync(caseId));

        var again = await h.RunCliAsync(null, "pair", "--db", h.Store.Path);
        Assert.True(again.ExitCode == 0, again.ToString());
        Assert.Contains("already hold store identity " + id, again.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingAForeignFileNeedsTheStoresIdentityTypedBackAndTheStoreKeepsIt()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var (_, path, _, sha256) = await CommittedFileOneAsync(h);
        var foreign = await h.AddForeignMetadataAsync("foreign");
        var (file, job) = await WorkerHarness.AddFileAsync(foreign.Store, foreign.CaseId, path, sha256);
        await using (var db = foreign.Store.CreateDbContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE jobs SET state = 'done', generation = 2 WHERE id = {job.Id}", Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE files SET state = 'parsed', parse_generation = 2 WHERE id = {file.Id}", Ct);
        }

        var (store, metadata) = await SnapshotAsync(h, foreign.Store);

        // The report says what the next start would do: one generation written over the store's.
        var asked = await h.RunCliAsync(null, "pair", "--db", foreign.Store.Path);
        output.WriteLine(asked.ToString());
        Assert.Equal(2, asked.ExitCode);
        Assert.Contains("0 with the same generation in the store", asked.Output, StringComparison.Ordinal);
        Assert.Contains("1 with a different generation in the store: the next start of a runner writes the metadata file's value over the store's", asked.Output, StringComparison.Ordinal);
        Assert.Contains($"the event store keeps store identity {h.StoreId}; the metadata file takes {h.StoreId} (it has {foreign.StoreId})", asked.Output, StringComparison.Ordinal);
        await AssertUnchangedAsync(h, foreign.Store, store, metadata);

        // The wrong answer, the file's own identity included: refused.
        foreach (var answer in new[] { foreign.StoreId!, StoreIdentity.NewId(), "yes" })
        {
            var wrong = await h.RunCliAsync(null, "pair", "--db", foreign.Store.Path, "--confirm", answer);
            Assert.Equal(2, wrong.ExitCode);
            Assert.Contains($"not confirmed: the answer is not {h.StoreId}", wrong.Error, StringComparison.Ordinal);
            await AssertUnchangedAsync(h, foreign.Store, store, metadata);
        }

        // The store's identity typed back: the metadata file takes it, and the audit row says what it had.
        var paired = await h.RunCliAsync(null, "pair", "--db", foreign.Store.Path, "--confirm", h.StoreId);
        Assert.True(paired.ExitCode == 0, paired.ToString());
        Assert.Equal(h.StoreId, await StoreIdentity.ReadAsync(foreign.Store.Path, Ct));
        Assert.Equal(store, await h.StoreSnapshotAsync()); // the store itself was not touched
        await using var after = foreign.Store.CreateDbContext();
        var row = await after.Audit.SingleAsync(a => a.Action == AuditActions.StorePaired, Ct);
        Assert.Contains($"\"previous_store_id\":\"{foreign.StoreId}\"", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"generation_different\":1", row.DetailsJson, StringComparison.Ordinal);
        Assert.True((await AuditVerifier.VerifyAsync(foreign.Store.Path, Ct)).Ok);
    }

    /// <summary>The harness's own pair with case 1, file 1, generation 1 committed and visible: the deployment the store belongs to.</summary>
    private static async Task<(long CaseId, string Path, int Lines, string Sha256)> CommittedFileOneAsync(WorkerHarness h)
    {
        var @case = await h.AddCaseAsync("one");
        var (path, lines, sha256) = await SliceAsync(h, "access.log", 1 << 20);
        var (file, job) = await h.AddFileAsync(@case.Id, path, sha256);
        Assert.Equal((1L, 1L), (@case.Id, file.Id));
        await Runner(h.Store, h).RunAsync(Ct);
        Assert.Equal(JobState.Done, (await h.JobAsync(job.Id)).State);
        Assert.Equal((lines, 1L, 1L), await h.VisibleAsync(@case.Id));
        Assert.Equal(1u, await h.MirroredGenerationAsync(file.Id));
        return (@case.Id, path, lines, sha256);
    }

    private static JobRunner Runner(MetadataStore store, WorkerHarness h)
    {
        var catalog = new SourceCatalog();
        catalog.Add(Urlog.Sources.LogSourceRegistry.Registrations);
        return new JobRunner(store, h.EventStore(Options.Pipeline.Writers), catalog, Options);
    }

    private static async Task<(string Store, string Metadata)> SnapshotAsync(WorkerHarness h, MetadataStore metadata)
    {
        SqliteConnection.ClearAllPools();
        return (await h.StoreSnapshotAsync(), await MetadataDump.TakeAsync(metadata.Path, Ct));
    }

    /// <summary>Nothing was written to either store: the event store's rows, generations, mutations and identity, and every table of the metadata file.</summary>
    private static async Task AssertUnchangedAsync(WorkerHarness h, MetadataStore metadata, string storeBefore, string metadataBefore)
    {
        Assert.Equal(storeBefore, await h.StoreSnapshotAsync());
        SqliteConnection.ClearAllPools();
        Assert.Equal(metadataBefore, await MetadataDump.TakeAsync(metadata.Path, Ct));
    }

    private static void AssertNamesBoth(string message, string metadataId, string storeId, string metadataPath)
    {
        Assert.Contains($"metadata file {metadataPath} has store identity {metadataId}", message, StringComparison.Ordinal);
        Assert.Contains($"has {storeId}", message, StringComparison.Ordinal);
        Assert.Contains("nothing was written", message, StringComparison.Ordinal);
    }

    /// <summary>The first <paramref name="bytes"/> of the nginx fixture, cut at a line end, as a file of its own.</summary>
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
