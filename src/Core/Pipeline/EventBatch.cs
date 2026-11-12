using System.Buffers.Binary;
using System.Text;
using Urlog.Core.Sources;

#pragma warning disable CA1819 // Properties should not return arrays: this type IS the column arrays. The insert path
// (the Native block writer) consumes each array whole; a copy or a wrapper per
// column would be exactly the per-batch allocation this layout exists to remove. RowCount bounds the valid prefix.

namespace Urlog.Core.Pipeline;

/// <summary>
/// One block of up to <see cref="Capacity"/> event rows as a struct of arrays, one array per pinned column, with
/// string columns as a single byte buffer plus cumulative end offsets.
/// <para>
/// <b>This is the ClickHouse Native block layout.</b> A Native <c>String</c> column is each value prefixed by its
/// varint length; <c>UInt64</c>/<c>UInt32</c>/<c>UInt16</c> columns are the little-endian arrays as they are here;
/// <c>DateTime64(3)</c> is the <c>Int64</c> milliseconds in <see cref="Ts"/>; <c>IPv6</c> is 16 bytes per row as in
/// <see cref="SourceIp"/>; <c>Map(LowCardinality(String), String)</c> is the key and value string columns plus the
/// cumulative pair offsets in <see cref="ExtrasOffsets"/> (the keys get a per-block dictionary on the wire). The logical
/// <c>message</c> is not stored: <see cref="MsgOff"/>/<see cref="MsgLen"/> locate it inside <c>raw</c>, and
/// <see cref="EventColumn.MessageText"/> holds it only when it is not a slice. The Native writer therefore serialises an <c>EventBatch</c> directly, column by
/// column, with no intermediate row objects. Keep the shape; do not add per-row objects.
/// </para>
/// Batches are pooled (<see cref="BatchPool"/>) and reused; string buffers grow in place on first use and then stay
/// (the extras columns also grow their offsets, since they hold one row per pair, not per event).
/// </summary>
public sealed class EventBatch
{
    private const int InitialStringBytes = 64 << 10;

    private readonly StringColumnBuffer[] _strings;

    internal EventBatch(BatchPool owner, int capacity)
    {
        Owner = owner;
        Capacity = capacity;
        CaseId = new ulong[capacity];
        HostId = new ulong[capacity];
        FileId = new ulong[capacity];
        FileOffset = new ulong[capacity];
        ParseGeneration = new uint[capacity];
        Ts = new long[capacity];
        EventCode = new uint[capacity];
        HttpStatus = new ushort[capacity];
        MsgOff = new uint[capacity];
        MsgLen = new uint[capacity];
        HttpResponseBodyBytes = new ulong[capacity];
        ProcessPid = new uint[capacity];
        SourceIp = new byte[capacity * 16];
        DestinationIp = new byte[capacity * 16];
        ExtrasOffsets = new int[capacity];
        _strings = new StringColumnBuffer[EventColumns.StringColumnCount];
        for (var i = 0; i < _strings.Length; i++)
        {
            _strings[i] = new StringColumnBuffer(capacity, InitialStringBytes);
        }

        ExtrasKeys = new StringColumnBuffer(capacity, InitialStringBytes);
        ExtrasValues = new StringColumnBuffer(capacity, InitialStringBytes);
    }

    internal BatchPool Owner { get; }

    /// <summary>Rows this batch can hold (<see cref="PipelineOptions.BatchRows"/>).</summary>
    public int Capacity { get; }

    /// <summary>Rows committed; arrays are valid up to this index.</summary>
    public int RowCount { get; internal set; }

    public ulong[] CaseId { get; }

    public ulong[] HostId { get; }

    public ulong[] FileId { get; }

    public ulong[] FileOffset { get; }

    public uint[] ParseGeneration { get; }

    /// <summary>UTC milliseconds since the Unix epoch, one per row (<c>DateTime64(3, 'UTC')</c>).</summary>
    public long[] Ts { get; }

    public uint[] EventCode { get; }

    public ushort[] HttpStatus { get; }

    /// <summary>Byte offset of the message inside the row's <c>raw</c> (<see cref="EventColumn.MsgOff"/>).</summary>
    public uint[] MsgOff { get; }

    /// <summary>Byte length of the message inside <c>raw</c>; 0 means the message is <see cref="EventColumn.MessageText"/>.</summary>
    public uint[] MsgLen { get; }

    public ulong[] HttpResponseBodyBytes { get; }

    public uint[] ProcessPid { get; }

    /// <summary>16 bytes per row, network order; IPv4 is stored mapped (<c>::ffff:a.b.c.d</c>).</summary>
    public byte[] SourceIp { get; }

    public byte[] DestinationIp { get; }

    /// <summary>Cumulative count of extras pairs per row: row i owns pairs <c>[ExtrasOffsets[i-1], ExtrasOffsets[i])</c>.</summary>
    public int[] ExtrasOffsets { get; }

    public StringColumnBuffer ExtrasKeys { get; }

    public StringColumnBuffer ExtrasValues { get; }

    public int ExtrasCount => RowCount == 0 ? 0 : ExtrasOffsets[RowCount - 1];

    /// <summary>Bytes of <c>raw</c> in this batch: the input volume it represents.</summary>
    public long RawBytes => Strings(EventColumn.Raw).ByteLength;

