using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Microsoft.Extensions.Logging;

namespace Urlog.Core.Tests;

/// <summary>
/// Which failure a run reports and who returns which chunk, with the interleaving forced by gates: a source that
/// holds its parsers inside <c>Parse</c> and a stream that fails on cue. The stress loop at the end is explicit
/// (<c>tools/stress-pipeline.ps1</c>) and stays out of the default run.
/// </summary>
public sealed class ParsePipelineFailureTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);
    private static readonly FileIdentity File1 = new(CaseId: 1, HostId: 2, FileId: 3, ParseGeneration: 1, DefaultHost: "web-01");

    private static PipelineOptions Options(int parallelism, int extraChunks = 3) => new()
    {
        ChunkBytes = 4096,
        MaxRecordBytes = 64 << 10,
        BatchRows = 500,
        Parallelism = parallelism,
        InFlightChunks = parallelism + extraChunks,
        InFlightBatches = 2,
    };

    [Fact]
    public async Task TheReadersFailureIsReportedWhenAParserFailsAfterIt()
    {
        var (data, _) = TestData.NumberedLines(64 << 10, seed: 21);
        var buffers = new PipelineBuffers(Options(parallelism: 1));
        var log = new ListLogger();
        using var source = new GatedSource(stateful: true, afterGate: () => throw new InvalidOperationException("parser failed second"));
        using var input = new FailingStream(data, failAt: 8192, source.Entered);

        var pipeline = new ParsePipeline(buffers, log);

        // The stream fails while the one parser is held in chunk 0. The parser is released, and fails, only once
        // the reader's failure is the recorded one.
        var release = Task.Run(
            async () =>
            {
                await Until(() => pipeline.FirstFailure is not null);
                source.Release();
            },
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            pipeline.RunAsync(input, source, File1, new CountingBatchSink(), TestContext.Current.CancellationToken));
        await release;

        var first = Assert.IsType<IOException>(ex.InnerException);
        Assert.Equal("stream failed first", first.Message);
        Assert.Equal(0, buffers.Chunks.Outstanding);
        Assert.Equal(0, buffers.Batches.Outstanding);

        var later = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Debug, later.Level);
        Assert.IsType<InvalidOperationException>(later.Exception);
        Assert.Contains("chunk 0 at offset 0", later.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingWhileTheReaderWaitsForTheQueueReturnsEveryChunkOnce()
    {
        var (data, _) = TestData.NumberedLines(256 << 10, seed: 22);
        var options = Options(parallelism: 2); // queue of 2, pool of 6
        var buffers = new PipelineBuffers(options);
        var log = new ListLogger();
        using var source = new GatedSource(stateful: false, data);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = new ParsePipeline(buffers, log).RunAsync(new MemoryStream(data), source, File1, new CountingBatchSink(), cts.Token);

        // Two parsers held in chunks 0 and 1, chunks 2 and 3 queued, the reader holding chunk 4 and its next
        // buffer: the whole pool is out and the queue is full, so TryWrite of chunk 4 can only say no. Whether the
        // reader is already parked in WaitToWriteAsync or reaches it after the cancellation, the wait throws and
        // the reader returns chunk 4: the outcome asserted below is the same, so there is nothing more to wait for.
        await Until(() => source.EnteredCount == 2 && buffers.Chunks.Outstanding == options.InFlightChunks + 1);
        await cts.CancelAsync();

        // The reader gives back its own two buffers while the parsers are still held; four stay out.
        await Until(() => buffers.Chunks.Outstanding == 4);
        source.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // Chunks 0 to 3 each reached a parser once, intact; chunk 4 never left the reader.
        Assert.Equal([0, 1, 2, 3], source.SeenChunks.Order());
        Assert.Equal(0, source.CorruptChunks);
        Assert.Equal(6, buffers.Chunks.Rented);
        Assert.Equal(0, buffers.Chunks.Outstanding);
        Assert.Equal(0, buffers.Batches.Outstanding);
        Assert.Empty(log.Entries);
    }

    /// <summary>
    /// The original scenario (one parser throws while the others run) in the configuration that showed the old
    /// hand-off defect most often: 60 of 10,000 runs reported the wrong failure, so 2,000 runs would have caught it
    /// with a probability above 99.99 %.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Stress")]
    public async Task StressAThrowingParserIsTheFailureReportedEveryTime()
    {
        const int Iterations = 2000;
        var (data, _) = TestData.NumberedLines(1 << 20, seed: 13);
        var lineStart = Array.IndexOf(data, (byte)'\n', 600_000) + 1;
        var marker = Encoding.UTF8.GetString(data, lineStart, 12);
        var buffers = new PipelineBuffers(Options(parallelism: 4, extraChunks: 4));
        var log = new ListLogger();
        var pipeline = new ParsePipeline(buffers, log);
        var source = new NumberedLineSource(throwOnText: marker);

        for (var i = 0; i < Iterations; i++)
        {
            var ex = await Assert.ThrowsAsync<PipelineException>(() =>
                pipeline.RunAsync(new MemoryStream(data), source, File1, new CountingBatchSink(), TestContext.Current.CancellationToken));
            Assert.True(ex.InnerException is InvalidDataException, $"iteration {i}: {ex}");
            Assert.True(buffers.Chunks.Outstanding == 0 && buffers.Batches.Outstanding == 0, $"iteration {i}: pools not balanced");
            Assert.True(log.Entries.IsEmpty, $"iteration {i}: a later failure was logged: {log.Entries.FirstOrDefault()}");
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + GateTimeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the pipeline did not reach the expected state");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Holds every <c>Parse</c> call at a gate; after the gate it checks the chunk against the input, or runs <paramref name="afterGate"/>.</summary>
    private sealed class GatedSource(bool stateful, byte[]? expected = null, Action? afterGate = null) : ILogSource, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _entered;
        private int _corrupt;

        public ManualResetEventSlim Entered { get; } = new();

        public int EnteredCount => Volatile.Read(ref _entered);

        public int CorruptChunks => Volatile.Read(ref _corrupt);

        public ConcurrentBag<int> SeenChunks { get; } = [];

        public SourceInfo Info { get; } = new("test-gated", [], "gated lines", 1, stateful);

        public Framing Framing => Framing.Line;

        public FieldSchema Schema { get; } = new(1, [EventColumn.Raw]);

        public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => 0f;

        public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch)
        {
            SeenChunks.Add(ctx.ChunkIndex);
            Interlocked.Increment(ref _entered);
            Entered.Set();
            if (!_release.Wait(GateTimeout))
            {
                throw new TimeoutException("the test never released the gate");
            }

            afterGate?.Invoke();
            if (expected is not null && !framedChunk.FirstSpan.SequenceEqual(expected.AsSpan((int)ctx.ChunkFileOffset, (int)framedChunk.Length)))
            {
                Interlocked.Increment(ref _corrupt);
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Dispose();
            Entered.Dispose();
        }
    }

    /// <summary>Serves <paramref name="failAt"/> bytes, then waits for <paramref name="cue"/> and throws.</summary>
    private sealed class FailingStream(byte[] data, int failAt, ManualResetEventSlim cue) : Stream
    {
        private int _position;
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAt)
            {
                if (!cue.Wait(GateTimeout))
                {
                    throw new TimeoutException("no parser reached the gate");
                }

                throw new IOException("stream failed first");
            }

            var n = Math.Min(count, failAt - _position);
            data.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset, n));
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger : ILogger<ParsePipeline>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public ConcurrentQueue<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
