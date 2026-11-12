using System.Text;
using Urlog.Core.Sources;
using Urlog.Sources.Nginx;
using Urlog.Sources.Syslog;

namespace Urlog.Sources.Tests.Detection;

/// <summary>
/// Sniff confidence of <see cref="SyslogSource"/> on its own sample and on foreign samples (rule: at least 0.9 on
/// its own, below 0.5 on every other source's), and the two sources against each other's samples.
/// </summary>
public sealed class SyslogSniffTests
{
    private const string JsonLinesSample =
        "{\"time\":\"2026-03-01T00:00:00Z\",\"level\":\"info\",\"msg\":\"started\"}\n" +
        "{\"time\":\"2026-03-01T00:00:01Z\",\"level\":\"warn\",\"msg\":\"slow query\",\"ms\":912}\n" +
        "{\"time\":\"2026-03-01T00:00:02Z\",\"level\":\"error\",\"msg\":\"GET / HTTP/1.1 500\"}\n";

    private const string NginxErrorSample =
        "2026/03/01 00:00:00 [error] 1234#1234: *5 open() \"/var/www/x\" failed (2: No such file or directory), client: 203.0.113.7, server: _, request: \"GET /x HTTP/1.1\"\n" +
        "2026/03/01 00:00:01 [warn] 1234#1234: *6 upstream server temporarily disabled while connecting to upstream\n" +
        "2026/03/01 00:00:02 [notice] 1234#1234: signal process started\n";

    private const string IisSample =
        "#Software: Microsoft Internet Information Services 10.0\n" +
        "#Fields: date time s-ip cs-method cs-uri-stem sc-status\n" +
        "2026-03-01 00:00:00 10.0.0.5 GET /default.htm 200\n" +
        "2026-03-01 00:00:01 10.0.0.5 GET /index.htm 404\n";

    private const string AppLogSample =
        "2026-03-01 00:00:00,123 INFO  [main] com.example.App - started\n" +
        "2026-03-01 00:00:01,456 WARN  [worker-1] com.example.Db - slow query\n" +
        "2026-03-01T00:00:02.000Z INFO started without a host or tag\n";

    private static readonly SyslogSource Source = new();

    [Fact]
    public void OwnSampleScoresAtLeastPoint9()
    {
        var confidence = Sniff(Fixtures.Bytes("syslog", "sample.log"), "sample.log");
        Assert.InRange(confidence, 0.9f, 1.0f);
    }

    [Fact]
    public void OwnSampleIsNotAPerfectMatch()
    {
        // The sample has five rejects (four of them non-blank lines): an honest score is below 1.
        var confidence = Sniff(Fixtures.Bytes("syslog", "sample.log"), "sample.log");
        Assert.InRange(confidence, 0.9f, 0.95f);
    }

    [Fact]
    public void NginxSampleScoresBelowPoint5()
    {
        Assert.Equal(0f, Sniff(Fixtures.Bytes("nginx", "sample.log"), "sample.log"));
    }

    [Fact]
    public void NginxSampleScoresBelowPoint5OnSyslogAndSyslogSampleBelowPoint5OnNginx()
    {
        var nginx = new NginxSource();
        var syslogSample = Fixtures.Bytes("syslog", "sample.log");
        var nginxSample = Fixtures.Bytes("nginx", "sample.log");
        Assert.InRange(Sniff(nginx, syslogSample, "sample.log"), 0f, 0.5f);
        Assert.InRange(Sniff(Source, nginxSample, "sample.log"), 0f, 0.5f);
    }

    [Fact]
    public void CoreFixtureIsSyslogShaped()
    {
        // fixtures/core/sample.log is the pipeline's own sample, written in the rsyslog ISO shape on purpose; it is not a source sample.
        Assert.Equal(1f, Sniff(Fixtures.Bytes("core", "sample.log"), "sample.log"));
    }