    /// <summary>The buffer of a string column (<see cref="EventColumns.IsString"/>).</summary>
    public StringColumnBuffer Strings(EventColumn column) => _strings[EventColumns.StringSlot(column)];

    /// <summary>Hands the batch back to its pool; the sink calls this when the insert is done.</summary>
    public void Return() => Owner.Return(this);

    internal StringColumnBuffer StringSlot(int slot) => _strings[slot];

    internal void Clear()
    {
        RowCount = 0;
        foreach (var s in _strings)
        {
            s.Clear();
        }

        ExtrasKeys.Clear();
        ExtrasValues.Clear();
    }
}

/// <summary>
/// A string column in Native shape: one UTF-8 byte buffer and the cumulative end offset of each row
/// (<c>row i = Bytes[Offsets[i-1]..Offsets[i])</c>, with <c>Offsets[-1] = 0</c>). The byte buffer doubles when
/// full and keeps its size afterwards, so growth is a one-time cost per pooled batch.
/// </summary>
public sealed class StringColumnBuffer
{
    private byte[] _bytes;
    private int[] _offsets;
    private int _rows;

    internal StringColumnBuffer(int capacity, int initialBytes)
    {
        _bytes = GC.AllocateUninitializedArray<byte>(initialBytes);
        _offsets = new int[capacity];
    }

    public int ByteLength { get; private set; }

    /// <summary>Rows closed so far.</summary>
    public int RowCount => _rows;

    /// <summary>The whole buffer; valid up to <see cref="ByteLength"/>.</summary>
    public byte[] Bytes => _bytes;

    /// <summary>Cumulative end offsets; valid up to <see cref="RowCount"/>.</summary>
    public int[] Offsets => _offsets;

    /// <summary>Capacity of <see cref="Bytes"/> (grows on demand).</summary>
    public int ByteCapacity => _bytes.Length;

    public ReadOnlySpan<byte> Row(int index)
    {
        var start = index == 0 ? 0 : _offsets[index - 1];
        return _bytes.AsSpan(start, _offsets[index] - start);
    }

    /// <summary>Reads row <paramref name="index"/> as a string (tests and diagnostics only).</summary>
    public string RowString(int index) => Encoding.UTF8.GetString(Row(index));

    /// <summary>Appends bytes to the pending row (the row is closed by <see cref="EndRow"/>).</summary>
    public void Append(ReadOnlySpan<byte> value)
    {
        EnsureFree(value.Length);
        value.CopyTo(_bytes.AsSpan(ByteLength));
        ByteLength += value.Length;
    }

    /// <summary>Appends <paramref name="value"/> ASCII-lowercased; non-ASCII bytes pass through unchanged.</summary>
    public void AppendLower(ReadOnlySpan<byte> value)
    {
        EnsureFree(value.Length);
        var dest = _bytes.AsSpan(ByteLength, value.Length);
        if (Ascii.ToLower(value, dest, out _) != System.Buffers.OperationStatus.Done)
        {
            // Non-ASCII content: lowercase the ASCII letters only, byte by byte, and keep the rest.
            for (var i = 0; i < value.Length; i++)
            {
                var b = value[i];
                dest[i] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
            }
        }

        ByteLength += value.Length;
    }

    /// <summary>Bytes appended to the pending (not yet closed) row.</summary>
    internal int PendingLength => ByteLength - (_rows == 0 ? 0 : _offsets[_rows - 1]);

    /// <summary>Rewinds the pending row to its start so a second write for the same column replaces the first.</summary>
    internal void ResetPending() => ByteLength = _rows == 0 ? 0 : _offsets[_rows - 1];

    /// <summary>Closes the pending row (an empty row when nothing was appended).</summary>
    internal void EndRow()
    {
        if (_rows == _offsets.Length)
        {
            // Only the extras columns get here: they hold one row per key/value pair, and a source may write several
            // pairs per event (nginx writes about five), so their offsets outgrow BatchRows. Like the byte buffer,
            // the array grows once and then stays with the pooled batch. Event columns never exceed Capacity.
            Array.Resize(ref _offsets, _offsets.Length * 2);
        }

        _offsets[_rows++] = ByteLength;
    }

    internal void Clear()
    {
        _rows = 0;
        ByteLength = 0;
    }

    private void EnsureFree(int count)
    {
        var needed = ByteLength + count;
        if (needed <= _bytes.Length)
        {
            return;
        }

        var newLength = Math.Max(needed, _bytes.Length * 2);
        var grown = GC.AllocateUninitializedArray<byte>(newLength);
        _bytes.AsSpan(0, ByteLength).CopyTo(grown);
        _bytes = grown;
    }
}

/// <summary>Helpers for the fixed-width IP storage.</summary>
public static class IpBytes
{
    /// <summary>Writes <paramref name="address"/> (4 or 16 bytes) as 16 bytes into <paramref name="dest"/>; IPv4 becomes <c>::ffff:a.b.c.d</c>.</summary>
    public static void Write(ReadOnlySpan<byte> address, Span<byte> dest)
    {
        switch (address.Length)
        {
            case 16:
                address.CopyTo(dest);
                break;
            case 4:
                dest[..10].Clear();
                BinaryPrimitives.WriteUInt16BigEndian(dest[10..], 0xFFFF);
                address.CopyTo(dest[12..]);
                break;
            default:
                throw new ArgumentException($"an address is 4 or 16 bytes, not {address.Length}", nameof(address));
        }
    }
}
