using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The audited half of a login: the rows for success, unknown name, wrong password and locked; the lockout at the
/// threshold with its session purge; the 15-minute expiry that restarts the count; the re-hash on success.
/// </summary>
public sealed class LoginOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LockoutPolicy Lockout = new(10, TimeSpan.FromMinutes(15));
    private static readonly SessionPolicy Sessions = new(TimeSpan.FromHours(8), TimeSpan.FromHours(24));
    private static readonly DateTime T0 = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private static Task<LoginResult> Attempt(MetadataDb db, TempDb temp, string name, bool verified, DateTime now, string? rehash = null) =>
        LoginOperations.RecordAsync(db, temp.Store.Audit, new LoginAttempt(name, "198.51.100.7", verified, rehash), Lockout, Sessions, now, Ct);

    [Fact]
    public async Task ASuccessfulLoginWritesTheSessionAndTheRow()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await UserOperations.CreateAsync(db, temp.Store.Audit, new AuditActor("root"), "alice", UserRole.Analyst, "h1", T0, Ct);

        var result = await Attempt(db, temp, " Alice ", verified: true, T0, rehash: "h2");
        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.NotNull(result.Session);
        Assert.Equal(43, result.Session.Token.Length);
        Assert.Equal(SessionOperations.IdOf(result.Session.Token), result.Session.Row.Id);
        Assert.Equal(T0 + Sessions.AbsoluteTimeout, result.Session.Row.ExpiresAt);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == alice.Id, Ct);
        Assert.Equal(("h2", T0, 0), (user.PasswordHash, user.LastLoginAt, user.FailedLogins));
        Assert.Equal(1, await db.Sessions.CountAsync(Ct));
        var row = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Equal(("login", "alice", (long?)alice.Id, AuditTargets.User), (row.Action, row.Actor, row.ActorId, row.TargetType));
        Assert.Equal($$"""{"address":"198.51.100.7","session":"{{result.Session.Row.Id[..12]}}","user_name":"alice"}""", row.DetailsJson);
    }

    [Fact]
    public async Task AnUnknownNameIsRecordedWithoutAnActorId()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var result = await Attempt(db, temp, "nobody", verified: false, T0);
        Assert.Equal(LoginOutcome.UnknownUser, result.Outcome);
        var illFormed = await Attempt(db, temp, "no such user!", verified: false, T0);
        Assert.Equal(LoginOutcome.UnknownUser, illFormed.Outcome);
        var huge = await Attempt(db, temp, new string('x', 5000), verified: false, T0);
        Assert.Equal(LoginOutcome.UnknownUser, huge.Outcome);

        var rows = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Skip(1).ToListAsync(Ct);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(("login_failed", (long?)null, (string?)null), (r.Action, r.ActorId, r.TargetType)));
        Assert.Equal("""{"address":"198.51.100.7","name":"nobody","reason":"unknown_user"}""", rows[0].DetailsJson);
        Assert.Equal(128, rows[2].Actor.Length);
    }

    [Fact]
    public async Task TenFailuresLockTheAccountAndEndItsSessions()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await UserOperations.CreateAsync(db, temp.Store.Audit, new AuditActor("root"), "alice", UserRole.Analyst, "h", T0, Ct);
        Assert.Equal(LoginOutcome.Success, (await Attempt(db, temp, "alice", verified: true, T0)).Outcome);
        Assert.Equal(1, await db.Sessions.CountAsync(Ct));

        for (var i = 1; i <= 9; i++)
        {
            var r = await Attempt(db, temp, "alice", verified: false, T0.AddSeconds(i));
            Assert.Equal((LoginOutcome.WrongPassword, false), (r.Outcome, r.LockedNow));
        }

        var tenth = await Attempt(db, temp, "alice", verified: false, T0.AddSeconds(10));
        Assert.Equal((LoginOutcome.WrongPassword, true), (tenth.Outcome, tenth.LockedNow));
        Assert.Equal(0, await db.Sessions.CountAsync(Ct));

        // The 11th, with the right password, is refused: the account is locked.
        var eleventh = await Attempt(db, temp, "alice", verified: true, T0.AddSeconds(11));
        Assert.Equal(LoginOutcome.Locked, eleventh.Outcome);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == alice.Id, Ct);
        Assert.Equal((10, T0.AddSeconds(10)), (user.FailedLogins, user.LockedAt));

        var actions = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync(Ct);
        Assert.Equal(["genesis", "user_create", "login", .. Enumerable.Repeat("login_failed", 10), "user_lockout", "login_failed"], actions);
        var lockout = await db.Audit.AsNoTracking().SingleAsync(a => a.Action == AuditActions.UserLockout, Ct);
        Assert.Equal("""{"address":"198.51.100.7","count":10,"locked_until":"2026-11-11T12:15:10.000000Z","user_name":"alice"}""", lockout.DetailsJson);
        var refused = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Equal("""{"address":"198.51.100.7","locked_until":"2026-11-11T12:15:10.000000Z","reason":"locked","user_name":"alice"}""", refused.DetailsJson);
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    [Fact]
    public async Task TheLockExpiresAfterTheDurationAndTheCountStartsFromZero()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await UserOperations.CreateAsync(db, temp.Store.Audit, new AuditActor("root"), "alice", UserRole.Analyst, "h", T0, Ct);
        for (var i = 1; i <= 10; i++)
        {
            await Attempt(db, temp, "alice", verified: false, T0.AddSeconds(i));
        }

        // One second before the expiry: still locked, even with the right password.
        Assert.Equal(LoginOutcome.Locked, (await Attempt(db, temp, "alice", verified: true, T0.AddSeconds(10).AddMinutes(15).AddSeconds(-1))).Outcome);
        // At the expiry a failure counts from zero: the eleventh wrong password is count 1, not a second lock.
        var after = await Attempt(db, temp, "alice", verified: false, T0.AddSeconds(10).AddMinutes(15));
        Assert.Equal((LoginOutcome.WrongPassword, false), (after.Outcome, after.LockedNow));
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == alice.Id, Ct);
        Assert.Equal((1, (DateTime?)null), (user.FailedLogins, user.LockedAt));
        // And the right password gets in.
        Assert.Equal(LoginOutcome.Success, (await Attempt(db, temp, "alice", verified: true, T0.AddMinutes(16))).Outcome);
        // The expiry itself wrote no row: user_lockout once, user_unlock never.
        Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == AuditActions.UserLockout, Ct));
        Assert.Equal(0, await db.Audit.CountAsync(a => a.Action == AuditActions.UserUnlock, Ct));
    }

    [Fact]
    public async Task AnAdminUnlockEndsTheLockEarly()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await UserOperations.CreateAsync(db, temp.Store.Audit, new AuditActor("root"), "alice", UserRole.Analyst, "h", T0, Ct);
        for (var i = 1; i <= 10; i++)
        {
            await Attempt(db, temp, "alice", verified: false, T0.AddSeconds(i));
        }

        Assert.Equal(LoginOutcome.Locked, (await Attempt(db, temp, "alice", verified: true, T0.AddMinutes(1))).Outcome);
        var root = await UserOperations.CreateAsync(db, temp.Store.Audit, new AuditActor("root"), "root", UserRole.Admin, "h", T0, Ct);
        await UserOperations.UnlockAsync(db, temp.Store.Audit, new AuditActor("root", root.Id), alice.Id, T0.AddMinutes(2), Ct);
        Assert.Equal(LoginOutcome.Success, (await Attempt(db, temp, "alice", verified: true, T0.AddMinutes(3))).Outcome);
    }

    [Fact]
    public async Task ASuccessfulLoginSweepsExpiredSessions()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        await UserOperations.CreateAsync(db, temp.Store.Audit, new AuditActor("root"), "alice", UserRole.Analyst, "h", T0, Ct);
        await Attempt(db, temp, "alice", verified: true, T0);
        await Attempt(db, temp, "alice", verified: true, T0.AddHours(1));
        Assert.Equal(2, await db.Sessions.CountAsync(Ct));
        // Eight and a half hours on: the first is idle-expired, the second is not; the login at that time sweeps the first.
        await Attempt(db, temp, "alice", verified: true, T0.AddHours(8.5));
        Assert.Equal(2, await db.Sessions.CountAsync(Ct));
        // A day and a bit: both earlier ones are absolutely expired.
        await Attempt(db, temp, "alice", verified: true, T0.AddHours(26));
        Assert.Equal(1, await db.Sessions.CountAsync(Ct));
    }
}
