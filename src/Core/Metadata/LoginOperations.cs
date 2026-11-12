using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>Lockout after <see cref="Threshold"/> consecutive failures, for <see cref="Duration"/> unless an admin unlocks earlier.</summary>
public sealed record LockoutPolicy(int Threshold, TimeSpan Duration)
{
    /// <summary>Ten consecutive failures lock the account for fifteen minutes (design record, "Security", Login).</summary>
    public static LockoutPolicy Default { get; } = new(10, TimeSpan.FromMinutes(15));
}

/// <summary>The session lifetimes the login writes into the row and the lookup checks.</summary>
public sealed record SessionPolicy(TimeSpan IdleTimeout, TimeSpan AbsoluteTimeout)
{
    public static SessionPolicy Default { get; } = new(TimeSpan.FromHours(8), TimeSpan.FromHours(24));
}

public enum LoginOutcome
{
    Success,
    UnknownUser,
    WrongPassword,
    Locked,
}

/// <summary>
/// What the API hands to <see cref="LoginOperations.RecordAsync"/> after it has verified the password <b>outside</b> any
/// transaction: the name as submitted, the client address, whether the password verified, and the re-hash to store when
/// the stored one was below the pinned parameters.
/// </summary>
public sealed record LoginAttempt(string Name, string ClientAddress, bool Verified, string? Rehash = null);

/// <summary>The outcome; <see cref="Session"/> and <see cref="User"/> are set on success only. <see cref="LockedNow"/>: this failure tripped the lock.</summary>
public sealed record LoginResult(LoginOutcome Outcome, User? User, NewSession? Session, bool LockedNow);

/// <summary>
/// The audited half of a login. The Argon2 verify runs before this, outside any transaction, so 150 ms of hashing
/// never holds the write lock; here one <c>BEGIN IMMEDIATE</c> transaction re-reads the account and applies the outcome
/// with its rows: <c>login_failed</c> (<c>reason</c> unknown_user | locked | wrong_password), <c>user_lockout</c> at the
/// threshold, <c>login</c> with the session row on success. A lock that has expired is cleared and the count starts from
/// zero; an account that got locked between the verify and this transaction is refused even with the right password.
/// </summary>
public static class LoginOperations
{
    /// <summary>The user name is bounded in the details so an attacker cannot put kilobytes into the chain.</summary>
    public const int NameDetailMaxLength = 128;

    /// <summary>True while <c>locked_at</c> is set and the policy's duration has not elapsed.</summary>
    public static bool IsLocked(User user, LockoutPolicy policy, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(policy);
        return user.LockedAt is { } lockedAt && lockedAt + policy.Duration > nowUtc;
    }

    public static async Task<LoginResult> RecordAsync(MetadataDb db, IAuditWriter audit, LoginAttempt attempt, LockoutPolicy lockout, SessionPolicy sessions, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(lockout);
        ArgumentNullException.ThrowIfNull(sessions);
        var submitted = attempt.Name.Length <= NameDetailMaxLength ? attempt.Name : attempt.Name[..NameDetailMaxLength];
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var user = UserOperations.TryNormalizeName(attempt.Name, out var normalized)
                ? await db.Users.FirstOrDefaultAsync(u => u.Name == normalized, cancellationToken).ConfigureAwait(false)
                : null;
            if (user is null)
            {
                var details = Details("unknown_user", attempt.ClientAddress);
                details["name"] = submitted;
                await audit.AppendAsync(db, new AuditEntry(submitted, null, AuditActions.LoginFailed, null, null, details), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LoginResult(LoginOutcome.UnknownUser, null, null, LockedNow: false);
            }

            await db.Entry(user).ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (IsLocked(user, lockout, nowUtc))
            {
                var details = Details("locked", attempt.ClientAddress);
                details["locked_until"] = user.LockedAt!.Value + lockout.Duration;
                await audit.AppendAsync(db, AuditEntry.ForUser(user.Name, user.Id, AuditActions.LoginFailed, user, details), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LoginResult(LoginOutcome.Locked, null, null, LockedNow: false);
            }

            if (user.LockedAt is not null)
            {
                // The lock expired: the count starts again from zero.
                user.LockedAt = null;
                user.FailedLogins = 0;
            }

            if (attempt.Verified)
            {
                user.FailedLogins = 0;
                user.LastLoginAt = nowUtc;
                if (attempt.Rehash is not null)
                {
                    user.PasswordHash = attempt.Rehash;
                }

                var session = SessionOperations.Create(user.Id, attempt.ClientAddress, nowUtc, sessions.AbsoluteTimeout);
                db.Sessions.Add(session.Row);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await SessionOperations.SweepAsync(db, nowUtc, sessions.IdleTimeout, cancellationToken).ConfigureAwait(false);
                var details = new Dictionary<string, object?> { ["address"] = attempt.ClientAddress, ["session"] = session.Row.Id[..12] };
                await audit.AppendAsync(db, AuditEntry.ForUser(user.Name, user.Id, AuditActions.Login, user, details), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LoginResult(LoginOutcome.Success, user, session, LockedNow: false);
            }

            user.FailedLogins++;
            var lockedNow = user.FailedLogins >= lockout.Threshold;
            if (lockedNow)
            {
                user.LockedAt = nowUtc;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var failed = Details("wrong_password", attempt.ClientAddress);
            failed["count"] = user.FailedLogins;
            await audit.AppendAsync(db, AuditEntry.ForUser(user.Name, user.Id, AuditActions.LoginFailed, user, failed), cancellationToken).ConfigureAwait(false);
            if (lockedNow)
            {
                await SessionOperations.DeleteForUserAsync(db, user.Id, cancellationToken).ConfigureAwait(false);
                var locked = new Dictionary<string, object?>
                {
                    ["count"] = user.FailedLogins,
                    ["address"] = attempt.ClientAddress,
                    ["locked_until"] = nowUtc + lockout.Duration,
                };
                await audit.AppendAsync(db, AuditEntry.ForUser(user.Name, user.Id, AuditActions.UserLockout, user, locked), cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new LoginResult(LoginOutcome.WrongPassword, null, null, lockedNow);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static Dictionary<string, object?> Details(string reason, string address) => new() { ["reason"] = reason, ["address"] = address };
}
