namespace Urlog.Core.Sources;

/// <summary>
/// What a source knows about a sample beyond one number: the layout that fits (nginx <c>combined</c>,
/// <c>ingress-nginx</c>, ...). <paramref name="LayoutKnown"/> is false with a null <paramref name="Layout"/> when the
/// lines have the source's shape and none of its layouts fits.
/// </summary>
public readonly record struct SniffDetail(float Confidence, string? Layout, bool LayoutKnown);

/// <summary>
/// Optional companion of <see cref="ILogSource.Sniff"/> for a source with layouts. The detector calls it in place of
/// <c>Sniff</c> and keeps the layout per file; <see cref="SniffDetail.Confidence"/> must equal what <c>Sniff</c>
/// returns when <paramref name="options"/> is null. Same rules as <c>Sniff</c>: never read beyond the two spans.
/// </summary>
public interface ISniffDetail
{
    /// <param name="options">The file's per-source options (<see cref="ParseContext.Options"/>), or null.</param>
    SniffDetail SniffDetailed(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx, IReadOnlyDictionary<string, string>? options);
}
