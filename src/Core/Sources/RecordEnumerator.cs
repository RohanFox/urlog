using System.Buffers;

namespace Urlog.Core.Sources;

/// <summary>One record of a framed chunk: its bytes without the terminator, and where it starts in the decoded file.</summary>
public readonly ref struct Record(ReadOnlySpan<byte> bytes, long fileOffset)
{
    public ReadOnlySpan<byte> Bytes { get; } = bytes;

    /// <summary>Offset of the first byte in the decoded (decompressed, UTF-8) stream; goes into <c>file_offset</c>.</summary>
    public long FileOffset { get; } = fileOffset;
}

/// <summary>
/// Walks the records of a framed chunk per its <see cref="Framing"/>, allocation-free. Line framing yields lines
/// without <c>\n</c> or <c>\r\n</c> (an empty line is a record; a final line without a terminator is a record).
/// </summary>
public ref struct RecordEnumerator
{
    private readonly Framing _framing;
    private readonly long _baseOffset;
    private ReadOnlySpan<byte> _rest;
    private int _position;
    private ReadOnlySpan<byte> _current;
    private long _currentOffset;

    public RecordEnumerator(ReadOnlySpan<byte> chunk, Framing framing, long baseOffset)
    {
        ArgumentNullException.ThrowIfNull(framing);
        _framing = framing;
        _baseOffset = baseOffset;
        _rest = chunk;
    }

    public readonly Record Current => new(_current, _currentOffset);

    public readonly RecordEnumerator GetEnumerator() => this;

    public bool MoveNext()
    {
        if (_rest.IsEmpty)
        {
            return false;
        }

        var length = _framing.NextRecord(_rest, out var consumed);
        _current = _rest[..length];
        _currentOffset = _baseOffset + _position;
        _rest = _rest[consumed..];
        _position += consumed;
        return true;
    }
}

public static class FramedChunkExtensions
{
    /// <summary>
    /// Records of <paramref name="framedChunk"/> with file offsets taken from <paramref name="ctx"/>. Core always
    /// hands plugins a single-segment sequence.
    /// </summary>
    public static RecordEnumerator Records(this ReadOnlySequence<byte> framedChunk, Framing framing, ParseContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!framedChunk.IsSingleSegment)
        {
            throw new ArgumentException("a framed chunk is always a single segment", nameof(framedChunk));
        }

        return new RecordEnumerator(framedChunk.FirstSpan, framing, ctx.ChunkFileOffset);
    }
}
