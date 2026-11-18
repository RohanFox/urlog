using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Urlog.Api.Search;

/// <summary>The two export formats.</summary>
public enum ExportFormat
{
    Csv,
    Json,
}

/// <summary>
/// An export as it is written (design record, "Query language", The search API): one row at a time into the
/// response's pipe, flushed when <see cref="FlushBytes"/> have gathered, so the memory in use is one row and the
/// pipe's buffer however long the result is, and a slow reader slows the read of the event store.
/// <para>
/// CSV is RFC 4180: a header row, CRLF, a cell quoted when it holds a comma, a quote or a line break. JSON is one
/// object a line. When a column is a field the catalog marks sensitive the file starts with a line that names
/// those fields. Cells are written as they are stored. The one exception is asked for by the caller
/// (<c>formula_guard</c>, CSV only): a cell a spreadsheet would read as a formula gets a leading apostrophe, and
/// the first line of the file says the cells were altered.
/// </para>
/// <para>
/// Every byte handed to the response goes through a SHA-256 on its way, so the audit row of an export can say
/// what was sent: the rows, the bytes and their hash, whole or up to where the export stopped.
/// </para>
/// </summary>
public sealed class ExportWriter : IDisposable
{
    public const int FlushBytes = 64 * 1024;

    private static readonly SearchValues<char> NeedsQuotes = SearchValues.Create(",\"\r\n");

    private readonly PipeWriter _output;
    private readonly ExportFormat _format;
    private readonly bool _formulaGuard;
    private readonly StringBuilder _line = new();
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private IReadOnlyList<string> _columns = [];
    private long _pending;

    public ExportWriter(PipeWriter output, ExportFormat format, bool formulaGuard)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _format = format;
        _formulaGuard = formulaGuard && format == ExportFormat.Csv;
    }

    /// <summary>Rows written so far.</summary>
    public long Rows { get; private set; }

    /// <summary>Bytes written so far, the lines before the first row included.</summary>
    public long Bytes { get; private set; }

    /// <summary>The SHA-256 of the bytes written so far, as lowercase hex. The writer goes on after it.</summary>
    public string Sha256() => Convert.ToHexStringLower(_hash.GetCurrentHash());

    public void Dispose() => _hash.Dispose();

    /// <summary>The lines before the first row: the altered note, the sensitive fields, and for CSV the header.</summary>
    public void Begin(IReadOnlyList<string> columns, IReadOnlyList<string> sensitive)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(sensitive);
        _columns = columns;
        if (_format == ExportFormat.Csv)
        {
            if (_formulaGuard)
            {
                Put("# altered: cells that begin with = + - @ or a tab carry a leading apostrophe (formula_guard); the stored values do not\r\n");
            }

            if (sensitive.Count > 0)
            {
                Put("# sensitive fields: " + string.Join(", ", sensitive) + "\r\n");
            }

            _line.Clear();
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0)
                {
                    _line.Append(',');
                }

                AppendCsv(columns[i], guard: false);
            }

            Put(_line.Append("\r\n").ToString());
        }
        else if (sensitive.Count > 0)
        {
            Put(JsonSerializer.Serialize(new Dictionary<string, object> { ["sensitive_fields"] = sensitive }) + "\n");
        }
    }

    /// <summary>One row. Returns after a flush when enough has gathered, which is where a slow reader holds the export back.</summary>
    public async ValueTask WriteAsync(object?[] row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_format == ExportFormat.Csv)
        {
            _line.Clear();
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0)
                {
                    _line.Append(',');
                }

                AppendCsv(CellText(row[i]), _formulaGuard);
            }

            Put(_line.Append("\r\n").ToString());
        }
        else
        {
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                for (var i = 0; i < row.Length && i < _columns.Count; i++)
                {
                    json.WritePropertyName(_columns[i]);
                    WriteJson(json, row[i]);
                }

                json.WriteEndObject();
            }

            _output.Write(buffer.WrittenSpan);
            _output.Write("\n"u8);
            _hash.AppendData(buffer.WrittenSpan);
            _hash.AppendData("\n"u8);
            _pending += buffer.WrittenCount + 1;
            Bytes += buffer.WrittenCount + 1;
        }

        Rows++;
        if (_pending >= FlushBytes)
        {
            _pending = 0;
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        _pending = 0;
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A cell as text: instants in UTC with a <c>Z</c>, an IPv4-mapped address as IPv4, a list or a map as JSON.</summary>
    public static string CellText(object? value) => value switch
    {
        null or DBNull => string.Empty,
        string s => s,
        DateTime dt => Instant(dt),
        DateTimeOffset dto => Instant(dto.UtcDateTime),
        IPAddress ip => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString(),
        bool b => b ? "true" : "false",
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        System.Collections.IDictionary or Array => Json(value),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>A cell as a JSON value: numbers as numbers, a map as an object, a list as an array, the rest as <see cref="CellText"/>.</summary>
    public static void WriteJson(Utf8JsonWriter json, object? value)
    {
        ArgumentNullException.ThrowIfNull(json);
        switch (value)
        {
            case null or DBNull:
                json.WriteNullValue();
                break;
            case bool b:
                json.WriteBooleanValue(b);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong u:
                json.WriteNumberValue(u);
                break;
            case float or double:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(d))
                {
                    json.WriteNumberValue(d);
                }
                else
                {
                    json.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));
                }

                break;
            case decimal m:
                json.WriteNumberValue(m);
                break;
            case string or DateTime or DateTimeOffset or IPAddress or byte[]:
                json.WriteStringValue(CellText(value));
                break;
            case System.Collections.IDictionary map:
                json.WriteStartObject();
                foreach (System.Collections.DictionaryEntry entry in map)
                {
                    json.WritePropertyName(CellText(entry.Key));
                    WriteJson(json, entry.Value);
                }

                json.WriteEndObject();
                break;
            case Array list:
                json.WriteStartArray();
                foreach (var item in list)
                {
                    WriteJson(json, item);
                }

                json.WriteEndArray();
                break;
            default:
                json.WriteStringValue(CellText(value));
                break;
        }
    }

    private static string Instant(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Json(object value)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var json = new Utf8JsonWriter(buffer))
        {
            WriteJson(json, value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void AppendCsv(string cell, bool guard)
    {
        if (guard && cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' or '\t')
        {
            cell = "'" + cell;
        }

        if (cell.AsSpan().IndexOfAny(NeedsQuotes) < 0)
        {
            _line.Append(cell);
            return;
        }

        _line.Append('"').Append(cell.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }

    private void Put(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var span = _output.GetSpan(bytes);
        var written = Encoding.UTF8.GetBytes(text, span);
        _hash.AppendData(span[..written]);
        _output.Advance(written);
        _pending += written;
        Bytes += written;
    }
}
