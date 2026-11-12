using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace Urlog.Core.Sources;

/// <summary>
/// Per-file, per-parser state handed to <see cref="ILogSource.Parse"/>. Core creates it; a plugin reads the hints,
/// keeps its own per-file state in <see cref="Scratch"/>, and reports malformed records with <see cref="Reject"/>.
/// With unordered parallel chunks each parser thread has its own context (and its own <see cref="Scratch"/>);
/// a <see cref="SourceInfo.Stateful"/> source runs on one thread, so its context really is per file.
/// </summary>
public sealed class ParseContext
{
    private static readonly byte[] RejectReasonKey = "reject_reason"u8.ToArray();

    private IBatchWriter? _batch;

    public ulong CaseId { get; init; }

    public ulong HostId { get; init; }

    public ulong FileId { get; init; }

    public uint ParseGeneration { get; init; } = 1;

    /// <summary>Lowercase UTF-8 host name for formats that carry none (nginx, IIS, app logs).</summary>
    public ReadOnlyMemory<byte> DefaultHost { get; init; }

    /// <summary>Offset of the source timezone for naive timestamps (RFC 3164, IIS). Formats with an offset ignore it.</summary>
    public TimeSpan TimeZoneHint { get; init; }

    /// <summary>Year for timestamps without one (RFC 3164). Never guess from the clock.</summary>
    public int YearHint { get; init; } = 1970;

    /// <summary>
    /// The file's own time as intake recorded it (UTC): its modification time on the box it was read from, or an
    /// archive entry's. Null when intake has none. Only for a source whose records may carry no timestamp at all.
    /// </summary>
    public DateTime? FileTime { get; init; }

    /// <summary>
    /// Per-file, per-source options set at intake (the file row, later the upload UI), keyed <c>&lt;source&gt;.&lt;option&gt;</c>:
    /// for example <c>nginx.log_format</c> = a <c>log_format</c> string or a built-in layout name. Null when none are set.
    /// A plugin reads its own keys and ignores the rest; values are part of the parse input, so the same bytes with the
    /// same options give the same rows.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>The value of <paramref name="key"/> in <see cref="Options"/>, or null.</summary>
    public string? Option(string key) => Options is not null && Options.TryGetValue(key, out var value) ? value : null;

    /// <summary>Per-file slot for plugin state (last-seen timestamp, header row, ...). Null at the start of each file.</summary>
    public object? Scratch { get; set; }

    /// <summary>Offset of the current chunk's first byte in the decoded (decompressed, UTF-8) stream. Set by core before each chunk.</summary>
    public long ChunkFileOffset { get; set; }

    /// <summary>Zero-based index of the current chunk within the file. Set by core before each chunk.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>True when the current chunk was cut inside a record because the record exceeded the size limit.</summary>
    public bool ChunkForcedSplit { get; set; }

    /// <summary>True when the current chunk starts inside a record cut from the previous chunk.</summary>
    public bool ChunkContinuesSplit { get; set; }

    public long Rejects { get; private set; }

    /// <summary>Attaches the batch that <see cref="Reject"/> writes to. Called by core before each chunk; tests may call it.</summary>
    public void Bind(IBatchWriter batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _batch = batch;
    }

    /// <summary>
    /// Writes <paramref name="record"/> as a raw row in the current batch (raw = the bytes, message = the whole of raw, host
    /// defaulted, <c>extras['reject_reason']</c> = <paramref name="reason"/>) and counts it. Use a <c>u8</c> literal for the reason.
    /// </summary>
    public void Reject(ReadOnlySpan<byte> record, long fileOffset, ReadOnlySpan<byte> reason)
    {
        var batch = _batch ?? throw new InvalidOperationException("ParseContext is not bound to a batch writer");
        batch.AppendUInt64(EventColumn.FileOffset, (ulong)fileOffset);
        batch.AppendString(EventColumn.Raw, record);
        batch.AppendMessage(0, record.Length);
        batch.AppendExtra(RejectReasonKey, reason);
        batch.CommitRow();
        Rejects++;
    }

    /// <summary>String overload for callers outside the hot path; transcodes on the stack, no heap allocation up to 256 bytes.</summary>
    public void Reject(ReadOnlySpan<byte> record, long fileOffset, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Span<byte> buffer = stackalloc byte[256];
        var status = Utf8.FromUtf16(reason, buffer, out _, out var written, replaceInvalidSequences: true, isFinalBlock: true);
        if (status != OperationStatus.Done)
        {
            Reject(record, fileOffset, Encoding.UTF8.GetBytes(reason));
            return;
        }

        Reject(record, fileOffset, buffer[..written]);
    }
}
