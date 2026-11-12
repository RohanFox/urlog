namespace Urlog.Core.Metadata;

/// <summary>
/// A login session. <see cref="Id"/> is the SHA-256 of the cookie token, so the file never yields a usable
/// session; the token itself exists only in the cookie. Expired by <see cref="ExpiresAt"/> (the absolute timeout) or by
/// <see cref="LastSeenAt"/> plus the idle timeout, both checked by <see cref="SessionOperations.FindAsync"/>; deleted on
/// logout, lockout, role change and password reset. Not an audited table: the <c>login</c> and <c>logout</c> rows are.
/// </summary>
public sealed class Session
{
    /// <summary>64 lowercase hex characters: SHA-256 of the token in the cookie.</summary>
    public required string Id { get; init; }

    public long UserId { get; init; }

    /// <summary>The token a mutating request must repeat in the <c>X-CSRF-Token</c> header.</summary>
    public required string CsrfToken { get; init; }

    /// <summary>The client address at login, as the trusted proxy reported it.</summary>
    public required string ClientAddress { get; init; }

    public DateTime CreatedAt { get; init; }

    /// <summary>Advanced at most once per touch interval; the idle timeout counts from here.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>The absolute timeout: created_at plus the configured span.</summary>
    public DateTime ExpiresAt { get; init; }
}
