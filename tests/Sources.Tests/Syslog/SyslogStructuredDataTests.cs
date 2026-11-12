using System.Text;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Syslog;

namespace Urlog.Sources.Tests.Syslog;

public sealed class SyslogStructuredDataTests
{
    [Theory]
    [InlineData("[a]", 3)]
    [InlineData("[a] msg", 3)]
    [InlineData("[a][b]", 6)]
    [InlineData("[a x=\"1\"]", 9)]
    [InlineData("[a x=\"1\" y=\"two words\"]", 23)]
    [InlineData("[a x=\"q\\\"q\"]", 12)]
    [InlineData("[a x=\"b\\]b\"]", 12)]
    [InlineData("[a x=\"c\\\\\"]", 11)]
    [InlineData("[a x=\"\"]", 8)]
    [InlineData("[exampleSDID@32473 iut=\"3\"][origin ip=\"1.2.3.4\"] m", 48)]
    [InlineData("[a x=\"[not an element]\"]", 24)]
    public void ScanAcceptsWellFormedElements(string text, int length)
    {
        Assert.Equal(length, SyslogStructuredData.Scan(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("[]")]
    [InlineData("[a")]
    [InlineData("[a ")]
    [InlineData("[a x")]
    [InlineData("[a x=")]
    [InlineData("[a x=1]")]
    [InlineData("[a x=\"1]")]
    [InlineData("[a x=\"1\"")]
    [InlineData("[a x=\"\\")]
    [InlineData("[a x=\"1\"y=\"2\"]")]
    [InlineData("[a =\"1\"]")]
    [InlineData("[a x\"1\"]")]
    [InlineData("[a x y=\"1\"]")]
    [InlineData("[a]]")]
    public void ScanRejectsMalformedElements(string text)
    {
        var length = SyslogStructuredData.Scan(Encoding.UTF8.GetBytes(text));
        // "[a]]" scans the first element and stops: the caller sees the stray bracket where a space must be.
        Assert.True(length < 0 || length < text.Length, text);
    }

    [Fact]
    public void WriteAppendsOneExtraPerParameter()
    {
        var extras = Write("[exampleSDID@32473 iut=\"3\" eventSource=\"App\\\"lication\" eventID=\"10\\]11\"][origin ip=\"203.0.113.7\"][empty]");
        Assert.Equal(
        [
            ("log.syslog.structured_data.exampleSDID@32473.iut", "3"),
            ("log.syslog.structured_data.exampleSDID@32473.eventSource", "App\\\"lication"),
            ("log.syslog.structured_data.exampleSDID@32473.eventID", "10\\]11"),
            ("log.syslog.structured_data.origin.ip", "203.0.113.7"),
            ("log.syslog.structured_data.empty", ""),
        ], extras);
    }

    [Fact]
    public void WriteFallsBackWhenTheKeyDoesNotFit()
    {
        var longId = new string('i', 240);
        var longName = new string('n', 240);
        var extras = Write($"[{longId} a=\"1\"][ok {longName}=\"2\" b=\"3\"][fine c=\"4\"]");
        Assert.Equal(
        [
            ("log.syslog.structured_data", $"[{longId} a=\"1\"]"),
            ("log.syslog.structured_data", $"{longName}=\"2\""),
            ("log.syslog.structured_data.ok.b", "3"),
            ("log.syslog.structured_data.fine.c", "4"),
        ], extras);
    }

    private static List<(string Key, string Value)> Write(string sd)
    {
        var bytes = Encoding.UTF8.GetBytes(sd);
        Assert.Equal(bytes.Length, SyslogStructuredData.Scan(bytes));
        var options = new PipelineOptions { ChunkBytes = 4096, BatchRows = 10, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 10 };
        var buffers = new PipelineBuffers(options);
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        var ctx = new ParseContext { DefaultHost = "h"u8.ToArray() };
        writer.Bind(ctx);

        SyslogStructuredData.Write(bytes, writer, new byte[256]);
        writer.AppendString(EventColumn.Raw, bytes);
        writer.CommitRow();
        writer.Flush();

        var batch = Assert.Single(sink.Batches);
        var row = ParsedRow.From(batch, 0);
        var ordered = new List<(string, string)>();
        for (var k = 0; k < batch.ExtrasOffsets[0]; k++)
        {
            ordered.Add((batch.ExtrasKeys.RowString(k), batch.ExtrasValues.RowString(k)));
        }

        Assert.Equal(ordered.Count, row.Extras.Count + ordered.Count(p => p.Item1 == "log.syslog.structured_data") - (ordered.Any(p => p.Item1 == "log.syslog.structured_data") ? 1 : 0));
        sink.ReturnAll();
        writer.Close();
        return ordered;
    }
}
