using Urlog.Core.Sources;

namespace Urlog.Core.Pipeline;

/// <summary>
/// Cuts a decoded byte stream into chunks of at most <see cref="PipelineOptions.ChunkBytes"/> that end on a record
/// boundary of the source's <see cref="Framing"/>. The bytes after the last complete record are carried into the
/// next buffer, so a record is never split. A record longer than the buffer grows the buffer (doubling, up to
/// <c>max(ChunkBytes, MaxRecordBytes)</c>); past that the record is cut and both chunks are flagged
/// (<see cref="Chunk.ForcedSplit"/>, <see cref="Chunk.ContinuesSplit"/>). A leading UTF-8 BOM is skipped, so
/// offsets of a BOM file start at 3. Buffers come from the pipeline's <see cref="ChunkPool"/>; the reader holds one.
/// </summary>
public sealed class ChunkReader
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private readonly ChunkPool _pool;
    private readonly PipelineOptions _options;

    public ChunkReader(ChunkPool pool, PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(options);
        _pool = pool;
        _options = options;
    }

    /// <summary>Chunks of <paramref name="input"/>. Each yielded chunk belongs to the caller until <see cref="Chunk.Return"/>.</summary>
    public IEnumerable<Chunk> Read(Stream input, Framing framing, bool skipUtf8Bom = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(framing);
        return ReadCore(input, framing, skipUtf8Bom);
    }

    private IEnumerable<Chunk> ReadCore(Stream input, Framing framing, bool skipUtf8Bom)
    {
        var limit = Math.Max(_options.ChunkBytes, _options.MaxRecordBytes);
        var buffer = _pool.Rent(_options.ChunkBytes);
        var filled = 0;
        long fileOffset = 0;
        var index = 0;
        var continues = false;
        var eof = false;
        var first = true;
        try
        {
            while (true)
            {
                while (!eof && filled < buffer.Length)
                {
                    var n = input.Read(buffer, filled, buffer.Length - filled);
                    if (n == 0)
                    {
                        eof = true;
                        break;
                    }

                    filled += n;
                }

                if (first)
                {
                    first = false;
                    if (skipUtf8Bom && filled >= 3 && buffer.AsSpan(0, 3).SequenceEqual(Utf8Bom))
                    {
                        Array.Copy(buffer, 3, buffer, 0, filled - 3);
                        filled -= 3;
                        fileOffset = 3;
                        if (!eof)
                        {
                            continue;
                        }
                    }
                }

                if (filled == 0)
                {
                    yield break;
                }

                var end = eof ? filled : framing.LastRecordEnd(buffer.AsSpan(0, filled));
                var forced = false;
                if (end <= 0)
                {
                    if (buffer.Length < limit)
                    {
                        var bigger = _pool.Rent(Math.Min(buffer.Length * 2, limit));
                        buffer.AsSpan(0, filled).CopyTo(bigger);
                        _pool.Return(buffer);
                        buffer = bigger;
                        continue;
                    }

                    end = filled;
                    forced = true;
                }

                var carry = filled - end;
                var next = _pool.Rent(Math.Max(_options.ChunkBytes, carry));
                if (carry > 0)
                {
                    buffer.AsSpan(end, carry).CopyTo(next);
                }

                var chunk = new Chunk(_pool, buffer, end, fileOffset, index++, forced, continues);
                buffer = next;
                filled = carry;
                fileOffset += end;
                continues = forced;
                yield return chunk;

                if (eof && filled == 0)
                {
                    yield break;
                }
            }
        }
        finally
        {
            _pool.Return(buffer);
        }
    }
}
