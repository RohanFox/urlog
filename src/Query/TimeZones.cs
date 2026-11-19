using System.Globalization;

namespace Urlog.Query;

/// <summary>
/// The display timezone the UI (or the CLI's <c>--tz</c>) sends: an IANA zone id, on Linux and on Windows alike
/// (<see cref="Urlog.Core.ZoneIds"/>), or a fixed offset (<c>+02:00</c>, <c>-0500</c>, <c>UTC+2</c>). An id that names
/// no zone is refused with the same message on both systems.
/// </summary>
public static class TimeZones
{
    public static TimeZoneInfo Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var text = id.Trim();
        if (text.Equals("UTC", StringComparison.OrdinalIgnoreCase) || text.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        if (TryParseOffset(text, out var offset))
        {
            var name = "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            return TimeZoneInfo.CreateCustomTimeZone(name, offset, name, name);
        }

        return Urlog.Core.ZoneIds.TryFind(text, out var zone)
            ? zone
            : throw new ArgumentException($"unknown timezone '{id}': use an IANA id like Europe/Bucharest or an offset like +02:00", nameof(id));
    }

    private static bool TryParseOffset(string text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        var s = text;
        if (s.StartsWith("UTC", StringComparison.OrdinalIgnoreCase) || s.StartsWith("GMT", StringComparison.OrdinalIgnoreCase))
        {
            s = s[3..];
            if (s.Length == 0)
            {
                return true;
            }
        }

        if (s.Length < 2 || (s[0] != '+' && s[0] != '-'))
        {
            return false;
        }

        var sign = s[0] == '-' ? -1 : 1;
        var body = s[1..].Replace(":", "", StringComparison.Ordinal);
        if (!int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return false;
        }

        int hours, minutes;
        if (body.Length <= 2)
        {
            hours = n;
            minutes = 0;
        }
        else if (body.Length is 3 or 4)
        {
            hours = n / 100;
            minutes = n % 100;
        }
        else
        {
            return false;
        }

        if (hours > 14 || minutes > 59)
        {
            return false;
        }

        offset = sign * new TimeSpan(hours, minutes, 0);
        return true;
    }
}
