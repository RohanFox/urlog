using System.Text;
using System.Text.RegularExpressions;
using Urlog.Sources.Nginx;
using Urlog.Sources.Syslog;

namespace Urlog.Sources.Generic;

/// <summary>
/// Best-effort timestamp of a record of unknown format: the leftmost of four shapes within the first 256 bytes.
/// <list type="bullet">
/// <item>ISO 8601 / RFC 3339, <c>yyyy-MM-dd[T ]HH:mm:ss[.,f...][Z|+/-hh[:mm]]</c>, also with slashes in the date; without a zone it is local time.</item>
/// <item>BSD syslog, <c>Mmm [d]d [yyyy ]HH:mm:ss[.f...]</c>: local time, the year from the line or tracked from the hint.</item>
/// <item>Common Log Format, <c>dd/Mon/yyyy:HH:mm:ss +/-zzzz</c>.</item>
/// <item>Unix epoch, 10 digits (optionally <c>.f...</c>) or 13 digits, only at the start of the record (after an optional
/// <c>[</c>) or after a <c>ts</c>, <c>time</c> or <c>timestamp</c> key, and only between 2000 and 2100.</item>
/// </list>
/// This is the one place the plugin contract allows a regex (design record, "Parser plugin contract"). It runs
/// over a stack copy of the window widened to chars and enumerates matches without allocating; a candidate that is
/// not a real date (month 13) is skipped and the search goes on.
/// </summary>
internal static partial class GenericTimestamps
{
    public const int WindowBytes = 256;

    private const string Months = "(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)";

    private const string Pattern =
        @"(?<![\w./-])\d{4}[-/]\d{2}[-/]\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?(?:Z|[+-]\d{2}(?::?\d{2})?)?(?!\d)"
        + @"|(?<![A-Za-z])" + Months + @" {1,2}\d{1,2} (?:\d{4} )?\d{2}:\d{2}:\d{2}"
        + @"|(?<!\d)\d{2}/" + Months + @"/\d{4}:\d{2}:\d{2}:\d{2} [+-]\d{4}"
        + @"|(?<=^\[?|(?:\b|"")(?i:ts|time|timestamp)""?\s?[=:]\s?""?)\d{10}(?:\d{3}|\.\d{1,9})?(?!\d)";

    private const long EpochFrom = 946_684_800;    // 2000-01-01
    private const long EpochUntil = 4_102_444_800; // 2100-01-01

    /// <summary>
    /// The first real timestamp in the first <see cref="WindowBytes"/> bytes of <paramref name="record"/>, as UTC
    /// milliseconds. <paramref name="zoneOffsetMilliseconds"/> is the source timezone for shapes without one.
    /// </summary>
    public static bool TryFind(ReadOnlySpan<byte> record, long zoneOffsetMilliseconds, YearTracker years, out long unixMilliseconds)
    {
        var window = record.Length > WindowBytes ? record[..WindowBytes] : record;
        Span<char> chars = stackalloc char[WindowBytes];
        var length = Encoding.Latin1.GetChars(window, chars);
        foreach (var match in Candidates().EnumerateMatches(chars[..length]))
        {
            var text = window.Slice(match.Index, match.Length);
            var first = text[0];
            bool ok;
            if ((uint)(first - '0') > 9)
            {
                ok = TryBsd(window[match.Index..], zoneOffsetMilliseconds, years, out unixMilliseconds);
            }
            else if (text.Length == ClfTimestamp.Length && text[2] == (byte)'/')
            {
                ok = ClfTimestamp.TryParse(text, out unixMilliseconds);
            }
            else if (text.Length >= 19 && (text[4] == (byte)'-' || text[4] == (byte)'/'))
            {
                ok = TryIso(text, zoneOffsetMilliseconds, out unixMilliseconds);
            }
            else
            {
                ok = TryEpoch(text, out unixMilliseconds);
            }

            if (ok)
            {
                return true;
            }
        }

        unixMilliseconds = 0;
        return false;
    }

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex Candidates();

