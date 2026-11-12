using System.Globalization;

namespace Urlog.Query;

/// <summary>
/// SPL time expressions (design record, "Query language", Time): relative <c>-24h</c>, <c>-7d@d</c>, <c>-1h@h</c>, <c>now</c>,
/// <c>@d</c>; absolute ISO 8601 with or without an offset. Units <c>s m h d w</c>; <c>@</c> snaps in the display
/// timezone and the result is UTC. An absolute time without an offset is read in the display timezone.
/// </summary>
public static class TimeExpr
{
    /// <summary>Parses <paramref name="text"/> relative to <paramref name="now"/> (UTC) in <paramref name="displayZone"/>. <paramref name="assumedZone"/> is set when the text had no offset of its own.</summary>
    public static DateTimeOffset Parse(string text, DateTimeOffset now, TimeZoneInfo displayZone, out bool assumedZone)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(displayZone);
        assumedZone = false;
        var s = text.Trim();
        if (s.Length == 0)
        {
            throw new FormatException("empty time");
        }

        if (s.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            return now;
        }

        if (s[0] is '-' or '+' or '@')
        {
            assumedZone = s.Contains('@'); // snapping depends on the zone; a plain offset does not
            return Relative(s, now, displayZone);
        }

        if (DateTimeOffset.TryParseExact(s, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ssK"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var withOffset)
            && HasOffset(s))
        {
            return withOffset;
        }

        if (DateTime.TryParseExact(s, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
        {
            assumedZone = true;
            var unspecified = DateTime.SpecifyKind(naive, DateTimeKind.Unspecified);
            var offset = displayZone.GetUtcOffset(unspecified);
            return new DateTimeOffset(unspecified, offset).ToUniversalTime();
        }

        if (long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) && s.Length >= 9)
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch); // epoch seconds, as Splunk accepts
        }

        throw new FormatException($"'{text}' is not a time: use -24h, -7d@d, now, or ISO 8601 (2026-03-01T00:00:00Z)");
    }

    private static bool HasOffset(string s) => s.EndsWith('Z') || s.EndsWith('z') || (s.Length > 6 && (s[^6] is '+' or '-') && s[^3] == ':');

    private static DateTimeOffset Relative(string s, DateTimeOffset now, TimeZoneInfo zone)
    {
        var i = 0;
        var amount = 0L;
        var unit = '\0';
        var sign = 1;
        if (s[0] is '-' or '+')
        {
            sign = s[0] == '-' ? -1 : 1;
            i = 1;
            var start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }

            if (i == start)
            {
                throw new FormatException($"'{s}' needs a number after the sign, like -24h");
            }

            amount = long.Parse(s.AsSpan(start, i - start), CultureInfo.InvariantCulture);
            if (i >= s.Length)
            {
                throw new FormatException($"'{s}' needs a unit: s, m, h, d or w");
            }

            unit = char.ToLowerInvariant(s[i]);
            i++;
            if (!"smhdw".Contains(unit))
            {
                throw new FormatException($"'{s}': unit '{s[i - 1]}' is not one of s, m, h, d, w");
            }
        }

        var result = now.ToUniversalTime() + Duration(sign * amount, unit);

        if (i < s.Length)
        {
            if (s[i] != '@' || i + 1 >= s.Length)
            {
                throw new FormatException($"'{s}': expected @<unit> for snapping");
            }

            var snap = char.ToLowerInvariant(s[i + 1]);
            if (i + 2 != s.Length || !"smhdw".Contains(snap))
            {
                throw new FormatException($"'{s}': snap to one of @s, @m, @h, @d, @w");
            }

            var local = TimeZoneInfo.ConvertTime(result, zone);
            var snapped = snap switch
            {
                's' => new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second, DateTimeKind.Unspecified),
                'm' => new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified),
                'h' => new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified),
                'd' => new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified),
                _ => new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified).AddDays(-(int)local.DayOfWeek), // @w: Sunday, as Splunk
            };
            result = new DateTimeOffset(snapped, zone.GetUtcOffset(snapped)).ToUniversalTime();
        }

        return result;
    }

    private static TimeSpan Duration(long amount, char unit) => unit switch
    {
        's' => TimeSpan.FromSeconds(amount),
        'm' => TimeSpan.FromMinutes(amount),
        'h' => TimeSpan.FromHours(amount),
        'd' => TimeSpan.FromDays(amount),
        'w' => TimeSpan.FromDays(7 * amount),
        _ => TimeSpan.Zero,
    };
}
