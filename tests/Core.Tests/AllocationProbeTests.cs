namespace Urlog.Core.Tests;

/// <summary>The probe reads zero for code that allocates nothing and does not hide code that allocates on every pass.</summary>
public sealed class AllocationProbeTests
{
    [Fact]
    public void CodeThatAllocatesNothingReadsZero()
    {
        var data = new byte[1 << 16];
        long sum = 0;

        var allocated = AllocationProbe.Least(_ =>
        {
            foreach (var b in data)
            {
                sum += b;
            }
        });

        Assert.Equal(0, allocated);
        Assert.Equal(0, sum);
    }

    [Fact]
    public void AnAllocationOnEveryPassIsSeen()
    {
        var kept = new object[AllocationProbe.Passes];

        var allocated = AllocationProbe.Least(pass => kept[pass] = new byte[100]);

        Assert.InRange(allocated, 100, 200);
    }

    [Fact]
    public void EveryPassRunsOnceAndInOrder()
    {
        var seen = new List<int>(AllocationProbe.Passes);

        AllocationProbe.Least(seen.Add);

        Assert.Equal(Enumerable.Range(0, AllocationProbe.Passes), seen);
    }
}
