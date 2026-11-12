using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>A session just created: the token for the cookie (never stored) and the row that holds its hash.</summary>
public sealed record NewSession(string Token, Session Row);

/// <summary>A live session and its user, as <see cref="SessionOperations.FindAsync"/> returns them on every request.</summary>
public sealed record SessionLookup(Session Session, User User);

/// <summary>
/// The server-side session store: rows in <c>sessions</c> keyed by the SHA-256 of the cookie token. Creating a row
/// is the caller's job inside the login transaction (<see cref="LoginOperations"/>); the other operations are their own
/// statements. Nothing here is audited: the <c>login</c> and <c>logout</c> rows are written by the callers.
/// </summary>
public static class SessionOperations
{
    /// <summary>Cookie and CSRF tokens are 32 random bytes, base64url (43 characters, no padding).</summary>
    public const int TokenBytes = 32;

    /// <summary>A fresh token pair and the row for it; the caller adds the row to the context.</summary>
    public static NewSession Create(long userId, string clientAddress, DateTime nowUtc, TimeSpan absoluteTimeout)
    {
        ArgumentNullException.ThrowIfNull(clientAddress);
        var token = NewToken();
        var row = new Session
        {
            Id = IdOf(token),
            UserId = userId,
            CsrfToken = NewToken(),
            ClientAddress = clientAddress,
            CreatedAt = nowUtc,
            LastSeenAt = nowUtc,
            ExpiresAt = nowUtc + absoluteTimeout,
        };
        return new NewSession(token, row);
    }

    /// <summary>The row id of a cookie token: SHA-256 over its UTF-8 bytes, lowercase hex.</summary>
    public static string IdOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>
    /// The session behind <paramref name="token"/> with its user, or null when there is none or it has expired (idle or
    /// absolute). A locked user is the caller's decision (<see cref="LoginOperations.IsLocked"/>); the row is returned.
    /// </summary>
    public static async Task<SessionLookup?> FindAsync(MetadataDb db, string token, DateTime nowUtc, TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var id = IdOf(token);
        var found = await db.Sessions.AsNoTracking()
            .Where(s => s.Id == id)
            .Join(db.Users.AsNoTracking(), s => s.UserId, u => u.Id, (s, u) => new { Session = s, User = u })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (found is null || found.Session.ExpiresAt <= nowUtc || found.Session.LastSeenAt + idleTimeout <= nowUtc)
        {
            return null;
        }

        return new SessionLookup(found.Session, found.User);
    }

    /// <summary>Advances <c>last_seen_at</c>; the caller rate-limits this to once per touch interval.</summary>
    public static Task<int> TouchAsync(MetadataDb db, string sessionId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Sessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, nowUtc), cancellationToken);
    }

    /// <summary>Deletes one session (logout). Returns the rows deleted (0 when it was already gone).</summary>
    public static Task<int> DeleteAsync(MetadataDb db, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Deletes every session of a user (lockout, role change, password reset); runs inside the caller's transaction.</summary>
    public static Task<int> DeleteForUserAsync(MetadataDb db, long userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Sessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Deletes every expired session (idle or absolute); called at every login so the table does not grow unbounded.</summary>
    public static Task<int> SweepAsync(MetadataDb db, DateTime nowUtc, TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var idleBefore = nowUtc - idleTimeout;
        return db.Sessions.Where(s => s.ExpiresAt <= nowUtc || s.LastSeenAt <= idleBefore).ExecuteDeleteAsync(cancellationToken);
    }

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
}
