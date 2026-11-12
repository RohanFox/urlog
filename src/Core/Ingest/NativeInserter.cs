using System.Collections.Concurrent;
using System.Text;
using Urlog.Core.Ingest.Native;
using Urlog.Core.Pipeline;

namespace Urlog.Core.Ingest;

/// <summary>
/// The ClickHouse sink of the parse pipeline (<see cref="IBatchSink"/>): <see cref="NativeInserterOptions.Writers"/>
/// dedicated threads, one <see cref="NativeConnection"/> each, fed by a bounded queue. <see cref="Submit"/> blocks
/// while the queue is full: that is the pipeline's backpressure. Each writer keeps one INSERT open and
/// streams batches into it as Data packets until <see cref="NativeInserterOptions.MaxRowsPerInsert"/> rows or
/// <see cref="NativeInserterOptions.InsertIdleTimeout"/> of silence, then ends it and waits for the acknowledgement.
/// <para>
/// <b>A batch goes back to the pool as soon as it is serialised</b>, not when the insert is acknowledged: its bytes
/// are in the socket by then, and a failed insert fails the job, whose recovery is <c>DELETE WHERE file_id</c> and
/// a full re-parse (design record, "Consistency"; no chunk resume). Pool memory therefore stays at the pipeline's formula
/// with 500k-row parts. The first failure is kept, every later <see cref="Submit"/> throws it, queued batches are
/// returned, and <see cref="CompleteAsync"/> rethrows it. <see cref="Rows"/> counts acknowledged rows only.
/// </para>
/// </summary>
public sealed class NativeInserter : IBatchSink, IAsyncDisposable
{
    private readonly NativeInserterOptions _options;
    private readonly byte[] _sql;
    private readonly BlockingCollection<EventBatch> _queue;
    private readonly Task[] _writers;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _failureLock = new();
    private readonly bool _returnBatches;
    private Exception? _failure;
    private long _rows;
    private long _blocks;
    private long _inserts;
    private long _bytesSent;
    private long _payloadBytes;
    private long _blocksSorted;
    private bool _completed;

