using System.Text;
using Urlog.Sources.Nginx;

namespace Urlog.Sources.Tests.Nginx;

/// <summary>
/// <see cref="NginxSource.TryScanCombined"/> is the combined layout unrolled for speed; it must agree with the
/// table-driven walk on every line it accepts, and accept exactly the lines the walk accepts for that layout.
/// </summary>
public sealed class NginxFastScanTests
{
    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string> { Path.Combine("fixtures", "nginx", "sample.log") };
        foreach (var dir in new[] { "custom", "adversarial" })
        {
            foreach (var file in Directory.GetFiles(Path.Combine(Fixtures.Root, "fixtures", "nginx", dir)).Order(StringComparer.Ordinal))
            {
                if (file.EndsWith(".log", StringComparison.Ordinal) || file.EndsWith(".bin", StringComparison.Ordinal))
                {
                    data.Add(Path.GetRelativePath(Fixtures.Root, file));
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FastScanAgreesWithTheGenericWalkOnEveryLine(string relativePath)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Fixtures.Root, relativePath));
        var (lines, accepted) = AssertAgreement(bytes);
        Assert.True(lines > 0, relativePath);
        if (relativePath.EndsWith("sample.log", StringComparison.Ordinal) || relativePath.Contains("combined", StringComparison.Ordinal))
        {
            Assert.True(accepted > 0, relativePath + ": the fast scan accepted nothing");
        }
    }

    [Fact]
    public void FastScanAgreesOnSyntheticEdgeCases()
    {
        var text =
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"-\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"\" 200 0 \"\" \"\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"a\\\"b\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"a\\\\\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"x\" trailing\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"x\"  \n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"x\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 2000000 0 \"-\" \"-\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 20x 0 \"-\" \"-\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\"  200 0 \"-\" \"-\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000]  \"GET / HTTP/1.1\" 200 0 \"-\" \"-\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000 ] \"GET / HTTP/1.1\" 200 0 \"-\" \"-\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00+0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"-\"\n" +
            "1.2.3.4 - - [32/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"-\"\n" +
            "1.2.3.4 -  - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"-\"\n" +
            "1.2.3.4  - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"-\"\n" +
            " 1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"-\"\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"-\" \"third\"\n" +
            "1.2.3.4 - -\n" +
            "1.2.3.4\n" +
            "\n" +
            "\"\n" +
            "a b c [01/Mar/2026:00:00:00 +0000] \"\"\n";
        var (lines, accepted) = AssertAgreement(Encoding.UTF8.GetBytes(text));
        Assert.Equal(28, lines);
        Assert.InRange(accepted, 7, 10);
    }

    [Fact]
    public void FastScanAgreesOnTheGeneratedShapes()
    {
        // The generator's four oddities: IPv6 client, long user agent, quotes in the request, missing fields.
        var text =
            "2001:db8:1:2::3 - - [01/Mar/2026:00:00:00 +0000] \"GET /a HTTP/1.1\" 200 1 \"-\" \"curl\"\n" +
            "10.0.0.1 - - [01/Mar/2026:00:00:00 +0000] \"GET /a HTTP/1.1\" 200 1 \"-\" \"" + new string('x', 4096) + "\"\n" +
            "10.0.0.1 - - [01/Mar/2026:00:00:00 +0000] \"GET /index.php?q=\\x22\\x3cscript\\x3e\\x22&name=\\\"admin\\\"' OR 1=1-- HTTP/1.1\" 200 1 \"-\" \"curl\"\n" +
            "10.0.0.1 - - [01/Mar/2026:00:00:00 +0000] \"-\" 400 0 \"-\" \"-\"\n";
        var (lines, accepted) = AssertAgreement(Encoding.UTF8.GetBytes(text));
        Assert.Equal(4, lines);
        Assert.Equal(4, accepted);
    }

    /// <summary>Runs both scanners over every line; returns (lines, lines the fast scan accepted).</summary>
    private static (int Lines, int Accepted) AssertAgreement(byte[] bytes)
    {
        var lines = 0;
        var accepted = 0;
        var fastStarts = new int[NginxLayout.MaxFields];
        var fastLengths = new int[NginxLayout.MaxFields];
        var slowStarts = new int[NginxLayout.MaxFields];
        var slowLengths = new int[NginxLayout.MaxFields];
        foreach (var record in new Urlog.Core.Sources.RecordEnumerator(bytes, Urlog.Core.Sources.Framing.Line, 0))
        {
            lines++;
            var line = record.Bytes;
            var fast = NginxSource.TryScanCombined(line, fastStarts, fastLengths, out var fastTyped);
            var slow = NginxSource.TokeniseGeneric(line, NginxLayout.Combined, slowStarts, slowLengths, out var slowTyped);
            var shown = Encoding.UTF8.GetString(line.Length > 120 ? line[..120] : line);
            Assert.True(fast == (slow == NginxSource.Status.Ok), $"fast={fast} generic={slow} for: {shown}");
            if (!fast)
            {
                continue;
            }

            accepted++;
            Assert.Equal(slowStarts.AsSpan(0, 9).ToArray(), fastStarts.AsSpan(0, 9).ToArray());
            Assert.Equal(slowLengths.AsSpan(0, 9).ToArray(), fastLengths.AsSpan(0, 9).ToArray());
            Assert.Equal(slowTyped.UnixMilliseconds, fastTyped.UnixMilliseconds);
            Assert.Equal(slowTyped.HttpStatus, fastTyped.HttpStatus);
            Assert.Equal(slowTyped.End, fastTyped.End);
        }

        return (lines, accepted);
    }
}
