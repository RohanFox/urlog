using System.ComponentModel.DataAnnotations;
using Urlog.Core.Auth;
using Urlog.Core.Metadata;

namespace Urlog.Api.Contracts;

/// <summary>What became of an upload: how far it is, and once intake has answered, the file it became or why it did not.</summary>
/// <param name="Offset">Bytes received; equal to <paramref name="Length"/> once the file is with intake.</param>
/// <param name="FileId">The file record: the new one for <c>done</c>, the one that already held the bytes for <c>duplicate</c>.</param>
public sealed record UploadStatusResponse(string Id, Urlog.Api.Uploads.UploadState State, long Offset, long Length, string Name, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, long? FileId, string? Sha256, long? JobId, string? Message);

// Request and response records of /api/v1. JSON is snake_case with enums as their stored text (ApiHost). No response
// carries password_hash, a session id or another session's CSRF token: the records have no such members.

/// <summary>Credentials for <c>POST /api/v1/auth/login</c>.</summary>
public sealed record LoginRequest
{
    [Required, MaxLength(256)]
    public required string Name { get; init; }

    [Required, MaxLength(PasswordPolicy.MaxLength)]
    public required string Password { get; init; }
}

/// <summary>An account as the API shows it: never the hash. <c>locked_until</c> is set while a lockout is in force.</summary>
public sealed record UserResponse(long Id, string Name, UserRole Role, DateTimeOffset? LockedUntil, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt)
{
    public static UserResponse From(User user, LockoutPolicy lockout, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(lockout);
        var lockedUntil = LoginOperations.IsLocked(user, lockout, nowUtc) ? user.LockedAt!.Value + lockout.Duration : (DateTime?)null;
        return new(user.Id, user.Name, user.Role, Utc(lockedUntil), Utc(user.LastLoginAt), Utc(user.CreatedAt)!.Value);
    }

    internal static DateTimeOffset? Utc(DateTime? value) => value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
}

/// <summary>The logged-in user and the CSRF token every mutating request must repeat in <c>X-CSRF-Token</c>.</summary>
public sealed record SessionResponse(UserResponse User, string CsrfToken);

/// <summary>Payload for <c>POST /api/v1/users</c> (admin).</summary>
public sealed record CreateUserRequest
{
    [Required, MaxLength(UserOperations.NameMaxLength)]
    public required string Name { get; init; }

    [Required, MaxLength(PasswordPolicy.MaxLength)]
    public required string Password { get; init; }

    public UserRole Role { get; init; } = UserRole.Analyst;
}

/// <summary>Payload for <c>PUT /api/v1/users/{id}/role</c>.</summary>
public sealed record ChangeRoleRequest
{
    public required UserRole Role { get; init; }
}

/// <summary>Payload for <c>POST /api/v1/users/{id}/password</c>: an admin sets a new password; the user's sessions end.</summary>
public sealed record SetPasswordRequest
{
    [Required, MaxLength(PasswordPolicy.MaxLength)]
    public required string Password { get; init; }
}

/// <summary>A case in the caller's list, with the caller's role in it.</summary>
public sealed record CaseSummaryResponse(long Id, string Name, CaseState State, MemberRole Role, string Owner, DateTimeOffset CreatedAt);

/// <summary>One case, as a member sees it.</summary>
public sealed record CaseResponse(long Id, string Name, CaseState State, string Owner, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, DateTimeOffset? ArchivedAt, int Members, MemberRole MyRole);

/// <summary>Payload for <c>POST /api/v1/cases</c>.</summary>
public sealed record CreateCaseRequest
{
    [Required, MaxLength(256)]
    public required string Name { get; init; }
}

public sealed record MemberResponse(long UserId, string Name, MemberRole Role, DateTimeOffset GrantedAt);

/// <summary>Payload for adding a member: the user by name and the role.</summary>
public sealed record AddMemberRequest
{
    [Required, MaxLength(UserOperations.NameMaxLength)]
    public required string User { get; init; }

    public MemberRole Role { get; init; } = MemberRole.Ro;
}

public sealed record ChangeMemberRoleRequest
{
    public required MemberRole Role { get; init; }
}

/// <summary>A case as the admin routes show it: metadata and the member list, never events or field statistics.</summary>
public sealed record AdminCaseResponse(long Id, string Name, CaseState State, string Owner, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt, DateTimeOffset? ArchivedAt, DateTimeOffset? DeletedAt, IReadOnlyList<MemberResponse> Members);
