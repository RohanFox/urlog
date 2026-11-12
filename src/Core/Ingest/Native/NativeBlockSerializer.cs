using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Ingest.Native;

/// <summary>
/// Serialises an <see cref="EventBatch"/> as one Native-format Data packet, column by column, straight from the
/// batch's arrays: no row objects, no <c>byte[]</c> per row. Numeric columns, timestamps and IPs are copied as the
/// little-endian arrays they already are; String columns are the batch's byte buffer cut by its offsets with a
/// varint length in front of each row; LowCardinality columns get a dictionary built per block (shared dictionaries
/// with additional keys, the serialisation clickhouse-client writes); Map is the LowCardinality prefix of its key
/// column, the extras pair offsets, then the keys as a LowCardinality column and the values as a String column (the
/// stream order the server's Array(Tuple(LowCardinality(String), String)) serialisation uses). An optional row <c>order</c> (see <see cref="BlockOrder"/>) writes the rows in that
/// order without touching the batch: a gather instead of a copy. The only per-block state is the dictionary and the
/// index scratch, reused. Not thread-safe: one per writer thread.
/// </summary>
public sealed class NativeBlockSerializer
{
    private const ulong SharedDictionariesWithAdditionalKeys = 1;
    private const ulong HasAdditionalKeysBit = 1UL << 9;
    private const ulong NeedUpdateDictionaryBit = 1UL << 10;
    private const int Chunk = 512;

    private static readonly byte[][] Names = Build(EventColumns.Name);
    private static readonly byte[][] Types = Build(EventColumns.ClickHouseType);

    private readonly LowCardinalityDictionary _dictionary = new();
    private readonly long[] _columnStarts = new long[EventColumns.Count];
    private int[] _indexes = [];
    private int[] _pairOrder = [];

    /// <summary>Payload offset (bytes from the packet's payload start) at which each column's data began in the last block. Diagnostics and tests.</summary>
    public ReadOnlySpan<long> LastColumnPayloadStarts => _columnStarts;

    /// <summary>Block info as clickhouse-client writes it: is_overflows = 0, bucket_num = -1.</summary>
    public static void WriteBlockInfo(ProtocolWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteVarUInt(1);
        w.WriteByte(0);
        w.WriteVarUInt(2);
        w.WriteInt32(-1);
        w.WriteVarUInt(0);
    }

    /// <summary>Writes <paramref name="batch"/> as a complete Data packet (packet type, empty table name, block), rows in <paramref name="order"/> when given.</summary>
    public void WriteBlock(ProtocolWriter w, EventBatch batch, ReadOnlySpan<int> order = default)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(batch);
        var rows = batch.RowCount;
        if (!order.IsEmpty && order.Length != rows)
        {
            throw new ArgumentException($"order has {order.Length} entries for {rows} rows", nameof(order));
        }

        w.RawVarUInt(2); // ClientPacket.Data
        w.RawString(default);
        var payloadStart = w.PayloadPosition;
        WriteBlockInfo(w);
        w.WriteVarUInt(EventColumns.Count);
        w.WriteVarUInt((ulong)rows);

        for (var c = 0; c < EventColumns.Count; c++)
        {
            var column = (EventColumn)c;
            w.WriteString(Names[c]);
            w.WriteString(Types[c]);
            _columnStarts[c] = w.PayloadPosition - payloadStart;
            if (rows == 0)
            {
                continue;
            }

            switch (EventColumns.Kind(column))
            {
                case EventColumnKind.Unsigned64:
                    WriteFixed(w, Unsigned64(batch, column), rows, order);
                    break;
                case EventColumnKind.Unsigned32:
                    WriteFixed(w, Unsigned32(batch, column), rows, order);
                    break;
                case EventColumnKind.Unsigned16:
                    WriteFixed(w, batch.HttpStatus, rows, order);
                    break;
                case EventColumnKind.Timestamp:
                    WriteFixed(w, batch.Ts, rows, order);
                    break;
                case EventColumnKind.IpAddress:
                    WriteIps(w, column == EventColumn.SourceIp ? batch.SourceIp : batch.DestinationIp, rows, order);
                    break;
                case EventColumnKind.Text:
                    WriteStrings(w, batch.Strings(column), rows, order);
                    break;
                case EventColumnKind.LowCardinalityText:
                    WriteLowCardinality(w, batch.Strings(column), rows, order);
                    break;
                case EventColumnKind.Map:
                    WriteExtras(w, batch, rows, order);
                    break;
                default:
                    throw new InvalidOperationException($"unknown column kind for {EventColumns.Name(column)}");
            }
        }

