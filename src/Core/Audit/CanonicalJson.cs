using System.Buffers;
using System.Globalization;
using System.Text;

namespace Urlog.Core.Audit;

/// <summary>
/// The one serialiser behind every audit hash (design record, "Audit log"). Its output is the hash input, so its
/// rules are pinned by vectors in <c>tests/Metadata.Tests/CanonicalJsonTests</c> and never follow a runtime default:
/// <list type="number">
/// <item>Object members sorted by key in ordinal UTF-16 code-unit order (RFC 8785's order); a duplicate key is refused.</item>
/// <item>No whitespace. UTF-8 without BOM.</item>
/// <item>Strings: <c>"</c> -> <c>\"</c>, <c>\</c> -> <c>\\</c>, U+0008/0009/000A/000C/000D -> <c>\b \t \n \f \r</c>, every other
/// control below U+0020 -> <c>\u00xx</c> with lowercase hex. Everything else raw UTF-8 (<c>/</c>, U+007F, U+2028, every
/// non-ASCII code point). A lone surrogate is refused.</item>
/// <item>Numbers: integers only, shortest decimal, optional <c>-</c>; <c>double</c>, <c>float</c> and <c>decimal</c> are refused.
/// Booleans and <c>null</c> as the literals.</item>
/// <item>A <see cref="DateTime"/> is a string <c>yyyy-MM-ddTHH:mm:ss.ffffffZ</c>: UTC, six digits always, truncated to
/// whole microseconds so the value survives SQLite's seven-digit text and Postgres's <c>timestamptz</c> unchanged.</item>
/// </list>
/// Accepted values: <c>null</c>, <see cref="string"/>, <see cref="bool"/>, the integer primitives, <see cref="DateTime"/>,
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> and <c>IReadOnlyList&lt;object?&gt;</c>, nested.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Rule 5: the timestamp form inside the hash input.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static byte[] Serialize(object? value)
    {
        var output = new ArrayBufferWriter<byte>(256);
        Write(value, output);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>The canonical text, for columns that store JSON (<c>details_json</c>).</summary>
    public static string SerializeToString(object? value) => Encoding.UTF8.GetString(Serialize(value));

    public static void Write(object? value, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        switch (value)
        {
            case null:
                output.Write("null"u8);
                break;
            case string s:
                WriteString(s, output);
                break;
            case bool b:
                output.Write(b ? "true"u8 : "false"u8);
                break;
            case int n:
                WriteInteger(n, output);
                break;
            case long n:
                WriteInteger(n, output);
                break;
            case short n:
                WriteInteger(n, output);
                break;
            case sbyte n:
                WriteInteger(n, output);
                break;
            case byte n:
                WriteInteger(n, output);
                break;
            case ushort n:
                WriteInteger(n, output);
                break;
            case uint n:
                WriteInteger(n, output);
                break;
            case ulong n:
                WriteInteger(n, output);
                break;
            case DateTime t:
                WriteString(FormatTimestamp(t), output);
                break;
            case float or double or decimal:
                throw new ArgumentException($"canonical JSON refuses {value.GetType().Name}: integers only (milliseconds, bytes), or a string", nameof(value));
            case IReadOnlyDictionary<string, object?> members:
                WriteObject(members, output);
                break;
            case IReadOnlyList<object?> items:
                WriteArray(items, output);
                break;
            default:
                throw new ArgumentException($"canonical JSON has no form for {value.GetType()}", nameof(value));
        }
    }

    /// <summary>A string value (null as the literal), without boxing: the typed form of <see cref="Write(object?, IBufferWriter{byte})"/>.</summary>
    public static void Write(string? value, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (value is null)
        {
            output.Write("null"u8);
        }
        else
        {
            WriteString(value, output);
        }
    }

    public static void Write(long value, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        WriteInteger(value, output);
    }

    public static void Write(long? value, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (value is { } n)
        {
            WriteInteger(n, output);
        }
        else
        {
            output.Write("null"u8);
        }
    }

    public static void Write(DateTime value, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Span<char> text = stackalloc char[27];
        if (!TruncateToMicroseconds(value).TryFormat(text, out var written, TimestampFormat, CultureInfo.InvariantCulture) || written != 27)
        {
            throw new InvalidOperationException("timestamp did not format to 27 characters");
        }

        output.Write("\""u8);
        var bytes = output.GetSpan(27);
        for (var i = 0; i < 27; i++)
        {
            bytes[i] = (byte)text[i];
        }

        output.Advance(27);
        output.Write("\""u8);
    }

    /// <summary>Rule 5 as a value: UTC, whole microseconds. What the audit writer stores, so the stored value re-hashes equal.</summary>
    public static DateTime TruncateToMicroseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - (utc.Ticks % TicksPerMicrosecond), DateTimeKind.Utc);
    }

    public static string FormatTimestamp(DateTime value) => TruncateToMicroseconds(value).ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static void WriteObject(IReadOnlyDictionary<string, object?> members, IBufferWriter<byte> output)
    {
        var sorted = new KeyValuePair<string, object?>[members.Count];
        var i = 0;
        foreach (var member in members)
        {
            ArgumentNullException.ThrowIfNull(member.Key, "key");
            sorted[i++] = member;
        }

        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        output.Write("{"u8);
        for (i = 0; i < sorted.Length; i++)
        {
            if (i > 0)
            {
                if (string.Equals(sorted[i - 1].Key, sorted[i].Key, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"canonical JSON refuses the duplicate key '{sorted[i].Key}'", nameof(members));
                }

                output.Write(","u8);
            }

            WriteString(sorted[i].Key, output);
            output.Write(":"u8);
            Write(sorted[i].Value, output);
        }

        output.Write("}"u8);
    }

    private static void WriteArray(IReadOnlyList<object?> items, IBufferWriter<byte> output)
    {
        output.Write("["u8);
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                output.Write(","u8);
            }

            Write(items[i], output);
        }

        output.Write("]"u8);
    }

    private static void WriteInteger<T>(T value, IBufferWriter<byte> output) where T : IUtf8SpanFormattable
    {
        var span = output.GetSpan(24);
        if (!value.TryFormat(span, out var written, default, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("integer did not fit in 24 bytes");
        }

        output.Advance(written);
    }

    private static void WriteString(string value, IBufferWriter<byte> output)
    {
        output.Write("\""u8);
        var rest = value.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done)
            {
                throw new ArgumentException("canonical JSON refuses a string with a lone surrogate", nameof(value));
            }

            rest = rest[consumed..];
            switch (rune.Value)
            {
                case '"':
                    output.Write("\\\""u8);
                    break;
                case '\\':
                    output.Write("\\\\"u8);
                    break;
                case '\b':
                    output.Write("\\b"u8);
                    break;
                case '\t':
                    output.Write("\\t"u8);
                    break;
                case '\n':
                    output.Write("\\n"u8);
                    break;
                case '\f':
                    output.Write("\\f"u8);
                    break;
                case '\r':
                    output.Write("\\r"u8);
                    break;
                case < 0x20:
                    var escape = output.GetSpan(6);
                    "\\u00"u8.CopyTo(escape);
                    escape[4] = Hex(rune.Value >> 4);
                    escape[5] = Hex(rune.Value & 0xF);
                    output.Advance(6);
                    break;
                default:
                    var raw = output.GetSpan(4);
                    output.Advance(rune.EncodeToUtf8(raw));
                    break;
            }
        }

        output.Write("\""u8);
    }

    private static byte Hex(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
}
