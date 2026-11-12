using System.Globalization;
using System.Text;

namespace Urlog.Sources.Tests.Detection;

/// <summary>
/// <c>fixtures/generic/</c>: the sample with one line per case the generic source must handle, its expected rows,
/// and the adversarial files. The expected timestamp of every line is written here by hand, in UTC, for a +02:00
/// source clock, the year 2026 and a file time of 2026-03-02 08:00 UTC: the parser's output is never copied in.
/// </summary>
internal static class GenericSample
{
    private const long Noon = 1_772_366_400; // 2026-03-01T12:00:00Z
    private const string FileTime = "2026-03-02T08:00:00.000Z";

    public static IEnumerable<(string Name, byte[] Bytes)> Build(FixtureWriter.Writer w)
    {
        // line, terminator, expected ts (null: a blank line, no row), expected timestamp.source (null: the line's own)
        (string Line, string Eol, string? Ts, string? Source)[] lines =
        [
            ("=== application log, node web-01 ===", "\n", FileTime, "file"),
            (string.Empty, "\n", null, null),
            ("2026-03-01 12:00:00,123 INFO  [main] com.example.App - started in 2.3 s", "\n", "2026-03-01T10:00:00.123Z", null),
            ("2026-03-01T12:00:01Z WARN cache miss ratio 0.42", "\n", "2026-03-01T12:00:01.000Z", null),
            ("2026-03-01T12:00:02.5+05:30 INFO offset with a half hour", "\n", "2026-03-01T06:30:02.500Z", null),
            ("2026-03-01T12:00:03.123456789-0800 DEBUG nanosecond fraction", "\n", "2026-03-01T20:00:03.123Z", null),
            ("[2026-03-01 12:00:04.250] [pid 4242] ERROR connection reset by peer", "\n", "2026-03-01T10:00:04.250Z", null),
            ("java.io.IOException: connection reset", "\n", "2026-03-01T10:00:04.250Z", "previous"),
            ("\tat com.example.Net.read(Net.java:42)", "\n", "2026-03-01T10:00:04.250Z", "previous"),
            ("2026/03/01 12:00:05 [error] 77#0: *9 open() \"/var/www/missing\" failed (2: No such file or directory)", "\n", "2026-03-01T10:00:05.000Z", null),
            ("[Mar  1 12:00:06] worker 3: reloaded configuration", "\n", "2026-03-01T10:00:06.000Z", null),
            ("[Mar 1 2026 12:00:07] worker 3: year on the line", "\n", "2026-03-01T10:00:07.000Z", null),
            ("backend said: 203.0.113.9 - - [01/Mar/2026:12:00:08 +0100] \"GET /x HTTP/1.1\" 200 5", "\n", "2026-03-01T11:00:08.000Z", null),
            (Number(Noon + 9) + " epoch seconds at the start", "\n", "2026-03-01T12:00:09.000Z", null),
            ("[" + Number(Noon + 10) + ".250] bracketed epoch with a fraction", "\n", "2026-03-01T12:00:10.250Z", null),
            (Number(Noon + 11) + "123 epoch milliseconds", "\n", "2026-03-01T12:00:11.123Z", null),
            ("level=info ts=" + Number(Noon + 12) + " msg=\"logfmt key\"", "\n", "2026-03-01T12:00:12.000Z", null),
            ("{\"timestamp\": " + Number(Noon + 13) + "500, \"level\": \"warn\", \"msg\": \"json key, milliseconds\"}", "\n", "2026-03-01T12:00:13.500Z", null),
            ("request id " + Number(Noon + 14) + " is not a timestamp here", "\n", "2026-03-01T12:00:13.500Z", "previous"),
            ("2026-13-45 99:99:99 is not a date, 2026-03-01 12:00:15 is", "\n", "2026-03-01T10:00:15.000Z", null),
            ("2026-03-01 23:59:60 leap second", "\n", "2026-03-01T22:00:00.000Z", null),
            ("2026-03-01 12:00:16 INFO пользователь Юрий вошёл, café ☕", "\n", "2026-03-01T10:00:16.000Z", null),
            ("2026-03-01 12:00:17 INFO line with CRLF", "\r\n", "2026-03-01T10:00:17.000Z", null),
            ("2026-03-01 12:00:18 DEBUG payload=" + new string('x', 10_200), "\n", "2026-03-01T10:00:18.000Z", null),
            (new string('A', 300) + " 2026-03-01 12:00:19 is past the first 256 bytes", "\n", "2026-03-01T10:00:18.000Z", "previous"),
            ("time=\"2026-03-01T12:00:20+02:00\" level=info msg=\"quoted iso\"", "\n", "2026-03-01T10:00:20.000Z", null),
            ("v1.2.2026-03-01 12:00:21 after a dot is part of something else", "\n", "2026-03-01T10:00:20.000Z", "previous"),
            ("Sun Mar  1 12:00:22 2026 ctime form: month, day and time are read, the year is tracked", "\n", "2026-03-01T10:00:22.000Z", null),
            ("2026-03-01 12:00:23 last line, no newline", string.Empty, "2026-03-01T10:00:23.000Z", null),
        ];

        using var sample = new MemoryStream();
        sample.Write([0xEF, 0xBB, 0xBF]);
        var expected = new StringBuilder();
        foreach (var (line, eol, ts, source) in lines)
        {
            var offset = sample.Position;
            sample.Write(Encoding.UTF8.GetBytes(line + eol));
            if (ts is null)
            {
                continue;
            }

            expected.Append("{\"file_offset\": ").Append(offset.ToString(CultureInfo.InvariantCulture))
                .Append(", \"ts\": \"").Append(ts).Append("\", \"host\": \"web-01\", \"message\": ").Append(Json(line)).Append(", \"raw\": ").Append(Json(line));
            if (source is not null)
            {
                expected.Append(", \"extras\": {\"timestamp.source\": \"").Append(source).Append("\"}");
            }

            expected.Append("}\n");
        }

        yield return ("sample.log", sample.ToArray());
        yield return ("expected.jsonl", Encoding.UTF8.GetBytes(expected.ToString()));

        var app = w.AppLines(t => FixtureWriter.Writer.Iso(t, ' ', 3, string.Empty), " {0} {1}");
        yield return ("adversarial/truncated-last-line.log", FixtureWriter.Writer.Text(app)[..^17]);
        yield return ("adversarial/latin1.log", FixtureWriter.Writer.Text(w.AppLines(t => FixtureWriter.Writer.Iso(t, ' ', 3, string.Empty), " {0} dépôt créé: {1}"), encoding: Encoding.Latin1));
        yield return ("adversarial/mixed-syslog.log", FixtureWriter.Writer.Text([.. app[..10], .. w.SyslogLines(10)]));
        yield return ("adversarial/nul-run.log", [.. FixtureWriter.Writer.Text(app[..10]), .. new byte[5000], (byte)'\n', .. FixtureWriter.Writer.Text(app[10..])]);
        yield return ("adversarial/garbage.bin", w.Random.Bytes(2048));
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A JSON string with the two mandatory escapes and control characters as <c>\uXXXX</c>; everything else as it is.</summary>
    private static string Json(string text)
    {
        var json = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            if (c is '"' or '\\')
            {
                json.Append('\\').Append(c);
            }
            else if (c < ' ')
            {
                json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                json.Append(c);
            }
        }

        return json.Append('"').ToString();
    }
}
