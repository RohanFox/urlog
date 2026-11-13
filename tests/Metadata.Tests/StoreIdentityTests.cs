using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Urlog.Tests.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The metadata file's side of the store identity: one row, written only with its audit row; a read that changes
/// nothing in a file; and the ceiling on file ids that keeps the range of <c>urlog ingest</c> apart.
/// </summary>
public sealed class StoreIdentityTests
{
    private const string Server = "store.example:9000";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFileWithoutAnIdentityReadsAsNoneAndTheReadChangesNothing()
    {
        // No file at all.
        await using var missing = TempDb.CreateUnmigrated();
        Assert.Null(await StoreIdentity.ReadAsync(missing.Path, Ct));
        Assert.False(File.Exists(missing.Path));

        // A file from before the identity existed: the table is not there, and the read does not add it.
        await using var old = TempDb.CreateUnmigrated();
        await using (var db = old.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261113061516_IntakeMembers", Ct);
        }

        SqliteConnection.ClearAllPools();
        var before = await MetadataDump.TakeAsync(old.Path, Ct);
        Assert.Null(await StoreIdentity.ReadAsync(old.Path, Ct));
        Assert.Equal(before, await MetadataDump.TakeAsync(old.Path, Ct));
        Assert.DoesNotContain("store_identity", before, StringComparison.Ordinal);

        // A migrated file nobody paired.
        await using var unpaired = await TempDb.CreateAsync();
        Assert.Null(await StoreIdentity.ReadAsync(unpaired.Path, Ct));
    }

    [Fact]
    public async Task TheFirstIdentityIsWrittenOnceWithItsAuditRow()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var id = StoreIdentity.NewId();
        Assert.True(StoreIdentity.IsValid(id));
        Assert.NotEqual(id, StoreIdentity.NewId());

        var row = await StoreIdentityOperations.CreateAsync(db, temp.Store.Audit, TempDb.Alice, id, Server, DateTime.UtcNow, Ct);

        Assert.Equal((StoreIdentityRow.SingleId, id, StoreIdentityOrigin.Created), (row.Id, row.StoreId, row.Origin));
        Assert.Equal(id, await StoreIdentity.ReadAsync(temp.Path, Ct));
        var audit = await db.Audit.OrderBy(a => a.Seq).ToListAsync(Ct);
        Assert.Equal([AuditActions.Genesis, AuditActions.StoreIdentityCreated], audit.Select(a => a.Action));
        Assert.Equal((AuditTargets.Store, (long?)null, "alice"), (audit[1].TargetType, audit[1].TargetId, audit[1].Actor));
        Assert.Equal($$"""{"server":"{{Server}}","store_id":"{{id}}"}""", audit[1].DetailsJson);

        // A file has one identity: a second one is a pairing, never a create.
        var again = await Assert.ThrowsAsync<MetadataException>(() => StoreIdentityOperations.CreateAsync(db, temp.Store.Audit, TempDb.Alice, StoreIdentity.NewId(), Server, DateTime.UtcNow, Ct));
        Assert.Contains(id, again.Message, StringComparison.Ordinal);
        Assert.Equal(2, await db.Audit.CountAsync(Ct));
        Assert.Equal(id, await StoreIdentity.ReadAsync(temp.Path, Ct));