        w.EndPacket();
    }

    private static ulong[] Unsigned64(EventBatch batch, EventColumn column) => column switch
    {
        EventColumn.CaseId => batch.CaseId,
        EventColumn.HostId => batch.HostId,
        EventColumn.FileId => batch.FileId,
        EventColumn.FileOffset => batch.FileOffset,
        EventColumn.HttpResponseBodyBytes => batch.HttpResponseBodyBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    private static uint[] Unsigned32(EventBatch batch, EventColumn column) => column switch
    {
        EventColumn.ParseGeneration => batch.ParseGeneration,
        EventColumn.EventCode => batch.EventCode,
        EventColumn.MsgOff => batch.MsgOff,
        EventColumn.MsgLen => batch.MsgLen,
        EventColumn.ProcessPid => batch.ProcessPid,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    /// <summary>A fixed-width column: the array as it is, or gathered through <paramref name="order"/>.</summary>
    private static void WriteFixed<T>(ProtocolWriter w, T[] values, int rows, ReadOnlySpan<int> order)
        where T : unmanaged
    {
        if (order.IsEmpty)
        {
            w.Write(MemoryMarshal.AsBytes(values.AsSpan(0, rows)));
            return;
        }

        var size = Marshal.SizeOf<T>();
        for (var i = 0; i < rows; i += Chunk)
        {
            var n = Math.Min(Chunk, rows - i);
            var span = MemoryMarshal.Cast<byte, T>(w.GetSpan(n * size));
            for (var j = 0; j < n; j++)
            {
                span[j] = values[order[i + j]];
            }

            w.Advance(n * size);
        }
    }

    private static void WriteIps(ProtocolWriter w, byte[] packed, int rows, ReadOnlySpan<int> order)
    {
        if (order.IsEmpty)
        {
            w.Write(packed.AsSpan(0, rows * 16));
            return;
        }

        for (var i = 0; i < rows; i += Chunk)
        {
            var n = Math.Min(Chunk, rows - i);
            var span = w.GetSpan(n * 16);
            for (var j = 0; j < n; j++)
            {
                packed.AsSpan(order[i + j] * 16, 16).CopyTo(span[(j * 16)..]);
            }

            w.Advance(n * 16);
        }
    }

    /// <summary>String column: varint length + bytes per row, from the buffer and the cumulative offsets.</summary>
    private static void WriteStrings(ProtocolWriter w, StringColumnBuffer column, int rows, ReadOnlySpan<int> order)
    {
        var bytes = column.Bytes;
        var offsets = column.Offsets;
        for (var i = 0; i < rows; i++)
        {
            var r = order.IsEmpty ? i : order[i];
            var start = r == 0 ? 0 : offsets[r - 1];
            WriteString(w, bytes, start, offsets[r] - start);
        }
    }

    private static void WriteString(ProtocolWriter w, byte[] bytes, int start, int length)
    {
        if (length <= 4096)
        {
            var span = w.GetSpan(length + 5);
            var n = ProtocolWriter.EncodeVarUInt((ulong)length, span);
            bytes.AsSpan(start, length).CopyTo(span[n..]);
            w.Advance(n + length);
        }
        else
        {
            w.WriteVarUInt((ulong)length);
            w.Write(bytes.AsSpan(start, length));
        }
    }

    private void WriteLowCardinality(ProtocolWriter w, StringColumnBuffer column, int rows, ReadOnlySpan<int> order)
    {
        w.WriteUInt64(SharedDictionariesWithAdditionalKeys); // the column's serialisation prefix (keys version)
        WriteLowCardinalityBody(w, column, rows, order);
    }

    /// <summary>The dictionary and the indexes of a LowCardinality column, after its prefix; <paramref name="rows"/> rows of <paramref name="column"/> in <paramref name="order"/>.</summary>
    private void WriteLowCardinalityBody(ProtocolWriter w, StringColumnBuffer column, int rows, ReadOnlySpan<int> order)
    {
        if (rows == 0)
        {
            return; // the server writes (and reads) nothing for an empty column: a block whose maps are all empty
        }

        var bytes = column.Bytes;
        var offsets = column.Offsets;
        if (_indexes.Length < rows)
        {
            _indexes = new int[Math.Max(rows, _indexes.Length * 2)];
        }

        var dictionary = _dictionary;
        dictionary.Reset(bytes);
        var indexes = _indexes;
        var start = 0;
        var previousStart = 0;
        var previousLength = -1;
        var previousIndex = 0;
        for (var i = 0; i < rows; i++)
        {
            var end = offsets[i];
            var length = end - start;
            int index;
            if (length == previousLength && bytes.AsSpan(start, length).SequenceEqual(bytes.AsSpan(previousStart, length)))
            {
                index = previousIndex; // sorted blocks: the host repeats for long runs, program and severity nearly always
            }
            else
            {
                index = dictionary.GetOrAdd(start, length);
                previousStart = start;
                previousLength = length;
                previousIndex = index;
            }

            indexes[i] = index;
            start = end;
        }

        var keys = dictionary.Count;
        var keyType = keys <= 256 ? 0UL : keys <= 65_536 ? 1UL : 2UL;
        w.WriteUInt64(keyType | HasAdditionalKeysBit | NeedUpdateDictionaryBit);
        w.WriteUInt64((ulong)keys);
        for (var k = 0; k < keys; k++)
        {
            WriteString(w, bytes, dictionary.Start(k), dictionary.Length(k));
        }

        w.WriteUInt64((ulong)rows);
        const int indexChunk = 4096;
        switch (keyType)
        {
            case 0:
                for (var i = 0; i < rows; i += indexChunk)
                {
                    var n = Math.Min(indexChunk, rows - i);
                    var span = w.GetSpan(n);
                    for (var j = 0; j < n; j++)
                    {
                        span[j] = (byte)indexes[order.IsEmpty ? i + j : order[i + j]];
                    }

                    w.Advance(n);
                }

                break;
            case 1:
                for (var i = 0; i < rows; i += indexChunk)
                {
                    var n = Math.Min(indexChunk, rows - i);
                    var span = w.GetSpan(n * 2);
                    for (var j = 0; j < n; j++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(span[(j * 2)..], (ushort)indexes[order.IsEmpty ? i + j : order[i + j]]);
                    }

                    w.Advance(n * 2);
                }

                break;
            default:
                if (order.IsEmpty)
                {
                    w.Write(MemoryMarshal.AsBytes(indexes.AsSpan(0, rows)));
                }
                else
                {
                    for (var i = 0; i < rows; i += indexChunk)
                    {
                        var n = Math.Min(indexChunk, rows - i);
                        var span = MemoryMarshal.Cast<byte, int>(w.GetSpan(n * 4));
                        for (var j = 0; j < n; j++)
                        {
                            span[j] = indexes[order[i + j]];
                        }

                        w.Advance(n * 4);
                    }
                }

                break;
        }
    }

    private void WriteExtras(ProtocolWriter w, EventBatch batch, int rows, ReadOnlySpan<int> order)
    {
        var offsets = batch.ExtrasOffsets;
        w.WriteUInt64(SharedDictionariesWithAdditionalKeys); // prefix of the nested LowCardinality key column, before the sizes
        if (order.IsEmpty)
        {
            for (var i = 0; i < rows; i += Chunk)
            {
                var n = Math.Min(Chunk, rows - i);
                var span = w.GetSpan(n * 8);
                for (var j = 0; j < n; j++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(span[(j * 8)..], (ulong)offsets[i + j]);
                }

                w.Advance(n * 8);
            }

            var pairs = batch.ExtrasCount;
            WriteLowCardinalityBody(w, batch.ExtrasKeys, pairs, default);
            WriteStrings(w, batch.ExtrasValues, pairs, default);
            return;
        }

        // Reordered rows: cumulative pair counts in the new order, then each row's pairs in the new order.
        ulong running = 0;
        for (var i = 0; i < rows; i += Chunk)
        {
            var n = Math.Min(Chunk, rows - i);
            var span = w.GetSpan(n * 8);
            for (var j = 0; j < n; j++)
            {
                var r = order[i + j];
                running += (ulong)(offsets[r] - (r == 0 ? 0 : offsets[r - 1]));
                BinaryPrimitives.WriteUInt64LittleEndian(span[(j * 8)..], running);
            }

            w.Advance(n * 8);
        }

        var total = (int)running;
        if (_pairOrder.Length < total)
        {
            _pairOrder = new int[Math.Max(total, _pairOrder.Length * 2)];
        }

        var pairOrder = _pairOrder;
        var next = 0;
        for (var i = 0; i < rows; i++)
        {
            var r = order[i];
            for (var p = r == 0 ? 0 : offsets[r - 1]; p < offsets[r]; p++)
            {
                pairOrder[next++] = p;
            }
        }

        WriteLowCardinalityBody(w, batch.ExtrasKeys, total, pairOrder.AsSpan(0, total));
        WriteStrings(w, batch.ExtrasValues, total, pairOrder.AsSpan(0, total));
    }

    private static byte[][] Build(Func<EventColumn, string> text)
    {
        var result = new byte[EventColumns.Count][];
        for (var c = 0; c < EventColumns.Count; c++)
        {
            result[c] = Encoding.UTF8.GetBytes(text((EventColumn)c));
        }

        return result;
    }
}

/// <summary>
/// Distinct values of one LowCardinality column within a block, keyed by content, storing only (start, length)
/// into the column's own byte buffer. Open addressing, grows by doubling; cleared per block.
/// </summary>
internal sealed class LowCardinalityDictionary
{
    private byte[] _bytes = [];
    private int[] _slots = new int[1024]; // entry index + 1; 0 = empty
    private int[] _start = new int[256];
    private int[] _length = new int[256];
    private uint[] _hash = new uint[256];
    private int _count;

    public int Count => _count;

    public int Start(int key) => _start[key];

    public int Length(int key) => _length[key];

    public void Reset(byte[] bytes)
    {
        _bytes = bytes;
        if (_count > 0)
        {
            Array.Clear(_slots);
        }

        _count = 0;
    }

    public int GetOrAdd(int start, int length)
    {
        var span = _bytes.AsSpan(start, length);
        var hash = Hash(span);
        var mask = _slots.Length - 1;
        var i = (int)(hash & (uint)mask);
        while (true)
        {
            var entry = _slots[i] - 1;
            if (entry < 0)
            {
                return Add(start, length, hash, i);
            }

            if (_hash[entry] == hash && _length[entry] == length && _bytes.AsSpan(_start[entry], length).SequenceEqual(span))
            {
                return entry;
            }

            i = (i + 1) & mask;
        }
    }

    private int Add(int start, int length, uint hash, int slot)
    {
        if (_count == _start.Length)
        {
            Array.Resize(ref _start, _count * 2);
            Array.Resize(ref _length, _count * 2);
            Array.Resize(ref _hash, _count * 2);
        }

        var entry = _count++;
        _start[entry] = start;
        _length[entry] = length;
        _hash[entry] = hash;
        _slots[slot] = entry + 1;
        if (_count * 2 > _slots.Length)
        {
            Rehash();
        }

        return entry;
    }

    private void Rehash()
    {
        var slots = new int[_slots.Length * 2];
        var mask = slots.Length - 1;
        for (var entry = 0; entry < _count; entry++)
        {
            var i = (int)(_hash[entry] & (uint)mask);
            while (slots[i] != 0)
            {
                i = (i + 1) & mask;
            }

            slots[i] = entry + 1;
        }

        _slots = slots;
    }

    private static uint Hash(ReadOnlySpan<byte> s)
    {
        var h = 0x9E3779B97F4A7C15UL ^ (ulong)s.Length;
        while (s.Length >= 8)
        {
            h = (h ^ BinaryPrimitives.ReadUInt64LittleEndian(s)) * 0xBF58476D1CE4E5B9UL;
            h ^= h >> 31;
            s = s[8..];
        }

        if (!s.IsEmpty)
        {
            ulong tail = 0;
            for (var i = 0; i < s.Length; i++)
            {
                tail |= (ulong)s[i] << (8 * i);
            }

            h = (h ^ tail) * 0x94D049BB133111EBUL;
            h ^= h >> 29;
        }

        return (uint)(h ^ (h >> 32));
    }
}

/// <summary>
/// Row order of a block by the table's sort key <c>(case_id, host, ts)</c>, with <c>file_offset</c> as the final
/// tie-break so the permutation is a pure function of the block (same input, same rows: design record, "Throughput SLO and benchmark harness").
/// A block that is already sorted (nginx: one host, time-ordered lines) costs one comparison pass and no permutation.
/// </summary>
public static class BlockOrder
{
    /// <summary>True when the rows are already in sort-key order.</summary>
    public static bool IsSorted(EventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var host = batch.Strings(EventColumn.Host);
        for (var i = 1; i < batch.RowCount; i++)
        {
            if (Compare(batch, host, i - 1, i) > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Fills <paramref name="scratch"/> (grown when needed) with the permutation that sorts the block and returns its length, <c>RowCount</c>.</summary>
    public static int Compute(EventBatch batch, ref int[] scratch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(scratch);
        var rows = batch.RowCount;
        if (scratch.Length < rows)
        {
            scratch = new int[Math.Max(rows, scratch.Length * 2)];
        }

        for (var i = 0; i < rows; i++)
        {
            scratch[i] = i;
        }

        Array.Sort(scratch, 0, rows, new RowComparer(batch));
        return rows;
    }

    private static int Compare(EventBatch batch, StringColumnBuffer host, int a, int b)
    {
        var c = batch.CaseId[a].CompareTo(batch.CaseId[b]);
        if (c != 0)
        {
            return c;
        }

        c = host.Row(a).SequenceCompareTo(host.Row(b));
        if (c != 0)
        {
            return c;
        }

        c = batch.Ts[a].CompareTo(batch.Ts[b]);
        return c != 0 ? c : batch.FileOffset[a].CompareTo(batch.FileOffset[b]);
    }

    private sealed class RowComparer(EventBatch batch) : IComparer<int>
    {
        private readonly StringColumnBuffer _host = batch.Strings(EventColumn.Host);

        public int Compare(int x, int y) => BlockOrder.Compare(batch, _host, x, y);
    }
}
