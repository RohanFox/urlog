using System.Text;
using Urlog.Core.Sources;
using Urlog.Sources.Nginx;

namespace Urlog.Sources.Tests.Detection;

/// <summary>
/// Sniff confidence of <see cref="NginxSource"/> on its own sample and on foreign samples (rule: at least 0.9 on
/// its own, below 0.5 on every other source's). The inline syslog sample predates <c>fixtures/syslog/sample.log</c>
///, which <see cref="EveryOtherSourceSampleScoresBelowPoint5"/> covers; <c>tests/Bench.Tests</c> checks the
/// generated syslog fixture as well.
/// </summary>
public sealed class NginxSniffTests
{
    private const string SyslogSample =
        "Mar  1 00:00:00 web-01 sshd[1234]: Accepted publickey for root from 10.0.0.1 port 22 ssh2\n" +
        "<34>Mar  1 00:00:01 web-01 sudo: root : TTY=pts/0 ; PWD=/root ; COMMAND=/bin/ls\n" +
        "<165>1 2026-03-01T00:00:02.000Z web-01 app 1234 ID47 [exampleSDID@32473 iut=\"3\"] message\n" +
        "<13>Mar  1 00:00:03 web-01 kernel: [12345.678] usb 1-1: new high-speed USB device\n" +
        "Mar  1 00:00:04 mail-01 postfix/smtpd[2211]: connect from unknown[203.0.113.5]\n" +
        "<38>1 2026-03-01T00:00:05+00:00 db-01 sshd 999 - - Failed password for invalid user admin from 198.51.100.9 port 4444 ssh2\n" +
        "Mar  1 00:00:06 web-01 CRON[777]: (root) CMD (run-parts /etc/cron.hourly)\n" +
        "<86>Mar  1 00:00:07 web-01 systemd[1]: Started Session 12 of user deploy.\n" +
        "Mar  1 00:00:08 web-01 nginx: 203.0.113.7 - - [01/Mar/2026:00:00:08 +0000] \"GET / HTTP/1.1\" 200 612 \"-\" \"curl\"\n" +
        "<30>Mar  1 00:00:09 web-01 dhclient[500]: DHCPACK of 10.0.0.5 from 10.0.0.1\n";

    private const string JsonLinesSample =
        "{\"time\":\"2026-03-01T00:00:00Z\",\"level\":\"info\",\"msg\":\"started\"}\n" +
        "{\"time\":\"2026-03-01T00:00:01Z\",\"level\":\"warn\",\"msg\":\"slow query\",\"ms\":912}\n" +
        "{\"time\":\"2026-03-01T00:00:02Z\",\"level\":\"error\",\"msg\":\"GET / HTTP/1.1 500\"}\n";

    private const string NginxErrorSample =
        "2026/03/01 00:00:00 [error] 1234#1234: *5 open() \"/var/www/x\" failed (2: No such file or directory), client: 203.0.113.7, server: _, request: \"GET /x HTTP/1.1\"\n" +
        "2026/03/01 00:00:01 [warn] 1234#1234: *6 upstream server temporarily disabled while connecting to upstream\n" +
        "2026/03/01 00:00:02 [notice] 1234#1234: signal process started\n";

    private static readonly NginxSource Source = new();

    [Fact]
    public void OwnSampleScoresAtLeastPoint9()
    {
        var confidence = Sniff(Fixtures.Bytes("nginx", "sample.log"), "sample.log");
        Assert.InRange(confidence, 0.9f, 1.0f);
    }

    [Fact]
    public void OwnSampleIsDetectedAsCombined()
    {
        var file = Fixtures.Bytes("nginx", "sample.log");
        var detection = NginxSource.Detect(file, file.AsSpan(Math.Max(0, file.Length - 4096)), new SniffContext("sample.log", file.Length), null);
        Assert.Equal("combined", detection.Layout);
        Assert.True(detection.LayoutKnown);
        Assert.Equal(detection.Confidence, Sniff(file, "sample.log"));
    }

    [Fact]
    public void OwnSampleIsNotAPerfectMatch()
    {
        // The sample has three rejects and two common-log lines: an honest score is below 1.
        var confidence = Sniff(Fixtures.Bytes("nginx", "sample.log"), "sample.log");
        Assert.True(confidence < 1.0f, confidence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CoreFixtureScoresBelowPoint5()
    {
        Assert.InRange(Sniff(Fixtures.Bytes("core", "sample.log"), "sample.log"), 0f, 0.5f);
    }

    [Theory]
    [InlineData(SyslogSample, "syslog")]
    [InlineData(JsonLinesSample, "jsonl")]
    [InlineData(NginxErrorSample, "nginx-error")]
    [InlineData("", "empty")]
    [InlineData("\n\n\n", "blank")]
    public void ForeignSamplesScoreBelowPoint5(string text, string name)
    {
        Assert.InRange(Sniff(Encoding.UTF8.GetBytes(text), name + ".log"), 0f, 0.5f);
    }

    [Fact]
    public void EveryOtherSourceSampleScoresBelowPoint5()
    {
        // Every committed fixtures/<source>/sample.log but our own (syslog, and the core pipeline's sample).
        var fixtures = Path.Combine(Fixtures.Root, "fixtures");
        foreach (var dir in Directory.GetDirectories(fixtures))
        {
            var sample = Path.Combine(dir, "sample.log");
            if (Path.GetFileName(dir) == "nginx" || !File.Exists(sample))
            {
                continue;
            }

            var confidence = Sniff(File.ReadAllBytes(sample), "sample.log");
            Assert.True(confidence < 0.5f, $"{sample}: {confidence}");
        }
    }

    [Fact]
    public void GarbageScoresZero()
    {
        Assert.Equal(0f, Sniff(Fixtures.Bytes("nginx", "adversarial/garbage.bin"), "garbage.bin"));
    }

    [Fact]
    public void BomDoesNotLowerTheScore()
    {
        var sample = Fixtures.Bytes("nginx", "sample.log");
        var withBom = Sniff(sample, "sample.log");
        var without = Sniff(sample.AsSpan(3).ToArray(), "sample.log");
        Assert.Equal(without, withBom);
    }

    [Fact]
    public void TailOfALargeFileCountsWhenItIsNotTheFormat()
    {
        // 100 KB of nginx then 4 KB of syslog: the head alone says 1.0; the tail pulls it down but stays above 0.8
        // because the tail is sampled at a quarter of the head's weight (50 lines vs 200).
        var line = "203.0.113.7 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 612 \"-\" \"curl/8.11.0\"\n";
        var sb = new StringBuilder();
        while (sb.Length < 100_000)
        {
            sb.Append(line);
        }

        var nginxOnly = Encoding.UTF8.GetBytes(sb.ToString());
        for (var i = 0; i < 60; i++)
        {
            sb.Append("Mar  1 00:00:00 web-01 sshd[1234]: Accepted publickey for root from 10.0.0.1 port 22 ssh2\n");
        }

        var mixed = Encoding.UTF8.GetBytes(sb.ToString());
        Assert.Equal(1.0f, Sniff(nginxOnly, "access.log"));
        var mixedScore = Sniff(mixed, "access.log");
        Assert.InRange(mixedScore, 0.75f, 0.85f);
    }

    private static float Sniff(byte[] file, string name)
    {
        var head = file.AsSpan(0, Math.Min(file.Length, 64 << 10));
        var tail = file.AsSpan(Math.Max(0, file.Length - (4 << 10)));
        return Source.Sniff(head, tail, new SniffContext(name, file.Length));
    }
}
