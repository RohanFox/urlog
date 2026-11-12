namespace Urlog.Core.Metadata;

/// <summary>A local account. <see cref="PasswordHash"/> is Argon2id; the login path keeps the lockout counters.</summary>
public sealed class User
{
    public long Id { get; init; }

    /// <summary>Lowercased, trimmed; unique.</summary>
    public required string Name { get; set; }

    public UserRole Role { get; set; } = UserRole.Analyst;

    public required string PasswordHash { get; set; }

    public int FailedLogins { get; set; }

    /// <summary>Set by the 10th failed login; null means not locked. Admin unlock clears it and the counter.</summary>
    public DateTime? LockedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; init; }
}
