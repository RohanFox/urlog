using Urlog.Sources.Nginx;

namespace Urlog.Sources.Syslog;

/// <summary>
/// The BSD syslog timestamp, <c>Mmm dd HH:MM:SS</c>, as RFC 3164 describes it and as the wild writes it: the day
/// space-padded (<c>Mar  1</c>) or not (<c>Mar 1</c>, <c>Mar 01</c>); an optional fraction after the seconds
/// (<c>00:00:00.123</c>); an optional four-digit year between the day and the time (<c>Mar  1 2026 00:00:00</c>,
/// some appliances); a Cisco clock marker before the month (<c>*Mar  1 00:00:00.123:</c>, the <c>*</c> or <c>.</c>
/// meaning "clock not synchronised") and its colon after the seconds. The result is naive: the caller supplies the
/// year (<see cref="Urlog.Core.Sources.ParseContext.YearHint"/>, tracked across the file) and the offset
/// (<see cref="Urlog.Core.Sources.ParseContext.TimeZoneHint"/>). Second 60 rolls into the next minute, as
/// <see cref="ClfTimestamp"/> does. Allocation-free; a mismatch is a false result, never an exception.
/// </summary>
public static class Rfc3164Timestamp
{
    private const long SecondsPerDay = 86_400;

    private static ReadOnlySpan<byte> MaxDayOfMonth => [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]; // the year is unknown here: Feb 29 is checked by the caller's arithmetic

    /// <summary>
    /// Parses a timestamp at the start of <paramref name="text"/>. <paramref name="consumed"/> is the number of bytes
    /// taken, including a Cisco trailing colon but not the space after it. <see cref="Parts.Year"/> is 0 when the
    /// line carries no year.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> text, out Parts parts, out int consumed)
    {
        parts = default;
        consumed = 0;
        var pos = 0;
        if (text.Length > 0 && (text[0] == (byte)'*' || text[0] == (byte)'.'))
        {
            pos = 1; // Cisco: clock not synchronised
        }

        // "Mmm d" is the shortest form: 3 + 1 + 1, then a space and at least "H:MM:SS".
        if (text.Length < pos + 13)
        {
            return false;
        }

        var month = ClfTimestamp.MonthNumber(text.Slice(pos, 3));
        if (month < 0 || text[pos + 3] != (byte)' ')
        {
            return false;
        }

        pos += 4;
        if (text[pos] == (byte)' ')
        {
            pos++; // space-padded day
        }

        var day = 0;
        var digits = 0;
        while (pos < text.Length && digits < 2 && (uint)(text[pos] - '0') <= 9)
        {
            day = (day * 10) + (text[pos] - '0');
            digits++;
            pos++;
        }

        if (digits == 0 || day < 1 || day > MaxDayOfMonth[month - 1] || pos >= text.Length || text[pos] != (byte)' ')
        {
            return false;
        }

        pos++;

        // An optional year: four digits and a space, where the hour would otherwise be.
        var year = 0;
        if (pos + 5 <= text.Length && text[pos + 4] == (byte)' ' && IsDigit(text, pos) && IsDigit(text, pos + 1) && IsDigit(text, pos + 2) && IsDigit(text, pos + 3))
        {
            year = ((text[pos] - '0') * 1000) + ((text[pos + 1] - '0') * 100) + ((text[pos + 2] - '0') * 10) + (text[pos + 3] - '0');
            pos += 5;
        }

        if (pos + 8 > text.Length || text[pos + 2] != (byte)':' || text[pos + 5] != (byte)':')
        {
            return false;
        }

        var hour = Digits2(text, pos);
        var minute = Digits2(text, pos + 3);
        var second = Digits2(text, pos + 6);
        if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 60)
        {
            return false;
        }

        pos += 8;
        var millis = 0;
        if (pos < text.Length && text[pos] == (byte)'.')
        {
            var at = pos + 1;
            digits = 0;
            while (at < text.Length && (uint)(text[at] - '0') <= 9)
            {
                if (digits < 3)
                {
                    millis = (millis * 10) + (text[at] - '0');
                }

                digits++;
                at++;
            }

            if (digits > 0)
            {
                while (digits < 3)
                {
                    millis *= 10;
                    digits++;
                }

                pos = at;
            }
        }

        if (pos < text.Length && text[pos] == (byte)':' && (pos + 1 == text.Length || text[pos + 1] == (byte)' '))
        {
            pos++; // Cisco: "HH:MM:SS.mmm:" before the message
        }

        parts = new Parts(year, month, day, hour, minute, second, millis);
        consumed = pos;
        return true;
    }

    /// <summary>
    /// UTC milliseconds for <paramref name="parts"/> in <paramref name="year"/>, written by a clock at
    /// <paramref name="offsetMilliseconds"/> from UTC (UTC = local - offset). Feb 29 of a common year lands on
    /// Mar 1 and second 60 in the next minute: the result is always a real instant.
    /// </summary>
    public static long ToUnixMilliseconds(in Parts parts, int year, long offsetMilliseconds)
    {
        var seconds = (ClfTimestamp.DaysFromCivil(year, parts.Month, parts.Day) * SecondsPerDay) + (parts.Hour * 3600L) + (parts.Minute * 60L) + parts.Second;
        return (seconds * 1000) + parts.Millis - offsetMilliseconds;
    }

    private static bool IsDigit(ReadOnlySpan<byte> text, int at) => (uint)(text[at] - '0') <= 9;

    private static int Digits2(ReadOnlySpan<byte> text, int at)
    {
        var hi = text[at] - '0';
        var lo = text[at + 1] - '0';
        return (uint)hi <= 9 && (uint)lo <= 9 ? (hi * 10) + lo : -1;
    }

    /// <summary>The fields of a BSD timestamp; <see cref="Year"/> is 0 unless the line carried one.</summary>
    public readonly record struct Parts(int Year, int Month, int Day, int Hour, int Minute, int Second, int Millis);
}
