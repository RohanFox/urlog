using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Nginx;
using Urlog.Sources.Syslog;

namespace Urlog.Bench.Fixtures;

/// <summary>
/// A set of full <see cref="EventBatch"/>es of <see cref="BlockSize"/> rows, parsed from the start of a 1 GiB fixture
/// by the real source through the real <see cref="EventBatchWriter"/>: the blocks the insert path sees in production,
/// held in memory and reused by every op of the Insert benches (and every cell of the insert matrix). The batches are
/// owned by this set, never returned to a pool by a sink (<c>NativeInserter(returnBatches: false)</c>).
/// </summary>
public sealed class EventBatchSet : IDisposable
{
    private readonly PipelineBuffers _buffers;

    private EventBatchSet(PipelineBuffers buffers, IReadOnlyList<EventBatch> batches, int blockSize, string shape)
    {
        _buffers = buffers;
        Batches = batches;
        BlockSize = blockSize;
        Shape = shape;
        foreach (var b in batches)
        {
            Rows += b.RowCount;
            RawBytes += b.RawBytes;
            Extras += b.ExtrasCount;
        }
    }

    public IReadOnlyList<EventBatch> Batches { get; }

    public int BlockSize { get; }

    /// <summary><c>nginx</c> or <c>syslog</c>.</summary>
    public string Shape { get; }

    public long Rows { get; }

    /// <summary>Bytes of <c>raw</c> across the set: the input volume these rows represent.</summary>
    public long RawBytes { get; }

    public long Extras { get; }

    /// <summary>Blocks already in <c>(case_id, host, ts)</c> order: every nginx block (one host, time-ordered lines); few syslog blocks (a host per event).</summary>
    public int SortedBlocks => Batches.Count(b => IsSorted(b));

    /// <summary>The nginx fixture: one host, blocks arrive pre-sorted.</summary>
    public static EventBatchSet Nginx(int blockSize, int count) =>
        Build(FixtureStore.Nginx1G().Path, new NginxSource(), blockSize, count, "nginx", new FileIdentity(1, 1, 1, 1, "web-01"));

    /// <summary>The syslog fixture: 24 hosts, a host per event, blocks are not sorted by the table's key.</summary>
    public static EventBatchSet Syslog(int blockSize, int count) =>
        Build(FixtureStore.Syslog1G().Path, new SyslogSource(), blockSize, count, "syslog", new FileIdentity(1, 1, 1, 1, "syslog-01", YearHint: 2026));

    public static EventBatchSet Build(string fixturePath, ILogSource source, int blockSize, int count, string shape, FileIdentity file)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        // The count is checked between chunks, so one chunk may flush a few batches past it: small chunks keep that
        // overshoot under a block and the pool has headroom for it (a blocked Rent here would be a deadlock).
        var options = new PipelineOptions
        {
            ChunkBytes = 8 << 20,
            BatchRows = blockSize,
            Parallelism = 1,
            InFlightChunks = 3,
            InFlightBatches = count + 8,
        };
        var buffers = new PipelineBuffers(options);
        var sink = new CollectingBatchSink();
        var writer = new EventBatchWriter(buffers.Batches, sink);
        var ctx = new ParseContext
        {
            CaseId = file.CaseId,
            HostId = file.HostId,
            FileId = file.FileId,
            ParseGeneration = file.ParseGeneration,
            DefaultHost = System.Text.Encoding.UTF8.GetBytes(file.DefaultHost.ToLowerInvariant()),
            TimeZoneHint = file.TimeZoneHint,
            YearHint = file.YearHint,
            Options = file.Options,
        };
        writer.Bind(ctx);

        using var stream = new FileStream(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var reader = new ChunkReader(buffers.Chunks, options);
        foreach (var chunk in reader.Read(stream, source.Framing))
        {
            ctx.ChunkFileOffset = chunk.FileOffset;
            ctx.ChunkIndex = chunk.Index;
            ctx.ChunkForcedSplit = chunk.ForcedSplit;
            ctx.ChunkContinuesSplit = chunk.ContinuesSplit;
            source.Parse(chunk.Sequence, ctx, writer);
            chunk.Return();
            if (sink.Batches.Count >= count)
            {
                break;
            }
        }

        writer.Discard();
        var batches = sink.Batches.Take(count).ToList();
        if (batches.Count < count)
        {
            throw new InvalidOperationException($"{fixturePath} yielded {batches.Count} blocks of {blockSize} rows, {count} requested");
        }

        return new EventBatchSet(buffers, batches, blockSize, shape);
    }

    public void Dispose()
    {
        foreach (var b in Batches)
        {
            b.Return();
        }

        _buffers.Batches.Dispose();
    }

    private static bool IsSorted(EventBatch batch)
    {
        var host = batch.Strings(EventColumn.Host);
        for (var i = 1; i < batch.RowCount; i++)
        {
            var c = batch.CaseId[i - 1].CompareTo(batch.CaseId[i]);
            if (c == 0)
            {
                c = host.Row(i - 1).SequenceCompareTo(host.Row(i));
            }

            if (c == 0)
            {
                c = batch.Ts[i - 1].CompareTo(batch.Ts[i]);
            }

            if (c > 0)
            {
                return false;
            }
        }

        return true;
    }
}
