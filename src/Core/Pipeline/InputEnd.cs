namespace Urlog.Core.Pipeline;

/// <summary>How a file's input ended, as far as its compression format can say.</summary>
public enum InputEndKind
{
    /// <summary>The end was not reached, or nothing looked. A commit refuses this value.</summary>
    Unknown,

    /// <summary>The format's own end structure was read and agrees with the content.</summary>
    Complete,

    /// <summary>The stored bytes stop before the format's end structure: the content is what could be decoded.</summary>
    Truncated,
}

/// <summary>
/// The end of one file's input. The default value is <see cref="InputEndKind.Unknown"/> on purpose: a code path
/// that never asked how the input ended cannot pass for one that found it complete.
/// </summary>
/// <param name="DecodedOffset">Decompressed bytes read before the end (before any transcoding).</param>
/// <param name="StoredOffset">
/// Where the compressed data ends in the stored input: its whole length for a truncated input, the length less
/// <paramref name="TrailingBytes"/> for a complete one.
/// </param>
/// <param name="TrailingBytes">
/// Stored bytes behind the last complete member, frame or stream that start no further one: padding or anything
/// else. They are not content and not a truncation; the count is kept so the file record can say so.
/// </param>
/// <param name="TrailingBytesExact">
/// False when <paramref name="TrailingBytes"/> is a lower bound and not a count: gzip with more trailing bytes
/// than the reader's look-ahead (short by less than one decoder block), and gzip of several members whose
/// trailing bytes begin with 0x1F (short by at most one byte). True means the count is proved.
/// </param>
public readonly record struct InputEnd(InputEndKind Kind, long DecodedOffset, long StoredOffset, long TrailingBytes = 0, bool TrailingBytesExact = true)
{
    public bool IsTruncated => Kind == InputEndKind.Truncated;

    public static InputEnd Complete(long decodedOffset, long storedOffset, long trailingBytes = 0, bool trailingBytesExact = true) =>
        new(InputEndKind.Complete, decodedOffset, storedOffset, trailingBytes, trailingBytesExact);

    public static InputEnd Truncated(long decodedOffset, long storedOffset) => new(InputEndKind.Truncated, decodedOffset, storedOffset);
}
