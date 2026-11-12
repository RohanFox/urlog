namespace Urlog.Core.Detection;

/// <summary>Byte-level questions about a sample: is it UTF-16 without a mark, how much of it is not text, what its first line says.</summary>
internal static class TextProbe
{
    private const int Utf16SampleBytes = 4096;

    /// <summary>
    /// UTF-16 without a byte-order mark: text in the Latin range has a zero in every other byte. True when at least
    /// 90 % of one half of the first 4 KB is NUL and under 10 % of the other half is.
    /// </summary>
    public static bool LooksUtf16(ReadOnlySpan<byte> head, out bool bigEndian)
    {
        bigEndian = false;
        var length = Math.Min(head.Length, Utf16SampleBytes) & ~1;
        if (length < 16)
        {
            return false;
        }

        var evenNul = 0;
        var oddNul = 0;
        for (var i = 0; i < length; i += 2)
        {
            if (head[i] == 0)
            {
                evenNul++;
            }

            if (head[i + 1] == 0)
            {
                oddNul++;
            }
        }

        var pairs = length / 2;
        if (oddNul * 10 >= pairs * 9 && evenNul * 10 < pairs)
        {
            return true;
        }

        if (evenNul * 10 >= pairs * 9 && oddNul * 10 < pairs)
        {
            bigEndian = true;
            return true;
        }

        return false;
    }

    /// <summary>NUL, or a C0 control other than tab, LF, CR, FF and ESC, or DEL.</summary>
    public static bool IsControl(byte b) => b < 0x20 ? b is not (0x09 or 0x0A or 0x0C or 0x0D or 0x1B) : b == 0x7F;

    public static bool HasControlByte(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (IsControl(b))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Counts the bytes of <paramref name="text"/> that are not text; <paramref name="nul"/> is set when one of them is NUL.</summary>
    public static int CountControl(ReadOnlySpan<byte> text, ref bool nul)
    {
        var count = 0;
        foreach (var b in text)
        {
            if (IsControl(b))
            {
                count++;
                nul |= b == 0;
            }
        }

        return count;
    }

    /// <summary>
    /// Text that is a script or a web page by its first line: <c>#!</c>, <c>&lt;?php</c>, <c>&lt;!DOCTYPE html</c>,
    /// <c>&lt;html</c> (the last two in any case, after leading white space). Null for anything else.
    /// </summary>
    public static string? FirstLineArtifact(ReadOnlySpan<byte> text)
    {
        if (text.StartsWith("#!"u8))
        {
            return "script";
        }

        text = text.TrimStart(" \t\r\n"u8);
        if (text.StartsWith("<?php"u8))
        {
            return "php";
        }

        if (StartsWithIgnoreCase(text, "<!doctype html"u8) || StartsWithIgnoreCase(text, "<html"u8))
        {
            return "html";
        }

        return null;
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> lowerPrefix)
    {
        if (text.Length < lowerPrefix.Length)
        {
            return false;
        }

        for (var i = 0; i < lowerPrefix.Length; i++)
        {
            var b = text[i];
            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b |= 0x20;
            }

            if (b != lowerPrefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
