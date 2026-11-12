using System.Buffers;
using Urlog.Core.Sources;

namespace Urlog.Sources.Syslog;

/// <summary>
/// RFC 5424 STRUCTURED-DATA: <c>[SD-ID (SP PARAM-NAME="PARAM-VALUE")*]+</c>, where a value escapes <c>"</c>,
/// <c>\</c> and <c>]</c> with a backslash. <see cref="Scan"/> validates the structure without writing anything (a
/// malformed element must reject the line before any extras are appended to the pending row); <see cref="Write"/>
/// walks the same bytes again and appends one <c>extras</c> entry per parameter under
/// <c>log.syslog.structured_data.&lt;sd-id&gt;.&lt;name&gt;</c>, the value as logged (escapes kept, as the nginx
/// source keeps them). An element without parameters is recorded as <c>log.syslog.structured_data.&lt;sd-id&gt;</c>
/// with an empty value. Keys are assembled on the caller's stack buffer; an element whose keys do not fit is written
/// whole under <c>log.syslog.structured_data</c>. Allocation-free.
/// </summary>
public static class SyslogStructuredData
{
    private static readonly SearchValues<byte> QuoteOrBackslash = SearchValues.Create("\"\\"u8);
    private static readonly SearchValues<byte> SdIdEnd = SearchValues.Create(" ]"u8);
    private static readonly SearchValues<byte> ParamNameEnd = SearchValues.Create("= ]\""u8);

    private static ReadOnlySpan<byte> KeyPrefix => "log.syslog.structured_data."u8;

    private static ReadOnlySpan<byte> FallbackKey => "log.syslog.structured_data"u8;

    /// <summary>
    /// Length of the structured data at the start of <paramref name="text"/> (which must start with <c>[</c>), or
    /// -1 when an element is malformed: unterminated, an empty SD-ID, a parameter without <c>="</c>.
    /// </summary>
    public static int Scan(ReadOnlySpan<byte> text)
    {
        var pos = 0;
        while (pos < text.Length && text[pos] == (byte)'[')
        {
            pos++;
            var rest = text[pos..];
            var idLength = rest.IndexOfAny(SdIdEnd);
            if (idLength <= 0)
            {
                return -1;
            }

            pos += idLength;
            while (true)
            {
                if (pos >= text.Length)
                {
                    return -1;
                }

                if (text[pos] == (byte)']')
                {
                    pos++;
                    break;
                }

                // A parameter: SP PARAM-NAME = " VALUE "
                pos++;
                rest = text[pos..];
                var nameLength = rest.IndexOfAny(ParamNameEnd);
                if (nameLength <= 0 || rest[nameLength] != (byte)'=' || nameLength + 1 >= rest.Length || rest[nameLength + 1] != (byte)'"')
                {
                    return -1;
                }

                pos += nameLength + 2;
                var valueLength = ClosingQuote(text[pos..]);
                if (valueLength < 0)
                {
                    return -1;
                }

                pos += valueLength + 1;
            }
        }

        return pos;
    }

    /// <summary>Appends the parameters of <paramref name="sd"/> (validated by <see cref="Scan"/>) to <paramref name="batch"/>.</summary>
    public static void Write(ReadOnlySpan<byte> sd, IBatchWriter batch, Span<byte> keyBuffer)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var pos = 0;
        while (pos < sd.Length && sd[pos] == (byte)'[')
        {
            var elementStart = pos;
            pos++;
            var id = sd[pos..];
            var idLength = id.IndexOfAny(SdIdEnd);
            id = id[..idLength];
            pos += idLength;

            // "log.syslog.structured_data." + id + "." then the parameter name
            var prefixLength = KeyPrefix.Length + idLength + 1;
            var fits = prefixLength <= keyBuffer.Length;
            if (fits)
            {
                KeyPrefix.CopyTo(keyBuffer);
                id.CopyTo(keyBuffer[KeyPrefix.Length..]);
                keyBuffer[prefixLength - 1] = (byte)'.';
            }

            if (sd[pos] == (byte)']')
            {
                // An element without parameters: record that it was there.
                if (fits)
                {
                    batch.AppendExtra(keyBuffer[..(prefixLength - 1)], default);
                }
                else
                {
                    batch.AppendExtra(FallbackKey, sd[elementStart..(pos + 1)]);
                }

                pos++;
                continue;
            }

            if (!fits)
            {
                // The SD-ID alone overflows the key buffer: the whole element, as logged, under the generic key.
                var close = ElementEnd(sd, pos);
                batch.AppendExtra(FallbackKey, sd[elementStart..close]);
                pos = close;
                continue;
            }

            while (sd[pos] != (byte)']')
            {
                pos++;
                var paramStart = pos;
                var name = sd[pos..];
                var nameLength = name.IndexOfAny(ParamNameEnd);
                name = name[..nameLength];
                pos += nameLength + 2;
                var valueLength = ClosingQuote(sd[pos..]);
                var value = sd.Slice(pos, valueLength);
                pos += valueLength + 1;

                if (prefixLength + nameLength <= keyBuffer.Length)
                {
                    name.CopyTo(keyBuffer[prefixLength..]);
                    batch.AppendExtra(keyBuffer[..(prefixLength + nameLength)], value);
                }
                else
                {
                    batch.AppendExtra(FallbackKey, sd[paramStart..pos]); // name="value" as logged
                }
            }

            pos++;
        }
    }

    /// <summary>Index just past the <c>]</c> that closes the element starting at <paramref name="at"/> (validated by <see cref="Scan"/>).</summary>
    private static int ElementEnd(ReadOnlySpan<byte> sd, int at)
    {
        var pos = at + 1;
        pos += sd[pos..].IndexOfAny(SdIdEnd);
        while (sd[pos] != (byte)']')
        {
            pos++;
            pos += sd[pos..].IndexOfAny(ParamNameEnd) + 2;
            pos += ClosingQuote(sd[pos..]) + 1;
        }

        return pos + 1;
    }

    /// <summary>Index of the closing quote of a value whose opening quote has been consumed; a backslash escapes the next byte.</summary>
    private static int ClosingQuote(ReadOnlySpan<byte> text)
    {
        var position = 0;
        while (true)
        {
            var i = text[position..].IndexOfAny(QuoteOrBackslash);
            if (i < 0)
            {
                return -1;
            }

            position += i;
            if (text[position] == (byte)'"')
            {
                return position;
            }

            position += 2;
            if (position >= text.Length)
            {
                return -1;
            }
        }
    }
}
