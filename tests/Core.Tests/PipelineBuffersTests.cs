using Urlog.Core.Pipeline;

namespace Urlog.Core.Tests;

public sealed class PipelineBuffersTests
{
    [Fact]
    public void DefaultsAreValidAndTheFormulaMatchesTheDocumentedFigures()
    {
        var d = PipelineOptions.Default;
        d.Validate();
        Assert.Equal(64 << 20, d.ChunkBytes);
        Assert.Equal(16 << 20, d.MaxRecordBytes);
        Assert.Equal(100_000, d.BatchRows);
        Assert.Equal(d.Parallelism + 2, d.InFlightChunks);
        Assert.Equal(d.Parallelism + 4, d.BatchPoolSize);

        // Reference box: 8 cores, nginx-shaped records of 223 B (docs: 11 × 64 MiB of chunks + 12 × ~46 MB of batches ≈ 1.25 GB).
        var reference = d with { Parallelism = 8, InFlightChunks = 10 };
        var batchBytes = reference.EstimateBatchBytes(223);
        Assert.Equal(100_000L * (234 + 223), batchBytes);
        var total = reference.EstimatePoolBytes(223);
        Assert.Equal((11L * (64 << 20)) + (12 * batchBytes), total);
        Assert.InRange(total, 1.15e9, 1.35e9);

        PipelineOptions.SmallBox.Validate();
        Assert.InRange(PipelineOptions.SmallBox.EstimatePoolBytes(223), 160e6, 200e6);
    }

    [Theory]
    [InlineData(1024, 16 << 20, 100_000, 2, 4, 2)]   // chunk too small
    [InlineData(1 << 20, 16 << 20, 100_000, 4, 5, 2)] // in-flight chunks < parallelism + 2
    [InlineData(1 << 20, 16 << 20, 0, 1, 3, 2)]       // no rows
    [InlineData(1 << 20, 16 << 20, 10, 0, 3, 2)]      // no parsers
    [InlineData(1 << 20, 16 << 20, 10, 1, 3, 0)]      // no in-flight batches
    public void InvalidOptionsAreRejected(int chunk, int maxRecord, int rows, int parallelism, int inFlightChunks, int inFlightBatches)
    {
        var o = new PipelineOptions { ChunkBytes = chunk, MaxRecordBytes = maxRecord, BatchRows = rows, Parallelism = parallelism, InFlightChunks = inFlightChunks, InFlightBatches = inFlightBatches };
        Assert.Throws<ArgumentOutOfRangeException>(o.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineBuffers(o));
    }

    [Fact]
    public void ChunkPoolReusesBuffersAndTracksOutstanding()
    {
        var options = new PipelineOptions { ChunkBytes = 4096, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 1, BatchRows = 10 };
        var pool = new ChunkPool(options);
        var a = pool.Rent(4096);
        var b = pool.Rent(4096);
        Assert.Equal(2, pool.Outstanding);
        pool.Return(a);
        pool.Return(b);
        Assert.Equal(0, pool.Outstanding);

        var c = pool.Rent(4096);
        Assert.True(ReferenceEquals(c, a) || ReferenceEquals(c, b), "a returned buffer is handed out again");
        pool.Return(c);

        var big = pool.Rent(8192);
        Assert.Equal(1, pool.OversizeAllocations);
        Assert.True(big.Length >= 8192);
        pool.Return(big);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public async Task BatchPoolBlocksWhenExhaustedAndResumesOnReturn()
    {
        var options = new PipelineOptions { ChunkBytes = 4096, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 1, BatchRows = 10 };
        using var pool = new BatchPool(options);
        Assert.Equal(2, pool.Size);

        var ct = TestContext.Current.CancellationToken;
        var first = pool.Rent(ct);
        var second = pool.Rent(ct);
        Assert.Equal(2, pool.Outstanding);
        Assert.Equal(2, pool.Created);

        var third = Task.Run(() => pool.Rent(ct), ct);
        var finishedEarly = await Task.WhenAny(third, Task.Delay(300, ct));
        Assert.NotSame(third, finishedEarly);

        first.Return();
        var got = await third.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Same(first, got);
        Assert.Equal(2, pool.Created);

        got.Return();
        second.Return();
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public void ReturnedBatchIsCleared()
    {
        var options = new PipelineOptions { ChunkBytes = 4096, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 1, BatchRows = 10 };
        using var pool = new BatchPool(options);
        var batch = pool.Rent(TestContext.Current.CancellationToken);
        batch.Strings(Urlog.Core.Sources.EventColumn.Raw).Append("abc"u8);
        batch.Return();
        var again = pool.Rent(TestContext.Current.CancellationToken);
        Assert.Same(batch, again);
        Assert.Equal(0, again.RowCount);
        Assert.Equal(0, again.Strings(Urlog.Core.Sources.EventColumn.Raw).ByteLength);
        again.Return();
    }
}
