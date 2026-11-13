namespace Urlog.Tests.Shared;

/// <summary>
/// Bytes a piece of code allocates on the calling thread, for the tests that hold a parser to zero.
/// <para>
/// One reading of <c>GC.GetAllocatedBytesForCurrentThread</c> around the code is not enough. A thread allocates
/// from a buffer the runtime hands it (about 8 KB), and the counter is "bytes handed to the thread, less the
/// unused rest of the buffer". When the runtime takes the buffer back while the code runs, the unused rest counts
/// as allocated although the code allocated nothing. Measured here: about once in 250 starts of the bench tests,
/// one pass over a tokeniser read 5,768 bytes, and 4,760 and 2,760 after 1,000 and 3,000 bytes had been allocated
/// just before it, which is the rest of the buffer and not anything the tokeniser did.
/// </para>
/// <para>
/// So the code runs several times and the least reading is the answer. Code that allocates per row allocates on
/// every pass; the buffer is taken back at most now and then.
/// </para>
/// </summary>
internal static class AllocationProbe
{
    /// <summary>How often <see cref="Least"/> runs the code: callers that count rows multiply by this.</summary>
    public const int Passes = 3;

    /// <summary>Runs <paramref name="pass"/> <see cref="Passes"/> times (it gets the pass number, from 0) and returns the least bytes one pass allocated.</summary>
    public static long Least(Action<int> pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        var least = long.MaxValue;
        for (var i = 0; i < Passes; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            pass(i);
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }
}
