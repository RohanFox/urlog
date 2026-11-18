using System.Buffers.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Urlog.Query;

/// <summary>The cursor is not one this search issued: malformed, of another version, or for another case or query text.</summary>
public sealed class CursorException : Exception
{
    public CursorException()
    {
    }

    public CursorException(string message)
        : base(message)
    {
    }

    public CursorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Where a paged listing stands (design record, "Query language", The search API): the range, zone and clock the
/// first request resolved, so a relative time means the same instant on every page; the key of the last row sent,
/// which is the values of the query's sort keys followed by <c>(ts, file_id, file_offset)</c>; and how many rows
/// came before. The order is total, because a visible row is the only one with its file and offset, so the next
/// page is the rows strictly after this key: none twice, none skipped, whatever their times.
/// <para>
/// It is opaque to the client and not signed. It grants nothing: every row it can reach is one the caller may
/// read by narrowing the range. <see cref="Binding"/> ties it to the case and the query text.
/// </para>
/// </summary>
/// <param name="From">Resolved start of the range, UTC milliseconds since the epoch.</param>
/// <param name="To">Resolved end of the range (exclusive), UTC milliseconds.</param>
/// <param name="Zone">The display zone id the first request named.</param>
/// <param name="Now">The clock of the first request, UTC milliseconds.</param>
/// <param name="Binding">First 16 hex characters of the SHA-256 over the case id and the query text.</param>
/// <param name="SortValues">The last row's value of each sort key of the query, as the result printed it.</param>
/// <param name="Ts">The last row's time as the result printed it (<c>yyyy-MM-dd HH:mm:ss.fff</c>, UTC).</param>
/// <param name="FileId">The last row's file.</param>
/// <param name="FileOffset">The last row's offset in its file.</param>
/// <param name="Sent">Rows sent before the page this cursor asks for.</param>
/// <param name="WindowSeconds">The length of the time window that found the last page; the next page starts from it. 0: none.</param>
public sealed record Cursor(
    long From,
    long To,
    string Zone,
    long Now,
    string Binding,
    ImmutableArray<string> SortValues,
    string Ts,
    ulong FileId,
    ulong FileOffset,
    int Sent,
    long WindowSeconds)
{
    public const int Version = 1;

    private const int MaxEncodedLength = 8192;

    /// <summary>What ties a cursor to its search: the case and the query text, nothing a page request may change.</summary>
    public static string Bind(ulong caseId, string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(caseId.ToString(CultureInfo.InvariantCulture) + "\n" + query));
        return Convert.ToHexStringLower(hash)[..16];
    }

    public string Encode()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("v", Version);
            json.WriteNumber("f", From);
            json.WriteNumber("t", To);
            json.WriteString("z", Zone);
            json.WriteNumber("now", Now);
            json.WriteString("b", Binding);
            json.WriteStartArray("k");
            foreach (var value in SortValues)
            {
                json.WriteStringValue(value);
            }

            json.WriteEndArray();
            json.WriteString("ts", Ts);
            json.WriteNumber("fid", FileId);
            json.WriteNumber("off", FileOffset);
            json.WriteNumber("n", Sent);
            json.WriteNumber("w", WindowSeconds);
            json.WriteEndObject();
        }

        return Base64Url.EncodeToString(stream.ToArray());
    }

    /// <summary>Reads a cursor and checks it against the request: throws <see cref="CursorException"/> for anything else.</summary>
    public static Cursor Decode(string text, ulong caseId, string query)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Length > MaxEncodedLength || !Base64Url.IsValid(text))
        {
            throw new CursorException("the cursor is not one this search issued");
        }

        Cursor cursor;
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(text));
            var root = document.RootElement;
            if (root.GetProperty("v").GetInt32() != Version)
            {
                throw new CursorException("the cursor is of another version: run the search again");
            }

            cursor = new Cursor(
                root.GetProperty("f").GetInt64(),
                root.GetProperty("t").GetInt64(),
                root.GetProperty("z").GetString() ?? throw new CursorException("the cursor is not one this search issued"),
                root.GetProperty("now").GetInt64(),
                root.GetProperty("b").GetString() ?? string.Empty,
                [.. root.GetProperty("k").EnumerateArray().Select(v => v.GetString() ?? string.Empty)],
                root.GetProperty("ts").GetString() ?? string.Empty,
                root.GetProperty("fid").GetUInt64(),
                root.GetProperty("off").GetUInt64(),
                root.GetProperty("n").GetInt32(),
                root.GetProperty("w").GetInt64());
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CursorException("the cursor is not one this search issued", e);
        }

        if (cursor.Binding != Bind(caseId, query))
        {
            throw new CursorException("the cursor belongs to another search: the case and the query text must be those of the first request");
        }

        if (cursor.Sent < 0
            || cursor.WindowSeconds < 0
            || cursor.To <= cursor.From
            || !DateTime.TryParseExact(cursor.Ts, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new CursorException("the cursor is not one this search issued");
        }

        return cursor;
    }
}
