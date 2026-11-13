using System.Globalization;

namespace Urlog.Core.Intake;

/// <summary>The year a file's first record is taken to be in, and whether anything but the arrival date supports it.</summary>
public readonly record struct YearHint(int Year, bool Assumed);

/// <summary>
/// The hints for formats with naive timestamps (design record, "Source timezone"): the zone's offset and the year.
/// Neither reads the clock, so the same record gives the same rows whenever it is parsed.
/// </summary>
public static class IntakeHints
{
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>
    /// The offset of <paramref name="zone"/> (an IANA id, <c>UTC</c>, or <c>+02:00</c>) at <paramref name="atUtc"/>:
    /// the file's own time, so a file is read with the offset that held when it was written. One offset per file;
    /// a file that spans a transition is flagged by <see cref="SpansTransition"/>.
    /// </summary>
    /// <exception cref="TimeZoneNotFoundException">The id is not an offset and the system does not know it.</exception>
    public static TimeSpan OffsetAt(string? zone, DateTime atUtc)
    {
        if (IsFixed(zone, out var offset))
        {
            return offset;
        }

        return TimeZoneInfo.FindSystemTimeZoneById(zone!).GetUtcOffset(DateTime.SpecifyKind(atUtc, DateTimeKind.Utc));
    }

    /// <summary>True for an id <see cref="OffsetAt"/> can resolve.</summary>
    public static bool IsKnownZone(string? zone)
    {
        if (IsFixed(zone, out _))
        {
            return true;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(zone!);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the zone's offset changes between the two instants: a file parsed with one offset then has times
    /// one hour off on one side of the change. Always false for UTC and fixed offsets.
    /// </summary>
    public static bool SpansTransition(string? zone, DateTime fromUtc, DateTime toUtc)
    {
        if (IsFixed(zone, out _) || toUtc <= fromUtc)
        {
            return false;
        }

        TimeZoneInfo info;
        try
        {
            info = TimeZoneInfo.FindSystemTimeZoneById(zone!);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }

        if (!info.SupportsDaylightSavingTime)
        {
            // Zones without DST can still have changed their base offset, which the day walk below would find, but
            // only across years; a log file that long is not read with one offset anyway.
            return info.GetUtcOffset(Utc(fromUtc)) != info.GetUtcOffset(Utc(toUtc));
        }

        if (toUtc - fromUtc > TimeSpan.FromDays(366))
        {
            return true;
        }

        // Two transitions are never less than a day apart, so a daily walk sees every change.
        var first = info.GetUtcOffset(Utc(fromUtc));
        for (var at = fromUtc.AddDays(1); at < toUtc; at = at.AddDays(1))
        {
            if (info.GetUtcOffset(Utc(at)) != first)
            {
                return true;
            }
        }

        return info.GetUtcOffset(Utc(toUtc)) != first;
    }

    /// <summary>
    /// The year of the file's first record. An explicit year wins. Otherwise the year of the file's own time, which
    /// is when its last line was written: one less when the first line's month is later than that month, since the
    /// file then began in the year before. With no file time, the arrival's year, marked assumed.
    /// </summary>
    /// <param name="head">The start of the decoded file; only its first line is looked at.</param>
    public static YearHint Year(int? given, DateTime? fileTimeUtc, ReadOnlySpan<byte> head, DateTime receivedAtUtc)
    {
        if (given is { } year)
        {
            return new YearHint(year, Assumed: false);
        }

        if (fileTimeUtc is not { } fileTime)
        {
            return new YearHint(receivedAtUtc.Year, Assumed: true);
        }

        var firstMonth = FirstMonth(head);
        return new YearHint(firstMonth > fileTime.Month ? fileTime.Year - 1 : fileTime.Year, Assumed: false);
    }

    /// <summary>The month (1 to 12) of a BSD syslog timestamp at the start of <paramref name="head"/>, after an optional <c>&lt;PRI&gt;</c>; 0 when there is none.</summary>
    public static int FirstMonth(ReadOnlySpan<byte> head)
    {
        // UTF-16 text has a NUL beside every ASCII byte; the first 64 bytes without them are enough for one timestamp.
        Span<byte> line = stackalloc byte[64];
        var length = 0;
        foreach (var b in head)
        {
            if (b is (byte)'\n' or (byte)'\r' || length == line.Length)
            {
                break;
            }

            if (b is 0 or 0xEF or 0xBB or 0xBF or 0xFF or 0xFE)
            {
                continue;
            }

            line[length++] = b;
        }

        line = line[..length];
        if (line.Length > 2 && line[0] == (byte)'<')
        {
            var close = line.IndexOf((byte)'>');
            if (close is < 2 or > 4)
            {
                return 0;
            }

            line = line[(close + 1)..];
        }

        if (line.Length < 4 || line[3] != (byte)' ')
        {
            return 0;
        }

        for (var m = 0; m < Months.Length; m++)
        {
            var name = Months[m];
            if (line[0] == name[0] && line[1] == name[1] && line[2] == name[2])
            {
                return m + 1;
            }
        }

        return 0;
    }

    private static bool IsFixed(string? zone, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(zone) || zone.Equals("UTC", StringComparison.OrdinalIgnoreCase) || zone.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (zone[0] is '+' or '-' && TimeSpan.TryParse(zone.AsSpan(1), CultureInfo.InvariantCulture, out var parsed))
        {
            offset = zone[0] == '-' ? -parsed : parsed;
            return true;
        }

        return false;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
