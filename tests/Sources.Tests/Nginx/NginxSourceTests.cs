using System.Buffers;
using System.Text;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Nginx;

namespace Urlog.Sources.Tests.Nginx;

public sealed class NginxSourceTests
{
    [Fact]
    public void RegisteredWithAliases()
    {
        var registration = Assert.Single(LogSourceRegistry.Registrations, r => r.Id == "nginx");
        Assert.Equal(["nginx-access", "apache-combined", "apache-common"], registration.Aliases);
        var source = registration.Factory();
        Assert.IsType<NginxSource>(source);
        Assert.Equal(Framing.Line, source.Framing);
        Assert.False(source.Info.Stateful);
    }

    [Fact]
    public async Task SampleRoundTripsExpectedJsonl()
    {
        var expected = Fixtures.ReadExpected("nginx");
        var actual = await Fixtures.ParseFileAsync(new NginxSource(), Fixtures.Path("nginx", "sample.log"));

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i] == actual[i], $"row {i} differs.\nexpected: {expected[i]}\nactual:   {actual[i]}");
        }

        // raw is the file's bytes at file_offset, byte for byte, with no terminator.
        var file = Fixtures.Bytes("nginx", "sample.log");
        foreach (var row in actual)
        {
            Assert.Equal(file.AsSpan((int)row.FileOffset, row.RawBytes.Length).ToArray(), row.RawBytes.ToArray());
            var next = (int)row.FileOffset + row.RawBytes.Length;
            Assert.True(next == file.Length || file[next] == (byte)'\n' || file[next] == (byte)'\r', $"raw at {row.FileOffset} does not end at a line terminator");
        }

        Assert.Equal(3, actual.Count(r => r.IsReject));
        Assert.Contains(actual, r => r.RawBytes.Length > 10_000);
    }

    [Fact]
    public void ParsingTheSampleAllocatesNothingPerRow()
    {
        byte[] sample = [.. Fixtures.Bytes("nginx", "sample.log").AsSpan(3), (byte)'\n']; // core strips the BOM before Parse; terminate the last line
        var data = new byte[sample.Length * 200];
        for (var i = 0; i < 200; i++)
        {
            sample.CopyTo(data, i * sample.Length);
        }

        var source = new NginxSource();
        var writer = new NullBatchWriter();
        var ctx = new ParseContext { CaseId = 1, HostId = 1, FileId = 1, DefaultHost = "web-01"u8.ToArray() };
        ctx.Bind(writer);

        source.Parse(new ReadOnlySequence<byte>(data), ctx, writer); // warm-up: JIT and tiering
        var rowsBefore = writer.Rows;
        var allocated = AllocationProbe.Least(_ => source.Parse(new ReadOnlySequence<byte>(data), ctx, writer));

        Assert.Equal(36 * 200 * AllocationProbe.Passes, writer.Rows - rowsBefore);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void MessageIsTheRequestStatusAndBytesSlice()
    {
        var rows = ParseLines(
            "203.0.113.7 - - [01/Mar/2026:00:00:00 +0000] \"GET /a?x=1 HTTP/1.1\" 200 612 \"https://r/\" \"UA\"\n" +
            "203.0.113.7 - - [01/Mar/2026:00:00:00 +0000] \"GET /b HTTP/1.1\" 404 -\n" +
            "203.0.113.7 - - [01/Mar/2026:00:00:00 +0000] \"-\" 400 0 \"-\" \"-\" extra\n" +
            "203.0.113.7 - - [01/Mar/2026:00:00:00 +0000] \"GET /c HTTP/1.1\" 200 1 \"-\" \"UA\"\n" +
            "203.0.113.7 - - [01/Mar/2026:00:00:00 +0000] \"GET /d HTTP/1.1\" 200 1 \"-\" \"UA\"\n");
        Assert.Equal(5, rows.Count);
        Assert.Equal("UA", rows[0].UserAgent); // the chunk is combined: referer and agent are typed, not trailing

        Assert.Equal("\"GET /a?x=1 HTTP/1.1\" 200 612", rows[0].Message);
        Assert.Equal("\"GET /b HTTP/1.1\" 404 -", rows[1].Message);
        Assert.Equal("\"-\" 400 0", rows[2].Message);
        Assert.Equal("/a", rows[0].UrlPath);
        Assert.Equal("x=1", rows[0].UrlQuery);
        Assert.Equal("", rows[2].UrlPath);
        Assert.Equal("extra", rows[2].Extras["trailing"]);
        Assert.Equal(400u, rows[2].EventCode);
        Assert.Equal("failure", rows[2].EventOutcome);
        Assert.Equal("failure", rows[1].EventOutcome);
        Assert.Equal(0UL, rows[1].HttpResponseBodyBytes);
        Assert.False(rows[1].Extras.ContainsKey("http.response.body.bytes"));
    }

    [Theory]
    [InlineData(199, "success")]
    [InlineData(200, "success")]
    [InlineData(304, "success")]
    [InlineData(399, "success")]
    [InlineData(400, "failure")]
    [InlineData(404, "failure")]
    [InlineData(499, "failure")]
    [InlineData(500, "failure")]
    [InlineData(503, "failure")]
    public void OutcomeFollowsTheStatusClass(int status, string outcome)
    {
        var rows = ParseLines($"1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" {status} 0\n");
        Assert.Equal(outcome, rows[0].EventOutcome);
        Assert.Equal((ushort)status, rows[0].HttpStatus);
        Assert.Equal((uint)status, rows[0].EventCode);
    }

    [Theory]
    [InlineData("1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"unterminated\n", "unterminated quote")]
    [InlineData("1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0 \"-\" \"escaped at end\\\n", "unterminated quote")]
    [InlineData("1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\n", "unterminated quote")]
    [InlineData("1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] GET / HTTP/1.1 200 0\n", "no request")]
    [InlineData("1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\"\n", "bad status")]
    [InlineData("1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 2000000 0 \"-\" \"-\"\n", "bad status")]
    [InlineData("1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200x 0 \"-\" \"-\"\n", "bad status")]
    [InlineData("1.2.3.4 - - [1/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0\n", "bad timestamp")]
    [InlineData("1.2.3.4 - - [01/Mar/2026:00:00:00 +0000 \"GET / HTTP/1.1\" 200 0\n", "bad timestamp")]
    [InlineData("1.2.3.4 - - 01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0\n", "bad timestamp")]
    [InlineData("1.2.3.4 - -\n", "layout mismatch")]
    [InlineData("1.2.3.4\n", "layout mismatch")]
    [InlineData(" leading space\n", "layout mismatch")]
    [InlineData("\n", "empty line")]
    [InlineData("\r\n", "empty line")]
    public void MalformedLinesAreRejectedWithAReason(string line, string reason)
    {
        // Pinned to combined: a lone line would otherwise be detected as whatever built-in fits it best.
        var rows = ParseLines(line, new Dictionary<string, string> { [NginxSource.LogFormatOption] = "combined" });
        var row = Assert.Single(rows);
        Assert.Equal(reason, row.Extras["reject_reason"]);
        Assert.Equal(row.Raw, row.Message);
        Assert.Equal("", row.Program);
    }

    [Fact]
    public void NonAddressClientGoesToExtrasAndIdentIsKept()
    {
        var rows = ParseLines(
            "unknown - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0\n" +
            "fe80::1%eth0 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0\n" +
            "::1 bob alice [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 0\n");
        Assert.Equal("::", rows[0].SourceIp);
        Assert.Equal("unknown", rows[0].Extras["source.address"]);
        Assert.Equal("fe80::1%eth0", rows[1].Extras["source.address"]);
        Assert.Equal("::1", rows[2].SourceIp);
        Assert.Equal("bob", rows[2].Extras["ident"]);
        Assert.Equal("alice", rows[2].UserName);
    }

    [Fact]
    public void RequestLineVariants()
    {
        var rows = ParseLines(
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET /two-tokens\" 200 0\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET /a b c HTTP/1.1\" 200 0\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"CONNECT host:443 HTTP/1.1\" 200 0\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"GET /p?a=1?b=2 SPDY/3\" 200 0\n" +
            "1.2.3.4 - - [01/Mar/2026:00:00:00 +0000] \"\" 200 0\n");
        Assert.Equal("/two-tokens", rows[0].UrlPath);
        Assert.Equal("", rows[0].HttpVersion);
        Assert.Equal("/a b c", rows[1].UrlPath);
        Assert.Equal("1.1", rows[1].HttpVersion);
        Assert.Equal("host:443", rows[2].UrlPath);
        Assert.Equal("connect", rows[2].HttpMethod); // LowCardinality: lowercased at ingest
        Assert.Equal("/p", rows[3].UrlPath);
        Assert.Equal("a=1?b=2", rows[3].UrlQuery);
        Assert.Equal("spdy/3", rows[3].HttpVersion);
        Assert.Equal("", rows[4].UrlPath);
        Assert.Equal("", rows[4].HttpMethod);
    }

    [Fact]
    public void ChunkBoundariesDoNotChangeRows()
    {
        var sample = Fixtures.Bytes("nginx", "sample.log").AsSpan(3).ToArray();
        var once = ParseBytes(sample, maxChunk: int.MaxValue);
        var small = ParseBytes(sample, maxChunk: 700); // several records per chunk, the 10 KB line alone in one
        Assert.Equal(once.Count, small.Count);
        for (var i = 0; i < once.Count; i++)
        {
            Assert.True(once[i] == small[i], $"row {i} differs between chunkings\n{once[i]}\n{small[i]}");
        }
    }

    /// <summary>Parses text as one chunk through the real batch writer and returns the rows in order.</summary>
    internal static List<ParsedRow> ParseLines(string text, IReadOnlyDictionary<string, string>? options = null) => ParseBytes(Encoding.UTF8.GetBytes(text), int.MaxValue, options);

    internal static List<ParsedRow> ParseBytes(byte[] data, int maxChunk, IReadOnlyDictionary<string, string>? options = null)
    {
        var pipelineOptions = new PipelineOptions { ChunkBytes = 4096, MaxRecordBytes = 1 << 20, BatchRows = 1000, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 1000 };
        var buffers = new PipelineBuffers(pipelineOptions);
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        var ctx = new ParseContext { CaseId = 1, HostId = 1, FileId = 1, DefaultHost = "web-01"u8.ToArray(), Options = options };
        writer.Bind(ctx);
        var source = new NginxSource();

        var offset = 0;
        var index = 0;
        while (offset < data.Length)
        {
            var rest = data.AsSpan(offset);
            var length = rest.Length <= maxChunk ? rest.Length : Framing.Line.LastRecordEnd(rest[..maxChunk]);
            if (length == 0)
            {
                // No whole record fits: the chunk reader grows its buffer; here the record simply becomes its own chunk.
                var newline = rest.IndexOf((byte)'\n');
                length = newline < 0 ? rest.Length : newline + 1;
            }

            ctx.ChunkFileOffset = offset;
            ctx.ChunkIndex = index++;
            source.Parse(new ReadOnlySequence<byte>(data, offset, length), ctx, writer);
            offset += length;
        }

        writer.Flush();
        var rows = new List<ParsedRow>();
        foreach (var batch in sink.Batches)
        {
            for (var i = 0; i < batch.RowCount; i++)
            {
                rows.Add(ParsedRow.From(batch, i));
            }
        }

        sink.ReturnAll();
        writer.Close();
        return rows;
    }
}
