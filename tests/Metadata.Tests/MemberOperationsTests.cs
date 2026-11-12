using System.Text;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The case ACL's writes: share, role change, unshare with their rows; the last-owner guard; an admin's grant,
/// to themselves included, is a row of the case's own trail.
/// </summary>
public sealed class MemberOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ShareChangeRoleAndUnshareWriteTheirRows()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var bob = await TempDb.AddUserAsync(db, "bob");
        var @case = await TempDb.AddCaseAsync(db, alice);
        var owner = new AuditActor("alice", alice);

        var member = await MemberOperations.ShareAsync(db, temp.Store.Audit, owner, @case.Id, bob, MemberRole.Ro, byAdmin: false, DateTime.UtcNow, Ct);
        Assert.Equal(MemberRole.Ro, member.Role);
        await Assert.ThrowsAsync<AlreadyMemberException>(() => MemberOperations.ShareAsync(db, temp.Store.Audit, owner, @case.Id, bob, MemberRole.Rw, false, DateTime.UtcNow, Ct));
        await Assert.ThrowsAsync<UserNotFoundException>(() => MemberOperations.ShareAsync(db, temp.Store.Audit, owner, @case.Id, 4242, MemberRole.Rw, false, DateTime.UtcNow, Ct));
        await Assert.ThrowsAsync<CaseNotFoundException>(() => MemberOperations.ShareAsync(db, temp.Store.Audit, owner, 4242, bob, MemberRole.Rw, false, DateTime.UtcNow, Ct));

        var changed = await MemberOperations.ChangeRoleAsync(db, temp.Store.Audit, owner, @case.Id, bob, MemberRole.Rw, byAdmin: false, DateTime.UtcNow, Ct);
        Assert.Equal(MemberRole.Rw, changed.Role);
        await MemberOperations.UnshareAsync(db, temp.Store.Audit, owner, @case.Id, bob, byAdmin: false, DateTime.UtcNow, Ct);
        await Assert.ThrowsAsync<NotAMemberException>(() => MemberOperations.UnshareAsync(db, temp.Store.Audit, owner, @case.Id, bob, false, DateTime.UtcNow, Ct));
        await Assert.ThrowsAsync<NotAMemberException>(() => MemberOperations.ChangeRoleAsync(db, temp.Store.Audit, owner, @case.Id, bob, MemberRole.Ro, false, DateTime.UtcNow, Ct));
        Assert.Equal(1, await db.CaseMembers.CountAsync(m => m.CaseId == @case.Id, Ct));

        var rows = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Skip(2).ToListAsync(Ct);
        Assert.Equal(["case_share", "case_role_change", "case_unshare"], rows.Select(r => r.Action).ToArray());
        Assert.All(rows, r => Assert.Equal((AuditTargets.Case, (long?)@case.Id, "alice", (long?)alice), (r.TargetType, r.TargetId, r.Actor, r.ActorId)));
        Assert.Equal($$"""{"by_admin":false,"case_name":"incident 1","role":"ro","user_id":{{bob}},"user_name":"bob"}""", rows[0].DetailsJson);
        Assert.Equal($$"""{"by_admin":false,"case_name":"incident 1","from":"ro","to":"rw","user_id":{{bob}},"user_name":"bob"}""", rows[1].DetailsJson);
        Assert.Equal($$"""{"by_admin":false,"case_name":"incident 1","role":"rw","user_id":{{bob}},"user_name":"bob"}""", rows[2].DetailsJson);
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    [Fact]
    public async Task ACaseKeepsAtLeastOneOwner()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var bob = await TempDb.AddUserAsync(db, "bob");
        var @case = await TempDb.AddCaseAsync(db, alice);
        var owner = new AuditActor("alice", alice);

        await Assert.ThrowsAsync<LastOwnerException>(() => MemberOperations.ChangeRoleAsync(db, temp.Store.Audit, owner, @case.Id, alice, MemberRole.Rw, false, DateTime.UtcNow, Ct));
        await Assert.ThrowsAsync<LastOwnerException>(() => MemberOperations.UnshareAsync(db, temp.Store.Audit, owner, @case.Id, alice, false, DateTime.UtcNow, Ct));
        await MemberOperations.ShareAsync(db, temp.Store.Audit, owner, @case.Id, bob, MemberRole.Owner, false, DateTime.UtcNow, Ct);
        // With a second owner, the first may step down and leave.
        await MemberOperations.ChangeRoleAsync(db, temp.Store.Audit, owner, @case.Id, alice, MemberRole.Ro, false, DateTime.UtcNow, Ct);
        await MemberOperations.UnshareAsync(db, temp.Store.Audit, owner, @case.Id, alice, false, DateTime.UtcNow, Ct);
        var left = await db.CaseMembers.AsNoTracking().SingleAsync(m => m.CaseId == @case.Id, Ct);
        Assert.Equal((bob, MemberRole.Owner), (left.UserId, left.Role));
        // A refused change wrote no row.
        Assert.Equal(["genesis", "case_create", "case_share", "case_role_change", "case_unshare"], await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync(Ct));
    }

    [Fact]
    public async Task AnAdminGrantToThemselvesIsInTheCaseTrail()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var root = await TempDb.AddUserAsync(db, "root");
        var @case = await TempDb.AddCaseAsync(db, alice);
        var admin = new AuditActor("root", root);

        await MemberOperations.ShareAsync(db, temp.Store.Audit, admin, @case.Id, root, MemberRole.Ro, byAdmin: true, DateTime.UtcNow, Ct);

        using var stream = new MemoryStream();
        Assert.Equal(2, await AuditExport.WriteCaseAsync(db, @case.Id, stream, Ct));
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var share = JsonDocument.Parse(lines[1]);
        var root1 = share.RootElement;
        Assert.Equal("case_share", root1.GetProperty("action").GetString());
        Assert.Equal("root", root1.GetProperty("actor").GetString());
        using var details = JsonDocument.Parse(root1.GetProperty("details_json").GetString()!);
        Assert.True(details.RootElement.GetProperty("by_admin").GetBoolean());
        Assert.Equal("root", details.RootElement.GetProperty("user_name").GetString());
    }
}