    private static bool TryBsd(ReadOnlySpan<byte> text, long zoneOffsetMilliseconds, YearTracker years, out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        if (!Rfc3164Timestamp.TryParse(text, out var parts, out _))
        {
            return false;
        }

        var year = parts.Year != 0 ? years.Set(parts.Year, parts.Month) : years.YearFor(parts.Month);
        unixMilliseconds = Rfc3164Timestamp.ToUnixMilliseconds(in parts, year, zoneOffsetMilliseconds);
        return true;
    }

    /// <summary>The regex fixed the positions; this checks the ranges and reads the fraction and the zone.</summary>
    private static bool TryIso(ReadOnlySpan<byte> text, long zoneOffsetMilliseconds, out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        var year = Number(text, 0, 4);
        var month = Number(text, 5, 2);
        var day = Number(text, 8, 2);
        var hour = Number(text, 11, 2);
        var minute = Number(text, 14, 2);
        var second = Number(text, 17, 2);
        if (year < 1970 || month < 1 || month > 12 || day < 1 || day > DaysIn(year, month) || hour > 23 || minute > 59 || second > 60)
        {
            return false;
        }

        var i = 19;
        var millis = 0;
        if (i < text.Length && (text[i] == (byte)'.' || text[i] == (byte)','))
        {
            i++;
            var digits = 0;
            while (i < text.Length && (uint)(text[i] - '0') <= 9)
            {
                if (digits < 3)
                {
                    millis = (millis * 10) + (text[i] - '0');
                }

                digits++;
                i++;
            }

            while (digits < 3)
            {
                millis *= 10;
                digits++;
            }
        }

        var offset = zoneOffsetMilliseconds;
        if (i < text.Length)
        {
            if (text[i] == (byte)'Z')
            {
                offset = 0;
            }
            else
            {
                var rest = text.Length - i - 1; // 2, 4 or 5: hh, hhmm, hh:mm
                var hours = Number(text, i + 1, 2);
                var minutes = rest == 2 ? 0 : Number(text, text.Length - 2, 2);
                if (hours > 23 || minutes > 59)
                {
                    return false;
                }

                offset = ((hours * 3600L) + (minutes * 60L)) * 1000;
                if (text[i] == (byte)'-')
                {
                    offset = -offset;
                }
            }
        }

        var seconds = (ClfTimestamp.DaysFromCivil(year, month, day) * 86_400L) + (hour * 3600L) + (minute * 60L) + second;
        unixMilliseconds = (seconds * 1000) + millis - offset;
        return true;
    }

    private static bool TryEpoch(ReadOnlySpan<byte> text, out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        long seconds = 0;
        for (var i = 0; i < 10; i++)
        {
            seconds = (seconds * 10) + (text[i] - '0');
        }

        if (seconds < EpochFrom || seconds >= EpochUntil)
        {
            return false;
        }

        var millis = 0;
        if (text.Length == 13)
        {
            millis = Number(text, 10, 3);
        }
        else if (text.Length > 11)
        {
            var digits = Math.Min(3, text.Length - 11);
            millis = Number(text, 11, digits);
            for (var d = digits; d < 3; d++)
            {
                millis *= 10;
            }
        }

        unixMilliseconds = (seconds * 1000) + millis;
        return true;
    }

    private static int Number(ReadOnlySpan<byte> text, int at, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            value = (value * 10) + (text[at + i] - '0');
        }

        return value;
    }

    private static int DaysIn(int year, int month)
    {
        ReadOnlySpan<byte> days = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
        return month == 2 && year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : days[month - 1];
    }
}

/// <summary>
/// The year of timestamps that carry none, tracked across the file as the syslog source does: December followed by
/// January moves to the next year, a late December line after January goes one back, a year on a line resets it.
/// </summary>
internal sealed class YearTracker(int yearHint)
{
    private int _year = yearHint;
    private int _lastMonth;

    public int YearFor(int month)
    {
        if (_lastMonth != 0)
        {
            if (month <= _lastMonth - 6)
            {
                _year++;
            }
            else if (month > _lastMonth + 6)
            {
                _year--;
            }
        }

        _lastMonth = month;
        return _year;
    }

    public int Set(int year, int month)
    {
        _year = year;
        _lastMonth = month;
        return year;
    }
}
