using Urlog.Core.Audit;
using Urlog.Core.Metadata;

namespace Urlog.Metadata.Tests;

/// <summary>The case ACL's reads: membership by state, the allowed-case list by state, no cross-user leakage, the role order.</summary>
public sealed class CaseAclTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MembershipFollowsTheCaseState()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var bob = await TempDb.AddUserAsync(db, "bob");
        var @case = await TempDb.AddCaseAsync(db, alice);

        var mine = await CaseAcl.MembershipAsync(db, alice, @case.Id, Ct);
        Assert.NotNull(mine);
        Assert.Equal((MemberRole.Owner, "incident 1"), (mine.Role, mine.Case.Name));
        Assert.Null(await CaseAcl.MembershipAsync(db, bob, @case.Id, Ct));
        Assert.Null(await CaseAcl.MembershipAsync(db, alice, 4242, Ct));

        await CaseOperations.TransitionAsync(db, AuditChain.Default, TempDb.Alice, @case.Id, CaseState.Closed, DateTime.UtcNow, Ct);
        Assert.NotNull(await CaseAcl.MembershipAsync(db, alice, @case.Id, Ct));
        Assert.Equal([@case.Id], await CaseAcl.AllowedCaseIdsAsync(db, alice, Ct));
        await CaseOperations.TransitionAsync(db, AuditChain.Default, TempDb.Alice, @case.Id, CaseState.Archived, DateTime.UtcNow, Ct);
        // Archived: visible (listed, opened), but its partitions are gone, so not in SQL_allowed_cases.
        Assert.NotNull(await CaseAcl.MembershipAsync(db, alice, @case.Id, Ct));
        Assert.Empty(await CaseAcl.AllowedCaseIdsAsync(db, alice, Ct));
        await CaseOperations.TransitionAsync(db, AuditChain.Default, TempDb.Alice, @case.Id, CaseState.SoftDeleted, DateTime.UtcNow, Ct);
        // Soft-deleted: hidden, like an unknown id.
        Assert.Null(await CaseAcl.MembershipAsync(db, alice, @case.Id, Ct));
        Assert.Empty(await CaseAcl.ListForUserAsync(db, alice, Ct));
    }

    [Fact]
    public async Task EachUserSeesOnlyTheirOwnList()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var bob = await TempDb.AddUserAsync(db, "bob");
        var one = await TempDb.AddCaseAsync(db, alice, "incident 1");
        var two = await TempDb.AddCaseAsync(db, bob, "incident 2");
        var three = await TempDb.AddCaseAsync(db, alice, "incident 3");
        await MemberOperations.ShareAsync(db, temp.Store.Audit, TempDb.Alice, three.Id, bob, MemberRole.Ro, false, DateTime.UtcNow, Ct);

        // Interleaved: no call changes what the next one answers.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal([one.Id, three.Id], await CaseAcl.AllowedCaseIdsAsync(db, alice, Ct));
            Assert.Equal([two.Id, three.Id], await CaseAcl.AllowedCaseIdsAsync(db, bob, Ct));
        }

        var bobs = await CaseAcl.ListForUserAsync(db, bob, Ct);
        Assert.Equal([(two.Id, MemberRole.Owner), (three.Id, MemberRole.Ro)], bobs.Select(m => (m.Case.Id, m.Role)).ToArray());
    }

    [Theory]
    [InlineData(MemberRole.Owner, MemberRole.Owner, true)]
    [InlineData(MemberRole.Owner, MemberRole.Ro, true)]
    [InlineData(MemberRole.Rw, MemberRole.Owner, false)]
    [InlineData(MemberRole.Rw, MemberRole.Rw, true)]
    [InlineData(MemberRole.Ro, MemberRole.Rw, false)]
    [InlineData(MemberRole.Ro, MemberRole.Ro, true)]
    public void OwnerContainsRwContainsRo(MemberRole actual, MemberRole minimum, bool satisfies) => Assert.Equal(satisfies, CaseAcl.Satisfies(actual, minimum));
}
