using System.Globalization;
using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Metadata;

/// <summary>A person's change to a host: each member is left alone or set.</summary>
public sealed record HostEdit(Change<string> Name, Change<string> Timezone, Change<IReadOnlyDictionary<string, string>?> SourceOptions);

/// <summary>
/// The answer of a host change. When it would have files parsed again and was not confirmed, nothing changed and
/// <paramref name="Cost"/> says what the confirmation would queue.
/// </summary>
public sealed record HostChange(Host Host, bool Applied, bool Changed, ReparseCost Cost, int Jobs);

/// <summary>
/// Host resolution is per case (design record, "Data model"): the same hostname in two cases is two rows; within a
/// case the lowercased name is unique and <see cref="GetOrCreateAsync"/> returns the existing row. No global registry.
/// A host that was merged into another keeps its row and its name, and every lookup by name follows it to the
/// surviving host. The changes a person makes (create, rename, zone, default source options, merge) are audited
/// on the case; the hosts intake creates on its own are stated by the <c>file_intake</c> rows of their files.
/// </summary>
public static class HostOperations
{
    public const int NameMaxLength = 253;

    /// <summary>Trimmed and lowercased, as the pipeline writes the <c>host</c> column.</summary>
    public static string Normalise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>The normalised name, refused when it is empty, too long, or holds a space, a slash or a control character.</summary>
    /// <exception cref="ArgumentException">Says what is wrong with the name.</exception>
    public static string CheckName(string? name)
    {
        var normalised = Normalise(name ?? string.Empty);
        if (normalised.Length is 0 or > NameMaxLength || normalised.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '!'))
        {
            throw new ArgumentException($"host: '{name}' is not a host name (1 to {NameMaxLength} characters, no space, slash or control character)");
        }