        var chain = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(chain.Ok, chain.Detail);
    }

    [Fact]
    public async Task APairingReplacesTheIdentityAndRecordsWhatItReplaced()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var first = StoreIdentity.NewId();
        var second = StoreIdentity.NewId();
        var report = new Dictionary<string, object?>(StringComparer.Ordinal) { ["files"] = 3L, ["generation_different"] = 0L };

        // Onto a file with no identity: previous is null.
        await StoreIdentityOperations.PairAsync(db, temp.Store.Audit, TempDb.Alice, first, Server, report, DateTime.UtcNow, Ct);
        Assert.Equal(first, await StoreIdentity.ReadAsync(temp.Path, Ct));

        // Onto a file that has one.
        var row = await StoreIdentityOperations.PairAsync(db, temp.Store.Audit, TempDb.Alice, second, Server, report, DateTime.UtcNow, Ct);
        Assert.Equal((second, StoreIdentityOrigin.Paired), (row.StoreId, row.Origin));
        Assert.Equal(second, await StoreIdentity.ReadAsync(temp.Path, Ct));
        Assert.Equal(1, await db.StoreIdentity.CountAsync(Ct));

        var paired = await db.Audit.Where(a => a.Action == AuditActions.StorePaired).OrderBy(a => a.Seq).ToListAsync(Ct);
        Assert.Equal(2, paired.Count);
        Assert.Equal($$"""{"files":3,"generation_different":0,"previous_store_id":null,"server":"{{Server}}","store_id":"{{first}}"}""", paired[0].DetailsJson);
        Assert.Equal($$"""{"files":3,"generation_different":0,"previous_store_id":"{{first}}","server":"{{Server}}","store_id":"{{second}}"}""", paired[1].DetailsJson);

        var chain = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(chain.Ok, chain.Detail);
    }

    [Fact]
    public async Task AnIdentityThatIsNot32LowercaseHexIsRefused()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        foreach (var bad in new[] { "", "abc", new string('g', 32), new string('A', 32), new string('a', 33) })
        {
            Assert.False(StoreIdentity.IsValid(bad));
            await Assert.ThrowsAsync<ArgumentException>(() => StoreIdentityOperations.CreateAsync(db, temp.Store.Audit, TempDb.Alice, bad, Server, DateTime.UtcNow, Ct));
        }

        Assert.Equal(1, await db.Audit.CountAsync(Ct));
        Assert.Null(await StoreIdentity.ReadAsync(temp.Path, Ct));
    }

    [Fact]
    public async Task TheTableHoldsOneRowAndAFailedAuditAppendLeavesNone()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var id = StoreIdentity.NewId();

        // The audit row and the identity are one transaction: no identity without its row.
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreIdentityOperations.CreateAsync(db, new FailingAudit(), TempDb.Alice, id, Server, DateTime.UtcNow, Ct));
        Assert.Null(await StoreIdentity.ReadAsync(temp.Path, Ct));

        await StoreIdentityOperations.CreateAsync(db, temp.Store.Audit, TempDb.Alice, id, Server, DateTime.UtcNow, Ct);
        var second = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlAsync($"INSERT INTO store_identity (id, store_id, created_at, origin) VALUES (2, {StoreIdentity.NewId()}, '2026-11-13 00:00:00', 'created')", Ct));
        Assert.Contains("ck_store_identity_single", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileIdsStopBelowTheFloorReservedForIngestWithoutARecord()
    {
        Assert.Equal(4611686018427387904L, StoreIdentity.IngestFileIdFloor);
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        var first = (await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(@case.Id, "one"), Ct)).File;

        // An id cannot be moved onto the floor.
        var moved = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlAsync($"UPDATE files SET id = {StoreIdentity.IngestFileIdFloor} WHERE id = {first.Id}", Ct));
        Assert.Contains("reserved for urlog ingest", moved.Message, StringComparison.Ordinal);

        // The last id below it is an id like any other.
        await db.Database.ExecuteSqlAsync($"UPDATE files SET id = {StoreIdentity.IngestFileIdFloor - 1} WHERE id = {first.Id}", Ct);
        db.ChangeTracker.Clear();
        Assert.Equal(StoreIdentity.IngestFileIdFloor - 1, (await db.Files.SingleAsync(Ct)).Id);

        // The next record would get the floor itself: refused, with its audit row, and nothing is left of it.
        var auditBefore = await db.Audit.CountAsync(Ct);
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(@case.Id, "two", "two.log"), Ct));
        Assert.Contains("reserved for urlog ingest", refused.InnerException!.Message, StringComparison.Ordinal);
        Assert.Equal(1, await db.Files.CountAsync(Ct));
        Assert.Equal(auditBefore, await db.Audit.CountAsync(Ct));
    }

    [Fact]
    public async Task CaseIdsStopBelowTheFloorReservedForIngestWithoutARecord()
    {
        Assert.Equal(4611686018427387904L, StoreIdentity.IngestCaseIdFloor);
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var first = await TempDb.AddCaseAsync(db, owner);
        Assert.Equal(1, first.Id);

        // A case cannot be written at the floor, or moved onto it.
        var direct = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlAsync($"INSERT INTO cases (id, name, state, owner_id, created_at) VALUES ({StoreIdentity.IngestCaseIdFloor}, 'at the floor', 'active', {owner}, '2026-11-13 00:00:00')", Ct));
        Assert.Contains("cases: ids from 4611686018427387904 are reserved for urlog ingest", direct.Message, StringComparison.Ordinal);

        // The last id below it is an id like any other.
        await db.Database.ExecuteSqlAsync($"INSERT INTO cases (id, name, state, owner_id, created_at) VALUES ({StoreIdentity.IngestCaseIdFloor - 1}, 'last', 'active', {owner}, '2026-11-13 00:00:00')", Ct);
        var moved = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlAsync($"UPDATE cases SET id = {StoreIdentity.IngestCaseIdFloor} WHERE id = {StoreIdentity.IngestCaseIdFloor - 1}", Ct));
        Assert.Contains("reserved for urlog ingest", moved.Message, StringComparison.Ordinal);

        // The next case would get the floor itself: refused, and neither it, its member row nor its audit row is left.
        var auditBefore = await db.Audit.CountAsync(Ct);
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => TempDb.AddCaseAsync(db, owner, "one too many"));
        Assert.Contains("reserved for urlog ingest", refused.InnerException!.Message, StringComparison.Ordinal);
        Assert.Equal([1L, StoreIdentity.IngestCaseIdFloor - 1], await db.Cases.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(Ct));
        Assert.Equal(1, await db.CaseMembers.CountAsync(Ct));
        Assert.Equal(auditBefore, await db.Audit.CountAsync(Ct));
    }

    [Fact]
    public async Task TheContentsOfAFileAreCountedWithoutChangingIt()
    {
        await using var temp = await TempDb.CreateAsync();
        long caseId, parsed, queued;
        await using (var db = temp.Open())
        {
            var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
            caseId = @case.Id;
            parsed = (await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(caseId, "one"), Ct)).File.Id;
            queued = (await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(caseId, "two", "two.log"), Ct)).File.Id;
            // Two finished parses of the first file: its committed generation is the higher one.
            await db.Database.ExecuteSqlAsync($"INSERT INTO jobs (case_id, file_id, kind, state, generation, attempt, created_at, chunks_read, chunks_parsed, chunks_acked, rows, rejects, bytes_read) VALUES ({caseId}, {parsed}, 'parse', 'done', 1, 1, '2026-11-13 00:00:00', 0, 0, 0, 0, 0, 0), ({caseId}, {parsed}, 'reparse', 'done', 3, 1, '2026-11-13 00:00:01', 0, 0, 0, 0, 0, 0), ({caseId}, {queued}, 'parse', 'queued', 0, 1, '2026-11-13 00:00:02', 0, 0, 0, 0, 0, 0)", Ct);
            Assert.Equal(new Dictionary<long, int> { [parsed] = 3 }, await JobOperations.CommittedGenerationsAsync(db, Ct));
        }

        SqliteConnection.ClearAllPools();
        var before = await MetadataDump.TakeAsync(temp.Path, Ct);
        var contents = await StoreIdentity.ReadContentsAsync(temp.Path, Ct);

        Assert.Equal(1, contents.Cases);
        Assert.Equal([parsed, queued], contents.FileIds.Order());
        // The same rule the runner's sync writes from.
        Assert.Equal(new Dictionary<long, int> { [parsed] = 3 }, contents.CommittedGenerations);
        Assert.Equal(before, await MetadataDump.TakeAsync(temp.Path, Ct));

        await using var none = TempDb.CreateUnmigrated();
        var empty = await StoreIdentity.ReadContentsAsync(none.Path, Ct);
        Assert.Equal((0L, 0, 0), (empty.Cases, empty.FileIds.Count, empty.CommittedGenerations.Count));
    }

    private sealed class FailingAudit : IAuditWriter
    {
        public Task<AuditRow> AppendAsync(MetadataDb db, AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the audit row could not be written");
    }
}
