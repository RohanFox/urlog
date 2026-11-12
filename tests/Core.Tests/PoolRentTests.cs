using Urlog.Core.Pipeline;

namespace Urlog.Core.Tests;

/// <summary>
/// A rent that fails leaves the pools as they were: the job runner keeps one set of buffers per slot for the life of
/// the process and checks it is balanced after every job, so a count that a failed allocation left high would read
/// as a leak for ever, and a batch slot it kept would shrink the pool for good.
/// </summary>
public sealed class PoolRentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ABatchRentThatCannotAllocateLeavesThePoolBalanced()
    {
        // More rows than an array can hold: the batch's first column allocation throws, with no memory taken.
        var options = new PipelineOptions { BatchRows = int.MaxValue, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 1, ChunkBytes = 4096 };
        using var pool = new BatchPool(options);
        Assert.Equal(2, pool.Size);

        for (var i = 0; i < 5; i++) // more failures than the pool has slots: a leaked slot would block the third rent for good
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            Assert.ThrowsAny<Exception>(() => pool.Rent(cts.Token));
            Assert.False(cts.IsCancellationRequested, "the rent waited for a slot a failed rent never gave back");
            Assert.Equal(0, pool.Outstanding);
            Assert.Equal(0, pool.Created);
        }
    }

    [Fact]
    public void AChunkRentThatCannotAllocateLeavesThePoolBalanced()
    {
        var pool = new ChunkPool(new PipelineOptions { ChunkBytes = 4096, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 1, BatchRows = 10 });
        Assert.ThrowsAny<Exception>(() => pool.Rent(int.MaxValue)); // above the largest array: the oversize allocation throws
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.Rented);
        Assert.Equal(0, pool.OversizeAllocations);

        var buffer = pool.Rent(4096);
        Assert.Equal(1, pool.Outstanding);
        pool.Return(buffer);
        Assert.Equal(0, pool.Outstanding);
    }
}
