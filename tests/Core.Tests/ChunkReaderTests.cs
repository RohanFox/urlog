using System.Text;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Tests;

/// <summary>The chunk reader's contract: a record is never split; CRLF; 10 KB lines; file smaller than a chunk; empty file.</summary>
public sealed class ChunkReaderTests
{
    private const int SmallChunk = 4096;

    private static PipelineOptions Options(int chunkBytes = SmallChunk, int maxRecordBytes = 16 << 20) => new()
    {
        ChunkBytes = chunkBytes,
        MaxRecordBytes = maxRecordBytes,
        Parallelism = 1,
        InFlightChunks = 3,
        InFlightBatches = 1,
        BatchRows = 10,
    };

    [Fact]
    public void LineRecordsAreNeverSplitAndBytesAreLossless()
    {
        var (data, lines) = TestData.NumberedLines(1 << 20, seed: 1);
        var (chunks, pool) = ReadAll(data, Framing.Line, Options());

        Assert.True(chunks.Count > 200, $"1 MiB in 4 KiB chunks should be many chunks, got {chunks.Count}");
        Assert.Equal(data, Concat(chunks));
        long offset = 0;
        var recordCount = 0;
        foreach (var c in chunks)
        {
            Assert.Equal(offset, c.FileOffset);
            Assert.InRange(c.Bytes.Length, 1, SmallChunk);
            Assert.Equal((byte)'\n', c.Bytes[^1]);
            Assert.False(c.ForcedSplit);
            Assert.False(c.ContinuesSplit);
            recordCount += CountRecords(c.Bytes, Framing.Line);
            offset += c.Bytes.Length;
        }

        Assert.Equal(lines, recordCount);
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.OversizeAllocations);
    }

    [Fact]
    public void CrlfLinesEndChunksOnTheLfAndRecordsDropTheCr()
    {
        var (data, lines) = TestData.NumberedLines(256 << 10, seed: 2, newline: "\r\n");
        var (chunks, _) = ReadAll(data, Framing.Line, Options());

        Assert.Equal(data, Concat(chunks));
        var records = 0;
        foreach (var c in chunks)
        {
            Assert.Equal("\r\n"u8.ToArray(), c.Bytes[^2..]);
            foreach (var r in new RecordEnumerator(c.Bytes, Framing.Line, c.FileOffset))
            {
                Assert.DoesNotContain((byte)'\r', r.Bytes.ToArray());
                records++;
            }
        }

        Assert.Equal(lines, records);
    }

    [Fact]
    public void TenKilobyteLinesGrowTheBufferInsteadOfSplitting()
    {
        var line = new string('x', 10 * 1024 - 1) + "\n";
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line, 25)));
        var (chunks, pool) = ReadAll(data, Framing.Line, Options(chunkBytes: SmallChunk));

        Assert.Equal(data, Concat(chunks));
        foreach (var c in chunks)
        {
            Assert.False(c.ForcedSplit);
            Assert.Equal(0, c.Bytes.Length % (10 * 1024));
            foreach (var r in new RecordEnumerator(c.Bytes, Framing.Line, c.FileOffset))
            {
                Assert.Equal(10 * 1024 - 1, r.Bytes.Length);
            }
        }

        Assert.True(pool.OversizeAllocations > 0, "a 10 KB line in a 4 KiB chunk must take the growth path");
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public void RecordPastTheLimitIsForceSplitAndFlagged()
    {
        var huge = new string('y', 10 * 1024) + "\n";
        var data = Encoding.UTF8.GetBytes("1 short\n" + huge + "3 after\n");
        var (chunks, pool) = ReadAll(data, Framing.Line, Options(chunkBytes: SmallChunk, maxRecordBytes: SmallChunk));

        Assert.Equal(data, Concat(chunks));
        var forced = chunks.Where(c => c.ForcedSplit).ToList();
        Assert.NotEmpty(forced);
        foreach (var c in chunks)
        {
            if (c.ContinuesSplit)
            {
                Assert.True(chunks[c.Index - 1].ForcedSplit, "a continuation must follow a forced split");
            }
        }

        Assert.All(forced, c => Assert.Equal(SmallChunk, c.Bytes.Length));
        Assert.Equal("3 after\n", Encoding.UTF8.GetString(chunks[^1].Bytes[^8..]));
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(0, pool.OversizeAllocations);
    }

    [Fact]
    public void FileSmallerThanAChunkIsOneChunk()
    {
        var data = "1 only\n2 two lines\n"u8.ToArray();
        var (chunks, pool) = ReadAll(data, Framing.Line, Options());
        var c = Assert.Single(chunks);
        Assert.Equal(data, c.Bytes);
        Assert.Equal(0, c.FileOffset);
        Assert.Equal(0, c.Index);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public void FinalLineWithoutTerminatorIsKept()
    {
        var data = "1 a\n2 b"u8.ToArray();
        var (chunks, _) = ReadAll(data, Framing.Line, Options());
        Assert.Equal(data, Concat(chunks));
        Assert.Equal(2, CountRecords(chunks[^1].Bytes, Framing.Line));
    }

    [Fact]
    public void EmptyFileYieldsNoChunks()
    {
        var (chunks, pool) = ReadAll([], Framing.Line, Options());
        Assert.Empty(chunks);
        Assert.Equal(0, pool.Outstanding);
    }

    [Fact]
    public void Utf8BomIsSkippedAndOffsetsStartAfterIt()
    {
        var data = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("1 a\n2 b\n"u8.ToArray()).ToArray();
        var (chunks, _) = ReadAll(data, Framing.Line, Options());
        var c = Assert.Single(chunks);
        Assert.Equal(3, c.FileOffset);
        Assert.Equal("1 a\n2 b\n", Encoding.UTF8.GetString(c.Bytes));
    }

    [Fact]
    public void MultiLineAndBlankLineFramingsNeverSplitARecord()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            sb.Append("2026-03-01T00:00:").Append(i % 60).Append(" record ").Append(i).Append('\n');
            for (var j = 0; j < i % 4; j++)
            {
                sb.Append("    continuation ").Append(j).Append('\n');
            }
        }

        var multi = Encoding.UTF8.GetBytes(sb.ToString());
        var (chunks, _) = ReadAll(multi, Framing.MultiLine("2026-"u8), Options());
        Assert.Equal(multi, Concat(chunks));
        Assert.True(chunks.Count > 20);
        var records = 0;
        foreach (var c in chunks)
        {
            Assert.StartsWith("2026-", Encoding.UTF8.GetString(c.Bytes[..5]), StringComparison.Ordinal);
            foreach (var r in new RecordEnumerator(c.Bytes, Framing.MultiLine("2026-"u8), c.FileOffset))
            {
                Assert.StartsWith("2026-", Encoding.UTF8.GetString(r.Bytes[..5]), StringComparison.Ordinal);
                records++;
            }
        }

        Assert.Equal(2000, records);

        sb.Clear();
        for (var i = 0; i < 2000; i++)
        {
            sb.Append("__CURSOR=").Append(i).Append("\nMESSAGE=hello ").Append(i).Append("\n\n");
        }

        var blank = Encoding.UTF8.GetBytes(sb.ToString());
        (chunks, _) = ReadAll(blank, Framing.BlankLineRecord, Options());
        Assert.Equal(blank, Concat(chunks));
        records = chunks.Sum(c => CountRecords(c.Bytes, Framing.BlankLineRecord));
        Assert.Equal(2000, records);
        Assert.All(chunks, c => Assert.StartsWith("__CURSOR=", Encoding.UTF8.GetString(c.Bytes[..9]), StringComparison.Ordinal));
    }

    [Fact]
    public void BinaryFramingCutsOnRecordSize()
    {
        var data = new byte[100_000];
        new Random(3).NextBytes(data);
        var (chunks, _) = ReadAll(data, Framing.Binary(new FixedSizeSplitter(24)), Options());
        Assert.Equal(data, Concat(chunks));
        foreach (var c in chunks.Take(chunks.Count - 1))
        {
            Assert.Equal(0, c.Bytes.Length % 24);
        }
    }

    [Fact]
    public void ReaderHoldsOneBufferAndReusesIt()
    {
        var (data, _) = TestData.NumberedLines(512 << 10, seed: 4);
        var options = Options();
        var pool = new ChunkPool(options);
        var reader = new ChunkReader(pool, options);
        var count = 0;
        foreach (var chunk in reader.Read(new MemoryStream(data), Framing.Line))
        {
            Assert.InRange(pool.Outstanding, 1, 2); // the yielded chunk plus the one being filled
            chunk.Return();
            count++;
        }

        Assert.True(count > 100);
        Assert.Equal(0, pool.Outstanding);
    }

    private static (List<ChunkCopy> Chunks, ChunkPool Pool) ReadAll(byte[] data, Framing framing, PipelineOptions options)
    {
        var pool = new ChunkPool(options);
        var reader = new ChunkReader(pool, options);
        var chunks = new List<ChunkCopy>();
        foreach (var chunk in reader.Read(new MemoryStream(data), framing))
        {
            chunks.Add(new ChunkCopy(chunk.Span.ToArray(), chunk.FileOffset, chunk.Index, chunk.ForcedSplit, chunk.ContinuesSplit));
            chunk.Return();
        }

        return (chunks, pool);
    }

    private static byte[] Concat(List<ChunkCopy> chunks) => chunks.SelectMany(c => c.Bytes).ToArray();

    private static int CountRecords(byte[] chunk, Framing framing)
    {
        var n = 0;
        foreach (var _ in new RecordEnumerator(chunk, framing, 0))
        {
            n++;
        }

        return n;
    }

    private sealed record ChunkCopy(byte[] Bytes, long FileOffset, int Index, bool ForcedSplit, bool ContinuesSplit);
}
