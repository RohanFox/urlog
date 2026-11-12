using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using Urlog.Core.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Urlog.Core.Pipeline;

/// <summary>Who the rows belong to. <paramref name="DefaultHost"/> is lowercased on the way in.</summary>
/// <param name="Options">Per-file source options (<see cref="ParseContext.Options"/>), for example <c>nginx.log_format</c>.</param>
public sealed record FileIdentity(ulong CaseId, ulong HostId, ulong FileId, uint ParseGeneration, string DefaultHost, TimeSpan TimeZoneHint = default, int YearHint = 1970, IReadOnlyDictionary<string, string>? Options = null, DateTime? FileTime = null);

public sealed record PipelineResult(long Rows, long Rejects, int Chunks, long Bytes, int ForcedSplits, TimeSpan Elapsed);

/// <summary>A parser or the reader failed; the whole file is re-parsed after <c>DELETE WHERE file_id</c> (no chunk resume by design).</summary>
public sealed class PipelineException : Exception
{
    public PipelineException()
    {
    }

    public PipelineException(string message)
        : base(message)
    {
    }

    public PipelineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reader -> bounded chunk queue -> parser threads -> <see cref="IBatchSink"/>. Parallelism is within the file: chunks
/// of an unordered source are parsed on <see cref="PipelineOptions.Parallelism"/> threads, a
/// <see cref="SourceInfo.Stateful"/> source on one, in order. The queue is sized so the reader, the queue and the
/// parsers together never hold more than <see cref="PipelineOptions.InFlightChunks"/> buffers, which is the chunk
/// pool's size; batches come from the batch pool and go to the sink, which returns them.
/// <para>A failed run reports the first failure in time. Each task records its own failure before it cancels the
/// others; a failure that loses that race is logged at debug level with its chunk context and not thrown.</para>
/// </summary>
public sealed class ParsePipeline
{
    private static readonly Action<ILogger, string, ulong, string, Exception?> LogLaterFailure = LoggerMessage.Define<string, ulong, string>(
        LogLevel.Debug,
        new EventId(1, "PipelineLaterFailure"),
        "{Source}: file {FileId}, {Where}: failed after the first failure of the run; not reported");

    private readonly PipelineBuffers _buffers;
    private readonly ILogger _logger;
    private RunState? _lastRun;

    public ParsePipeline(PipelineBuffers buffers, ILogger<ParsePipeline>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        _buffers = buffers;
        _logger = logger ?? NullLogger<ParsePipeline>.Instance;
    }

    public PipelineOptions Options => _buffers.Options;

    /// <summary>The first failure recorded by the most recently started run, or null. For tests that order failures.</summary>
    internal Exception? FirstFailure => Volatile.Read(ref _lastRun)?.First;

    /// <summary>Parses one decoded stream (see <see cref="InputDecoding.OpenDecoded"/>) to completion.</summary>
    public Task<PipelineResult> RunAsync(Stream decodedInput, ILogSource source, FileIdentity file, IBatchSink sink, CancellationToken cancellationToken = default) =>
        RunAsync(decodedInput, source, file, sink, null, cancellationToken);

    /// <summary>As above, with live counters for the job runner: <paramref name="progress"/> is updated once per chunk, never per row.</summary>
    public async Task<PipelineResult> RunAsync(Stream decodedInput, ILogSource source, FileIdentity file, IBatchSink sink, PipelineProgress? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decodedInput);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(sink);

