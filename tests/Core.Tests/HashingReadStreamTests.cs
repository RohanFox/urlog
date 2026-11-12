using System.IO.Compression;
using System.Security.Cryptography;
using Urlog.Core.Pipeline;

namespace Urlog.Core.Tests;

/// <summary>
/// The hashing stream's digest is the digest of the file's bytes whatever reads through it, including the bytes a
/// decoder never asks for, and the pipeline's live counters end where its result does.
/// </summary>
public sealed class HashingReadStreamTests
{
    private static readonly PipelineOptions Small = new() { ChunkBytes = 4096, MaxRecordBytes = 4096, BatchRows = 50, Parallelism = 2, InFlightChunks = 4, InFlightBatches = 2, Writers = 1 };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(70_001)]
    public void TheHashingStreamHashesExactlyTheBytesOfTheFile(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        using var hashing = new HashingReadStream(new MemoryStream(bytes));

        // Read some through odd-sized reads, leave the rest for the drain.
        var read = new byte[977];
        var taken = 0;
        while (taken < length / 3)
        {
            var n = hashing.Read(read, 0, read.Length);
            if (n == 0)
            {
                break;
            }

            taken += n;
        }

        var digest = hashing.DrainAndFinish(new byte[4096]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), digest);
        Assert.Equal(length, hashing.BytesHashed);
        Assert.Throws<ObjectDisposedException>(() => hashing.DrainAndFinish(new byte[16]));
        Assert.False(hashing.CanWrite);
        Assert.False(hashing.CanSeek);
    }

    [Fact]
    public async Task TheDigestIsOfTheCompressedBytesIncludingWhatTheDecoderNeverAsksFor()
    {
        var (text, lines) = TestData.NumberedLines(30_000, seed: 21);
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(text);
        }

        // Bytes after the gzip member that no decoder reads: still part of the evidence file, still in its hash.
        packed.Write("trailing bytes the decoder stops before"u8);
        var file = packed.ToArray();

        var buffers = new PipelineBuffers(Small);
        using var hashing = new HashingReadStream(new MemoryStream(file));
        await using var decoded = InputDecoding.OpenDecoded(hashing, CompressionKind.Gzip, InputEncoding.Utf8);
        var progress = new PipelineProgress();
        var sink = new CountingBatchSink();
        var result = await new ParsePipeline(buffers).RunAsync(decoded, new NumberedLineSource(), new FileIdentity(1, 1, 1, 1, "web-01"), sink, progress, Ct);

        Assert.Equal(lines, result.Rows);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(file)), hashing.DrainAndFinish(new byte[4096]));
        Assert.Equal(file.Length, hashing.BytesHashed);
        Assert.True(hashing.HashTime > TimeSpan.Zero);

        // The live counters end where the result does.
        Assert.Equal((result.Chunks, result.Chunks, result.Rows, result.Rejects, result.Bytes), ((int)progress.ChunksRead, (int)progress.ChunksParsed, progress.Rows, progress.Rejects, progress.BytesRead));
        Assert.Equal(sink.Rows, progress.Rows);
        Assert.Equal(0, buffers.Chunks.Outstanding);
        Assert.Equal(0, buffers.Batches.Outstanding);
    }
}
