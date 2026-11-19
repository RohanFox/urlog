using NodaTime.TimeZones;

namespace Urlog.Core.Tests;

/// <summary>
/// IANA zone ids resolve on Linux and on Windows, to the same offsets, and an id that names no zone is refused on
/// both. The build has no ICU, so on Windows the ids go through the table NodaTime carries.
/// </summary>
public sealed class ZoneIdsTests
{
    private static readonly DateTime Summer = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Winter = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Europe/Berlin", 60, 120)]
    [InlineData("Europe/Bucharest", 120, 180)]
    [InlineData("Europe/Vienna", 60, 120)]
    [InlineData("America/New_York", -300, -240)]
    [InlineData("Asia/Tokyo", 540, 540)]
    [InlineData("Asia/Kolkata", 330, 330)]
    [InlineData("Australia/Sydney", 660, 600)]
    [InlineData("UTC", 0, 0)]
    [InlineData("Etc/UTC", 0, 0)]
    [InlineData("Europe/Kyiv", 120, 180)]
    public void AnIanaIdResolvesToTheSameOffsetsOnEverySystem(string id, int winterMinutes, int summerMinutes)
    {
        Assert.True(ZoneIds.TryFind(id, out var zone), id);
        Assert.Equal((TimeSpan.FromMinutes(winterMinutes), TimeSpan.FromMinutes(summerMinutes)), (zone.GetUtcOffset(Winter), zone.GetUtcOffset(Summer)));
        // The zone goes by the id it was asked for, whatever the system calls it.
        Assert.Equal(id, zone.Id);
        Assert.Same(zone, ZoneIds.Find(id));
    }

    [Theory]
    [InlineData("Asia/Calcutta", "Asia/Kolkata", 330)]
    [InlineData("Europe/Kiev", "Europe/Kyiv", 120)]
    [InlineData("America/Buenos_Aires", "America/Argentina/Buenos_Aires", -180)]
    public void AnOlderNameOfAZoneResolvesToTheZoneUnderItsNameOfToday(string older, string today, int winterMinutes)
    {
        // Exports and old configurations still carry these, and a system's zone database may not (Ubuntu 24.04).
        Assert.True(ZoneIds.TryFind(older, out var zone), older);
        Assert.Equal((today, TimeSpan.FromMinutes(winterMinutes)), (zone.Id, zone.GetUtcOffset(Winter)));
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("Europe/Berlin ")]
    [InlineData("europe/berlinn")]
    [InlineData("Berlin")]
    [InlineData("../../etc/passwd")]
    public void AnIdThatNamesNoZoneIsRefusedWithTheSameMessageOnEverySystem(string id)
    {
        if (id.EndsWith(' '))
        {
            // Space around an id is not part of it.
            Assert.True(ZoneIds.TryFind(id, out _));
            return;
        }

        Assert.False(ZoneIds.TryFind(id, out _));
        var refused = Assert.Throws<TimeZoneNotFoundException>(() => ZoneIds.Find(id));
        Assert.Equal($"unknown timezone '{id}': use an IANA id like Europe/Bucharest, UTC, or an offset like +02:00", refused.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoIdIsNoZone(string? id)
    {
        Assert.False(ZoneIds.TryFind(id, out _));
    }

    /// <summary>
    /// Every id of the table: it resolves, and the zone returned under the IANA id gives the offsets of the system
    /// zone it was found through, month by month over sixty years, under the name the zone has today.
    /// </summary>
    [Fact]
    public void EveryIdOfTheTableResolvesAndKeepsTheOffsetsOfItsSystemZone()
    {
        var table = TzdbDateTimeZoneSource.Default;
        var unresolved = new List<string>();
        var different = new List<string>();
        var renamed = 0;
        foreach (var (id, windows) in table.TzdbToWindowsIds.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!ZoneIds.TryFind(id, out var zone))
            {
                unresolved.Add(id);
                continue;
            }

            // What the id was found through: on Windows the zone the table names, elsewhere the id itself or, for
            // an older name the system's database lacks, the name the zone has today.
            TimeZoneInfo system;
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var direct))
            {
                // The system knows the id itself, and the zone is the system's as it stands.
                system = direct;
                Assert.Equal(direct.Id, zone.Id);
            }
            else
            {
                var today = table.CanonicalIdMap[id];
                system = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? windows : today);
                renamed += today == id ? 0 : 1;
                Assert.Equal(today, zone.Id);
            }
            for (var at = new DateTime(1990, 1, 15, 12, 0, 0, DateTimeKind.Utc); at.Year < 2050; at = at.AddMonths(1))
            {
                if (zone.GetUtcOffset(at) != system.GetUtcOffset(at))
                {
                    different.Add($"{id} ({windows}) at {at:yyyy-MM}: {zone.GetUtcOffset(at)} against {system.GetUtcOffset(at)}");
                    break;
                }
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{table.TzdbToWindowsIds.Count} ids in the table (tzdb {table.TzdbVersion}), {unresolved.Count} not resolved here: {string.Join(", ", unresolved)}; {renamed} found under the name the zone has today");
        Assert.True(different.Count == 0, string.Join("\n", different));
        Assert.True(table.TzdbToWindowsIds.Count > 300, "the table is there");
        if (OperatingSystem.IsWindows())
        {
            Assert.True(unresolved.Count == 0, "ids of the table that name no zone of this system: " + string.Join(", ", unresolved));
        }
        else
        {
            // A system's zone database may be older than the table by a few ids.
            Assert.True(unresolved.Count <= 10, "ids of the table this system's zone database lacks: " + string.Join(", ", unresolved));
        }
    }
}
