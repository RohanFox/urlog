using System.Text;
using Urlog.Core.Sources;
using Urlog.Sources.Nginx;

namespace Urlog.Sources.Tests.Nginx;

/// <summary>
/// The synthetic layout fixtures under <c>fixtures/nginx/custom/</c>: each <c>&lt;name&gt;.log</c> parsed with its
/// <c>&lt;name&gt;.format</c> as the <c>nginx.log_format</c> option must give <c>&lt;name&gt;.expected.jsonl</c>; the
/// built-ins must also be found without the option, and the corpus layout must be reported as "layout unknown".
/// </summary>
public sealed class NginxLayoutFixtureTests
{
    private static string Custom(string file) => Path.Combine(Fixtures.Root, "fixtures", "nginx", "custom", file);

    private static string Format(string name) => File.ReadAllText(Custom(name + ".format")).Trim();

    public static TheoryData<string> Layouts() => ["combined", "common", "ingress-nginx", "geoip-upstream"];

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task FixtureRoundTripsWithItsFormatAsTheOption(string name)
    {
        var options = new Dictionary<string, string> { [NginxSource.LogFormatOption] = Format(name) };
        var expected = Fixtures.ReadExpectedFile(Custom(name + ".expected.jsonl"));
        var actual = await Fixtures.ParseFileAsync(new NginxSource(), Custom(name + ".log"), options: options);

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i] == actual[i], $"{name} row {i} differs.\nexpected: {expected[i]}\nactual:   {actual[i]}");
        }

        var file = await File.ReadAllBytesAsync(Custom(name + ".log"), TestContext.Current.CancellationToken);
        foreach (var row in actual)
        {
            Assert.Equal(file.AsSpan((int)row.FileOffset, row.RawBytes.Length).ToArray(), row.RawBytes.ToArray());
        }
    }

    [Theory]
    [InlineData("combined")]
    [InlineData("common")]
    [InlineData("ingress-nginx")]
    public async Task BuiltInsAreFoundWithoutTheOptionAndByName(string name)
    {
        var expected = Fixtures.ReadExpectedFile(Custom(name + ".expected.jsonl"));
        var auto = await Fixtures.ParseFileAsync(new NginxSource(), Custom(name + ".log"));
        var byName = await Fixtures.ParseFileAsync(new NginxSource(), Custom(name + ".log"), options: new Dictionary<string, string> { [NginxSource.LogFormatOption] = name.ToUpperInvariant() });
        Assert.Equal(expected, auto);
        Assert.Equal(expected, byName);
    }

    [Fact]
    public async Task CorpusLayoutIsNotParsedWithoutItsFormat()
    {
        var rows = await Fixtures.ParseFileAsync(new NginxSource(), Custom("geoip-upstream.log"));
        Assert.Equal(6, rows.Count);
        // No built-in fits the 4 geoip lines; the last line is plain combined and is picked up by the per-line fallback.
        Assert.Equal(5, rows.Count(r => r.IsReject));
        Assert.False(rows[^1].IsReject);
        Assert.Equal("::ffff:203.0.113.7", rows[^1].SourceIp);
    }

    [Fact]
    public async Task FixedLayoutIsStrict()
    {
        // With the option set the other built-ins are not tried: the combined line at the end of the geoip fixture is a reject,
        // and the ingress fixture parsed as "combined" keeps its extra fields as trailing.
        var geoip = await Fixtures.ParseFileAsync(new NginxSource(), Custom("geoip-upstream.log"), options: new Dictionary<string, string> { [NginxSource.LogFormatOption] = Format("geoip-upstream") });
        Assert.True(geoip[^1].IsReject);

        var ingressAsCombined = await Fixtures.ParseFileAsync(new NginxSource(), Custom("ingress-nginx.log"), options: new Dictionary<string, string> { [NginxSource.LogFormatOption] = "combined" });
        Assert.All(ingressAsCombined, r => Assert.False(r.IsReject));
        Assert.All(ingressAsCombined, r => Assert.True(r.Extras.ContainsKey("trailing")));
        Assert.StartsWith("123 0.002 [default-web-80]", ingressAsCombined[0].Extras["trailing"], StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidOptionIsAConfigurationError()
    {
        var options = new Dictionary<string, string> { [NginxSource.LogFormatOption] = "$remote_addr$status" };
        Assert.Throws<FormatException>(() => NginxSourceTests.ParseLines("x\n", options));
    }

    [Theory]
    [InlineData("combined", "combined", 0.8f, true)]
    [InlineData("common", "common", 0.55f, true)]
    [InlineData("ingress-nginx", "ingress-nginx", 0.99f, true)]
    [InlineData("geoip-upstream", null, 0.5f, false)]
    public void DetectReportsTheLayout(string name, string? layout, float atLeast, bool known)
    {
        var file = File.ReadAllBytes(Custom(name + ".log"));
        var detection = NginxSource.Detect(file, file.AsSpan(Math.Max(0, file.Length - 4096)), new SniffContext(name + ".log", file.Length), null);
        Assert.Equal(layout, detection.Layout);
        Assert.Equal(known, detection.LayoutKnown);
        Assert.InRange(detection.Confidence, atLeast, 1.0f);
    }

    [Fact]
    public void DetectWithTheCorpusFormatScoresItsFixtureHigh()
    {
        var file = File.ReadAllBytes(Custom("geoip-upstream.log"));
        var detection = NginxSource.Detect(file, file.AsSpan(Math.Max(0, file.Length - 4096)), new SniffContext("x.log", file.Length), Format("geoip-upstream"));
        Assert.Equal("custom", detection.Layout);
        Assert.True(detection.LayoutKnown);
        Assert.InRange(detection.Confidence, 0.6f, 0.7f); // 4 of 6 lines fit (one has trailing bytes), 2 are deliberate misfits
    }

    [Fact]
    public void UnknownLayoutScoresPointSix()
    {
        // The corpus layout, all lines well formed: nginx-like shape, no built-in fits.
        var lines = File.ReadLines(Custom("geoip-upstream.log")).Take(4).ToArray();
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        var detection = NginxSource.Detect(bytes, bytes, new SniffContext("x.log", bytes.Length), null);
        Assert.Null(detection.Layout);
        Assert.False(detection.LayoutKnown);
        Assert.Equal(0.6f, detection.Confidence, 0.001f);
    }

    [Fact]
    public void TimeIso8601AndMsecLayoutsParse()
    {
        var iso = NginxSourceTests.ParseLines(
            "ts=2026-03-01T00:00:05+01:00 ip=203.0.113.7 \"GET /iso HTTP/1.1\" 201\n",
            new Dictionary<string, string> { [NginxSource.LogFormatOption] = "ts=$time_iso8601 ip=$remote_addr \"$request\" $status" });
        var row = Assert.Single(iso);
        Assert.Equal("2026-02-28T23:00:05.000Z", row.Ts);
        Assert.Equal("/iso", row.UrlPath);
        Assert.Equal((ushort)201, row.HttpStatus);
        Assert.Equal("\"GET /iso HTTP/1.1\" 201", row.Message);

        var msec = NginxSourceTests.ParseLines(
            "1772323200.123 203.0.113.7 \"GET /m HTTP/1.1\" 200 0.5\n1772323200 203.0.113.7 \"GET /m HTTP/1.1\" 200 -\nx 203.0.113.7 \"GET /m HTTP/1.1\" 200 1\n",
            new Dictionary<string, string> { [NginxSource.LogFormatOption] = "$msec $remote_addr \"$request\" $status $request_time" });
        Assert.Equal(3, msec.Count);
        Assert.Equal("2026-03-01T00:00:00.123Z", msec[0].Ts);
        Assert.Equal("500000000", msec[0].Extras["event.duration"]);
        Assert.Equal("2026-03-01T00:00:00.000Z", msec[1].Ts);
        Assert.False(msec[1].Extras.ContainsKey("event.duration"));
        Assert.Equal("bad timestamp", msec[2].Extras["reject_reason"]);
    }
}
