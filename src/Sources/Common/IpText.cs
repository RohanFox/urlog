namespace Urlog.Sources.Common;

/// <summary>
/// Textual IPv4 / IPv6 address to network-order bytes, allocation-free, for tokenisers that hand the result to
/// <see cref="Urlog.Core.Sources.IBatchWriter.AppendIp"/>. Accepts dotted-quad IPv4 (each octet 1-3 digits, 0-255)
/// and RFC 4291 IPv6 text (1-4 hex digits per group, one <c>::</c>, an optional embedded dotted IPv4 as the last two
/// groups). Rejects zone ids (<c>%eth0</c>), brackets, ports and anything else: the caller keeps the original text.
/// Shared by the sources (nginx client address, syslog structured data); lives here and not in Core because it is
/// text parsing, not pipeline mechanics.
/// </summary>
public static class IpText
{
    /// <summary>
    /// Parses <paramref name="text"/> into <paramref name="destination"/> (at least 16 bytes) and returns the address
    /// length written: 4 for IPv4, 16 for IPv6, 0 when the text is not an address.
    /// </summary>
    public static int TryParse(ReadOnlySpan<byte> text, Span<byte> destination)
    {
        if (text.Length < 2)
        {
            return 0;
        }

        // Cheap dispatch: an IPv6 address contains a colon; an IPv4 address never does.
        return text.IndexOf((byte)':') < 0
            ? TryParseIPv4(text, destination) ? 4 : 0
            : TryParseIPv6(text, destination) ? 16 : 0;
    }

    /// <summary>Dotted-quad IPv4 into 4 bytes.</summary>
    public static bool TryParseIPv4(ReadOnlySpan<byte> text, Span<byte> destination)
    {
        if (destination.Length < 4)
        {
            return false;
        }

        var i = 0;
        for (var octet = 0; octet < 4; octet++)
        {
            if (octet > 0)
            {
                if (i >= text.Length || text[i] != (byte)'.')
                {
                    return false;
                }

                i++;
            }

            var value = 0;
            var digits = 0;
            while (i < text.Length && (uint)(text[i] - '0') <= 9)
            {
                value = (value * 10) + (text[i] - '0');
                digits++;
                i++;
                if (digits > 3 || value > 255)
                {
                    return false;
                }
            }

            if (digits == 0)
            {
                return false;
            }

            destination[octet] = (byte)value;
        }

        return i == text.Length;
    }

    /// <summary>RFC 4291 IPv6 text into 16 bytes.</summary>
    public static bool TryParseIPv6(ReadOnlySpan<byte> text, Span<byte> destination)
    {
        if (destination.Length < 16 || text.IsEmpty)
        {
            return false;
        }

        Span<ushort> groups = stackalloc ushort[8];
        var count = 0;
        var compressAt = -1;
        var i = 0;

        if (text[0] == (byte)':')
        {
            if (text.Length < 2 || text[1] != (byte)':')
            {
                return false; // a single leading colon
            }

            compressAt = 0;
            i = 2;
        }

        while (i < text.Length)
        {
            if (count == 8)
            {
                return false;
            }

            // A group: 1-4 hex digits, or a dotted IPv4 as the last two groups.
            var start = i;
            var value = 0;
            var digits = 0;
            while (i < text.Length && digits <= 4)
            {
                var h = HexValue(text[i]);
                if (h < 0)
                {
                    break;
                }

                value = (value << 4) | h;
                digits++;
                i++;
            }

            if (digits > 4)
            {
                return false;
            }

            if (i < text.Length && text[i] == (byte)'.')
            {
                // Embedded IPv4: must be the final part and leave room for two groups.
                if (count > 6)
                {
                    return false;
                }

                Span<byte> v4 = stackalloc byte[4];
                if (!TryParseIPv4(text[start..], v4))
                {
                    return false;
                }

                groups[count++] = (ushort)((v4[0] << 8) | v4[1]);
                groups[count++] = (ushort)((v4[2] << 8) | v4[3]);
                i = text.Length;
                break;
            }

            if (digits == 0)
            {
                return false;
            }

            groups[count++] = (ushort)value;
            if (i == text.Length)
            {
                break;
            }

            if (text[i] != (byte)':')
            {
                return false;
            }

            i++;
            if (i == text.Length)
            {
                return false; // trailing single colon
            }

            if (text[i] == (byte)':')
            {
                if (compressAt >= 0)
                {
                    return false; // a second "::"
                }

                compressAt = count;
                i++;
            }
        }

        if (compressAt < 0 ? count != 8 : count > 7)
        {
            return false;
        }

        var zeros = compressAt < 0 ? 0 : 8 - count;
        var g = 0;
        for (var slot = 0; slot < 8; slot++)
        {
            ushort v;
            if (compressAt >= 0 && slot >= compressAt && slot < compressAt + zeros)
            {
                v = 0;
            }
            else
            {
                v = groups[g++];
            }

            destination[slot * 2] = (byte)(v >> 8);
            destination[(slot * 2) + 1] = (byte)v;
        }

        return true;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
