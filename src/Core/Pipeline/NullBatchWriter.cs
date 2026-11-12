using Urlog.Core.Sources;

namespace Urlog.Core.Pipeline;

/// <summary>
/// An <see cref="IBatchWriter"/> that stores nothing and counts: rows, string bytes, extras. For the
/// <c>Tokenise*</c> benches (parse cost alone) and for tests that only need the row count. Allocation-free.
/// </summary>
public sealed class NullBatchWriter : IBatchWriter
{
    private int _rowCount;

    public int RowCount => _rowCount;

    /// <summary>Rows committed over the writer's lifetime (never resets).</summary>
    public long Rows { get; private set; }

    /// <summary>Bytes passed to <see cref="AppendString"/>.</summary>
    public long StringBytes { get; private set; }

    /// <summary>Bytes passed as <c>raw</c>: the input volume represented.</summary>
    public long RawBytes { get; private set; }

    public long Extras { get; private set; }

    public void AppendUInt64(EventColumn column, ulong value)
    {
    }

    public void AppendUInt32(EventColumn column, uint value)
    {
    }

    public void AppendUInt16(EventColumn column, ushort value)
    {
    }

    public void AppendTimestamp(long unixMilliseconds)
    {
    }

    public void AppendString(EventColumn column, ReadOnlySpan<byte> utf8)
    {
        StringBytes += utf8.Length;
        if (column == EventColumn.Raw)
        {
            RawBytes += utf8.Length;
        }
    }

    public void AppendMessage(int offset, int length)
    {
    }

    public void AppendIp(EventColumn column, ReadOnlySpan<byte> address)
    {
    }

    public void AppendExtra(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) => Extras++;

    public void CommitRow()
    {
        _rowCount++;
        Rows++;
    }

    /// <summary>Resets the per-batch row count (mirrors a flush).</summary>
    public void Flush() => _rowCount = 0;
}
