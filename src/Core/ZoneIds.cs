using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using NodaTime.TimeZones;

namespace Urlog.Core;

/// <summary>
/// Zone ids as a person gives them: IANA ids (<c>Europe/Berlin</c>) on every system. The runtime resolves an id
/// through the system's own zone data. On Linux that data is the IANA database, so an IANA id is found directly.
/// On Windows the system knows its own ids (<c>W. Europe Standard Time</c>) and the runtime needs ICU to map an
/// IANA id to one, which a build with invariant globalization leaves out. An id the system does not know is
/// therefore looked up in the table of IANA and Windows ids that NodaTime carries (the Unicode CLDR mapping,
/// compiled into its assembly; managed code, Apache-2.0), and the Windows zone it names is returned under the id
/// that was asked for. The same table knows the older names of zones, which a Linux zone database may have
/// dropped (Ubuntu 24.04 ships them in a package of their own): such a name resolves to the zone under the name
/// it has today, on both systems. The offsets are the system's in every case. An id neither knows is unknown on
/// both systems.
/// </summary>
public static class ZoneIds
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Known = new(StringComparer.Ordinal);

    /// <summary>The zone of <paramref name="id"/>; false for an empty id and for one neither the system nor the table knows.</summary>
    public static bool TryFind(string? id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        zone = Known.GetOrAdd(id.Trim(), Resolve);
        return zone is not null;
    }

    /// <exception cref="TimeZoneNotFoundException">Neither the system nor the table knows the id.</exception>
    public static TimeZoneInfo Find(string? id) =>
        TryFind(id, out var zone) ? zone : throw new TimeZoneNotFoundException($"unknown timezone '{id}': use an IANA id like Europe/Bucharest, UTC, or an offset like +02:00");

    private static TimeZoneInfo? Resolve(string id)
    {
        if (TrySystem(id) is { } direct)
        {
            return direct;
        }

        // An older name of a zone (Asia/Calcutta, Europe/Kiev), which a system's zone database may no longer carry:
        // the zone under the name it has today.
        var table = TzdbDateTimeZoneSource.Default;
        table.CanonicalIdMap.TryGetValue(id, out var canonical);
        var name = canonical ?? id;
        if (!string.Equals(name, id, StringComparison.Ordinal) && TrySystem(name) is { } renamed)
        {
            return renamed;
        }

        // An IANA id on a system that has its own ids: the Windows zone the table names for it.
        if (!table.TzdbToWindowsIds.TryGetValue(id, out var windows) && !(canonical is not null && table.TzdbToWindowsIds.TryGetValue(canonical, out windows)))
        {
            return null;
        }

        return TrySystem(windows) is { } system ? Named(system, name) : null;
    }

    private static TimeZoneInfo? TrySystem(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// The system zone with its rules, carrying the IANA id it was found by: what is done with the zone later (the
    /// zone name a query sends to the event store) then names an IANA zone, not the Windows zone it was found
    /// through. A zone whose rules the runtime does not take back is returned as the system has it.
    /// </summary>
    private static TimeZoneInfo Named(TimeZoneInfo system, string id)
    {
        try
        {
            var rules = system.GetAdjustmentRules();
            return rules.Length == 0
                ? TimeZoneInfo.CreateCustomTimeZone(id, system.BaseUtcOffset, id, system.StandardName)
                : TimeZoneInfo.CreateCustomTimeZone(id, system.BaseUtcOffset, id, system.StandardName, system.DaylightName, rules);
        }
        catch (Exception e) when (e is InvalidTimeZoneException or ArgumentException)
        {
            return system;
        }
    }
}
