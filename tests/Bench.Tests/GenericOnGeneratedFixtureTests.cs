using System.Buffers;
using Urlog.Bench.Fixtures;
using Urlog.Bench.Stub;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Generic;

namespace Urlog.Bench.Tests;

/// <summary>
/// The generic source against its fixed-seed generator: every non-blank line is a row, nothing is rejected, a
/// timestamp is found on exactly the lines the generator wrote one on, and nothing allocates per row.
/// </summary>
public sealed class GenericOnGeneratedFixtureTests : IDisposable
{
    private const long SampleBytes = 1 << 20;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urlog-bench-tests-" + Guid.NewGuid().ToString("N"));

    public GenericOnGeneratedFixtureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void EveryNonBlankLineIsARowAndNothingAllocates()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "generic.log"), new GenericGen(FixtureStore.GenericSeed), SampleBytes);
        var data = File.ReadAllBytes(meta.Path);
        var source = new GenericSource();
        var writer = new NullBatchWriter();
        var rows = meta.Lines - meta.Counts![GenericGen.BlankLines];

        var ctx = InMemoryChunks.Context("app-01", writer);
        InMemoryChunks.Parse(source, data, ctx, writer, maxChunk: 100_000); // warm: JIT, the regex runner
        Assert.Equal(rows, writer.Rows);
        Assert.Equal(0, ctx.Rejects);
        Assert.Equal(meta.Bytes - meta.Lines, writer.RawBytes); // every byte but the line terminators went through raw

        ctx = InMemoryChunks.Context("app-01", writer);
        var before = GC.GetAllocatedBytesForCurrentThread();
        InMemoryChunks.Parse(source, data, ctx, writer, maxChunk: 100_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(rows * 2, writer.Rows);
        Assert.Equal(0, ctx.Rejects);
        Assert.InRange(allocated, 0, 1024); // the per-file state and nothing per row
    }

    [Fact]
    public void ATimestampIsFoundOnExactlyTheLinesThatCarryOne()
    {
        var meta = FixtureStore.Generate(Path.Combine(_dir, "generic.log"), new GenericGen(FixtureStore.GenericSeed), 256 << 10);
        var data = File.ReadAllBytes(meta.Path);
        var options = new PipelineOptions { ChunkBytes = 1 << 20, BatchRows = 100_000, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 4 };
        var buffers = new PipelineBuffers(options);
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        var ctx = new ParseContext { CaseId = 1, HostId = 1, FileId = 1, DefaultHost = "app-01"u8.ToArray(), YearHint = 2026 };
        writer.Bind(ctx);
        new GenericSource().Parse(new ReadOnlySequence<byte>(data), ctx, writer);
        writer.Flush();

        var batch = Assert.Single(sink.Batches);
        var counts = meta.Counts!;
        Assert.Equal(meta.Lines, counts[GenericGen.TimestampLines] + counts[GenericGen.NoTimestampLines] + counts[GenericGen.BlankLines]);
        Assert.Equal(counts[GenericGen.TimestampLines] + counts[GenericGen.NoTimestampLines], batch.RowCount);

        long own = 0, inherited = 0;
        for (var i = 0; i < batch.RowCount; i++)
        {
            Assert.InRange(batch.Ts[i], 1_772_323_200_000L - 7_200_000L, 1_772_323_200_000L + (86_400_000L * 30)); // March 2026, as generated
            var hasExtra = (i == 0 ? 0 : batch.ExtrasOffsets[i - 1]) != batch.ExtrasOffsets[i];
            if (hasExtra)
            {
                inherited++;
            }
            else
            {
                own++;
            }
        }

        Assert.Equal(counts[GenericGen.TimestampLines], own);
        Assert.Equal(counts[GenericGen.NoTimestampLines], inherited);
        Assert.InRange((double)counts[GenericGen.NoTimestampLines] / meta.Lines, 0.10, 0.45);
        sink.ReturnAll();
        writer.Close();
    }
}
