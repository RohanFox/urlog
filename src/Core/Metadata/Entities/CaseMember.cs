namespace Urlog.Core.Metadata;

/// <summary>The case ACL (design record, "Security"). The owner row is written with the case by <see cref="CaseOperations.CreateAsync"/>.</summary>
public sealed class CaseMember
{
    public long CaseId { get; init; }

    public long UserId { get; init; }

    public MemberRole Role { get; set; }

    public DateTime GrantedAt { get; init; }
}
