using Urlog.Core.Metadata;

namespace Urlog.Core.Audit;

/// <summary>Who did it: the user name (or the OS user, or <see cref="AuditActions.SystemActor"/>) and the user id when there is one.</summary>
public sealed record AuditActor(string Name, long? UserId = null)
{
    /// <summary>
    /// The OS user that ran the command when the actor is an account named on the command line
    /// (<c>--as</c>): the row then says both who it was done as and who was at the keyboard. Null otherwise.
    /// </summary>
    public string? OsUser { get; init; }

    /// <summary><paramref name="details"/> with <c>os_user</c> added when this actor carries one.</summary>
    public Dictionary<string, object?> Stamp(Dictionary<string, object?> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (OsUser is not null)
        {
            details["os_user"] = OsUser;
        }

        return details;
    }

    /// <summary>The tool acting on its own behalf (scheduled backup, sweep, reconciler).</summary>
    public static AuditActor System { get; } = new(AuditActions.SystemActor);
}

/// <summary>
/// What a caller hands to <see cref="IAuditWriter.AppendAsync"/>: everything of a row except what the chain assigns
/// (<c>seq</c>, <c>ts_utc</c>, <c>prev_hash</c>, <c>hash</c>). <paramref name="Details"/> becomes <c>details_json</c> through
/// <see cref="CanonicalJson"/> (integers, strings, booleans, nested objects and arrays; never results, never secrets).
/// Because a SQLite id can be reused after the newest row is deleted, the details name the target: a case row
/// carries <c>case_name</c>, a file row <c>case_name</c>, <c>sha256</c>, <c>original_name</c> and <c>size</c>, a user row
/// <c>user_name</c>; the factories below do that.
/// </summary>
public sealed record AuditEntry(string Actor, long? ActorId, string Action, string? TargetType, long? TargetId, IReadOnlyDictionary<string, object?> Details)
{
    /// <summary>A row about a case: <c>case_name</c> in the details, plus <paramref name="extra"/>.</summary>
    public static AuditEntry ForCase(string actor, long? actorId, string action, CaseRecord @case, IReadOnlyDictionary<string, object?>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(@case);
        return new(actor, actorId, action, AuditTargets.Case, @case.Id, With(extra, ("case_name", @case.Name)));
    }

    /// <summary>A row about an evidence file: the case name, the hash, the original name and the size in the details.</summary>
    public static AuditEntry ForFile(string actor, long? actorId, string action, EvidenceFile file, string caseName, IReadOnlyDictionary<string, object?>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new(actor, actorId, action, AuditTargets.File, file.Id,
            With(extra, ("case_name", caseName), ("sha256", file.Sha256), ("original_name", file.OriginalName), ("size", file.Size)));
    }

    /// <summary>A row about a user account: <c>user_name</c> in the details.</summary>
    public static AuditEntry ForUser(string actor, long? actorId, string action, User user, IReadOnlyDictionary<string, object?>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new(actor, actorId, action, AuditTargets.User, user.Id, With(extra, ("user_name", user.Name)));
    }

    private static Dictionary<string, object?> With(IReadOnlyDictionary<string, object?>? extra, params (string Key, object? Value)[] members)
    {
        var details = extra is null
            ? new Dictionary<string, object?>(members.Length, StringComparer.Ordinal)
            : new Dictionary<string, object?>(extra, StringComparer.Ordinal);
        foreach (var (key, value) in members)
        {
            details[key] = value;
        }

        return details;
    }
}
