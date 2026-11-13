using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The first two audited callers (the model later operations copy): each operation's change and its audit row are one
/// transaction. A successful operation leaves exactly its row; a failed audit write leaves neither the row nor the change.
/// </summary>
public sealed class AuditedOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateTransitionAndIntakeWriteTheirRows()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var aliceId = await TempDb.AddUserAsync(db);
        var alice = new AuditActor("alice", aliceId);

        var @case = await CaseOperations.CreateAsync(db, temp.Store.Audit, alice, "incident 1", aliceId, DateTime.UtcNow, Ct);
        var file = (await FileOperations.AddAsync(db, temp.Store.Audit, alice, TempDb.NewFile(@case.Id, "line\n", "a.log"), Ct)).File;
        var again = await FileOperations.AddAsync(db, temp.Store.Audit, alice, TempDb.NewFile(@case.Id, "line\n", "b.log"), Ct);
        Assert.True(again.Duplicate);
        Assert.Equal("close", await CaseOperations.TransitionAsync(db, temp.Store.Audit, alice, @case.Id, CaseState.Closed, DateTime.UtcNow, Ct));
        Assert.Equal("reopen", await CaseOperations.TransitionAsync(db, temp.Store.Audit, alice, @case.Id, CaseState.Active, DateTime.UtcNow, Ct));

        var rows = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).ToListAsync(Ct);
        Assert.Equal(["genesis", "case_create", "file_intake", "file_duplicate", "case_close", "case_reopen"], rows.Select(r => r.Action).ToArray());
        Assert.All(rows.Skip(1), r => Assert.Equal(("alice", (long?)aliceId), (r.Actor, r.ActorId)));

        // The details name the target, so a reused id stays unambiguous.
        Assert.Equal((AuditTargets.Case, (long?)@case.Id), (rows[1].TargetType, rows[1].TargetId));
        Assert.Equal($$"""{"case_name":"incident 1","owner_id":{{aliceId}}}""", rows[1].DetailsJson);
        Assert.Equal((AuditTargets.File, (long?)file.Id), (rows[2].TargetType, rows[2].TargetId));
        Assert.Equal($$"""{"archive_path":null,"case_name":"incident 1","original_name":"a.log","sha256":"{{file.Sha256}}","size":5,"source":"web"}""", rows[2].DetailsJson);
        Assert.Equal((long?)file.Id, rows[3].TargetId);
        Assert.Equal($$"""{"arrival_archive_path":null,"arrival_host":null,"arrival_name":"b.log","arrival_source":"web","case_name":"incident 1","original_name":"a.log","sha256":"{{file.Sha256}}","size":5}""", rows[3].DetailsJson);
        Assert.Equal("""{"case_name":"incident 1","from":"active","to":"closed"}""", rows[4].DetailsJson);
        Assert.Equal("""{"case_name":"incident 1","from":"closed","to":"active"}""", rows[5].DetailsJson);

        var verified = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(verified.Ok, verified.Detail);
        Assert.Equal(6, verified.HeadSeq);
    }

    [Fact]
    public async Task ARefusedOperationWritesNoRow()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        await CaseOperations.TransitionAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, CaseState.Closed, DateTime.UtcNow, Ct);
        var before = await db.Audit.CountAsync(Ct);

        await Assert.ThrowsAsync<InvalidCaseTransitionException>(() => CaseOperations.TransitionAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, CaseState.Purged, DateTime.UtcNow, Ct));
        await Assert.ThrowsAsync<CaseNotActiveException>(() => FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(@case.Id, "x"), Ct));
        await Assert.ThrowsAsync<CaseNotFoundException>(() => CaseOperations.TransitionAsync(db, temp.Store.Audit, TempDb.Alice, 4242, CaseState.Closed, DateTime.UtcNow, Ct));

        Assert.Equal(before, await db.Audit.CountAsync(Ct));
        Assert.Equal(CaseState.Closed, (await db.Cases.AsNoTracking().SingleAsync(Ct)).State);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task AFailedAuditWriteRollsBackTheCaseCreate()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);

        await Assert.ThrowsAsync<AuditFailure>(() => CaseOperations.CreateAsync(db, new FailingWriter(temp.Store.Audit), TempDb.Alice, "incident 1", owner, DateTime.UtcNow, Ct));

        await AssertNothingHappenedAsync(temp, db, cases: 0, members: 0, files: 0, auditRows: 1);
        // The same context goes on working, and the id the failed create consumed is not lost to a gap in seq.
        var created = await CaseOperations.CreateAsync(db, temp.Store.Audit, TempDb.Alice, "incident 1", owner, DateTime.UtcNow, Ct);
        Assert.Equal(2, (await db.Audit.AsNoTracking().SingleAsync(a => a.Action == AuditActions.CaseCreate, Ct)).Seq);
        Assert.Equal(created.Id, (await db.Cases.AsNoTracking().SingleAsync(Ct)).Id);
    }

    [Fact]
    public async Task AFailedAuditWriteRollsBackTheTransition()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));

        await Assert.ThrowsAsync<AuditFailure>(() => CaseOperations.TransitionAsync(db, new FailingWriter(temp.Store.Audit), TempDb.Alice, @case.Id, CaseState.Closed, DateTime.UtcNow, Ct));

        await AssertNothingHappenedAsync(temp, db, cases: 1, members: 1, files: 0, auditRows: 2);
        await using var fresh = temp.Open();
        var stored = await fresh.Cases.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((CaseState.Active, (DateTime?)null), (stored.State, stored.ClosedAt));
        // The context that failed holds no stale `closed` either: the same call now succeeds from `active`.
        Assert.Equal("close", await CaseOperations.TransitionAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, CaseState.Closed, DateTime.UtcNow, Ct));
    }

    [Fact]
    public async Task AFailedAuditWriteRollsBackTheFileIntakeAndTheDuplicate()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));

        await Assert.ThrowsAsync<AuditFailure>(() => FileOperations.AddAsync(db, new FailingWriter(temp.Store.Audit), TempDb.Alice, TempDb.NewFile(@case.Id, "bytes"), Ct));
        await AssertNothingHappenedAsync(temp, db, cases: 1, members: 1, files: 0, auditRows: 2);

        // The retry is an intake, not a duplicate: the failed attempt left no file row behind.
        var retry = await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(@case.Id, "bytes"), Ct);
        Assert.False(retry.Duplicate);

        await Assert.ThrowsAsync<AuditFailure>(() => FileOperations.AddAsync(db, new FailingWriter(temp.Store.Audit), TempDb.Alice, TempDb.NewFile(@case.Id, "bytes", "again.log"), Ct));
        await AssertNothingHappenedAsync(temp, db, cases: 1, members: 1, files: 1, auditRows: 3);
    }

    [Fact]
    public async Task AStoreWithoutAGenesisRowRefusesTheChangeToo()
    {
        // The real writer failing, not a fake: EF migrations only, so the append has no tail to chain to.
        await using var temp = TempDb.CreateUnmigrated();
        await using var db = temp.Open();
        await db.Database.MigrateAsync(Ct);
        var owner = await TempDb.AddUserAsync(db);

        await Assert.ThrowsAsync<AuditChainException>(() => CaseOperations.CreateAsync(db, temp.Store.Audit, TempDb.Alice, "incident 1", owner, DateTime.UtcNow, Ct));

        Assert.Equal(0, await db.Cases.CountAsync(Ct));
        Assert.Equal(0, await db.CaseMembers.CountAsync(Ct));
        Assert.Equal(0, await db.Audit.CountAsync(Ct));
    }

    private static async Task AssertNothingHappenedAsync(TempDb temp, MetadataDb used, int cases, int members, int files, int auditRows)
    {
        Assert.Null(used.Database.CurrentTransaction);
        Assert.Empty(used.ChangeTracker.Entries());
        await using var fresh = temp.Open();
        Assert.Equal(cases, await fresh.Cases.CountAsync(Ct));
        Assert.Equal(members, await fresh.CaseMembers.CountAsync(Ct));
        Assert.Equal(files, await fresh.Files.CountAsync(Ct));
        Assert.Equal(auditRows, await fresh.Audit.CountAsync(Ct));
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    /// <summary>Writes the row for real, then fails: the worst case, a row already in the transaction when the caller sees the error.</summary>
    private sealed class FailingWriter(IAuditWriter inner) : IAuditWriter
    {
        public async Task<AuditRow> AppendAsync(MetadataDb db, AuditEntry entry, CancellationToken cancellationToken = default)
        {
            await inner.AppendAsync(db, entry, cancellationToken);
            throw new AuditFailure();
        }
    }

    private sealed class AuditFailure : Exception;
}
