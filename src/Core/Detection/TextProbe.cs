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
    /// <summary>A run of this many NUL bytes or more is padding (a crash, a power loss, a preallocated file), not content.</summary>
    public const int PaddingRun = 16;

    /// <summary>The most control bytes text may hold outside its padding, as a share of what is left.</summary>
    public const double ControlShareLimit = 0.03;

    /// <summary>Text has a line break at least this often on average, when it holds control bytes at all.</summary>
    public const int BytesPerLineLimit = 4096;

    /// <summary>The fewest bytes outside padding a window is judged on; a smaller rest says nothing.</summary>
    public const int LeastRest = 256;

    /// <summary>
    /// Reads one window of a file as text or not (design record, "Format detection", control bytes). Runs of
    /// <see cref="PaddingRun"/> or more NUL bytes are taken out first. A window without any control byte is
    /// text whatever its lines. A window with some is text when, in what is left, they are at most
    /// <see cref="ControlShareLimit"/> of the bytes and a line break comes at least every
    /// <see cref="BytesPerLineLimit"/> bytes on average (a window under <see cref="LeastRest"/> bytes needs no
    /// line break). A window that is padding alone says nothing.
    /// </summary>
    public static WindowReading Read(ReadOnlySpan<byte> window)
    {
        if (window.IsEmpty)
        {
            return new WindowReading(WindowKind.Nothing, false, 0, 0);
        }

        var control = 0;
        var padding = 0;
        var restControl = 0;
        var lines = 0;
        var i = 0;
        while (i < window.Length)
        {
            var b = window[i];
            if (b == 0)
            {
                var end = i + 1;
                while (end < window.Length && window[end] == 0)
                {
                    end++;
                }

                var run = end - i;
                control += run;
                if (run >= PaddingRun)
                {
                    padding += run;
                }
                else
                {
                    restControl += run;
                }

                i = end;
                continue;
            }

            if (IsControl(b))
            {
                control++;
                restControl++;
            }
            else if (b == (byte)'\n')
            {
                lines++;
            }

            i++;
        }

        var rest = window.Length - padding;
        if (control == 0)
        {
            return new WindowReading(WindowKind.Text, false, 0, rest);
        }

        if (rest < Math.Min(LeastRest, window.Length / 2))
        {
            return new WindowReading(WindowKind.Nothing, true, control, rest);
        }

        // Lines are asked of anything long enough to have them; a scrap shorter than that is judged on its bytes.
        var lined = lines > 0 ? rest <= (long)lines * BytesPerLineLimit : window.Length < LeastRest;
        var text = restControl <= rest * ControlShareLimit && lined;
        return new WindowReading(text ? WindowKind.Text : WindowKind.NotText, true, control, rest);
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

/// <summary>What a window of a file reads as.</summary>
internal enum WindowKind
{
    /// <summary>Padding alone, or too little outside it to judge.</summary>
    Nothing,

    Text,

    NotText,
}

/// <param name="Control">True when the window holds any control byte, padding included.</param>
/// <param name="ControlBytes">How many, padding included.</param>
/// <param name="Rest">Bytes outside padding.</param>
internal readonly record struct WindowReading(WindowKind Kind, bool Control, int ControlBytes, int Rest);
