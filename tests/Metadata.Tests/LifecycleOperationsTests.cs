using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The lifecycle operations a person asks for and the metadata side of the purge: every pair of states through
/// the operations, what each one needs the case to hold, who may ask, and what the audit chain says afterwards.
/// </summary>
public sealed class LifecycleOperationsTests
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private static readonly PurgeBackup Backup = new(new string('b', 64), "metadata-20261113T100000Z.sqlite", 4096);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Now => DateTime.UtcNow;

    public static TheoryData<CaseState, CaseState> AllPairs
    {
        get
        {
            var data = new TheoryData<CaseState, CaseState>();
            foreach (var from in Enum.GetValues<CaseState>())
            {
                foreach (var to in Enum.GetValues<CaseState>())
                {
                    data.Add(from, to);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public async Task EveryPairThroughTheOperationsWorksAndAuditsOrIsRefusedNamingTheState(CaseState from, CaseState to)
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var @case = await TempDb.AddCaseAsync(db, owner);
        await PutInStateAsync(db, @case.Id, from, prior: CaseState.Active);
        var before = await db.Audit.CountAsync(Ct);
        var allowed = CaseLifecycle.Operation(from, to) is { } operation && (operation != CaseLifecycle.Restore || to == CaseState.Active);

        if (!allowed)
        {
            var refusal = await Assert.ThrowsAnyAsync<MetadataException>(() => ApplyAsync(temp, db, @case.Id, from, to));
            Assert.True(refusal is InvalidCaseTransitionException or LifecycleRefusedException, refusal.GetType().Name);
            Assert.Contains(Names.Of(from), refusal.Message, StringComparison.Ordinal);
            Assert.Equal(from, await db.Cases.AsNoTracking().Where(c => c.Id == @case.Id).Select(c => c.State).SingleAsync(Ct));
            Assert.Equal(before, await db.Audit.CountAsync(Ct));
            return;
        }

        await ApplyAsync(temp, db, @case.Id, from, to);
        var row = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Equal("case_" + CaseLifecycle.Operation(from, to), row.Action);
        Assert.Equal(@case.Id, row.TargetId);
        Assert.Contains("\"case_name\":\"incident 1\"", row.DetailsJson, StringComparison.Ordinal);
        if (to == CaseState.Purged)
        {
            // Never a stored state: the row of the case goes in the transaction that writes case_purge_complete.
            Assert.False(await db.Cases.AnyAsync(c => c.Id == @case.Id, Ct));
        }
        else
        {
            Assert.Equal(to, await db.Cases.AsNoTracking().Where(c => c.Id == @case.Id).Select(c => c.State).SingleAsync(Ct));
        }

        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    /// <summary>
    /// What each operation a person asks for does in each state a case can be in: the state it leaves, or null for a
    /// refusal. An operation carries out its own transition only. The table of transitions also holds archived to
    /// closed (a re-import) and soft-deleted to active (a restore), and a close or a reopen is neither.
    /// </summary>
    public static TheoryData<string, CaseState, CaseState?> OperationsByState => new()
    {
        { CaseLifecycle.Close, CaseState.Active, CaseState.Closed },
        { CaseLifecycle.Close, CaseState.Closed, null },
        { CaseLifecycle.Close, CaseState.Archived, null },
        { CaseLifecycle.Close, CaseState.SoftDeleted, null },
        { CaseLifecycle.Close, CaseState.Purging, null },
        { CaseLifecycle.Reopen, CaseState.Active, null },
        { CaseLifecycle.Reopen, CaseState.Closed, CaseState.Active },
        { CaseLifecycle.Reopen, CaseState.Archived, null },
        { CaseLifecycle.Reopen, CaseState.SoftDeleted, null },
        { CaseLifecycle.Reopen, CaseState.Purging, null },
        { CaseLifecycle.SoftDelete, CaseState.Active, CaseState.SoftDeleted },
        { CaseLifecycle.SoftDelete, CaseState.Closed, CaseState.SoftDeleted },
        { CaseLifecycle.SoftDelete, CaseState.Archived, CaseState.SoftDeleted },
        { CaseLifecycle.SoftDelete, CaseState.SoftDeleted, null },
        { CaseLifecycle.SoftDelete, CaseState.Purging, null },
        { CaseLifecycle.Restore, CaseState.Active, null },
        { CaseLifecycle.Restore, CaseState.Closed, null },
        { CaseLifecycle.Restore, CaseState.Archived, null },
        { CaseLifecycle.Restore, CaseState.SoftDeleted, CaseState.Closed },
        { CaseLifecycle.Restore, CaseState.Purging, null },
    };

    [Theory]
    [MemberData(nameof(OperationsByState))]
    public async Task AnOperationCarriesOutItsOwnTransitionAndNoOtherThatEndsInTheSameState(string operation, CaseState from, CaseState? expected)
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        // A soft-deleted case in this table was closed when it was deleted, so a restore has one answer.
        await PutInStateAsync(db, @case.Id, from, CaseState.Closed);
        var rows = await db.Audit.CountAsync(Ct);
        var audit = temp.Store.Audit;

        Task<CaseRecord> Ask() => operation switch
        {
            CaseLifecycle.Close => LifecycleOperations.CloseAsync(db, audit, TempDb.Alice, @case.Id, Now, Ct),
            CaseLifecycle.Reopen => LifecycleOperations.ReopenAsync(db, audit, TempDb.Alice, @case.Id, Now, Ct),
            CaseLifecycle.SoftDelete => LifecycleOperations.SoftDeleteAsync(db, audit, TempDb.Alice, @case.Id, "incident 1", Week, Now, Ct),
            _ => LifecycleOperations.RestoreAsync(db, audit, TempDb.Alice, @case.Id, Now, Ct),
        };

        if (expected is { } to)
        {
            Assert.Equal(to, (await Ask()).State);
            var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
            Assert.Equal((AuditActions.ForCaseOperation(operation), rows + 1), (row.Action, await db.Audit.CountAsync(Ct)));
            return;
        }

        var refused = await Assert.ThrowsAsync<InvalidCaseTransitionException>(Ask);
        Assert.Contains(Names.Of(from), refused.Message, StringComparison.Ordinal);
        Assert.Equal((from, rows), ((await db.Cases.AsNoTracking().SingleAsync(c => c.Id == @case.Id, Ct)).State, await db.Audit.CountAsync(Ct)));
    }

    [Fact]
    public async Task AReopenOfASoftDeletedCaseThatWasActiveIsNoRestore()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        await PutInStateAsync(db, @case.Id, CaseState.SoftDeleted, CaseState.Active);
        var rows = await db.Audit.CountAsync(Ct);

        var refused = await Assert.ThrowsAsync<InvalidCaseTransitionException>(() => LifecycleOperations.ReopenAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, Now, Ct));
        Assert.Contains("cannot be reopened", refused.Message, StringComparison.Ordinal);
        Assert.Contains("restore", refused.Message, StringComparison.Ordinal);
        Assert.Equal((CaseState.SoftDeleted, rows), ((await db.Cases.AsNoTracking().SingleAsync(c => c.Id == @case.Id, Ct)).State, await db.Audit.CountAsync(Ct)));
        Assert.Equal(CaseState.Active, (await LifecycleOperations.RestoreAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, Now, Ct)).State);
    }

    [Fact]
    public async Task CloseWaitsForARunningJobHoldsTheQueuedOnesAndReopenLetsThemRun()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var @case = await TempDb.AddCaseAsync(db, owner);
        var first = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(@case.Id, "a\n", "a.log"), Ct)).File;
        var second = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(@case.Id, "b\n", "b.log"), Ct)).File;
        await JobOperations.EnqueueAsync(db, first.Id, Now, Ct);
        var queued = await JobOperations.EnqueueAsync(db, second.Id, Now, Ct);
        var claimed = await JobOperations.ClaimAsync(db, "w:1", Now, Ct);
        Assert.NotNull(claimed);

        var refused = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.CloseAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, Now, Ct));
        Assert.Contains($"job {claimed.Job.Id} is parsing file {first.Id}", refused.Message, StringComparison.Ordinal);
        var deleteRefused = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, "incident 1", Week, Now, Ct));
        Assert.Contains("is parsing", deleteRefused.Message, StringComparison.Ordinal);

        await JobOperations.FailAsync(db, claimed.Job.Id, "w:1", "stopped for the test", Now, Ct);
        await LifecycleOperations.CloseAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, Now, Ct);
        var row = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Equal(AuditActions.CaseClose, row.Action);
        Assert.Contains("\"queued_jobs_held\":1", row.DetailsJson, StringComparison.Ordinal);

        // Held, not cancelled: nothing claims for a closed case, and nothing new is queued or recorded.
        Assert.Null(await JobOperations.ClaimAsync(db, "w:1", Now, Ct));
        Assert.Equal(JobState.Queued, await db.Jobs.AsNoTracking().Where(j => j.Id == queued.Id).Select(j => j.State).SingleAsync(Ct));
        await Assert.ThrowsAsync<CaseNotActiveException>(() => JobOperations.EnqueueAsync(db, first.Id, Now, Ct));
        var intake = await Assert.ThrowsAsync<CaseNotActiveException>(() => FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(@case.Id, "c\n", "c.log"), Ct));
        Assert.Contains("closed", intake.Message, StringComparison.Ordinal);

        await LifecycleOperations.ReopenAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, Now, Ct);
        Assert.Equal(queued.Id, (await JobOperations.ClaimAsync(db, "w:1", Now, Ct))!.Job.Id);
    }

    [Theory]
    [InlineData(CaseState.Closed)]
    [InlineData(CaseState.Archived)]
    [InlineData(CaseState.SoftDeleted)]
    [InlineData(CaseState.Purging)]
    public async Task NothingIsClaimedForACaseThatIsNotActive(CaseState state)
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var @case = await TempDb.AddCaseAsync(db, owner);
        var file = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(@case.Id, "a\n"), Ct)).File;
        await JobOperations.EnqueueAsync(db, file.Id, Now, Ct);
        await PutInStateAsync(db, @case.Id, state, prior: CaseState.Active);
        Assert.Null(await JobOperations.ClaimAsync(db, "w:1", Now, Ct));
    }

    [Fact]
    public async Task SoftDeleteNeedsTheNameTypedExactlyAndRestoreReturnsToTheStateLeft()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var @case = await TempDb.AddCaseAsync(db, owner, "Breach 2026/11");
        var closedAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        await LifecycleOperations.CloseAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, closedAt, Ct);

        foreach (var typed in new[] { "breach 2026/11", "Breach 2026/11 ", "", "Breach" })
        {
            var refused = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, typed, Week, Now, Ct));
            Assert.Contains("not the case's name", refused.Message, StringComparison.Ordinal);
        }

        var deletedAt = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        var deleted = await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, "Breach 2026/11", Week, deletedAt, Ct);
        Assert.Equal((CaseState.SoftDeleted, CaseState.Closed, deletedAt), (deleted.State, deleted.PriorState, deleted.DeletedAt));
        var row = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Contains("\"purge_due\":\"2026-11-10T09:00:00Z\"", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"by_admin\":false", row.DetailsJson, StringComparison.Ordinal);

        var restored = await LifecycleOperations.RestoreAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, Now, Ct);
        // The close time survives the round trip; the delete time and the prior state are cleared.
        Assert.Equal((CaseState.Closed, (CaseState?)null, (DateTime?)null, closedAt), (restored.State, restored.PriorState, restored.DeletedAt, restored.ClosedAt!.Value));
        var again = await Assert.ThrowsAsync<InvalidCaseTransitionException>(() => LifecycleOperations.RestoreAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, Now, Ct));
        Assert.Contains("closed", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachOperationAsksForItsRole()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var ownerId = await TempDb.AddUserAsync(db, "olga");
        var analystId = await TempDb.AddUserAsync(db, "ravi");
        var adminId = await AddAdminAsync(db, "ada");
        var @case = await TempDb.AddCaseAsync(db, ownerId);
        db.CaseMembers.Add(new CaseMember { CaseId = @case.Id, UserId = analystId, Role = MemberRole.Rw, GrantedAt = Now });
        await db.SaveChangesAsync(Ct);
        var owner = new AuditActor("olga", ownerId);
        var analyst = new AuditActor("ravi", analystId);
        var admin = new AuditActor("ada", adminId);

        Assert.Equal(LifecycleRole.Owner, CaseLifecycle.RequiredRole(CaseLifecycle.Close));
        Assert.Equal(LifecycleRole.Owner, CaseLifecycle.RequiredRole(CaseLifecycle.Reopen));
        Assert.Equal(LifecycleRole.Owner, CaseLifecycle.RequiredRole(CaseLifecycle.Archive));
        Assert.Equal(LifecycleRole.Owner, CaseLifecycle.RequiredRole(CaseLifecycle.Reimport));
        Assert.Equal(LifecycleRole.OwnerOrAdmin, CaseLifecycle.RequiredRole(CaseLifecycle.SoftDelete));
        Assert.Equal(LifecycleRole.OwnerOrAdmin, CaseLifecycle.RequiredRole(CaseLifecycle.Restore));
        Assert.Equal(LifecycleRole.Admin, CaseLifecycle.RequiredRole(CaseLifecycle.Purge));

        // A member who is not an owner, and an administrator who is not a member, do not close.
        var byAnalyst = await Assert.ThrowsAsync<LifecycleRoleException>(() => LifecycleOperations.CloseAsync(db, temp.Store.Audit, analyst, @case.Id, Now, Ct));
        Assert.Contains("an owner of the case", byAnalyst.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<LifecycleRoleException>(() => LifecycleOperations.CloseAsync(db, temp.Store.Audit, admin, @case.Id, Now, Ct));
        await LifecycleOperations.CloseAsync(db, temp.Store.Audit, owner, @case.Id, Now, Ct);
        await Assert.ThrowsAsync<LifecycleRoleException>(() => LifecycleOperations.ReopenAsync(db, temp.Store.Audit, analyst, @case.Id, Now, Ct));

        await Assert.ThrowsAsync<LifecycleRoleException>(() => LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, analyst, @case.Id, "incident 1", Week, Now, Ct));
        await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, admin, @case.Id, "incident 1", Week, Now, Ct);
        var row = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Equal(("ada", (long?)adminId), (row.Actor, row.ActorId));
        Assert.Contains("\"by_admin\":true", row.DetailsJson, StringComparison.Ordinal);

        // The purge: administrators only, the owner included in the refusal.
        await Assert.ThrowsAsync<LifecycleRoleException>(() => LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, owner, @case.Id, twoPerson: false, Now, Ct));
        await Assert.ThrowsAsync<LifecycleRoleException>(() => LifecycleOperations.RestoreAsync(db, temp.Store.Audit, analyst, @case.Id, Now, Ct));
        await LifecycleOperations.RestoreAsync(db, temp.Store.Audit, owner, @case.Id, Now, Ct);
        Assert.Equal(0, await db.CaseTasks.CountAsync(Ct));
    }

    [Fact]
    public async Task AnAccountActedAsFromTheBoxLeavesBothNamesInEveryRow()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var ownerId = await TempDb.AddUserAsync(db, "olga");
        var adminId = await AddAdminAsync(db, "ada");
        var @case = await TempDb.AddCaseAsync(db, ownerId);
        // What `urlog case ... --as <account>` passes: the account as the actor, the OS user beside it.
        var olga = new AuditActor("olga", ownerId) { OsUser = "svc-operator" };
        var ada = new AuditActor("ada", adminId) { OsUser = "svc-operator" };

        await LifecycleOperations.CloseAsync(db, temp.Store.Audit, olga, @case.Id, Now, Ct);
        await LifecycleOperations.ReopenAsync(db, temp.Store.Audit, olga, @case.Id, Now, Ct);
        await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, olga, @case.Id, "incident 1", Week, Now, Ct);
        await LifecycleOperations.RestoreAsync(db, temp.Store.Audit, olga, @case.Id, Now, Ct);
        await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, ada, @case.Id, "incident 1", Week, Now, Ct);
        await LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, ada, @case.Id, twoPerson: false, Now, Ct);

        var rows = await db.Audit.AsNoTracking().Where(a => a.TargetType == AuditTargets.Case && a.Action != AuditActions.CaseCreate).OrderBy(a => a.Seq).ToListAsync(Ct);
        Assert.Equal(
            [AuditActions.CaseClose, AuditActions.CaseReopen, AuditActions.CaseSoftDelete, AuditActions.CaseRestore, AuditActions.CaseSoftDelete, AuditActions.CasePurgeRequest],
            rows.Select(r => r.Action));
        Assert.Equal(["olga", "olga", "olga", "olga", "ada", "ada"], rows.Select(r => r.Actor));
        Assert.Equal([ownerId, ownerId, ownerId, ownerId, adminId, adminId], rows.Select(r => r.ActorId!.Value));
        Assert.All(rows, row => Assert.Equal("svc-operator", JsonDocument.Parse(row.DetailsJson).RootElement.GetProperty("os_user").GetString()));

        // The operator on the box acting as nobody's account is the actor, and there is no second name to give.
        await LifecycleOperations.RestoreAsync(db, temp.Store.Audit, new AuditActor("svc-operator"), @case.Id, Now, Ct);
        var plain = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Equal(("svc-operator", (long?)null), (plain.Actor, plain.ActorId));
        Assert.False(JsonDocument.Parse(plain.DetailsJson).RootElement.TryGetProperty("os_user", out _));
    }

    [Fact]
    public async Task TwoPersonPurgeNeedsTwoDifferentAdministrators()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var ownerId = await TempDb.AddUserAsync(db, "olga");
        var ada = new AuditActor("ada", await AddAdminAsync(db, "ada"));
        var bo = new AuditActor("bo", await AddAdminAsync(db, "bo"));
        var @case = await TempDb.AddCaseAsync(db, ownerId);

        var early = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, ada, @case.Id, twoPerson: true, Now, Ct));
        Assert.Contains("is active; only a soft-deleted case can be purged", early.Message, StringComparison.Ordinal);
        await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, "incident 1", Week, Now, Ct);

        // The operator on the box is nobody's account: with the flag on a request has to name one.
        await Assert.ThrowsAsync<LifecycleRoleException>(() => LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, new AuditActor("root"), @case.Id, twoPerson: true, Now, Ct));

        var request = await LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, ada, @case.Id, twoPerson: true, Now, Ct);
        Assert.Equal((CaseTaskState.Requested, "ada", (string?)null), (request.State, request.RequestedBy, request.ConfirmedBy));
        Assert.Empty(await LifecycleOperations.PendingPurgesAsync(db, Ct));
        // Nothing marks the case on a request alone.
        Assert.False(await LifecycleOperations.MarkPurgingAsync(db, temp.Store.Audit, request.Id, Backup, Now, Ct));

        var self = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, ada, @case.Id, twoPerson: true, Now, Ct));
        Assert.Contains("a second administrator has to confirm", self.Message, StringComparison.Ordinal);

        var confirmed = await LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, bo, @case.Id, twoPerson: true, Now, Ct);
        Assert.Equal((request.Id, CaseTaskState.Running, "ada", "bo"), (confirmed.Id, confirmed.State, confirmed.RequestedBy, confirmed.ConfirmedBy));
        Assert.Single(await LifecycleOperations.PendingPurgesAsync(db, Ct));
        var rows = await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.CasePurgeRequest).OrderBy(a => a.Seq).ToListAsync(Ct);
        Assert.Equal(["ada", "bo"], rows.Select(r => r.Actor));
        Assert.Contains("\"step\":\"requested\"", rows[0].DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"step\":\"confirmed\"", rows[1].DetailsJson, StringComparison.Ordinal);

        var third = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, ada, @case.Id, twoPerson: true, Now, Ct));
        Assert.Contains("already has a purge under way", third.Message, StringComparison.Ordinal);

        // The mark names both.
        Assert.True(await LifecycleOperations.MarkPurgingAsync(db, temp.Store.Audit, request.Id, Backup, Now, Ct));
        var mark = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Equal((AuditActions.CasePurge, "bo"), (mark.Action, mark.Actor));
        Assert.Contains("\"requested_by\":\"ada\"", mark.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"confirmed_by\":\"bo\"", mark.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"backup_sha256\":\"{Backup.Sha256}\"", mark.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheFlagOneAdministratorReleasesAPurgeAndARestoreWithdrawsIt()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var ownerId = await TempDb.AddUserAsync(db, "olga");
        var ada = new AuditActor("ada", await AddAdminAsync(db, "ada"));
        var @case = await TempDb.AddCaseAsync(db, ownerId);
        await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, "incident 1", Week, Now, Ct);

        var task = await LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, ada, @case.Id, twoPerson: false, Now, Ct);
        Assert.Equal(CaseTaskState.Running, task.State);

        // Not marked yet: the owner can still have the case back, and the purge is gone with the same commit.
        var restored = await LifecycleOperations.RestoreAsync(db, temp.Store.Audit, new AuditActor("olga", ownerId), @case.Id, Now, Ct);
        Assert.Equal(CaseState.Active, restored.State);
        var row = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Contains("\"purge_requests_withdrawn\":1", row.DetailsJson, StringComparison.Ordinal);
        var after = await db.CaseTasks.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(CaseTaskState.Withdrawn, after.State);
        Assert.Contains("restored by olga", after.Message, StringComparison.Ordinal);
        Assert.False(await LifecycleOperations.MarkPurgingAsync(db, temp.Store.Audit, task.Id, Backup, Now, Ct));
        Assert.Equal(CaseState.Active, await db.Cases.AsNoTracking().Select(c => c.State).SingleAsync(Ct));
    }

    [Fact]
    public async Task UnfinishedWorkRefusesEveryOtherTransitionAndAfterTheMarkThereIsNoRestore()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var @case = await TempDb.AddCaseAsync(db, owner);
        db.CaseTasks.Add(new CaseTask { CaseId = @case.Id, Kind = CaseTaskKind.Archive, State = CaseTaskState.Running, RequestedBy = "olga", CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync(Ct);

        var close = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.CloseAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, Now, Ct));
        Assert.Contains("unfinished work (archive, running, asked for by olga)", close.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, "incident 1", Week, Now, Ct));

        await db.CaseTasks.ExecuteUpdateAsync(s => s.SetProperty(t => t.State, CaseTaskState.Done), Ct);
        await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, "incident 1", Week, Now, Ct);
        var task = await LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, twoPerson: false, Now, Ct);
        Assert.True(await LifecycleOperations.MarkPurgingAsync(db, temp.Store.Audit, task.Id, Backup, Now, Ct));
        var restore = await Assert.ThrowsAsync<InvalidCaseTransitionException>(() => LifecycleOperations.RestoreAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, Now, Ct));
        Assert.Contains("purging", restore.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScheduledPurgeIsDueAfterTheWindowAndNotBefore()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var due = await TempDb.AddCaseAsync(db, owner, "due");
        var restored = await TempDb.AddCaseAsync(db, owner, "restored");
        var asked = await TempDb.AddCaseAsync(db, owner, "asked");
        var open = await TempDb.AddCaseAsync(db, owner, "open");
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (var @case in new[] { due, restored, asked })
        {
            await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, @case.Name, Week, t0, Ct);
        }

        await LifecycleOperations.RestoreAsync(db, temp.Store.Audit, TempDb.Alice, restored.Id, t0.AddDays(1), Ct);
        await LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, TempDb.Alice, asked.Id, twoPerson: false, t0.AddDays(2), Ct);

        Assert.Empty(await LifecycleOperations.DueForPurgeAsync(db, Week, t0.AddDays(7).AddSeconds(-1), Ct));
        Assert.Equal([due.Id], await LifecycleOperations.DueForPurgeAsync(db, Week, t0.AddDays(7), Ct));
        Assert.Equal([due.Id], await LifecycleOperations.DueForPurgeAsync(db, Week, t0.AddDays(30), Ct));
        Assert.NotEqual(open.Id, due.Id);
    }

    [Fact]
    public async Task ThePurgeCancelsQueuedJobsAndItsLastStepRemovesEveryRowOfTheCaseAndKeepsWhatItWas()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var ownerId = await TempDb.AddUserAsync(db, "olga");
        var other = await TempDb.AddCaseAsync(db, ownerId, "kept");
        var keptFile = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(other.Id, "kept\n", "kept.log"), Ct)).File;
        var @case = await TempDb.AddCaseAsync(db, ownerId, "to purge");
        var host = await HostOperations.GetOrCreateAsync(db, @case.Id, "web-01", Now, Ct);
        var container = TempDb.NewFile(@case.Id, "container bytes", "bundle.zip");
        container.HostId = host.Id;
        container = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, container, Ct)).File;
        var member = new EvidenceFile
        {
            CaseId = @case.Id, ParentFileId = container.Id, RootFileId = container.Id, ArchiveDepth = 1, ArchivePath = "bundle.zip!access.log", MemberOrdinal = 0,
            Sha256 = Sha("member"), Size = 6, OriginalName = "access.log", StoredPath = "bundle.zip", Source = FileSource.Web, Uploader = "alice", ReceivedAt = Now, Encoding = "utf8",
        };
        await FileOperations.AddMembersAsync(db, temp.Store.Audit, container.Id, [new MemberDraft(member, "web-01")], 1, Now, Ct);
        var plain = (await FileOperations.AddAsync(db, temp.Store.Audit, TempDb.Alice, TempDb.NewFile(@case.Id, "plain\n", "plain.log"), Ct)).File;
        var queued = await JobOperations.EnqueueAsync(db, plain.Id, Now, Ct);
        var hashes = await db.Files.AsNoTracking().Where(f => f.CaseId == @case.Id).Select(f => f.Sha256).ToListAsync(Ct);
        Assert.Equal(3, hashes.Count);

        await LifecycleOperations.SoftDeleteAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, "to purge", Week, Now, Ct);
        var task = await LifecycleOperations.RequestPurgeAsync(db, temp.Store.Audit, TempDb.Alice, @case.Id, twoPerson: false, Now, Ct);
        Assert.True(await LifecycleOperations.MarkPurgingAsync(db, temp.Store.Audit, task.Id, Backup, Now, Ct));
        var cancelled = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == queued.Id, Ct);
        Assert.Equal((JobState.Cancelled, "cancelled: the case is being purged"), (cancelled.State, cancelled.Message));
        var marked = await db.CaseTasks.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((CaseTaskState.Committed, LifecycleOperations.StepStore, Backup.Sha256), (marked.State, marked.Step, marked.BackupSha256));

        // The sync that mirrors generations into the event store leaves a purging case alone.
        db.Jobs.Add(new Job { CaseId = @case.Id, FileId = plain.Id, Kind = JobKind.Parse, State = JobState.Done, Generation = 4, CreatedAt = Now });
        db.Jobs.Add(new Job { CaseId = other.Id, FileId = keptFile.Id, Kind = JobKind.Parse, State = JobState.Done, Generation = 2, CreatedAt = Now });
        await db.SaveChangesAsync(Ct);
        Assert.Equal([keptFile.Id], (await JobOperations.CommittedGenerationsAsync(db, Ct)).Keys);

        var summary = await LifecycleOperations.CompletePurgeAsync(db, temp.Store.Audit, @case.Id, Now, Ct);
        Assert.NotNull(summary);
        Assert.Equal(("to purge", ownerId, "olga", 3, 2, (long)("container bytes".Length + "plain\n".Length)), (summary.CaseName, summary.OwnerId, summary.OwnerName, summary.FileCount, summary.ArrivedFileCount, summary.TotalBytes));
        Assert.Equal(LifecycleOperations.EvidenceDigest(hashes), summary.EvidenceSha256);
        Assert.Equal(Backup.Sha256, summary.BackupSha256);

        var row = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Equal((AuditActions.CasePurgeComplete, AuditActions.SystemActor, (long?)@case.Id), (row.Action, row.Actor, row.TargetId));
        using var details = JsonDocument.Parse(row.DetailsJson);
        Assert.Equal("to purge", details.RootElement.GetProperty("case_name").GetString());
        Assert.Equal("olga", details.RootElement.GetProperty("owner_name").GetString());
        Assert.Equal(ownerId, details.RootElement.GetProperty("owner_id").GetInt64());
        Assert.Equal(3, details.RootElement.GetProperty("file_count").GetInt32());
        Assert.Equal(21, details.RootElement.GetProperty("total_bytes").GetInt64());
        Assert.Equal(summary.EvidenceSha256, details.RootElement.GetProperty("evidence_sha256").GetString());
        Assert.Equal(Backup.Sha256, details.RootElement.GetProperty("backup_sha256").GetString());

        // Zero rows of the case in every table but the audit chain; the other case is whole.
        await using var connection = new SqliteConnection(MetadataStore.ConnectionString(temp.Path));
        await connection.OpenAsync(Ct);
        foreach (var (table, column) in new[] { ("cases", "id"), ("case_members", "case_id"), ("hosts", "case_id"), ("files", "case_id"), ("jobs", "case_id"), ("case_tasks", "case_id"), ("case_stats", "case_id") })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table} WHERE {column} = {@case.Id}";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(Ct))!);
        }

        Assert.Equal(CaseState.Active, await db.Cases.AsNoTracking().Where(c => c.Id == other.Id).Select(c => c.State).SingleAsync(Ct));
        Assert.True(await db.Files.AnyAsync(f => f.Id == keptFile.Id, Ct));
        Assert.True(await db.Audit.AnyAsync(a => a.TargetType == AuditTargets.Case && a.TargetId == @case.Id && a.Action == AuditActions.CaseCreate, Ct));
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);

        // A repeat after the commit (a resume that found the case gone) is not an error and writes nothing.
        var count = await db.Audit.CountAsync(Ct);
        Assert.Null(await LifecycleOperations.CompletePurgeAsync(db, temp.Store.Audit, @case.Id, Now, Ct));
        Assert.Equal(count, await db.Audit.CountAsync(Ct));

        // The id is free again, and the trail tells the two cases apart by name.
        var reused = await TempDb.AddCaseAsync(db, ownerId, "next case");
        Assert.Equal(@case.Id, reused.Id);
    }

    [Fact]
    public void TheEvidenceDigestIsOverTheSortedHashesOnePerLine()
    {
        var a = new string('a', 64);
        var b = new string('b', 64);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(a + "\n" + b + "\n")));
        Assert.Equal(expected, LifecycleOperations.EvidenceDigest([b, a]));
        Assert.Equal(expected, LifecycleOperations.EvidenceDigest([a, b]));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), LifecycleOperations.EvidenceDigest([]));
    }

    [Fact]
    public async Task TheMigrationAddsTheTaskTableAndTheColumnAndKeepsTheIdFloors()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var connection = new SqliteConnection(MetadataStore.ConnectionString(temp.Path));
        await connection.OpenAsync(Ct);
        async Task<List<string>> NamesAsync(string sql)
        {
            var names = new List<string>();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        Assert.Contains("verified_at", await NamesAsync("SELECT name FROM pragma_table_info('files')"));
        Assert.Contains("step", await NamesAsync("SELECT name FROM pragma_table_info('case_tasks')"));
        // The floors on case and file ids are triggers; a migration that rebuilt either table would have dropped them.
        var triggers = await NamesAsync("SELECT tbl_name FROM sqlite_master WHERE type = 'trigger'");
        Assert.Contains("files", triggers);
        Assert.Contains("cases", triggers);
        var bad = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO case_tasks (case_id, kind, state, attempt, requested_by, created_at, updated_at) VALUES (1, 'purge', 'paused', 0, 'x', 'now', 'now')";
            await command.ExecuteNonQueryAsync(Ct);
        });
        Assert.Contains("CHECK", bad.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static async Task<long> AddAdminAsync(MetadataDb db, string name)
    {
        var user = new User { Name = name, PasswordHash = "argon2id$placeholder", CreatedAt = DateTime.UtcNow, Role = UserRole.Admin };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user.Id;
    }

    /// <summary>Puts the row straight into a state, as an earlier run of the operations would have left it.</summary>
    private static async Task PutInStateAsync(MetadataDb db, long caseId, CaseState state, CaseState prior)
    {
        var now = DateTime.UtcNow;
        await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(
            s => s.SetProperty(c => c.State, state)
                .SetProperty(c => c.PriorState, state is CaseState.SoftDeleted or CaseState.Purging ? prior : (CaseState?)null)
                .SetProperty(c => c.DeletedAt, state is CaseState.SoftDeleted or CaseState.Purging ? now : (DateTime?)null),
            Ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>The operation that would move the case to <paramref name="to"/>, as a caller would pick it.</summary>
    private static async Task ApplyAsync(TempDb temp, MetadataDb db, long caseId, CaseState from, CaseState to)
    {
        var audit = temp.Store.Audit;
        switch (to)
        {
            case CaseState.Closed when from is CaseState.Active or CaseState.Closed:
                await LifecycleOperations.CloseAsync(db, audit, TempDb.Alice, caseId, Now, Ct);
                break;
            case CaseState.Active when from is CaseState.Closed or CaseState.Active:
                await LifecycleOperations.ReopenAsync(db, audit, TempDb.Alice, caseId, Now, Ct);
                break;
            case CaseState.Active when from == CaseState.SoftDeleted:
                await LifecycleOperations.RestoreAsync(db, audit, TempDb.Alice, caseId, Now, Ct);
                break;
            case CaseState.SoftDeleted:
                await LifecycleOperations.SoftDeleteAsync(db, audit, TempDb.Alice, caseId, "incident 1", Week, Now, Ct);
                break;
            case CaseState.Purging:
                var task = await LifecycleOperations.RequestPurgeAsync(db, audit, TempDb.Alice, caseId, twoPerson: false, Now, Ct);
                Assert.True(await LifecycleOperations.MarkPurgingAsync(db, audit, task.Id, Backup, Now, Ct));
                break;
            case CaseState.Purged:
                Assert.NotNull(await LifecycleOperations.CompletePurgeAsync(db, audit, caseId, Now, Ct));
                break;
            default:
                // Archive and re-import are carried out by their own work; here the table alone answers.
                await CaseOperations.TransitionAsync(db, audit, TempDb.Alice, caseId, to, Now, Ct);
                break;
        }
    }
}
