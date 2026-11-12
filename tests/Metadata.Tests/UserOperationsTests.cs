using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>Accounts: the name rule, uniqueness, the last-admin guard, and every change with its audit row.</summary>
public sealed class UserOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AuditActor Cli = new("root");

    [Theory]
    [InlineData("Alice", "alice")]
    [InlineData("  bob.smith_2-x ", "bob.smith_2-x")]
    [InlineData("0day", "0day")]
    public void NamesAreTrimmedAndLowercased(string given, string expected) => Assert.Equal(expected, UserOperations.NormalizeName(given));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".alice")]
    [InlineData("-alice")]
    [InlineData("al ice")]
    [InlineData("alice@example")]
    [InlineData("ålice")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234x")]
    public void NamesOutsideTheRuleAreRefused(string given)
    {
        Assert.Throws<InvalidUserNameException>(() => UserOperations.NormalizeName(given));
        Assert.False(UserOperations.TryNormalizeName(given, out _));
    }

    [Fact]
    public async Task CreateWritesTheRowAndRefusesADuplicateName()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await UserOperations.CreateAsync(db, temp.Store.Audit, Cli, "Alice", UserRole.Admin, "argon2id$x", DateTime.UtcNow, Ct);
        Assert.Equal("alice", alice.Name);
        Assert.Equal(UserRole.Admin, alice.Role);
        await Assert.ThrowsAsync<UserNameTakenException>(() => UserOperations.CreateAsync(db, temp.Store.Audit, Cli, "ALICE ", UserRole.Analyst, "argon2id$y", DateTime.UtcNow, Ct));
        await Assert.ThrowsAsync<InvalidUserNameException>(() => UserOperations.CreateAsync(db, temp.Store.Audit, Cli, "al ice", UserRole.Analyst, "argon2id$y", DateTime.UtcNow, Ct));

        var rows = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).ToListAsync(Ct);
        Assert.Equal(["genesis", "user_create"], rows.Select(r => r.Action).ToArray());
        Assert.Equal(("root", (long?)null, AuditTargets.User, (long?)alice.Id), (rows[1].Actor, rows[1].ActorId, rows[1].TargetType, rows[1].TargetId));
        Assert.Equal("""{"role":"admin","user_name":"alice"}""", rows[1].DetailsJson);
        Assert.Equal(1, await db.Users.CountAsync(Ct));
    }

    [Fact]
    public async Task RoleChangeIsAuditedEndsSessionsAndKeepsOneAdmin()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await UserOperations.CreateAsync(db, temp.Store.Audit, Cli, "alice", UserRole.Admin, "h", DateTime.UtcNow, Ct);
        var bob = await UserOperations.CreateAsync(db, temp.Store.Audit, Cli, "bob", UserRole.Analyst, "h", DateTime.UtcNow, Ct);
        db.Sessions.Add(SessionOperations.Create(bob.Id, "198.51.100.7", DateTime.UtcNow, TimeSpan.FromHours(1)).Row);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        // The only admin cannot step down.
        await Assert.ThrowsAsync<LastAdminException>(() => UserOperations.ChangeRoleAsync(db, temp.Store.Audit, Cli, alice.Id, UserRole.Analyst, DateTime.UtcNow, Ct));
        await Assert.ThrowsAsync<UserNotFoundException>(() => UserOperations.ChangeRoleAsync(db, temp.Store.Audit, Cli, 4242, UserRole.Analyst, DateTime.UtcNow, Ct));

        var promoted = await UserOperations.ChangeRoleAsync(db, temp.Store.Audit, new AuditActor("alice", alice.Id), bob.Id, UserRole.Admin, DateTime.UtcNow, Ct);
        Assert.Equal(UserRole.Admin, promoted.Role);
        Assert.Equal(0, await db.Sessions.CountAsync(s => s.UserId == bob.Id, Ct));
        // Now alice may step down: bob is admin.
        await UserOperations.ChangeRoleAsync(db, temp.Store.Audit, Cli, alice.Id, UserRole.Analyst, DateTime.UtcNow, Ct);

        var rows = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Where(a => a.Action == AuditActions.UserRoleChange).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("alice", (long?)alice.Id, (long?)bob.Id), (rows[0].Actor, rows[0].ActorId, rows[0].TargetId));
        Assert.Equal("""{"from":"analyst","to":"admin","user_name":"bob"}""", rows[0].DetailsJson);
        Assert.Equal("""{"from":"admin","to":"analyst","user_name":"alice"}""", rows[1].DetailsJson);
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    [Fact]
    public async Task UnlockAndPasswordResetAreAudited()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var lockedAt = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        var alice = await UserOperations.CreateAsync(db, temp.Store.Audit, Cli, "alice", UserRole.Analyst, "old", DateTime.UtcNow, Ct);
        var tracked = await db.Users.FindAsync([alice.Id], Ct);
        tracked!.FailedLogins = 10;
        tracked.LockedAt = lockedAt;
        db.Sessions.Add(SessionOperations.Create(alice.Id, "198.51.100.7", DateTime.UtcNow, TimeSpan.FromHours(1)).Row);
        await db.SaveChangesAsync(Ct);

        var unlocked = await UserOperations.UnlockAsync(db, temp.Store.Audit, Cli, alice.Id, DateTime.UtcNow, Ct);
        Assert.Equal((0, (DateTime?)null), (unlocked.FailedLogins, unlocked.LockedAt));
        var reset = await UserOperations.ResetPasswordAsync(db, temp.Store.Audit, Cli, alice.Id, "new", DateTime.UtcNow, Ct);
        Assert.Equal("new", reset.PasswordHash);
        Assert.Equal(0, await db.Sessions.CountAsync(Ct));

        var rows = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Skip(2).ToListAsync(Ct);
        Assert.Equal(["user_unlock", "user_password_reset"], rows.Select(r => r.Action).ToArray());
        Assert.Equal("""{"failed_logins":10,"locked_at":"2026-11-11T10:00:00.000000Z","user_name":"alice"}""", rows[0].DetailsJson);
        Assert.Equal("""{"sessions_ended":1,"user_name":"alice"}""", rows[1].DetailsJson);
        Assert.DoesNotContain(rows, r => r.DetailsJson.Contains("new", StringComparison.Ordinal) || r.DetailsJson.Contains("old", StringComparison.Ordinal));
    }
}
