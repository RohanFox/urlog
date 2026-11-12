using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>The state machine refuses every pair the lifecycle table does not list (all 36), restore only to the prior state.</summary>
public sealed class CaseLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The lifecycle table of the design record, written out independently of <see cref="CaseLifecycle"/>. Prior state Active for the soft-deleted row.</summary>
    private static readonly Dictionary<(CaseState, CaseState), string> Allowed = new()
    {
        [(CaseState.Active, CaseState.Closed)] = "close",
        [(CaseState.Active, CaseState.SoftDeleted)] = "soft_delete",
        [(CaseState.Closed, CaseState.Active)] = "reopen",
        [(CaseState.Closed, CaseState.Archived)] = "archive",
        [(CaseState.Closed, CaseState.SoftDeleted)] = "soft_delete",
        [(CaseState.Archived, CaseState.Closed)] = "reimport",
        [(CaseState.Archived, CaseState.SoftDeleted)] = "soft_delete",
        [(CaseState.SoftDeleted, CaseState.Active)] = "restore",
        [(CaseState.SoftDeleted, CaseState.Purging)] = "purge",
        [(CaseState.Purging, CaseState.Purged)] = "purge_complete",
    };

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

    public static TheoryData<CaseState, CaseState> RestorePairs
    {
        get
        {
            var data = new TheoryData<CaseState, CaseState>();
            foreach (var prior in new[] { CaseState.Active, CaseState.Closed, CaseState.Archived })
            {
                foreach (var to in new[] { CaseState.Active, CaseState.Closed, CaseState.Archived })
                {
                    data.Add(prior, to);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void EveryPairMatchesTheTable(CaseState from, CaseState to)
    {
        Assert.Equal(36, AllPairs.Count);
        var @case = new CaseRecord { Name = "c", OwnerId = 1, CreatedAt = DateTime.UtcNow, State = from, PriorState = from == CaseState.SoftDeleted ? CaseState.Active : null };
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        if (Allowed.TryGetValue((from, to), out var expected))
        {
            Assert.True(CaseLifecycle.CanTransition(@case, to, out var operation));
            Assert.Equal(expected, operation);
            Assert.Equal(expected, CaseLifecycle.Apply(@case, to, now));
            Assert.Equal(to, @case.State);
        }
        else
        {
            Assert.False(CaseLifecycle.CanTransition(@case, to, out _));
            var error = Assert.Throws<InvalidCaseTransitionException>(() => CaseLifecycle.Apply(@case, to, now));
            Assert.Contains(Names.Of(from), error.Message, StringComparison.Ordinal);
            Assert.Contains(Names.Of(to), error.Message, StringComparison.Ordinal);
            Assert.Equal(from, @case.State);
        }
    }

    [Theory]
    [MemberData(nameof(RestorePairs))]
    public void RestoreReturnsOnlyToThePriorState(CaseState prior, CaseState to)
    {
        var @case = new CaseRecord { Name = "c", OwnerId = 1, CreatedAt = DateTime.UtcNow, State = CaseState.SoftDeleted, PriorState = prior, DeletedAt = DateTime.UtcNow };
        if (prior == to)
        {
            Assert.Equal("restore", CaseLifecycle.Apply(@case, to, DateTime.UtcNow));
            Assert.Equal(prior, @case.State);
            Assert.Null(@case.PriorState);
            Assert.Null(@case.DeletedAt);
        }
        else
        {
            var error = Assert.Throws<InvalidCaseTransitionException>(() => CaseLifecycle.Apply(@case, to, DateTime.UtcNow));
            Assert.Contains("restores to " + Names.Of(prior), error.Message, StringComparison.Ordinal);
            Assert.Equal(CaseState.SoftDeleted, @case.State);
        }
    }

    [Fact]
    public void ReimportNeverLandsInActive()
    {
        // Archive is reachable only from closed, so a re-imported case is closed: frozen evidence stays frozen.
        Assert.Null(CaseLifecycle.Operation(CaseState.Active, CaseState.Archived));
        Assert.Null(CaseLifecycle.Operation(CaseState.Archived, CaseState.Active));
        Assert.Equal("reimport", CaseLifecycle.Operation(CaseState.Archived, CaseState.Closed));
        foreach (var to in Enum.GetValues<CaseState>())
        {
            if (to is not CaseState.Closed and not CaseState.SoftDeleted)
            {
                Assert.Null(CaseLifecycle.Operation(CaseState.Archived, to));
            }
        }

        var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var @case = new CaseRecord { Name = "c", OwnerId = 1, CreatedAt = t0 };
        CaseLifecycle.Apply(@case, CaseState.Closed, t0.AddDays(1));
        CaseLifecycle.Apply(@case, CaseState.Archived, t0.AddDays(2));
        CaseLifecycle.Apply(@case, CaseState.Closed, t0.AddDays(3));
        Assert.Equal(CaseState.Closed, @case.State);
        Assert.Equal(t0.AddDays(3), @case.ClosedAt);
        Assert.Equal(t0.AddDays(2), @case.ArchivedAt);
    }

    [Fact]
    public void TimestampsFollowTheOperations()
    {
        var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var @case = new CaseRecord { Name = "c", OwnerId = 1, CreatedAt = t0 };

        CaseLifecycle.Apply(@case, CaseState.Closed, t0.AddHours(1));
        Assert.Equal(t0.AddHours(1), @case.ClosedAt);

        CaseLifecycle.Apply(@case, CaseState.SoftDeleted, t0.AddHours(2));
        Assert.Equal(CaseState.Closed, @case.PriorState);
        Assert.Equal(t0.AddHours(2), @case.DeletedAt);

        // Restore undoes the delete and leaves the close time alone.
        CaseLifecycle.Apply(@case, CaseState.Closed, t0.AddHours(3));
        Assert.Equal(CaseState.Closed, @case.State);
        Assert.Null(@case.PriorState);
        Assert.Null(@case.DeletedAt);
        Assert.Equal(t0.AddHours(1), @case.ClosedAt);

        CaseLifecycle.Apply(@case, CaseState.SoftDeleted, t0.AddHours(4));
        CaseLifecycle.Apply(@case, CaseState.Purging, t0.AddHours(5));
        Assert.Equal(t0.AddHours(5), @case.PurgingAt);
        CaseLifecycle.Apply(@case, CaseState.Purged, t0.AddHours(6));
        Assert.Equal(t0.AddHours(6), @case.PurgedAt);
        Assert.Equal(t0.AddHours(4), @case.DeletedAt);
    }

    [Fact]
    public async Task TransitionsPersistAndAStaleTransitionFails()
    {
        await using var temp = await TempDb.CreateAsync();
        long caseId;
        await using (var db = temp.Open())
        {
            var owner = await TempDb.AddUserAsync(db);
            caseId = (await TempDb.AddCaseAsync(db, owner)).Id;
            Assert.Equal("close", await CaseOperations.TransitionAsync(db, AuditChain.Default, TempDb.Alice, caseId, CaseState.Closed, DateTime.UtcNow, Ct));
        }

        await using (var check = temp.Open())
        {
            var @case = await check.Cases.SingleAsync(c => c.Id == caseId, Ct);
            Assert.Equal(CaseState.Closed, @case.State);
            Assert.NotNull(@case.ClosedAt);
            Assert.Equal(DateTimeKind.Utc, @case.ClosedAt!.Value.Kind);
            await Assert.ThrowsAsync<InvalidCaseTransitionException>(() => CaseOperations.TransitionAsync(check, AuditChain.Default, TempDb.Alice, caseId, CaseState.Purged, DateTime.UtcNow, Ct));
            await Assert.ThrowsAsync<CaseNotFoundException>(() => CaseOperations.TransitionAsync(check, AuditChain.Default, TempDb.Alice, 12345, CaseState.Closed, DateTime.UtcNow, Ct));
        }

        // Two contexts read `closed`; the first reopens; the second's reopen is stale and must not apply twice.
        await using var first = temp.Open();
        await using var second = temp.Open();
        var a = await first.Cases.SingleAsync(c => c.Id == caseId, Ct);
        var b = await second.Cases.SingleAsync(c => c.Id == caseId, Ct);
        CaseLifecycle.Apply(a, CaseState.Active, DateTime.UtcNow);
        await first.SaveChangesAsync(Ct);
        CaseLifecycle.Apply(b, CaseState.Active, DateTime.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task CreateWritesTheOwnerMemberRow()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var @case = await CaseOperations.CreateAsync(db, AuditChain.Default, TempDb.Alice, "  incident 42  ", owner, DateTime.UtcNow, Ct);

        Assert.Equal("incident 42", @case.Name);
        Assert.Equal(CaseState.Active, @case.State);
        var member = await db.CaseMembers.SingleAsync(m => m.CaseId == @case.Id, Ct);
        Assert.Equal(owner, member.UserId);
        Assert.Equal(MemberRole.Owner, member.Role);
    }
}
