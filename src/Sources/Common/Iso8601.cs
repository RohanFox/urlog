namespace Urlog.Sources.Common;

/// <summary>
/// ISO 8601 / RFC 3339 timestamps with an explicit zone to UTC milliseconds, allocation-free:
/// <c>YYYY-MM-DDTHH:MM:SS[.fraction](Z|+/-HH:MM|+/-HHMM|+/-HH)</c>, the <c>T</c> may be a space, the fraction any length
/// (truncated to milliseconds). Used by nginx <c>$time_iso8601</c> and syslog RFC 5424. A timestamp without a zone
/// is rejected: the caller decides what a naive time means (<see cref="Urlog.Core.Sources.ParseContext.TimeZoneHint"/>).
/// </summary>
public static class Iso8601
{
    /// <summary>Parses the whole of <paramref name="text"/>; false on any deviation.</summary>
    public static bool TryParse(ReadOnlySpan<byte> text, out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        if (text.Length < 20)
        {
            return false;
        }

        var year = Digits(text, 0, 4);
        var month = Digits(text, 5, 2);
        var day = Digits(text, 8, 2);
        var hour = Digits(text, 11, 2);
        var minute = Digits(text, 14, 2);
        var second = Digits(text, 17, 2);
        if (year < 0 || month < 1 || month > 12 || day < 1 || hour > 23 || minute > 59 || second > 60
            || text[4] != (byte)'-' || text[7] != (byte)'-' || (text[10] != (byte)'T' && text[10] != (byte)'t' && text[10] != (byte)' ')
            || text[13] != (byte)':' || text[16] != (byte)':' || day > ClfDays(year, month))
        {
            return false;
        }

        var i = 19;
        var millis = 0;
        if (text[i] == (byte)'.' || text[i] == (byte)',')
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

            if (digits == 0)
            {
                return false;
            }

            while (digits < 3)
            {
                millis *= 10;
                digits++;
            }
        }

        if (i >= text.Length)
        {
            return false; // no zone
        }

        long offsetSeconds;
        var z = text[i];
        if (z == (byte)'Z' || z == (byte)'z')
        {
            offsetSeconds = 0;
            i++;
        }
        else if (z == (byte)'+' || z == (byte)'-')
        {
            var rest = text.Length - i - 1;
            if (rest != 2 && rest != 4 && rest != 5)
            {
                return false;
            }

            var oh = Digits(text, i + 1, 2);
            var om = 0;
            if (rest == 5)
            {
                if (text[i + 3] != (byte)':')
                {
                    return false;
                }

                om = Digits(text, i + 4, 2);
            }
            else if (rest == 4)
            {
                om = Digits(text, i + 3, 2);
            }

            if (oh < 0 || oh > 23 || om < 0 || om > 59)
            {
                return false;
            }

            offsetSeconds = (oh * 3600L) + (om * 60L);
            if (z == (byte)'+')
            {
                offsetSeconds = -offsetSeconds; // UTC = local - offset
            }

            i = text.Length;
        }
        else
        {
            return false;
        }

        if (i != text.Length)
        {
            return false;
        }

        var seconds = (Urlog.Sources.Nginx.ClfTimestamp.DaysFromCivil(year, month, day) * 86_400L) + (hour * 3600L) + (minute * 60L) + second + offsetSeconds;
        unixMilliseconds = (seconds * 1000) + millis;
        return true;
    }

    private static int Digits(ReadOnlySpan<byte> text, int at, int count)
    {
        if (at + count > text.Length)
        {
            return -1;
        }

        var value = 0;
        for (var i = 0; i < count; i++)
        {
            var d = text[at + i] - '0';
            if ((uint)d > 9)
            {
                return -1;
            }

            value = (value * 10) + d;
        }

        return value;
    }

    private static int ClfDays(int year, int month)
    {
        ReadOnlySpan<byte> days = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
        if (month == 2 && year % 4 == 0 && (year % 100 != 0 || year % 400 == 0))
        {
            return 29;
        }

        return days[month - 1];
    }
}
