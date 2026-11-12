using System.Text;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Tests;

public sealed class ParsePipelineTests
{
    private static readonly FileIdentity File1 = new(CaseId: 1, HostId: 2, FileId: 3, ParseGeneration: 1, DefaultHost: "WEB-01");

    // inFlightBatches is large for the tests that keep every batch (CollectingBatchSink): a pool that small would block.
    private static PipelineOptions Options(int parallelism = 3, int chunkBytes = 4096, int batchRows = 500, int inFlightBatches = 2) => new()
    {
        ChunkBytes = chunkBytes,
        MaxRecordBytes = 64 << 10,
        BatchRows = batchRows,
        Parallelism = parallelism,
        InFlightChunks = parallelism + 3,
        InFlightBatches = inFlightBatches,
    };

    [Fact]
    public async Task EveryRecordArrivesExactlyOnceAcrossParallelParsers()
    {
        var (data, lines) = TestData.NumberedLines(2 << 20, seed: 10, badEvery: 97);
        var buffers = new PipelineBuffers(Options(inFlightBatches: 1000));
        var sink = new CollectingBatchSink();
        var pipeline = new ParsePipeline(buffers);

        var result = await pipeline.RunAsync(new MemoryStream(data), new NumberedLineSource(), File1, sink, TestContext.Current.CancellationToken);

        var expectedRejects = lines / 97;
        Assert.Equal(lines, result.Rows);
        Assert.Equal(expectedRejects, result.Rejects);
        Assert.Equal(data.Length, result.Bytes);
        Assert.True(result.Chunks > 300, $"expected hundreds of 4 KiB chunks, got {result.Chunks}");
        Assert.Equal(0, result.ForcedSplits);

        var offsets = new HashSet<ulong>();
        long rawBytes = 0;
        var rejected = 0;
        foreach (var batch in sink.Batches)
        {
            Assert.InRange(batch.RowCount, 1, 500);
            for (var i = 0; i < batch.RowCount; i++)
            {
                Assert.Equal(1UL, batch.CaseId[i]);
                Assert.Equal(2UL, batch.HostId[i]);
                Assert.Equal(3UL, batch.FileId[i]);
                Assert.True(offsets.Add(batch.FileOffset[i]), $"offset {batch.FileOffset[i]} seen twice");
                Assert.Equal("web-01", batch.Strings(EventColumn.Host).RowString(i));
                var raw = batch.Strings(EventColumn.Raw).Row(i);
                Assert.Equal(raw.ToArray(), data.AsSpan((int)batch.FileOffset[i], raw.Length).ToArray());
                rawBytes += raw.Length;
                if (batch.ExtrasOffsets[i] - (i == 0 ? 0 : batch.ExtrasOffsets[i - 1]) == 1)
                {
                    rejected++;
                    Assert.Equal("", batch.Strings(EventColumn.Program).RowString(i));
                }
                else
                {
                    Assert.Equal("test", batch.Strings(EventColumn.Program).RowString(i));
                    var lineNumber = long.Parse(Encoding.UTF8.GetString(raw).Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
                    Assert.Equal(lineNumber, batch.Ts[i]);
                }
            }
        }

        Assert.Equal(lines, offsets.Count);
        Assert.Equal(expectedRejects, rejected);
        Assert.Equal(data.Length - lines, rawBytes); // every byte except the newlines is in raw

        sink.ReturnAll();
        Assert.Equal(0, buffers.Chunks.Outstanding);
        Assert.Equal(0, buffers.Batches.Outstanding);
        Assert.True(buffers.Batches.Created <= buffers.Batches.Size);
    }

    [Fact]
    public async Task StatefulSourceSeesChunksInOrderOnOneThread()
    {
        var (data, lines) = TestData.NumberedLines(512 << 10, seed: 11);
        var buffers = new PipelineBuffers(Options(parallelism: 4, inFlightBatches: 1000));
        var sink = new CollectingBatchSink();

        var result = await new ParsePipeline(buffers).RunAsync(new MemoryStream(data), new NumberedLineSource(stateful: true), File1, sink, TestContext.Current.CancellationToken);

        Assert.Equal(lines, result.Rows);
        long expected = 1;
        foreach (var batch in sink.Batches)
        {
            for (var i = 0; i < batch.RowCount; i++)
            {
                Assert.Equal(expected++, batch.Ts[i]);
            }
        }

        sink.ReturnAll();
    }

    [Fact]
    public async Task AdversarialInputsProduceRejectsNotExceptions()
    {
        var buffers = new PipelineBuffers(Options());
        var pipeline = new ParsePipeline(buffers);

        var truncated = Encoding.UTF8.GetBytes("1 ok\n2 ok\n3 cut in the mid");
        var garbage = new byte[64 << 10];
        new Random(12).NextBytes(garbage);
        var mixed = Encoding.UTF8.GetBytes("1 numbered\n<34>Mar  1 00:00:00 host prog: syslog line\n2 numbered\n203.0.113.1 - - [01/Mar/2026:00:00:00 +0000] \"GET / HTTP/1.1\" 200 1\n");
        var utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetBytes("1 looks like utf-16 but declared utf-8\n");
        var empty = Array.Empty<byte>();

        var t = await pipeline.RunAsync(new MemoryStream(truncated), new NumberedLineSource(), File1, new CountingBatchSink(), TestContext.Current.CancellationToken);
        Assert.Equal(3, t.Rows);
        Assert.Equal(0, t.Rejects);

        var g = await pipeline.RunAsync(new MemoryStream(garbage), new NumberedLineSource(), File1, new CountingBatchSink(), TestContext.Current.CancellationToken);
        Assert.True(g.Rows > 0);
        Assert.Equal(g.Rows, g.Rejects + (g.Rows - g.Rejects)); // every record landed, as a row or a reject
        Assert.True(g.Rejects > g.Rows / 2, "random bytes should mostly be rejects");

        var m = await pipeline.RunAsync(new MemoryStream(mixed), new NumberedLineSource(), File1, new CountingBatchSink(), TestContext.Current.CancellationToken);
        Assert.Equal(4, m.Rows);
        Assert.Equal(2, m.Rejects);

        // UTF-16 read as UTF-8: the 0x0A 0x00 line end splits into two records ("...\n" and "\0"), both rejected, none thrown.
        var u = await pipeline.RunAsync(new MemoryStream(utf16), new NumberedLineSource(), File1, new CountingBatchSink(), TestContext.Current.CancellationToken);
        Assert.Equal(2, u.Rows);
        Assert.Equal(2, u.Rejects);

        var e = await pipeline.RunAsync(new MemoryStream(empty), new NumberedLineSource(), File1, new CountingBatchSink(), TestContext.Current.CancellationToken);
        Assert.Equal(0, e.Rows);
        Assert.Equal(0, e.Chunks);

        Assert.Equal(0, buffers.Chunks.Outstanding);
        Assert.Equal(0, buffers.Batches.Outstanding);
    }

    [Fact]
    public async Task AThrowingParserFailsTheJobWithChunkContextAndLeavesThePoolsBalanced()
    {
        var (data, _) = TestData.NumberedLines(1 << 20, seed: 13);
        var buffers = new PipelineBuffers(Options());
        var sink = new CountingBatchSink();
        var lineStart = Array.IndexOf(data, (byte)'\n', 600_000) + 1;
        var marker = Encoding.UTF8.GetString(data, lineStart, 12);

        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            new ParsePipeline(buffers).RunAsync(new MemoryStream(data), new NumberedLineSource(throwOnText: marker), File1, sink, TestContext.Current.CancellationToken));

        Assert.Contains("test-lines", ex.Message, StringComparison.Ordinal);
        Assert.Contains("chunk", ex.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Equal(0, buffers.Chunks.Outstanding);
        Assert.Equal(0, buffers.Batches.Outstanding);
    }

    [Fact]
    public async Task CancellationStopsTheRunAndLeavesThePoolsBalanced()
    {
        var (data, _) = TestData.NumberedLines(4 << 20, seed: 14);
        var buffers = new PipelineBuffers(Options(parallelism: 2));
        using var cts = new CancellationTokenSource();
        using var slowSink = new BlockingSink(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ParsePipeline(buffers).RunAsync(new MemoryStream(data), new NumberedLineSource(), File1, slowSink, cts.Token));

        slowSink.ReleaseAll();
        Assert.Equal(0, buffers.Chunks.Outstanding);
        Assert.Equal(0, buffers.Batches.Outstanding);
    }

    [Fact]
    public async Task DecodedInputsRunThroughTheSamePipeline()
    {
        var (data, lines) = TestData.NumberedLines(300 << 10, seed: 15);
        var gz = new MemoryStream();
        using (var g = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            g.Write(data);
        }

        var utf16 = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        var text = Encoding.UTF8.GetString(data);
        var be = utf16.GetPreamble().Concat(utf16.GetBytes(text)).ToArray();

        var buffers = new PipelineBuffers(Options());
        var pipeline = new ParsePipeline(buffers);
        var a = await pipeline.RunAsync(InputDecoding.OpenDecoded(new MemoryStream(gz.ToArray()), CompressionKind.Gzip, InputEncoding.Utf8), new NumberedLineSource(), File1, new CountingBatchSink(), TestContext.Current.CancellationToken);
        var b = await pipeline.RunAsync(InputDecoding.OpenDecoded(new MemoryStream(be), CompressionKind.None, InputEncoding.Utf16BigEndian), new NumberedLineSource(), File1, new CountingBatchSink(), TestContext.Current.CancellationToken);

        Assert.Equal(lines, a.Rows);
        Assert.Equal(lines, b.Rows);
        Assert.Equal(data.Length, a.Bytes);
        Assert.Equal(data.Length, b.Bytes);
    }

    /// <summary>Holds the first submitted batch until released, and cancels the run as soon as it gets one.</summary>
    private sealed class BlockingSink(CancellationTokenSource cts) : IBatchSink, IDisposable
    {
        private readonly List<EventBatch> _held = [];
        private readonly ManualResetEventSlim _release = new();

        public void Submit(EventBatch batch)
        {
            lock (_held)
            {
                _held.Add(batch);
            }

            cts.Cancel();
            _release.Wait(TimeSpan.FromSeconds(2));
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void ReleaseAll()
        {
            _release.Set();
            lock (_held)
            {
                foreach (var b in _held)
                {
                    b.Return();
                }

                _held.Clear();
            }
        }

        public void Dispose() => _release.Dispose();
    }
}