    /// <param name="queueCapacity">Batches that may wait for a writer; default <c>max(1, pipeline.InFlightBatches - Writers)</c> when <paramref name="pipeline"/> is given, else 1.</param>
    /// <param name="returnBatches">True (the pipeline): a batch goes back to its pool once serialised. False (benches, tests): the caller keeps its batches.</param>
    public NativeInserter(NativeInserterOptions options, PipelineOptions? pipeline = null, int? queueCapacity = null, bool returnBatches = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _returnBatches = returnBatches;
        _sql = Encoding.UTF8.GetBytes($"INSERT INTO {options.Table} VALUES");
        var capacity = queueCapacity ?? (pipeline is null ? 1 : Math.Max(1, pipeline.InFlightBatches - options.Writers));
        _queue = new BlockingCollection<EventBatch>(capacity);
        _writers = new Task[options.Writers];
        for (var w = 0; w < options.Writers; w++)
        {
            _writers[w] = Task.Factory.StartNew(WriterLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    public NativeInserterOptions Options => _options;

    /// <summary>Rows acknowledged by the server.</summary>
    public long Rows => Interlocked.Read(ref _rows);

    /// <summary>Batches serialised and returned to the pool.</summary>
    public long Blocks => Interlocked.Read(ref _blocks);

    /// <summary>INSERT queries acknowledged.</summary>
    public long Inserts => Interlocked.Read(ref _inserts);

    /// <summary>Blocks that were not in sort-key order and were permuted on the client (<see cref="NativeInserterOptions.SortBlocks"/>).</summary>
    public long BlocksSorted => Interlocked.Read(ref _blocksSorted);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public long PayloadBytes => Interlocked.Read(ref _payloadBytes);

    public Exception? Failure
    {
        get
        {
            lock (_failureLock)
            {
                return _failure;
            }
        }
    }

    public void Submit(EventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var failure = Failure;
        if (failure is not null)
        {
            Release(batch);
            throw new ClickHouseInsertException("the insert path failed earlier; the job must restart", failure);
        }

        try
        {
            _queue.Add(batch, _stop.Token); // blocks while every writer is busy and the queue is full: backpressure
        }
        catch (OperationCanceledException)
        {
            Release(batch);
            throw new ClickHouseInsertException("the insert path failed earlier; the job must restart", Failure ?? new ClickHouseInsertException("inserter stopped"));
        }
        catch (InvalidOperationException e)
        {
            Release(batch);
            throw new ClickHouseInsertException("the inserter is already completed", e);
        }
    }

    /// <summary>Ends every open INSERT, waits for the acknowledgements and rethrows the first failure.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_completed)
        {
            _completed = true;
            _queue.CompleteAdding();
        }

        await Task.WhenAll(_writers).WaitAsync(cancellationToken).ConfigureAwait(false);
        var failure = Failure;
        if (failure is not null)
        {
            throw failure is ClickHouseInsertException ready ? ready : new ClickHouseInsertException("insert path failed", failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            _completed = true;
            _queue.CompleteAdding();
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_writers).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // reported through Failure; disposal only waits
        }

        while (_queue.TryTake(out var leftover))
        {
            Release(leftover);
        }

        _queue.Dispose();
        _stop.Dispose();
    }

    private void Release(EventBatch batch)
    {
        if (_returnBatches)
        {
            batch.Return();
        }
    }

    private void Fail(Exception e)
    {
        lock (_failureLock)
        {
            _failure ??= e;
        }

        _stop.Cancel();
    }

    private void WriterLoop()
    {
        NativeConnection? connection = null;
        var serializer = new NativeBlockSerializer();
        var scratch = Array.Empty<int>();
        var rowsInInsert = 0;
        var idleMs = (int)_options.InsertIdleTimeout.TotalMilliseconds;
        try
        {
            while (true)
            {
                EventBatch? batch;
                bool got;
                try
                {
                    got = _queue.TryTake(out batch, idleMs, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!got)
                {
                    if (connection is { InsertOpen: true })
                    {
                        connection.EndInsert();
                        Account(ref rowsInInsert);
                    }

                    if (_queue.IsCompleted)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    connection ??= NativeConnection.Connect(_options);
                    if (!connection.InsertOpen)
                    {
                        connection.BeginInsert(_sql);
                        rowsInInsert = 0;
                    }

                    ReadOnlySpan<int> order = default;
                    if (_options.SortBlocks && !BlockOrder.IsSorted(batch!))
                    {
                        var n = BlockOrder.Compute(batch!, ref scratch);
                        order = scratch.AsSpan(0, n);
                        Interlocked.Increment(ref _blocksSorted);
                    }

                    connection.WriteBlock(serializer, batch!, order);
                    rowsInInsert += batch!.RowCount;
                    Interlocked.Increment(ref _blocks);
                }
                finally
                {
                    Release(batch!);
                }

                if (rowsInInsert >= _options.MaxRowsPerInsert)
                {
                    connection.EndInsert();
                    Account(ref rowsInInsert);
                }
            }

            if (connection is { InsertOpen: true })
            {
                connection.EndInsert();
                Account(ref rowsInInsert);
            }
        }
        catch (Exception e)
        {
            Fail(e);
            // Keep the parsers from blocking on a dead sink: hand every queued batch back.
            while (_queue.TryTake(out var leftover))
            {
                Release(leftover);
            }
        }
        finally
        {
            if (connection is not null)
            {
                Interlocked.Add(ref _bytesSent, connection.BytesSent);
                Interlocked.Add(ref _payloadBytes, connection.PayloadBytes);
                connection.Dispose();
            }
        }
    }

    private void Account(ref int rowsInInsert)
    {
        Interlocked.Add(ref _rows, rowsInInsert);
        Interlocked.Increment(ref _inserts);
        rowsInInsert = 0;
    }
}