        return normalised;
    }

    /// <summary>The case's host of that name, created when missing. Safe under a concurrent create of the same name (the unique index decides).</summary>
    public static async Task<Host> GetOrCreateAsync(MetadataDb db, long caseId, string name, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var normalised = Normalise(name);
        if (normalised.Length == 0)
        {
            throw new ArgumentException("a host name is required", nameof(name));
        }

        var existing = await FindLiveAsync(db, caseId, normalised, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var host = new Host { CaseId = caseId, Name = normalised, CreatedAt = nowUtc };
        db.Hosts.Add(host);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch (DbUpdateException)
        {
            // Lost the race (or the case does not exist): forget our row and take the winner's, else let the error through.
            db.Entry(host).State = EntityState.Detached;
            existing = await FindLiveAsync(db, caseId, normalised, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    /// <summary>The host of that name in the case, or the one it was merged into; null when the case has none.</summary>
    public static async Task<Host?> FindLiveAsync(MetadataDb db, long caseId, string normalised, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.CaseId == caseId && h.Name == normalised, cancellationToken).ConfigureAwait(false);
        // A chain ends: a merge points every host that was merged into its source at the target.
        for (var hops = 0; host?.MergedIntoId is { } into && hops < 8; hops++)
        {
            host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == into, cancellationToken).ConfigureAwait(false);
        }

        return host;
    }

    /// <summary>
    /// Inside the caller's transaction: the live host of that name, created with a <c>host_create</c> row when the
    /// case has none. For a host a person names while changing files.
    /// </summary>
    internal static async Task<Host> ResolveAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, CaseRecord @case, string name, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var normalised = CheckName(name);
        var existing = await FindLiveAsync(db, @case.Id, normalised, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var host = new Host { CaseId = @case.Id, Name = normalised, CreatedAt = nowUtc };
        db.Hosts.Add(host);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.HostCreate, @case, actor.Stamp(Describe(host))), cancellationToken).ConfigureAwait(false);
        return host;
    }

    /// <summary>A new host of the case, with its <c>host_create</c> row.</summary>
    /// <exception cref="SettingsRefusedException">The case has a host of that name, merged ones included.</exception>
    public static async Task<Host> CreateAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, SourceRules rules, long caseId, string name, string? timezone, IReadOnlyDictionary<string, string>? sourceOptions, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        Guard(db, audit, actor, rules, clock);
        var normalised = CheckName(name);
        var zone = string.IsNullOrWhiteSpace(timezone) ? null : timezone.Trim();
        FileSettingsOperations.CheckZone(zone);
        SourceOptionSet.Check(sourceOptions, rules);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var @case = await FileSettingsOperations.ActiveCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            await RequireFreeNameAsync(db, caseId, normalised, cancellationToken).ConfigureAwait(false);
            var host = new Host
            {
                CaseId = caseId,
                Name = normalised,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
                Timezone = zone ?? "UTC",
                TimezoneAssumed = zone is null,
                SourceOptions = SourceOptionSet.Serialize(sourceOptions),
            };
            db.Hosts.Add(host);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.HostCreate, @case, actor.Stamp(Describe(host))), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Renames a host, sets its zone or its default source options. Each of these changes what a parse of some of
    /// its files writes, and those files are parsed again: every file for a rename, the files without a zone of
    /// their own whose source reads one for a zone, the files of the source an option belongs to that set no value
    /// of their own for an option. When that is at least one file and <paramref name="confirm"/> is false, nothing
    /// changes and the answer carries the cost.
    /// </summary>
    /// <exception cref="StaleWriteException"><paramref name="expectedVersion"/> is not the host's.</exception>
    /// <exception cref="SettingsRefusedException">The host was merged away, or the new name is taken.</exception>
    public static async Task<HostChange> UpdateAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, SourceRules rules, long caseId, long hostId, int expectedVersion, HostEdit edit, bool confirm, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        Guard(db, audit, actor, rules, clock);
        ArgumentNullException.ThrowIfNull(edit);
        var newName = edit.Name.IsSet ? CheckName(edit.Name.Value) : null;
        var newZone = edit.Timezone.IsSet ? (edit.Timezone.Value ?? string.Empty).Trim() : null;
        if (edit.Timezone.IsSet)
        {
            if (newZone!.Length == 0)
            {
                throw new ArgumentException("timezone: a zone is required");
            }

            FileSettingsOperations.CheckZone(newZone);
        }

        if (edit.SourceOptions.IsSet)
        {
            SourceOptionSet.Check(edit.SourceOptions.Value, rules);
        }

        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var @case = await FileSettingsOperations.ActiveCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            var host = await LoadAsync(db, caseId, hostId, expectedVersion, cancellationToken).ConfigureAwait(false);
            var details = actor.Stamp(new Dictionary<string, object?>(StringComparer.Ordinal) { ["host_id"] = host.Id, ["host_name"] = host.Name });
            var renamed = newName is not null && !string.Equals(newName, host.Name, StringComparison.Ordinal);
            var rezoned = newZone is not null && (!string.Equals(newZone, host.Timezone, StringComparison.Ordinal) || host.TimezoneAssumed);
            var zoneMoves = newZone is not null && !string.Equals(newZone, host.Timezone, StringComparison.Ordinal);
            var oldOptions = SourceOptionSet.Parse(host.SourceOptions);
            var newOptionsJson = edit.SourceOptions.IsSet ? SourceOptionSet.Serialize(edit.SourceOptions.Value) : host.SourceOptions;
            var newOptions = SourceOptionSet.Parse(newOptionsJson);
            var changedKeys = oldOptions.Keys.Union(newOptions.Keys, StringComparer.Ordinal)
                .Where(k => !(oldOptions.TryGetValue(k, out var a) && newOptions.TryGetValue(k, out var b) && string.Equals(a, b, StringComparison.Ordinal)))
                .ToList();
            if (!renamed && !rezoned && changedKeys.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return new HostChange(host, Applied: true, Changed: false, ReparseCost.None, 0);
            }

            if (renamed)
            {
                await RequireFreeNameAsync(db, caseId, newName!, cancellationToken).ConfigureAwait(false);
            }

            var owners = changedKeys.Select(SourceRules.OptionOwner).Distinct(StringComparer.Ordinal).Select(o => rules.TryResolve(o, out var id) ? id : o).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = await db.Files.Where(f => f.HostId == host.Id).OrderBy(f => f.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var affected = new List<EvidenceFile>();
            foreach (var file in files)
            {
                var source = SourceRules.EffectiveSource(file);
                var hit = renamed
                    || (zoneMoves && file.Timezone is null && rules.ReadsZone(source))
                    || (changedKeys.Count > 0 && owners.Contains(source) && OwnOptionsLeaveOpen(file, changedKeys));
                if (hit)
                {
                    affected.Add(file);
                }
            }

            var planner = await ReparsePlanner.LoadAsync(db, [.. affected.Select(f => f.Id)], cancellationToken).ConfigureAwait(false);
            foreach (var file in affected)
            {
                planner.Count(file);
            }

            var cost = planner.Cost;
            if (cost.Files > 0 && !confirm)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                return new HostChange(host, Applied: false, Changed: false, cost, 0);
            }

            // Counted once above; the planner that queues starts from the same jobs.
            var queue = await ReparsePlanner.LoadAsync(db, [.. affected.Select(f => f.Id)], cancellationToken).ConfigureAwait(false);
            foreach (var file in affected)
            {
                queue.Apply(db, file, nowUtc);
            }

            if (renamed)
            {
                details["name"] = FileSettingsOperations.FromTo(host.Name, newName);
                host.Name = newName!;
            }

            if (rezoned)
            {
                details["timezone"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["from"] = host.Timezone, ["to"] = newZone, ["was_assumed"] = host.TimezoneAssumed };
                host.Timezone = newZone!;
                host.TimezoneAssumed = false;
            }

            if (changedKeys.Count > 0)
            {
                details["source_options"] = FileSettingsOperations.FromTo(Plain(oldOptions), Plain(newOptions));
                host.SourceOptions = newOptionsJson;
            }

            host.Version++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            details["reparse_files"] = queue.Cost.Files;
            details["reparse_bytes"] = queue.Cost.Bytes;
            details["jobs_queued"] = queue.Jobs.Count;
            details["files_changed"] = affected.Count;
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.HostUpdate, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new HostChange(host, Applied: true, Changed: true, queue.Cost, queue.Jobs.Count);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>
    /// Merges <paramref name="hostId"/> into <paramref name="intoId"/>: its files point at the surviving host, and
    /// its row stays with <c>merged_into</c>, so the events that carry its id still name a host and its name leads
    /// to the survivor. No file is parsed again: rows keep the host name and id they were written with.
    /// </summary>
    /// <returns>The surviving host and the number of files moved.</returns>
    public static async Task<(Host Into, int FilesMoved)> MergeAsync(MetadataDb db, IAuditWriter audit, AuditActor actor, long caseId, long hostId, int expectedVersion, long intoId, TimeProvider clock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            var @case = await FileSettingsOperations.ActiveCaseAsync(db, caseId, cancellationToken).ConfigureAwait(false);
            var host = await LoadAsync(db, caseId, hostId, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (hostId == intoId)
            {
                throw new SettingsRefusedException($"host {hostId} cannot be merged into itself");
            }

            var into = await db.Hosts.FirstOrDefaultAsync(h => h.Id == intoId && h.CaseId == caseId, cancellationToken).ConfigureAwait(false) ?? throw new RecordNotFoundException("host", intoId, caseId);
            await db.Entry(into).ReloadAsync(cancellationToken).ConfigureAwait(false);
            if (into.MergedIntoId is not null)
            {
                throw new SettingsRefusedException($"host {intoId} ('{into.Name}') was itself merged into host {into.MergedIntoId}; merge into that one");
            }

            var moved = await db.Files.Where(f => f.HostId == hostId).ExecuteUpdateAsync(s => s.SetProperty(f => f.HostId, intoId), cancellationToken).ConfigureAwait(false);
            // Hosts merged into this one earlier follow it, so a name never leads through more than one row.
            await db.Hosts.Where(h => h.MergedIntoId == hostId).ExecuteUpdateAsync(s => s.SetProperty(h => h.MergedIntoId, intoId), cancellationToken).ConfigureAwait(false);
            host.MergedIntoId = intoId;
            host.Version++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = actor.Stamp(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["host_id"] = host.Id,
                ["host_name"] = host.Name,
                ["into_host_id"] = into.Id,
                ["into_host_name"] = into.Name,
                ["files_moved"] = moved,
            });
            await audit.AppendAsync(db, AuditEntry.ForCase(actor.Name, actor.UserId, AuditActions.HostMerge, @case, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (into, moved);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static bool OwnOptionsLeaveOpen(EvidenceFile file, List<string> changedKeys)
    {
        var own = SourceOptionSet.Parse(file.OverrideOptions);
        return changedKeys.Any(k => !own.ContainsKey(k));
    }

    private static async Task<Host> LoadAsync(MetadataDb db, long caseId, long hostId, int expectedVersion, CancellationToken cancellationToken)
    {
        var host = await db.Hosts.FirstOrDefaultAsync(h => h.Id == hostId && h.CaseId == caseId, cancellationToken).ConfigureAwait(false) ?? throw new RecordNotFoundException("host", hostId, caseId);
        await db.Entry(host).ReloadAsync(cancellationToken).ConfigureAwait(false);
        if (host.Version != expectedVersion)
        {
            throw new StaleWriteException($"host {hostId}", expectedVersion.ToString(CultureInfo.InvariantCulture), host.Version.ToString(CultureInfo.InvariantCulture));
        }

        if (host.MergedIntoId is { } into)
        {
            throw new SettingsRefusedException($"host {hostId} ('{host.Name}') was merged into host {into}; change that one");
        }

        return host;
    }

    private static async Task RequireFreeNameAsync(MetadataDb db, long caseId, string normalised, CancellationToken cancellationToken)
    {
        var taken = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.CaseId == caseId && h.Name == normalised, cancellationToken).ConfigureAwait(false);
        if (taken is not null)
        {
            throw new SettingsRefusedException(taken.MergedIntoId is { } into
                ? $"the case has had a host named '{normalised}' (host {taken.Id}, merged into host {into}); the name stays with it"
                : $"the case already has a host named '{normalised}' (host {taken.Id}); to make two hosts one, merge them");
        }
    }

    private static Dictionary<string, object?> Describe(Host host) => new(StringComparer.Ordinal)
    {
        ["host_id"] = host.Id,
        ["host_name"] = host.Name,
        ["timezone"] = host.Timezone,
        ["timezone_assumed"] = host.TimezoneAssumed,
        ["source_options"] = host.SourceOptions is null ? null : Plain(SourceOptionSet.Parse(host.SourceOptions)),
    };

    private static Dictionary<string, object?>? Plain(IReadOnlyDictionary<string, string> options) =>
        options.Count == 0 ? null : options.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);

    private static void Guard(MetadataDb db, IAuditWriter audit, AuditActor actor, SourceRules rules, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(clock);
    }
}