    [Theory]
    [InlineData(JsonLinesSample, "jsonl")]
    [InlineData(NginxErrorSample, "nginx-error")]
    [InlineData(IisSample, "iis")]
    [InlineData(AppLogSample, "app")]
    [InlineData("", "empty")]
    [InlineData("\n\n\n", "blank")]
    public void ForeignSamplesScoreBelowPoint5(string text, string name)
    {
        Assert.InRange(Sniff(Encoding.UTF8.GetBytes(text), name + ".log"), 0f, 0.5f);
    }

    [Fact]
    public void EveryOtherSourceSampleScoresBelowPoint5()
    {
        // Every committed fixtures/<source>/sample.log but our own and the core pipeline's (which is syslog-shaped).
        var fixtures = Path.Combine(Fixtures.Root, "fixtures");
        var checkedSamples = 0;
        foreach (var dir in Directory.GetDirectories(fixtures))
        {
            var sample = Path.Combine(dir, "sample.log");
            var name = Path.GetFileName(dir);
            if (name is "syslog" or "core" || !File.Exists(sample))
            {
                continue;
            }

            var confidence = Sniff(File.ReadAllBytes(sample), "sample.log");
            Assert.True(confidence < 0.5f, $"{sample}: {confidence}");
            checkedSamples++;
        }

        Assert.True(checkedSamples >= 1, "the nginx sample at least");
    }

    [Fact]
    public void GarbageScoresZero()
    {
        Assert.Equal(0f, Sniff(Fixtures.Bytes("syslog", "adversarial/garbage.bin"), "garbage.bin"));
    }

    [Fact]
    public void BomDoesNotLowerTheScore()
    {
        var sample = Fixtures.Bytes("syslog", "sample.log");
        var withBom = Sniff(sample, "sample.log");
        var without = Sniff(sample.AsSpan(3).ToArray(), "sample.log");
        Assert.Equal(without, withBom);
    }

    [Fact]
    public void TailOfALargeFileCountsWhenItIsNotTheFormat()
    {
        // 100 KB of syslog then 4 KB of nginx: the head alone says 1.0; the tail pulls it down but stays above 0.8
        // because the tail is sampled at a quarter of the head's weight (50 lines vs 200).
        var line = "<38>Mar  1 00:00:00 web-01 sshd[1234]: Accepted publickey for root from 10.0.0.1 port 22 ssh2\n";
        var sb = new StringBuilder();
        while (sb.Length < 100_000)
        {
            sb.Append(line);
        }

        var syslogOnly = Encoding.UTF8.GetBytes(sb.ToString());
        for (var i = 0; i < 60; i++)
        {
            sb.Append("203.0.113.7 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 612 \"-\" \"curl/8.11.0\"\n");
        }

        var mixed = Encoding.UTF8.GetBytes(sb.ToString());
        Assert.Equal(1.0f, Sniff(syslogOnly, "syslog"));
        Assert.InRange(Sniff(mixed, "syslog"), 0.75f, 0.85f);
    }

    [Fact]
    public void EveryShapeCountsAsAMatch()
    {
        var text =
            "<34>Mar  1 00:00:00 web-01 sshd[1234]: bsd with pri\n" +
            "Mar  1 00:00:01 web-01 sshd: bsd without pri\n" +
            "<30>2026-03-01T00:00:02.123+02:00 web-01 app[1]: iso with pri\n" +
            "2026-03-01T00:00:03Z web-01 app: iso without pri\n" +
            "<165>1 2026-03-01T00:00:04Z web-01 app 1 - [sd a=\"1\"] 5424 with pri\n" +
            "1 2026-03-01T00:00:05Z - - - - - 5424 nil\n" +
            "<189>42: *Mar  1 00:00:06.000: %SYS-5-CONFIG_I: cisco\n";
        Assert.Equal(1.0f, Sniff(Encoding.UTF8.GetBytes(text), "shapes.log"));
    }

    private static float Sniff(byte[] file, string name) => Sniff(Source, file, name);

    private static float Sniff(ILogSource source, byte[] file, string name)
    {
        var head = file.AsSpan(0, Math.Min(file.Length, 64 << 10));
        var tail = file.AsSpan(Math.Max(0, file.Length - (4 << 10)));
        return source.Sniff(head, tail, new SniffContext(name, file.Length));
    }
}
