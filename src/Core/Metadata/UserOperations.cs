using System.Text.RegularExpressions;
using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>
/// Local accounts, audited like <see cref="CaseOperations"/>: one <c>BEGIN IMMEDIATE</c> transaction per change
/// with its row. Names are lowercase, <c>^[a-z0-9][a-z0-9._-]{0,63}$</c>, unique. The password hash is opaque here
/// (<c>Urlog.Core.Auth.PasswordHasher</c> makes it); it is never put in a row's details.
/// </summary>
public static partial class UserOperations
{
    public const int NameMaxLength = 64;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex NameRegex();

    /// <summary>Trimmed and lowercased; refused when it does not match the rule.</summary>
    /// <exception cref="InvalidUserNameException">Empty, too long, or a character outside the rule.</exception>
    public static string NormalizeName(string? name)
    {
        var normalized = (name ?? string.Empty).Trim().ToLowerInvariant();
        return NameRegex().IsMatch(normalized) ? normalized : throw new InvalidUserNameException(name ?? string.Empty);
    }

    /// <summary>True when the text is a well-formed name once normalized (a login for an ill-formed name is an unknown user, not an error).</summary>
    public static bool TryNormalizeName(string? name, out string normalized)
    {
        normalized = (name ?? string.Empty).Trim().ToLowerInvariant();
        return NameRegex().IsMatch(normalized);
    }

    public static Task<User?> FindByNameAsync(MetadataDb db, string normalizedName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Name == normalizedName, cancellationToken);
    }

    /// <summary>A new account with a <c>user_create</c> row (<c>user_name</c>, <c>role</c>) in one transaction.</summary>
    /// <exception cref="InvalidUserNameException">The name does not match the rule.</exception>
    /// <exception cref="UserNameTakenException">Another account has the name.</exception>
    public static async Task<User> CreateAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, string name, UserRole role, string passwordHash, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        var normalized = NormalizeName(name);
        var user = new User { Name = normalized, Role = role, PasswordHash = passwordHash, CreatedAt = nowUtc };
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            if (await db.Users.AnyAsync(u => u.Name == normalized, cancellationToken).ConfigureAwait(false))
            {
                throw new UserNameTakenException(normalized);
            }

            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException e)
            {
                // A provider without the immediate lock (Postgres) can lose the race on the unique index.
                throw new UserNameTakenException(normalized, e);
            }

            var details = new Dictionary<string, object?> { ["role"] = Names.Of(role) };
            await audit.AppendAsync(db, AuditEntry.ForUser(actor.Name, actor.UserId, AuditActions.UserCreate, user, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return user;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Changes the role, deletes the user's sessions (the new role applies at once) and writes <c>user_role_change</c>
    /// (<c>from</c>, <c>to</c>). Refused when it would leave no admin.
    /// </summary>
    /// <exception cref="UserNotFoundException">No such user.</exception>
    /// <exception cref="LastAdminException">The user is the only admin and <paramref name="to"/> is not admin.</exception>
    public static async Task<User> ChangeRoleAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long userId, UserRole to, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var user = await LoadAsync(db, userId, cancellationToken).ConfigureAwait(false);
            var from = user.Role;
            if (from == UserRole.Admin && to != UserRole.Admin
                && !await db.Users.AnyAsync(u => u.Id != userId && u.Role == UserRole.Admin, cancellationToken).ConfigureAwait(false))
            {
                throw new LastAdminException(user.Name);
            }

            user.Role = to;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await SessionOperations.DeleteForUserAsync(db, userId, cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?> { ["from"] = Names.Of(from), ["to"] = Names.Of(to) };
            await audit.AppendAsync(db, AuditEntry.ForUser(actor.Name, actor.UserId, AuditActions.UserRoleChange, user, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return user;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Clears the lock and the failure counter and writes <c>user_unlock</c> (<c>failed_logins</c>, <c>locked_at</c>): the admin's or the CLI's early end of a lockout.</summary>
    /// <exception cref="UserNotFoundException">No such user.</exception>
    public static async Task<User> UnlockAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long userId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var user = await LoadAsync(db, userId, cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?> { ["failed_logins"] = user.FailedLogins, ["locked_at"] = user.LockedAt };
            user.FailedLogins = 0;
            user.LockedAt = null;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(db, AuditEntry.ForUser(actor.Name, actor.UserId, AuditActions.UserUnlock, user, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return user;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Stores a new hash, deletes the user's sessions and writes <c>user_password_reset</c>; the hash is not in the row.</summary>
    /// <exception cref="UserNotFoundException">No such user.</exception>
    public static async Task<User> ResetPasswordAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long userId, string passwordHash, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var user = await LoadAsync(db, userId, cancellationToken).ConfigureAwait(false);
            user.PasswordHash = passwordHash;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var sessions = await SessionOperations.DeleteForUserAsync(db, userId, cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?> { ["sessions_ended"] = sessions };
            await audit.AppendAsync(db, AuditEntry.ForUser(actor.Name, actor.UserId, AuditActions.UserPasswordReset, user, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return user;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>The tracked row, reloaded under the write lock.</summary>
    internal static async Task<User> LoadAsync(MetadataDb db, long userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.FindAsync([userId], cancellationToken).ConfigureAwait(false) ?? throw new UserNotFoundException(userId);
        await db.Entry(user).ReloadAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }
}
