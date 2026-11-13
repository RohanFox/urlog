using System.Buffers;
using System.Text;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Syslog;

namespace Urlog.Sources.Tests.Syslog;

public sealed class SyslogSourceTests
{
    /// <summary>The round-trip context of <c>fixtures/syslog/expected.jsonl</c>: a +02:00 clock, a file that starts in 2025.</summary>
    private static readonly TimeSpan SampleOffset = TimeSpan.FromHours(2);
    private const int SampleYear = 2025;

    [Fact]
    public void RegisteredWithAliasesAndStateful()
    {
        var registration = Assert.Single(LogSourceRegistry.Registrations, r => r.Id == "syslog");
        Assert.Equal(["rfc3164", "rfc5424", "postfix"], registration.Aliases);
        var source = registration.Factory();
        Assert.IsType<SyslogSource>(source);
        Assert.Equal(Framing.Line, source.Framing);
        Assert.True(source.Info.Stateful, "the year is tracked across the file, so chunks must arrive in order");
        Assert.Contains(EventColumn.Host, source.Schema.Columns);
        Assert.Contains(EventColumn.Severity, source.Schema.Columns);
    }

    [Fact]
    public async Task SampleRoundTripsExpectedJsonl()
    {
        var expected = Fixtures.ReadExpected("syslog");
        var actual = await Fixtures.ParseFileAsync(new SyslogSource(), Fixtures.Path("syslog", "sample.log"), timeZoneHint: SampleOffset, yearHint: SampleYear);

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i] == actual[i], $"row {i} differs.\nexpected: {expected[i]}\nactual:   {actual[i]}");
        }

        // raw is the file's bytes at file_offset, byte for byte, with no terminator.
        var file = Fixtures.Bytes("syslog", "sample.log");
        foreach (var row in actual)
        {
            Assert.Equal(file.AsSpan((int)row.FileOffset, row.RawBytes.Length).ToArray(), row.RawBytes.ToArray());
            var next = (int)row.FileOffset + row.RawBytes.Length;
            Assert.True(next == file.Length || file[next] == (byte)'\n' || file[next] == (byte)'\r', $"raw at {row.FileOffset} does not end at a line terminator");
        }

        Assert.Equal(5, actual.Count(r => r.IsReject));
        Assert.Contains(actual, r => r.RawBytes.Length > 10_000);
        Assert.Contains(actual, r => r.Host == "2001:db8::1");
        Assert.Contains(actual, r => r.Program == "postfix" && r.Extras["postfix.daemon"] == "smtpd");
    }

    [Fact]
    public void ParsingTheSampleAllocatesNothingPerRow()
    {
        byte[] sample = [.. Fixtures.Bytes("syslog", "sample.log").AsSpan(3), (byte)'\n']; // core strips the BOM before Parse; terminate the last line
        var data = new byte[sample.Length * 200];
        for (var i = 0; i < 200; i++)
        {
            sample.CopyTo(data, i * sample.Length);
        }

        var source = new SyslogSource();
        var writer = new NullBatchWriter();
        var ctx = new ParseContext { CaseId = 1, HostId = 1, FileId = 1, DefaultHost = "web-01"u8.ToArray(), YearHint = SampleYear, TimeZoneHint = SampleOffset };
        ctx.Bind(writer);

        source.Parse(new ReadOnlySequence<byte>(data), ctx, writer); // warm-up: JIT and tiering
        var rowsBefore = writer.Rows;
        var allocated = AllocationProbe.Least(_ => source.Parse(new ReadOnlySequence<byte>(data), ctx, writer));

        Assert.Equal(46 * 200 * AllocationProbe.Passes, writer.Rows - rowsBefore);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ChunkBoundariesDoNotChangeRows()
    {
        // Stateful: the year tracked across chunks must give the same rows whether the sample is one chunk or many.
        var sample = Fixtures.Bytes("syslog", "sample.log").AsSpan(3).ToArray();
        var once = Parse(sample, maxChunk: int.MaxValue);
        var small = Parse(sample, maxChunk: 300); // a few records per chunk, the 10 KB line alone in one
        Assert.Equal(once.Count, small.Count);
        for (var i = 0; i < once.Count; i++)
        {
            Assert.True(once[i] == small[i], $"row {i} differs between chunkings\n{once[i]}\n{small[i]}");
        }
    }

    [Fact]
    public void NaiveTimestampsUseTheHints()
    {
        const string line = "<13>Mar  1 12:00:00 web-01 app: hint check\n";
        Assert.Equal("1970-03-01T12:00:00.000Z", ParseLines(line)[0].Ts); // defaults: year 1970, UTC
        Assert.Equal("2026-03-01T12:00:00.000Z", ParseLines(line, yearHint: 2026)[0].Ts);
        Assert.Equal("2026-03-01T10:00:00.000Z", ParseLines(line, yearHint: 2026, timeZoneHint: TimeSpan.FromHours(2))[0].Ts);   // local = UTC + 2
        Assert.Equal("2026-03-01T17:30:00.000Z", ParseLines(line, yearHint: 2026, timeZoneHint: TimeSpan.FromHours(-5.5))[0].Ts); // local = UTC - 5:30
    }

    [Fact]
    public void TimestampsWithAnOffsetIgnoreTheHints()
    {
        var rows = ParseLines(
            "<13>2026-03-01T12:00:00+02:00 web-01 app: iso 3164\n" +
            "<13>1 2026-03-01T12:00:00.5-01:00 web-01 app - - - rfc 5424\n", yearHint: 1999, timeZoneHint: TimeSpan.FromHours(9));
        Assert.Equal("2026-03-01T10:00:00.000Z", rows[0].Ts);
        Assert.Equal("2026-03-01T13:00:00.500Z", rows[1].Ts);
    }

    [Fact]
    public void YearRollsOverAcrossChunksInFileOrder()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 40; i++)
        {
            sb.Append("<13>Dec 31 23:59:").Append((i % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture)).Append(" web-01 app: old year\n");
        }

        for (var i = 0; i < 40; i++)
        {
            sb.Append("<13>Jan  1 00:00:").Append((i % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture)).Append(" web-01 app: new year\n");
        }

        sb.Append("<13>Dec 31 23:59:59 web-01 app: late line from the old year\n");
        sb.Append("<13>Jan  1 00:01:00 web-01 app: and back\n");
        sb.Append("<13>Jun 15 00:00:00 web-01 app: same year, far ahead\n");
        sb.Append("<13>Jul 15 2030 00:00:00 web-01 app: a year on the line resets the tracking\n");
        sb.Append("<13>Jan  1 00:00:00 web-01 app: after the explicit year\n");

        var rows = Parse(Encoding.UTF8.GetBytes(sb.ToString()), maxChunk: 500, yearHint: 2025, timeZoneHint: TimeSpan.Zero); // many chunks: the state must carry over
        Assert.All(rows.Take(40), r => Assert.StartsWith("2025-12-31", r.Ts, StringComparison.Ordinal));
        Assert.All(rows.Skip(40).Take(40), r => Assert.StartsWith("2026-01-01", r.Ts, StringComparison.Ordinal));
        Assert.Equal("2025-12-31T23:59:59.000Z", rows[80].Ts);
        Assert.Equal("2026-01-01T00:01:00.000Z", rows[81].Ts);
        Assert.Equal("2026-06-15T00:00:00.000Z", rows[82].Ts);
        Assert.Equal("2030-07-15T00:00:00.000Z", rows[83].Ts);
        Assert.Equal("2031-01-01T00:00:00.000Z", rows[84].Ts);
    }

    [Fact]
    public void HostIsPerEventOrTheDefault()
    {
        var rows = ParseLines(
            "<13>Mar  1 00:00:00 db-01 app: with host\n" +
            "<13>Mar  1 00:00:01 app: no host, tag with colon\n" +
            "<13>Mar  1 00:00:02 app[7]: no host, tag with pid\n" +
            "<13>Mar  1 00:00:03 DB-02.Example.COM app: host is lowercased\n" +
            "<13>1 2026-03-01T00:00:04Z - app - - - nil host\n" +
            "<13>1 2026-03-01T00:00:05Z Mail-01 app - - - 5424 host\n");
        Assert.Equal(["db-01", "web-01", "web-01", "db-02.example.com", "web-01", "mail-01"], rows.Select(r => r.Host));
        Assert.All(rows, r => Assert.Equal("app", r.Program));
        Assert.Equal(7U, rows[2].ProcessPid);
    }

    [Fact]
    public void TagForms()
    {
        var rows = ParseLines(
            "<13>Mar  1 00:00:00 h sshd[1234]: pid and colon\n" +
            "<13>Mar  1 00:00:01 h sshd: colon only\n" +
            "<13>Mar  1 00:00:02 h sshd[1234] no colon after the pid\n" +
            "<13>Mar  1 00:00:03 h sshd[session]: pid is not a number\n" +
            "<13>Mar  1 00:00:04 h last message repeated 3 times\n" +
            "<13>Mar  1 00:00:05 h app:no space\n" +
            "<13>Mar  1 00:00:06 h app[1]:no space\n" +
            "<13>Mar  1 00:00:07 h sudo:   padded : message\n" +
            "<13>Mar  1 00:00:08 h kernel [0.000] bracket after a space is not a tag\n" +
            "<13>Mar  1 00:00:09 h broken[1 no closing bracket\n" +
            "<13>Mar  1 00:00:10 h :empty tag\n" +
            "<13>Mar  1 00:00:11 h\n" +
            "<13>Mar  1 00:00:12 h \n" +
            "<13>Mar  1 00:00:13\n");
        Assert.Equal(("sshd", "pid and colon", 1234U), (rows[0].Program, rows[0].Message, rows[0].ProcessPid));
        Assert.Equal(("sshd", "colon only"), (rows[1].Program, rows[1].Message));
        Assert.Equal(0U, rows[1].ProcessPid);
        Assert.Equal(("sshd", "no colon after the pid", 1234U), (rows[2].Program, rows[2].Message, rows[2].ProcessPid));
        Assert.Equal(("sshd", "pid is not a number", "session"), (rows[3].Program, rows[3].Message, rows[3].Extras["log.syslog.procid"]));
        Assert.Equal(0U, rows[3].ProcessPid);
        Assert.Equal(("", "last message repeated 3 times"), (rows[4].Program, rows[4].Message));
        Assert.Equal(("app", "no space"), (rows[5].Program, rows[5].Message));
        Assert.Equal(("app", "no space"), (rows[6].Program, rows[6].Message));
        Assert.Equal(("sudo", "  padded : message"), (rows[7].Program, rows[7].Message));
        Assert.Equal(("", "kernel [0.000] bracket after a space is not a tag"), (rows[8].Program, rows[8].Message));
        Assert.Equal(("", "broken[1 no closing bracket"), (rows[9].Program, rows[9].Message));
        Assert.Equal(("", "empty tag"), (rows[10].Program, rows[10].Message));
        Assert.Equal(("h", "", ""), (rows[11].Host, rows[11].Program, rows[11].Message));
        Assert.Equal(("h", "", ""), (rows[12].Host, rows[12].Program, rows[12].Message));
        Assert.Equal(("web-01", "", ""), (rows[13].Host, rows[13].Program, rows[13].Message));
        Assert.All(rows, r => Assert.False(r.IsReject, r.ToString()));
    }

    [Fact]
    public void PostfixHook()
    {
        var rows = ParseLines(
            "<22>Mar  1 00:00:00 mail-01 postfix/smtpd[2211]: connect from unknown[203.0.113.9]\n" +
            "<22>1 2026-03-01T00:00:01Z mail-01 postfix/qmgr 1000 - - 4F3C1A2B: removed\n" +
            "<22>Mar  1 00:00:02 mail-01 postfix/: empty daemon\n" +
            "<22>Mar  1 00:00:03 mail-01 postfix: no daemon\n" +
            "<22>Mar  1 00:00:04 mail-01 notpostfix/smtpd: other program\n");
        Assert.Equal(("postfix", "smtpd", 2211U), (rows[0].Program, rows[0].Extras["postfix.daemon"], rows[0].ProcessPid));
        Assert.Equal(("postfix", "qmgr", 1000U), (rows[1].Program, rows[1].Extras["postfix.daemon"], rows[1].ProcessPid));
        Assert.Equal("postfix/", rows[2].Program);
        Assert.False(rows[2].Extras.ContainsKey("postfix.daemon"));
        Assert.Equal("postfix", rows[3].Program);
        Assert.False(rows[3].Extras.ContainsKey("postfix.daemon"));
        Assert.Equal("notpostfix/smtpd", rows[4].Program);
        Assert.False(rows[4].Extras.ContainsKey("postfix.daemon"));
    }

    [Theory]
    [InlineData(0, "emerg", "kern")]
    [InlineData(7, "debug", "kern")]
    [InlineData(13, "notice", "user")]
    [InlineData(22, "info", "mail")]
    [InlineData(30, "info", "daemon")]
    [InlineData(34, "crit", "auth")]
    [InlineData(38, "info", "auth")]
    [InlineData(43, "err", "syslog")]
    [InlineData(52, "warning", "lpr")]
    [InlineData(85, "notice", "authpriv")]
    [InlineData(78, "info", "cron")]
    [InlineData(94, "info", "ftp")]
    [InlineData(102, "info", "ntp")]
    [InlineData(110, "info", "security")]
    [InlineData(118, "info", "console")]
    [InlineData(126, "info", "solaris-cron")]
    [InlineData(134, "info", "local0")]
    [InlineData(165, "notice", "local4")]
    [InlineData(191, "debug", "local7")]
    public void SeverityAndFacilityFromPri(int pri, string severity, string facility)
    {
        var row = Assert.Single(ParseLines($"<{pri}>Mar  1 00:00:00 h app: pri\n"));
        Assert.Equal(severity, row.Severity);
        Assert.Equal(facility, row.SyslogFacility);
    }

    [Fact]
    public void NoPriLeavesSeverityAndFacilityUnset()
    {
        var rows = ParseLines(
            "Mar  1 00:00:00 h app: no pri\n" +
            "1 2026-03-01T00:00:00Z h app - - - no pri 5424\n");
        Assert.All(rows, r =>
        {
            Assert.Equal("", r.Severity);
            Assert.Equal("", r.SyslogFacility);
            Assert.False(r.IsReject);
        });
    }

    [Theory]
    [InlineData("<192>Mar  1 00:00:00 h app: pri out of range\n")]
    [InlineData("<1000>Mar  1 00:00:00 h app: four digits\n")]
    [InlineData("<>Mar  1 00:00:00 h app: empty pri\n")]
    [InlineData("<13Mar  1 00:00:00 h app: unterminated pri\n")]
    [InlineData("<-1>Mar  1 00:00:00 h app: negative\n")]
    public void MalformedPriIsNotATimestamp(string line)
    {
        var row = Assert.Single(ParseLines(line));
        Assert.Equal("bad timestamp", row.Extras["reject_reason"]);
    }

    [Fact]
    public void Rfc5424Fields()
    {
        var rows = ParseLines(
            "<165>1 2026-03-01T00:00:00.000Z mymachine.example.com evntslog 1234 ID47 [exampleSDID@32473 iut=\"3\" eventSource=\"Application\" eventID=\"1011\"] ﻿An application event log entry...\n" +
            "<165>1 2026-03-01T00:00:01Z h app worker-3 MSG-7 - without structured data\n" +
            "<165>1 2026-03-01T00:00:02Z h app - - -\n" +
            "<165>1 2026-03-01T00:00:03Z h app - - [one][two a=\"1\"]\n" +
            "<165>1 - h app - - - nil timestamp\n" +
            "<165>1 2026-03-01T00:00:05Z h app - - - \n");
        Assert.All(rows, r => Assert.False(r.IsReject, r.ToString()));

        Assert.Equal("mymachine.example.com", rows[0].Host);
        Assert.Equal("evntslog", rows[0].Program);
        Assert.Equal("notice", rows[0].Severity);
        Assert.Equal(1234U, rows[0].ProcessPid);
        Assert.Equal("ID47", rows[0].Extras["log.syslog.msgid"]);
        Assert.Equal("3", rows[0].Extras["log.syslog.structured_data.exampleSDID@32473.iut"]);
        Assert.Equal("Application", rows[0].Extras["log.syslog.structured_data.exampleSDID@32473.eventSource"]);
        Assert.Equal("1011", rows[0].Extras["log.syslog.structured_data.exampleSDID@32473.eventID"]);
        Assert.Equal("An application event log entry...", rows[0].Message); // the BOM is not part of the message
        Assert.StartsWith("<165>1 2026-03-01T00:00:00.000Z mymachine.example.com evntslog 1234 ID47 [exampleSDID@32473", rows[0].Raw, StringComparison.Ordinal);
        Assert.Contains("﻿", rows[0].Raw, StringComparison.Ordinal);

        Assert.Equal("worker-3", rows[1].Extras["log.syslog.procid"]);
        Assert.Equal("MSG-7", rows[1].Extras["log.syslog.msgid"]);
        Assert.Equal("without structured data", rows[1].Message);

        Assert.Equal("", rows[2].Message);
        Assert.Empty(rows[2].Extras); // nothing but the facility, which is a column now
        Assert.NotEqual("", rows[2].SyslogFacility);

        Assert.Equal("", rows[3].Message);
        Assert.Equal("", rows[3].Extras["log.syslog.structured_data.one"]);
        Assert.Equal("1", rows[3].Extras["log.syslog.structured_data.two.a"]);

        Assert.Equal(ParsedRow.Epoch, rows[4].Ts);
        Assert.Equal("nil timestamp", rows[4].Message);

        Assert.Equal("", rows[5].Message); // a trailing space after the SD: an empty message
    }

    [Fact]
    public void StructuredDataValuesAreStoredAsLogged()
    {
        var row = Assert.Single(ParseLines("<14>1 2026-03-01T00:00:00Z h app - - [x a=\"q\\\"q\" b=\"b\\]b\" c=\"c\\\\c\" d=\"\" e=\"sp ace\"] m\n"));
        Assert.Equal("q\\\"q", row.Extras["log.syslog.structured_data.x.a"]);
        Assert.Equal("b\\]b", row.Extras["log.syslog.structured_data.x.b"]);
        Assert.Equal("c\\\\c", row.Extras["log.syslog.structured_data.x.c"]);
        Assert.Equal("", row.Extras["log.syslog.structured_data.x.d"]);
        Assert.Equal("sp ace", row.Extras["log.syslog.structured_data.x.e"]);
        Assert.Equal("m", row.Message);
    }

    [Fact]
    public void StructuredDataKeysThatDoNotFitFallBackToTheElement()
    {
        var longId = new string('k', 300);
        var row = Assert.Single(ParseLines($"<14>1 2026-03-01T00:00:00Z h app - - [{longId} v=\"1\"][ok a=\"2\"] m\n"));
        Assert.Equal($"[{longId} v=\"1\"]", row.Extras["log.syslog.structured_data"]);
        Assert.Equal("2", row.Extras["log.syslog.structured_data.ok.a"]);
    }

    [Theory]
    [InlineData("\n", "empty line")]
    [InlineData("\r\n", "empty line")]
    [InlineData("<38>Foo  1 00:01:24 h sshd: bad month\n", "bad timestamp")]
    [InlineData("<38>Mar 32 00:01:24 h sshd: bad day\n", "bad timestamp")]
    [InlineData("<38>Mar  1 24:00:00 h sshd: bad hour\n", "bad timestamp")]
    [InlineData("<38>Mar  1 00:60:00 h sshd: bad minute\n", "bad timestamp")]
    [InlineData("<38>Mar  1 00:00:61 h sshd: bad second\n", "bad timestamp")]
    [InlineData("<38>Mar  1 00:00:00x h sshd: junk after the time\n", "bad timestamp")]
    [InlineData("<38>2026-03-01 00:00:00 h sshd: iso without an offset is not a syslog timestamp\n", "bad timestamp")]
    [InlineData("<38>2026-03-01T00:00:00 h sshd: no offset\n", "bad timestamp")]
    [InlineData("this line has no timestamp at all\n", "bad timestamp")]
    [InlineData("203.0.113.7 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 612 \"-\" \"-\"\n", "bad timestamp")]
    [InlineData("{\"time\":\"2026-03-01T00:00:00Z\",\"msg\":\"json\"}\n", "bad timestamp")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h\n", "truncated header")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 -\n", "truncated header")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - \n", "truncated header")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h  app 1 - - double space\n", "truncated header")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - x message\n", "bad structured data")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - [unterminated a=\"x\" msg\n", "bad structured data")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - [] empty id\n", "bad structured data")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - [id a=x] unquoted\n", "bad structured data")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - [id a] no value\n", "bad structured data")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - [id a=\"x\"]junk\n", "bad structured data")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - [id a=\"\\\n", "bad structured data")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - [id\n", "bad structured data")]
    [InlineData("<38>1 2026-03-01T00:00:00Z h app 1 - [id a=\"1\"\n", "bad structured data")]
    [InlineData("<38>1 2026-13-01T00:00:00Z h app 1 - - bad month\n", "bad timestamp")]
    public void MalformedLinesAreRejectedWithAReason(string line, string reason)
    {
        var row = Assert.Single(ParseLines(line));
        Assert.Equal(reason, row.Extras["reject_reason"]);
        Assert.Equal(row.Raw, row.Message);
        Assert.Equal("", row.Program);
        Assert.Equal("", row.Severity);
        Assert.Equal("web-01", row.Host);
    }

    [Fact]
    public void CiscoStylePrefixAndTimestamp()
    {
        var rows = ParseLines(
            "<189>42: Mar  1 00:00:00 fw-01 %SYS-5-CONFIG_I: Configured from console\n" +
            "<189>43: *Mar  1 00:00:01.456: %LINK-3-UPDOWN: Interface down\n" +
            "<189>44: .Mar  1 00:00:02: %LINK-3-UPDOWN: unsynchronised clock\n" +
            "<189>Mar  1 00:00:03 fw-01 %SYS-5-CONFIG_I: no sequence\n" +
            "<189>45:Mar  1 00:00:04 fw-01 %SYS-5-CONFIG_I: no space after the sequence\n", yearHint: 2026);
        Assert.Equal(("42", "fw-01", "%sys-5-config_i", "Configured from console", "2026-03-01T00:00:00.000Z"), (rows[0].Extras["event.sequence"], rows[0].Host, rows[0].Program, rows[0].Message, rows[0].Ts));
        Assert.Equal(("43", "web-01", "%link-3-updown", "Interface down", "2026-03-01T00:00:01.456Z"), (rows[1].Extras["event.sequence"], rows[1].Host, rows[1].Program, rows[1].Message, rows[1].Ts));
        Assert.Equal(("44", "web-01", "%link-3-updown", "unsynchronised clock", "2026-03-01T00:00:02.000Z"), (rows[2].Extras["event.sequence"], rows[2].Host, rows[2].Program, rows[2].Message, rows[2].Ts));
        Assert.False(rows[3].Extras.ContainsKey("event.sequence"));
        Assert.True(rows[4].IsReject);
        Assert.Equal("bad timestamp", rows[4].Extras["reject_reason"]);
    }

    [Fact]
    public void BsdTimestampVariants()
    {
        var rows = ParseLines(
            "<13>Mar  1 00:00:00 h app: padded day\n" +
            "<13>Mar 1 00:00:00 h app: single space\n" +
            "<13>Mar 01 00:00:00 h app: zero padded\n" +
            "<13>Mar 15 00:00:00 h app: two digits\n" +
            "<13>Mar 15 00:00:00.123456 h app: fraction\n" +
            "<13>Mar 15 2032 00:00:00 h app: year on the line\n" +
            "<13>Feb 29 00:00:00 h app: leap day in a leap year\n" +
            "<13>Jun 30 23:59:60 h app: second 60\n", yearHint: 2028);
        Assert.All(rows, r => Assert.False(r.IsReject, r.ToString()));
        Assert.Equal("2028-03-01T00:00:00.000Z", rows[0].Ts);
        Assert.Equal("2028-03-01T00:00:00.000Z", rows[1].Ts);
        Assert.Equal("2028-03-01T00:00:00.000Z", rows[2].Ts);
        Assert.Equal("2028-03-15T00:00:00.000Z", rows[3].Ts);
        Assert.Equal("2028-03-15T00:00:00.123Z", rows[4].Ts);
        Assert.Equal("2032-03-15T00:00:00.000Z", rows[5].Ts);
        Assert.Equal("2032-02-29T00:00:00.000Z", rows[6].Ts); // the explicit year carries forward
        Assert.Equal("2032-07-01T00:00:00.000Z", rows[7].Ts);
    }

    [Fact]
    public void MessageBytesPassThroughUnchanged()
    {
        var text = "<13>Mar  1 00:00:00 h app: Ünïcödé Юрий 山田太郎 🔐 \u0001\u007f tab\tend\n";
        var row = Assert.Single(ParseLines(text));
        Assert.Equal("Ünïcödé Юрий 山田太郎 🔐 \u0001\u007f tab\tend", row.Message);
        Assert.Equal(text.TrimEnd('\n'), row.Raw);
    }

    /// <summary>Parses text as one chunk through the real batch writer with the syslog defaults (host web-01, UTC, year 1970).</summary>
    internal static List<ParsedRow> ParseLines(string text, int yearHint = 1970, TimeSpan timeZoneHint = default) =>
        Parse(Encoding.UTF8.GetBytes(text), int.MaxValue, yearHint, timeZoneHint);

    internal static List<ParsedRow> Parse(byte[] data, int maxChunk, int yearHint = SampleYear, TimeSpan? timeZoneHint = null) =>
        Fixtures.ParseBytes(new SyslogSource(), data, maxChunk, timeZoneHint: timeZoneHint ?? SampleOffset, yearHint: yearHint);
}
