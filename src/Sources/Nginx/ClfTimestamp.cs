namespace Urlog.Sources.Nginx;

/// <summary>
/// The Common Log Format timestamp, <c>dd/Mon/yyyy:HH:MM:SS +/-zzzz</c> (the 26 bytes between <c>[</c> and <c>]</c>),
/// to UTC milliseconds since the Unix epoch, hand-rolled and allocation-free. The format carries its own offset,
/// so <see cref="Urlog.Core.Sources.ParseContext.TimeZoneHint"/> does not apply. Second 60 (a leap second as
/// logged by a kernel that steps) rolls into the next minute, which keeps the result monotonic and deterministic.
/// Validation is strict: month name, day within the month, hour/minute/second/offset ranges; anything else is a
/// reject for the caller, never an exception.
/// </summary>
public static class ClfTimestamp
{
    /// <summary>Length of the text between the brackets.</summary>
    public const int Length = 26;

    private const long SecondsPerDay = 86_400;

    // "Jan".."Dec" as little-endian 24-bit integers of their three ASCII bytes, index = month - 1.
    private static readonly int[] MonthCodes =
    [
        Code("Jan"u8), Code("Feb"u8), Code("Mar"u8), Code("Apr"u8), Code("May"u8), Code("Jun"u8),
        Code("Jul"u8), Code("Aug"u8), Code("Sep"u8), Code("Oct"u8), Code("Nov"u8), Code("Dec"u8),
    ];

    private static ReadOnlySpan<byte> DaysInMonth => [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary>Parses exactly <see cref="Length"/> bytes of <c>dd/Mon/yyyy:HH:MM:SS +/-zzzz</c>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> text, out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        if (text.Length != Length
            || text[2] != (byte)'/' || text[6] != (byte)'/' || text[11] != (byte)':' || text[14] != (byte)':'
            || text[17] != (byte)':' || text[20] != (byte)' ')
        {
            return false;
        }

        var day = Digits2(text, 0);
        var month = MonthNumber(text.Slice(3, 3));
        var year = (Digits2(text, 7) * 100) + Digits2(text, 9);
        var hour = Digits2(text, 12);
        var minute = Digits2(text, 15);
        var second = Digits2(text, 18);
        var sign = text[21];
        var offsetHours = Digits2(text, 22);
        var offsetMinutes = Digits2(text, 24);

        // Digits2 returns a negative number for a non-digit, so one range check per field covers both.
        if (month < 0 || day < 1 || year < 0 || hour > 23 || minute > 59 || second > 60 || offsetHours > 23 || offsetMinutes > 59
            || (sign != (byte)'+' && sign != (byte)'-') || day > DaysIn(year, month))
        {
            return false;
        }

        var seconds = (DaysFromCivil(year, month, day) * SecondsPerDay) + (hour * 3600L) + (minute * 60L) + second;
        var offset = (offsetHours * 3600L) + (offsetMinutes * 60L);
        seconds += sign == (byte)'+' ? -offset : offset; // local = UTC + offset, so UTC = local - offset
        unixMilliseconds = seconds * 1000;
        return true;
    }

    /// <summary>Days since 1970-01-01 for a proleptic Gregorian date (Howard Hinnant's days_from_civil).</summary>
    internal static long DaysFromCivil(int year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        var era = (year >= 0 ? year : year - 399) / 400;
        var yearOfEra = year - (era * 400);
        var dayOfYear = ((153 * (month + (month > 2 ? -3 : 9))) + 2) / 5 + day - 1;
        var dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
        return (era * 146_097L) + dayOfEra - 719_468;
    }

    private static int DaysIn(int year, int month)
    {
        if (month == 2 && year % 4 == 0 && (year % 100 != 0 || year % 400 == 0))
        {
            return 29;
        }

        return DaysInMonth[month - 1];
    }

    /// <summary>Two ASCII digits at <paramref name="at"/>, or a negative number when either byte is not a digit.</summary>
    private static int Digits2(ReadOnlySpan<byte> text, int at)
    {
        var hi = text[at] - '0';
        var lo = text[at + 1] - '0';
        return (uint)hi <= 9 && (uint)lo <= 9 ? (hi * 10) + lo : -1;
    }

    /// <summary>1..12 for <c>Jan</c>..<c>Dec</c> (exactly three bytes, capitalised as syslog and CLF write them), else -1. Shared with the syslog source.</summary>
    internal static int MonthNumber(ReadOnlySpan<byte> name)
    {
        var code = Code(name);
        var months = MonthCodes;
        for (var i = 0; i < months.Length; i++)
        {
            if (months[i] == code)
            {
                return i + 1;
            }
        }

        return -1;
    }

    private static int Code(ReadOnlySpan<byte> name) => name[0] | (name[1] << 8) | (name[2] << 16);
}
