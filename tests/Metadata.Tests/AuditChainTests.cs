using System.Data;
using System.Diagnostics;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The chain and what the trigger refuses: the genesis row, the same-transaction rule,
/// no gap on rollback, 4 concurrent writers × 2,500 rows, and the three triggers against EF and raw SQL.
/// </summary>
public sealed class AuditChainTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditEntry Entry(string actor = "alice", long? actorId = null, string action = "case_create") =>
        new(actor, actorId, action, AuditTargets.Case, 1, new Dictionary<string, object?> { ["case_name"] = "incident 1" });

    [Fact]
    public async Task MigrateWritesTheGenesisRowOnce()
    {
        await using var temp = await TempDb.CreateAsync();
        await temp.Store.MigrateAsync(Ct);
        await using var db = temp.Open();
        var rows = await db.Audit.OrderBy(a => a.Seq).ToListAsync(Ct);
        var genesis = Assert.Single(rows);
        Assert.Equal(1, genesis.Seq);
        Assert.Equal(AuditActions.SystemActor, genesis.Actor);
        Assert.Null(genesis.ActorId);
        Assert.Equal(AuditActions.Genesis, genesis.Action);
        Assert.Null(genesis.TargetType);
        Assert.Null(genesis.TargetId);
        Assert.Equal(AuditHash.ZeroHash, genesis.PrevHash);
        Assert.Equal(AuditHash.FormatVersion, genesis.SchemaVersion);
        Assert.Equal(AuditHash.Compute(genesis), genesis.Hash);
        Assert.Contains("\"migration\":\"20261113082434_StoreIdentity\"", genesis.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"tool\":\"urlog ", genesis.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(DateTimeKind.Utc, genesis.TsUtc.Kind);
        Assert.Equal(0, genesis.TsUtc.Ticks % 10);
    }

    [Fact]
    public async Task AppendChainsToTheTailInsideTheTransaction()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 6, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567));
        await using var temp = await TempDb.CreateAsync(clock);
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        AuditRow written;
        await using (var tx = await AuditChain.BeginTransactionAsync(db, Ct))
        {
            written = await temp.Store.Audit.AppendAsync(db, Entry(actorId: alice), Ct);
            await tx.CommitAsync(Ct);
        }

        var genesis = await db.Audit.AsNoTracking().SingleAsync(a => a.Seq == 1, Ct);
        var stored = await db.Audit.AsNoTracking().SingleAsync(a => a.Seq == 2, Ct);
        Assert.Equal(written.Hash, stored.Hash);
        Assert.Equal(genesis.Hash, stored.PrevHash);
        Assert.Equal(AuditHash.Compute(stored), stored.Hash);
        Assert.Equal("""{"case_name":"incident 1"}""", stored.DetailsJson);
        Assert.Equal(alice, stored.ActorId);
        Assert.Null(stored.SchemaVersion);
        // Truncated to microseconds before hashing and storing, so the stored value re-hashes equal.
        Assert.Equal(new DateTime(2026, 11, 6, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234560), stored.TsUtc);
    }

    [Fact]
    public async Task AppendOutsideATransactionIsRefused()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var e = await Assert.ThrowsAsync<AuditTransactionException>(() => temp.Store.Audit.AppendAsync(db, Entry(), Ct));
        Assert.Contains("BeginTransactionAsync", e.Message, StringComparison.Ordinal);
        Assert.Equal(1, await db.Audit.CountAsync(Ct));
    }

    [Fact]
    public async Task ADeferredTransactionCannotForkTheChain()
    {
        // Measured: ReadUncommitted is the one level Microsoft.Data.Sqlite starts as a deferred BEGIN, and under WAL it
        // reports Serializable, so the level check cannot see it. It is still safe: the tail read takes a snapshot, and
        // when another writer commits before this transaction's INSERT, WAL refuses the write (SQLITE_BUSY_SNAPSHOT)
        // instead of forking. The caller fails and retries; it never waits, which is why the immediate form is the rule.
        await using var temp = await TempDb.CreateAsync();
        await using var deferred = temp.Open();
        await using var tx = await deferred.Database.BeginTransactionAsync(IsolationLevel.ReadUncommitted, Ct);
        Assert.Equal(IsolationLevel.Serializable, tx.GetDbTransaction().IsolationLevel);
        Assert.Equal(1, await deferred.Audit.CountAsync(Ct)); // the snapshot is taken here

        await using (var other = temp.Open())
        await using (var otherTx = await AuditChain.BeginTransactionAsync(other, Ct))
        {
            await temp.Store.Audit.AppendAsync(other, Entry(actor: "bob", action: "case_close"), Ct);
            await otherTx.CommitAsync(Ct);
        }

        var e = await Assert.ThrowsAsync<DbUpdateException>(() => temp.Store.Audit.AppendAsync(deferred, Entry(), Ct));
        var sqlite = Assert.IsType<SqliteException>(e.InnerException);
        Assert.Equal(5, sqlite.SqliteErrorCode); // SQLITE_BUSY (extended: BUSY_SNAPSHOT)
        await tx.RollbackAsync(Ct);

        await using var reader = temp.Open();
        Assert.Equal(["genesis", "case_close"], await reader.Audit.OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync(Ct));
        Assert.True(await ChainHoldsAsync(reader));
    }

    [Fact]
    public async Task TheTransactionHoldsTheWriteLockFromBegin()
    {
        // The property the chain rests on: BeginTransactionAsync is BEGIN IMMEDIATE on this provider, so a second
        // writer cannot even start while the first is open (busy after its timeout), let alone read the same tail.
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        await using var tx = await AuditChain.BeginTransactionAsync(db, Ct);
        Assert.Equal(IsolationLevel.Serializable, tx.GetDbTransaction().IsolationLevel);

        await using var second = new SqliteConnection(new SqliteConnectionStringBuilder(MetadataStore.ConnectionString(temp.Path)) { DefaultTimeout = 1 }.ToString());
        await second.OpenAsync(Ct);
        var clock = Stopwatch.StartNew();
        var e = Assert.Throws<SqliteException>(() => second.BeginTransaction(IsolationLevel.Serializable));
        Assert.Equal(5, e.SqliteErrorCode); // SQLITE_BUSY
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"busy after {clock.Elapsed.TotalMilliseconds:F0} ms: the busy timeout was not waited");
    }

    [Fact]
    public async Task ARollbackLeavesNoGap()
    {
        await using var temp = await TempDb.CreateAsync();
        await using (var db = temp.Open())
        await using (var tx = await AuditChain.BeginTransactionAsync(db, Ct))
        {
            var row = await temp.Store.Audit.AppendAsync(db, Entry(), Ct);
            Assert.Equal(2, row.Seq);
            await tx.RollbackAsync(Ct);
        }

        await using (var db = temp.Open())
        await using (var tx = await AuditChain.BeginTransactionAsync(db, Ct))
        {
            var row = await temp.Store.Audit.AppendAsync(db, Entry(action: "case_close"), Ct);
            Assert.Equal(2, row.Seq);
            await tx.CommitAsync(Ct);
        }

        await using var reader = temp.Open();
        Assert.Equal([1L, 2L], await reader.Audit.OrderBy(a => a.Seq).Select(a => a.Seq).ToListAsync(Ct));
        Assert.True(await ChainHoldsAsync(reader));
    }

    [Fact]
    public async Task FourWritersTenThousandRowsNoGapsChainHolds()
    {
        await using var temp = await TempDb.CreateAsync();
        const int writers = 4, perWriter = 2_500;
        var clock = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, writers).Select(async w =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await using var db = temp.Open();
                await using var tx = await AuditChain.BeginTransactionAsync(db, Ct);
                await temp.Store.Audit.AppendAsync(db, Entry(actor: "writer-" + w, action: "case_close"), Ct);
                await tx.CommitAsync(Ct);
            }
        });
        await Task.WhenAll(tasks);
        clock.Stop();
        await Timing.RecordAsync(output, $"{writers} writers x {perWriter} transactions: {clock.Elapsed.TotalSeconds:F2} s ({writers * perWriter / clock.Elapsed.TotalSeconds:N0} rows/s)");

        await using var reader = temp.Open();
        var seqs = await reader.Audit.AsNoTracking().OrderBy(a => a.Seq).Select(a => a.Seq).ToListAsync(Ct);
        Assert.Equal(writers * perWriter + 1, seqs.Count);
        Assert.Equal(Enumerable.Range(1, seqs.Count).Select(i => (long)i), seqs);
        Assert.True(await ChainHoldsAsync(reader));
    }

    [Fact]
    public async Task UpdateAndDeleteAreRefusedByTheTriggers()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        await using (var tx = await AuditChain.BeginTransactionAsync(db, Ct))
        {
            await temp.Store.Audit.AppendAsync(db, Entry(), Ct);
            await tx.CommitAsync(Ct);
        }

        var update = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE audit SET actor = 'mallory' WHERE seq = 2", Ct));
        Assert.Contains("append-only: update refused", update.Message, StringComparison.Ordinal);
        var delete = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audit WHERE seq = 2", Ct));
        Assert.Contains("append-only: delete refused", delete.Message, StringComparison.Ordinal);
        var deleteAll = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audit", Ct));
        Assert.Contains("delete refused", deleteAll.Message, StringComparison.Ordinal);

        // Through EF: Remove is a DELETE and fails the same way; the entity has no setters to UPDATE through.
        var row = await db.Audit.SingleAsync(a => a.Seq == 2, Ct);
        db.Audit.Remove(row);
        var ef = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Contains("delete refused", ef.InnerException?.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();

        Assert.Equal(2, await db.Audit.CountAsync(Ct));
        Assert.Equal("alice", (await db.Audit.AsNoTracking().SingleAsync(a => a.Seq == 2, Ct)).Actor);
    }

    [Fact]
    public async Task AnInsertThatForksTheChainIsRefusedByTheTrigger()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var genesis = await db.Audit.AsNoTracking().SingleAsync(Ct);
        // {0} and {1} are EF parameters; '{{}}' is the details text.
        const string Insert = "INSERT INTO audit (seq, ts_utc, actor, action, details_json, prev_hash, hash) VALUES ({0}, '2026-11-06 12:00:00', 'mallory', 'case_close', '{{}}', {1}, 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff')";

        var gap = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(Insert, [3L, genesis.Hash], Ct));
        Assert.Contains("seq must be the last seq + 1", gap.Message, StringComparison.Ordinal);
        var fork = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(Insert, [2L, AuditHash.ZeroHash], Ct));
        Assert.Contains("prev_hash must be the last row", fork.Message, StringComparison.Ordinal);
        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(Insert, [1L, AuditHash.ZeroHash], Ct));
        Assert.Contains("seq must be the last seq + 1", duplicate.Message, StringComparison.Ordinal);

        // The well-formed continuation is accepted (its hash is wrong, which verify-audit catches: the trigger pins the links, not the content).
        Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(Insert, [2L, genesis.Hash], Ct));
        Assert.Equal(2, await db.Audit.CountAsync(Ct));
    }

    [Fact]
    public async Task AppendWithoutAGenesisRowIsRefused()
    {
        await using var temp = TempDb.CreateUnmigrated();
        await using var db = temp.Open();
        await db.Database.MigrateAsync(Ct); // the EF migrations alone: no genesis row
        await using var tx = await AuditChain.BeginTransactionAsync(db, Ct);
        var e = await Assert.ThrowsAsync<AuditChainException>(() => temp.Store.Audit.AppendAsync(db, Entry(), Ct));
        Assert.Contains("genesis", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntryFactoriesNameTheTarget()
    {
        var @case = new CaseRecord { Id = 3, Name = "incident 1", OwnerId = 1, CreatedAt = DateTime.UtcNow };
        var entry = AuditEntry.ForCase("alice", 1, AuditActions.ForCaseOperation(CaseLifecycle.Close), @case, new Dictionary<string, object?> { ["reason"] = "report signed" });
        Assert.Equal("case_close", entry.Action);
        Assert.Equal((AuditTargets.Case, 3L), (entry.TargetType, entry.TargetId));
        Assert.Equal("""{"case_name":"incident 1","reason":"report signed"}""", CanonicalJson.SerializeToString(entry.Details));

        var file = TempDb.NewFile(3, "a line\n");
        var fileEntry = AuditEntry.ForFile("alice", 1, AuditActions.FileIntake, file, "incident 1");
        Assert.Equal(AuditTargets.File, fileEntry.TargetType);
        Assert.Equal($$"""{"case_name":"incident 1","original_name":"access.log","sha256":"{{file.Sha256}}","size":7}""", CanonicalJson.SerializeToString(fileEntry.Details));

        var user = new User { Id = 9, Name = "bob", PasswordHash = "x", CreatedAt = DateTime.UtcNow };
        Assert.Equal("""{"user_name":"bob"}""", CanonicalJson.SerializeToString(AuditEntry.ForUser("alice", 1, AuditActions.UserCreate, user).Details));
    }

    /// <summary>The walk verify-audit makes, inline: every link recomputes and every prev_hash is the previous hash.</summary>
    internal static async Task<bool> ChainHoldsAsync(MetadataDb db)
    {
        var prev = AuditHash.ZeroHash;
        var expected = 1L;
        await foreach (var row in db.Audit.AsNoTracking().OrderBy(a => a.Seq).AsAsyncEnumerable().WithCancellation(Ct))
        {
            if (row.Seq != expected || row.PrevHash != prev || AuditHash.Compute(row) != row.Hash)
            {
                return false;
            }

            prev = row.Hash;
            expected++;
        }

        return expected > 1;
    }
}

/// <summary>The numbers the ticket quotes: always on the output helper; appended to the file URLOG_AUDIT_TIMING names when set.</summary>
internal static class Timing
{
    public static async Task RecordAsync(ITestOutputHelper output, string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("URLOG_AUDIT_TIMING") is { Length: > 0 } path)
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, TestContext.Current.CancellationToken);
        }
    }
}

internal sealed class FakeClock(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}