        var options = _buffers.Options;
        var parallelism = source.Info.Stateful ? 1 : options.Parallelism;
        var queueCapacity = options.InFlightChunks - 1 - parallelism; // reader holds one, each parser holds one
        var channel = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(queueCapacity)
        {
            SingleWriter = true,
            SingleReader = parallelism == 1,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var defaultHost = Encoding.UTF8.GetBytes(file.DefaultHost.ToLowerInvariant());
        var stopwatch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;

        var run = new RunState();
        Volatile.Write(ref _lastRun, run);
        var readerStats = new ReaderStats();
        var reader = Task.Run(() => ReadAsync(decodedInput, source, file, channel.Writer, readerStats, progress, run, token), CancellationToken.None);

        var parsers = new Task<ParserStats>[parallelism];
        for (var p = 0; p < parallelism; p++)
        {
            parsers[p] = Task.Run(() => ParseAsync(channel.Reader, source, file, defaultHost, sink, progress, run, cts, token), CancellationToken.None);
        }

        // A failing task records its failure and stops the others itself, so waiting for all of them is enough. The
        // tasks' own exceptions are not read: Task.WhenAll reports by task order, the run reports by time.
        await Task.WhenAll(parsers.Concat<Task>([reader]).Select(Quiet)).ConfigureAwait(false);

        // Whatever happened, return every chunk still queued, so the pools stay balanced.
        while (channel.Reader.TryRead(out var leftover))
        {
            leftover.Return();
        }

        var failure = run.First;
        if (failure is not null)
        {
            if (failure is PipelineException || (failure is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            throw new PipelineException($"{source.Info.Id}: file {file.FileId} failed: {failure.Message}", failure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await sink.CompleteAsync(cancellationToken).ConfigureAwait(false);

        long rows = 0, rejects = 0;
        foreach (var p in parsers)
        {
            rows += p.Result.Rows;
            rejects += p.Result.Rejects;
        }

        return new PipelineResult(rows, rejects, readerStats.Chunks, readerStats.Bytes, readerStats.ForcedSplits, stopwatch.Elapsed);
    }

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Recorded by the task itself (RunState); here we only wait for it to end.
        }
    }

    private async Task ReadAsync(Stream input, ILogSource source, FileIdentity file, ChannelWriter<Chunk> writer, ReaderStats stats, PipelineProgress? progress, RunState run, CancellationToken token)
    {
        try
        {
            var reader = new ChunkReader(_buffers.Chunks, _buffers.Options);
            foreach (var chunk in reader.Read(input, source.Framing))
            {
                stats.Chunks++;
                stats.Bytes += chunk.Length;
                if (chunk.ForcedSplit)
                {
                    stats.ForcedSplits++;
                }

                progress?.ChunkRead(chunk.Length);

                // TryWrite answers at once whether the queue took the chunk, so ownership is never in doubt. A
                // cancelled WriteAsync is not that answer: on .NET 10.0.14 it can throw after the item was queued
                // (tools/repro-channel-cancel), and the reader then returned a chunk a parser was about to read.
                try
                {
                    while (!writer.TryWrite(chunk))
                    {
                        if (!await writer.WaitToWriteAsync(token).ConfigureAwait(false))
                        {
                            throw new ChannelClosedException();
                        }
                    }
                }
                catch
                {
                    chunk.Return(); // TryWrite said no: still the reader's
                    throw;
                }
            }

            writer.Complete();
        }
        catch (Exception e)
        {
            if (!run.TryRecord(e))
            {
                LogIfLaterFailure(source, file, "reader", e, run);
            }

            writer.TryComplete(e);
            throw;
        }
    }

    private async Task<ParserStats> ParseAsync(ChannelReader<Chunk> reader, ILogSource source, FileIdentity file, byte[] defaultHost, IBatchSink sink, PipelineProgress? progress, RunState run, CancellationTokenSource cts, CancellationToken token)
    {
        var writer = new EventBatchWriter(_buffers.Batches, sink, token);
        var ctx = new ParseContext
        {
            CaseId = file.CaseId,
            HostId = file.HostId,
            FileId = file.FileId,
            ParseGeneration = file.ParseGeneration,
            DefaultHost = defaultHost,
            TimeZoneHint = file.TimeZoneHint,
            YearHint = file.YearHint,
            Options = file.Options,
            FileTime = file.FileTime,
        };
        writer.Bind(ctx);
        Chunk? current = null;
        long rowsSeen = 0, rejectsSeen = 0;
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                current = chunk;
                ctx.ChunkFileOffset = chunk.FileOffset;
                ctx.ChunkIndex = chunk.Index;
                ctx.ChunkForcedSplit = chunk.ForcedSplit;
                ctx.ChunkContinuesSplit = chunk.ContinuesSplit;
                source.Parse(chunk.Sequence, ctx, writer);
                chunk.Return();
                current = null;
                if (progress is not null)
                {
                    progress.ChunkParsed(writer.RowsCommitted - rowsSeen, ctx.Rejects - rejectsSeen);
                    rowsSeen = writer.RowsCommitted;
                    rejectsSeen = ctx.Rejects;
                }
            }

            writer.Flush();
            writer.Close();
            return new ParserStats(writer.RowsCommitted, ctx.Rejects);
        }
        catch (Exception e)
        {
            var where = "between chunks";
            var report = e;
            if (current is not null)
            {
                where = $"chunk {current.Index} at offset {current.FileOffset}";
                if (e is not OperationCanceledException)
                {
                    report = new PipelineException($"{source.Info.Id}: file {file.FileId}, {where}: {e.GetType().Name}: {e.Message}", e);
                }
            }

            // Record before cancelling: whatever the cancellation shakes out of the other tasks comes second.
            if (!run.TryRecord(report))
            {
                LogIfLaterFailure(source, file, where, e, run);
            }

            await cts.CancelAsync().ConfigureAwait(false);
            writer.Discard();
            current?.Return();
            if (ReferenceEquals(report, e))
            {
                throw;
            }

            throw report;
        }
    }

    /// <summary>Cancellation, and the reader's failure arriving through the queue, echo the first failure; they are not logged.</summary>
    private void LogIfLaterFailure(ILogSource source, FileIdentity file, string where, Exception e, RunState run)
    {
        if (e is OperationCanceledException || ReferenceEquals(e, run.First))
        {
            return;
        }

        LogLaterFailure(_logger, source.Info.Id, file.FileId, where, e);
    }

    /// <summary>The first failure of a run, set once by whichever task fails first.</summary>
    private sealed class RunState
    {
        private Exception? _first;

        public Exception? First => Volatile.Read(ref _first);

        public bool TryRecord(Exception failure) => Interlocked.CompareExchange(ref _first, failure, null) is null;
    }

    private sealed class ReaderStats
    {
        public int Chunks { get; set; }

        public long Bytes { get; set; }

        public int ForcedSplits { get; set; }
    }

    private readonly record struct ParserStats(long Rows, long Rejects);
}
