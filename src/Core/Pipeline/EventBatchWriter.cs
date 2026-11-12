using Urlog.Core.Sources;

namespace Urlog.Core.Pipeline;

/// <summary>
/// The real <see cref="IBatchWriter"/>: fills a pooled <see cref="EventBatch"/> row by row and hands a full batch to
/// the <see cref="IBatchSink"/>, then takes the next batch from the pool (blocking there is the backpressure).
/// One writer per parser thread; not thread-safe.
/// </summary>
public sealed class EventBatchWriter : IBatchWriter
{
    private const uint HostBit = 1u << (int)EventColumn.Host;
    private const uint MsgBit = 1u << (int)EventColumn.MsgLen;
    private const uint MessageTextBit = 1u << (int)EventColumn.MessageText;

    private readonly BatchPool _pool;
    private readonly IBatchSink _sink;
    private readonly CancellationToken _cancellation;
    private EventBatch? _batch;
    private uint _written; // bit per EventColumn set on the pending row
    private int _pendingExtras;
    private ulong _caseId;
    private ulong _hostId;
    private ulong _fileId;
    private uint _generation;
    private ReadOnlyMemory<byte> _defaultHost;

    /// <param name="cancellation">Observed while waiting for a batch from the pool, so a cancelled job never blocks on backpressure.</param>
    public EventBatchWriter(BatchPool pool, IBatchSink sink, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(sink);
        _pool = pool;
        _sink = sink;
        _cancellation = cancellation;
    }

    public int RowCount => _batch?.RowCount ?? 0;

    /// <summary>The batch being filled; rented on first use and after every flush.</summary>
    private EventBatch Batch => _batch ??= _pool.Rent(_cancellation);

    /// <summary>Rows committed through this writer over its lifetime.</summary>
    public long RowsCommitted { get; private set; }

    /// <summary>Batches handed to the sink.</summary>
    public long BatchesFlushed { get; private set; }

    /// <summary>Sets the identity core stamps on every row (<c>case_id</c>, <c>host_id</c>, <c>file_id</c>, <c>parse_generation</c>, default host).</summary>
    public void Bind(ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _caseId = context.CaseId;
        _hostId = context.HostId;
        _fileId = context.FileId;
        _generation = context.ParseGeneration;
        _defaultHost = context.DefaultHost;
        context.Bind(this);
    }

    public void AppendUInt64(EventColumn column, ulong value)
    {
        var batch = Batch;
        var i = batch.RowCount;
        switch (column)
        {
            case EventColumn.FileOffset:
                batch.FileOffset[i] = value;
                break;
            case EventColumn.CaseId:
                batch.CaseId[i] = value;
                break;
            case EventColumn.HostId:
                batch.HostId[i] = value;
                break;
            case EventColumn.FileId:
                batch.FileId[i] = value;
                break;
            case EventColumn.HttpResponseBodyBytes:
                batch.HttpResponseBodyBytes[i] = value;
                break;
            default:
                throw WrongKind(column, "UInt64");
        }

        _written |= 1u << (int)column;
    }

    public void AppendUInt32(EventColumn column, uint value)
    {
        var batch = Batch;
        var i = batch.RowCount;
        switch (column)
        {
            case EventColumn.EventCode:
                batch.EventCode[i] = value;
                break;
            case EventColumn.ParseGeneration:
                batch.ParseGeneration[i] = value;
                break;
            case EventColumn.ProcessPid:
                batch.ProcessPid[i] = value;
                break;
            case EventColumn.MsgOff:
            case EventColumn.MsgLen:
                throw new ArgumentException("the message slice is written with AppendMessage(offset, length)", nameof(column));
            default:
                throw WrongKind(column, "UInt32");
        }

        _written |= 1u << (int)column;
    }

    public void AppendUInt16(EventColumn column, ushort value)
    {
        if (column != EventColumn.HttpStatus)
        {
            throw WrongKind(column, "UInt16");
        }

        var batch = Batch;
        batch.HttpStatus[batch.RowCount] = value;
        _written |= 1u << (int)column;
    }

    public void AppendTimestamp(long unixMilliseconds)
    {
        var batch = Batch;
        batch.Ts[batch.RowCount] = unixMilliseconds;
        _written |= 1u << (int)EventColumn.Ts;
    }

    public void AppendMessage(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var batch = Batch;
        var i = batch.RowCount;
        batch.MsgOff[i] = (uint)offset;
        batch.MsgLen[i] = (uint)length;
        _written |= MsgBit;
    }

    public void AppendString(EventColumn column, ReadOnlySpan<byte> utf8)
    {
        var kind = EventColumns.Kind(column);
        if (kind != EventColumnKind.Text && kind != EventColumnKind.LowCardinalityText)
        {
            throw WrongKind(column, "String");
        }

        var buffer = Batch.StringSlot(EventColumns.StringSlot(column));
        var bit = 1u << (int)column;
        if ((_written & bit) != 0)
        {
            buffer.ResetPending();
        }

        if (kind == EventColumnKind.LowCardinalityText)
        {
            buffer.AppendLower(utf8);
        }
        else
        {
            buffer.Append(utf8);
        }

        _written |= bit;
    }

    public void AppendIp(EventColumn column, ReadOnlySpan<byte> address)
    {
        var batch = Batch;
        var dest = column switch
        {
            EventColumn.SourceIp => batch.SourceIp,
            EventColumn.DestinationIp => batch.DestinationIp,
            _ => throw WrongKind(column, "IPv6"),
        };
        IpBytes.Write(address, dest.AsSpan(batch.RowCount * 16, 16));
        _written |= 1u << (int)column;
    }

    public void AppendExtra(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        var batch = Batch;
        batch.ExtrasKeys.Append(key);
        batch.ExtrasKeys.EndRow();
        batch.ExtrasValues.Append(value);
        batch.ExtrasValues.EndRow();
        _pendingExtras++;
    }

    public void CommitRow()
    {
        var batch = Batch;
        var i = batch.RowCount;
        var written = _written;

        batch.CaseId[i] = _caseId;
        batch.HostId[i] = _hostId;
        batch.FileId[i] = _fileId;
        batch.ParseGeneration[i] = _generation;

        if ((written & (1u << (int)EventColumn.FileOffset)) == 0)
        {
            batch.FileOffset[i] = 0;
        }

        if ((written & (1u << (int)EventColumn.Ts)) == 0)
        {
            batch.Ts[i] = 0;
        }

        if ((written & (1u << (int)EventColumn.EventCode)) == 0)
        {
            batch.EventCode[i] = 0;
        }

        if ((written & (1u << (int)EventColumn.HttpStatus)) == 0)
        {
            batch.HttpStatus[i] = 0;
        }

        if ((written & (1u << (int)EventColumn.HttpResponseBodyBytes)) == 0)
        {
            batch.HttpResponseBodyBytes[i] = 0;
        }

        if ((written & (1u << (int)EventColumn.ProcessPid)) == 0)
        {
            batch.ProcessPid[i] = 0;
        }

        if ((written & MsgBit) == 0)
        {
            batch.MsgOff[i] = 0;
            batch.MsgLen[i] = 0;
        }
        else
        {
            // A plugin bug, not bad input: the message is either a slice of this row's raw or message_text, never both,
            // and the slice must lie inside the raw bytes written for the row.
            if ((written & MessageTextBit) != 0)
            {
                throw new InvalidOperationException("a row has either a message slice (AppendMessage) or message_text, not both");
            }

            var rawLength = batch.StringSlot(EventColumns.StringSlot(EventColumn.Raw)).PendingLength;
            if ((long)batch.MsgOff[i] + batch.MsgLen[i] > rawLength)
            {
                throw new InvalidOperationException($"message slice [{batch.MsgOff[i]}, +{batch.MsgLen[i]}) lies outside the row's {rawLength}-byte raw");
            }
        }

        if ((written & (1u << (int)EventColumn.SourceIp)) == 0)
        {
            batch.SourceIp.AsSpan(i * 16, 16).Clear();
        }

        if ((written & (1u << (int)EventColumn.DestinationIp)) == 0)
        {
            batch.DestinationIp.AsSpan(i * 16, 16).Clear();
        }

        if ((written & HostBit) == 0 && !_defaultHost.IsEmpty)
        {
            batch.StringSlot(EventColumns.StringSlot(EventColumn.Host)).AppendLower(_defaultHost.Span);
        }

        for (var slot = 0; slot < EventColumns.StringColumnCount; slot++)
        {
            batch.StringSlot(slot).EndRow();
        }

        var previousExtras = i == 0 ? 0 : batch.ExtrasOffsets[i - 1];
        batch.ExtrasOffsets[i] = previousExtras + _pendingExtras;

        batch.RowCount = i + 1;
        RowsCommitted++;
        _written = 0;
        _pendingExtras = 0;

        if (batch.RowCount == batch.Capacity)
        {
            Flush();
        }
    }

    /// <summary>
    /// Hands the current batch to the sink if it holds rows. The next row rents a fresh batch from the pool, and that
    /// rent blocks while the sink holds every batch: that is the backpressure. Call once at end of file.
    /// </summary>
    public void Flush()
    {
        var full = _batch;
        if (full is null || full.RowCount == 0)
        {
            return;
        }

        _batch = null;
        BatchesFlushed++;
        _sink.Submit(full); // the sink owns it from here and returns it to the pool when done
    }

    /// <summary>Returns the current (empty or partial) batch to the pool without submitting it. Call when a job fails. Idempotent.</summary>
    public void Discard()
    {
        var batch = _batch;
        _batch = null;
        batch?.Return();
    }

    /// <summary>Gives an empty current batch back to the pool; the writer is unusable afterwards.</summary>
    public void Close()
    {
        if (_batch is { RowCount: > 0 })
        {
            throw new InvalidOperationException("Flush or Discard before Close");
        }

        Discard();
    }

    private static ArgumentException WrongKind(EventColumn column, string expected) =>
        new($"{EventColumns.Name(column)} is {EventColumns.ClickHouseType(column)}, not {expected}", nameof(column));
}
