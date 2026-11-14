using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Intake;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Lifecycle;

/// <summary>An archive, or what is asked of it, cannot be accepted. Nothing was dropped, removed or restored on its account.</summary>
public sealed class ArchiveRefusedException : Exception
{
    public ArchiveRefusedException()
    {
    }

    public ArchiveRefusedException(string message)
        : base(message)
    {
    }

    public ArchiveRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ArchiveRefusedException(string message, long? fileId, string? expected, string? actual)
        : base(message)
    {
        FileId = fileId;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The file record the refusal is about, when it is about one.</summary>
    public long? FileId { get; }

    public string? Expected { get; }

    public string? Actual { get; }
}

public sealed record ManifestCase(long Id, string Name, long OwnerId, DateTime CreatedAt, DateTime? ClosedAt);

public sealed record ManifestHost(long Id, string Name, string Timezone);

/// <summary>One file record as it stood at the archive, archive members included.</summary>
public sealed record ManifestFile(long Id, long? ParentFileId, long? RootFileId, long? HostId, string Sha256, long Size, string OriginalName, string StoredPath, string? ArchivePath, string Source, string Uploader, DateTime ReceivedAt, string State, int ParseGeneration, long RowCount);

/// <summary>
/// One stored file in the tar. <see cref="Name"/> is <c>files/&lt;file id&gt;</c> and nothing of the evidence's own
/// name. <see cref="TarSha256"/> and <see cref="TarSize"/> are of the bytes as they went into the tar; they differ
/// from the record's only for a file flagged <see cref="Changed"/>. A file that was not there is
/// <see cref="Missing"/> and has no entry in the tar.
/// </summary>
public sealed record ManifestEntry(long FileId, string Name, string RecordSha256, long RecordSize, string? TarSha256, long? TarSize, bool Missing, bool Changed, bool External);

/// <summary>
/// <c>manifest.json</c> of an archive (design record, "Case lifecycle", Archive): what the tar holds, entry by
/// entry with its hash, the records of the case, and the hash of the tar itself. Its own SHA-256 is in the
/// <c>case_archive</c> audit row, so a changed manifest is caught by the chain.
/// </summary>
public sealed record ArchiveManifest
{
    public const int CurrentFormat = 1;
    public const string TarName = "evidence.tar.zst";
    public const string AuditName = "audit.jsonl";
    public const string ManifestName = "manifest.json";
    public const string EntryPrefix = "files/";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public int Format { get; init; } = CurrentFormat;

    public required string StoreIdentity { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required ManifestCase Case { get; init; }

    public required IReadOnlyList<ManifestHost> Hosts { get; init; }

    public required IReadOnlyList<ManifestFile> Files { get; init; }

    public required IReadOnlyList<ManifestEntry> Entries { get; init; }

    public required string TarSha256 { get; init; }

    public required long TarSize { get; init; }

    public required string AuditSha256 { get; init; }

    public required long AuditLines { get; init; }

    public required long AuditHeadSeq { get; init; }

    public required string AuditHeadHash { get; init; }

    public static string EntryName(long fileId) => EntryPrefix + fileId.ToString(CultureInfo.InvariantCulture);

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <exception cref="ArchiveRefusedException">The bytes are not a manifest of a format this build reads.</exception>
    public static ArchiveManifest Parse(ReadOnlySpan<byte> bytes)
    {
        ArchiveManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ArchiveManifest>(bytes, Json);
        }
        catch (JsonException e)
        {
            throw new ArchiveRefusedException("the manifest is not readable JSON of the expected shape: " + e.Message, e);
        }

        if (manifest is null || manifest.Format != CurrentFormat || manifest.Entries is null || manifest.Files is null || manifest.Case is null)
        {
            throw new ArchiveRefusedException($"the manifest is of format {manifest?.Format.ToString(CultureInfo.InvariantCulture) ?? "none"}; this build reads format {CurrentFormat}");
        }

        return manifest;
    }
}

/// <summary>What <see cref="ArchiveWriter.BuildAsync"/> wrote into a directory.</summary>
public sealed record ArchiveBuild(ArchiveManifest Manifest, string ManifestSha256, long ManifestSize, int Files, long Bytes, IReadOnlyList<ArchiveFlag> Flagged);

/// <summary>
/// Writes an archive into a directory: <c>evidence.tar.zst</c> with every stored file of the case that arrived,
/// each read once and hashed as it is written, then <c>audit.jsonl</c> and <c>manifest.json</c>. It changes nothing
/// else: no record, no stored file, nothing in the event store.
/// </summary>
public static class ArchiveWriter
{
    public const int CompressionLevel = 3;

    public static async Task<ArchiveBuild> BuildAsync(MetadataStore store, MemberStore members, long caseId, string directory, Action<string>? beforeFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(members);
        Directory.CreateDirectory(directory);
        CaseRecord @case;
        List<Host> hosts;
        List<EvidenceFile> files;
        await using (var db = store.CreateDbContext())
        {
            @case = await db.Cases.AsNoTracking().FirstAsync(c => c.Id == caseId, cancellationToken).ConfigureAwait(false);
            hosts = await db.Hosts.AsNoTracking().Where(h => h.CaseId == caseId).OrderBy(h => h.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            files = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId).OrderBy(f => f.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var entries = new List<ManifestEntry>();
        var flagged = new List<ArchiveFlag>();
        long bytes = 0;
        string tarSha;
        long tarSize;
        var tarPath = Path.Combine(directory, ArchiveManifest.TarName);
        await using (var output = new FileStream(tarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        {
            await using var hashed = new HashingStream(output, write: true);
            await using (var zstd = new ZstdSharp.CompressionStream(hashed, CompressionLevel, leaveOpen: true))
            await using (var tar = new TarWriter(zstd, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var file in files.Where(f => f.ParentFileId is null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = ArchiveManifest.EntryName(file.Id);
                    var external = Path.IsPathRooted(file.StoredPath);
                    var path = members.StoredPath(file);
                    beforeFile?.Invoke(path);
                    if (!File.Exists(path))
                    {
                        entries.Add(new ManifestEntry(file.Id, name, file.Sha256, file.Size, null, null, Missing: true, Changed: false, external));
                        flagged.Add(new ArchiveFlag(file.Id, file.OriginalName, file.Sha256, file.Size, IntegrityOperations.Missing, null));
                        continue;
                    }

                    await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    await using var reading = new HashingStream(source, write: false);
                    // The entry is named by the record's id. Nothing of the evidence's own name or place goes into the tar.
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = reading, Mode = UnixFileMode.UserRead | UnixFileMode.GroupRead, ModificationTime = DateTimeOffset.UnixEpoch };
                    await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
                    var (sha, size) = reading.Finish();
                    bytes += size;
                    var changed = !string.Equals(sha, file.Sha256, StringComparison.Ordinal) || size != file.Size;
                    entries.Add(new ManifestEntry(file.Id, name, file.Sha256, file.Size, sha, size, Missing: false, changed, external));
                    if (changed)
                    {
                        flagged.Add(new ArchiveFlag(file.Id, file.OriginalName, file.Sha256, file.Size, sha, size));
                    }
                }
            }

            await hashed.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            (tarSha, tarSize) = hashed.Finish();
        }

        // The case's trail, as it stands now; the head it ends at goes into the manifest.
        long lines;
        var auditPath = Path.Combine(directory, ArchiveManifest.AuditName);
        long headSeq;
        string headHash;
        await using (var db = store.CreateDbContext())
        {
            await using var output = new FileStream(auditPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            lines = await AuditExport.WriteCaseAsync(db, caseId, output, cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            var head = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(cancellationToken).ConfigureAwait(false);
            (headSeq, headHash) = (head.Seq, head.Hash);
        }

        var manifest = new ArchiveManifest
        {
            StoreIdentity = await StoreIdentity.ReadAsync(store.Path, cancellationToken).ConfigureAwait(false) ?? string.Empty,
            CreatedAt = store.Clock.GetUtcNow().UtcDateTime,
            Case = new ManifestCase(@case.Id, @case.Name, @case.OwnerId, @case.CreatedAt, @case.ClosedAt),
            Hosts = hosts.ConvertAll(h => new ManifestHost(h.Id, h.Name, h.Timezone)),
            Files = files.ConvertAll(f => new ManifestFile(f.Id, f.ParentFileId, f.RootFileId, f.HostId, f.Sha256, f.Size, f.OriginalName, f.StoredPath, f.ArchivePath, Names.Of(f.Source), f.Uploader, f.ReceivedAt, Names.Of(f.State), f.ParseGeneration, f.RowCount)),
            Entries = entries,
            TarSha256 = tarSha,
            TarSize = tarSize,
            AuditSha256 = ArchiveReader.HashFile(auditPath).Sha256,
            AuditLines = lines,
            AuditHeadSeq = headSeq,
            AuditHeadHash = headHash,
        };
        var json = manifest.ToBytes();
        await using (var output = new FileStream(Path.Combine(directory, ArchiveManifest.ManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await output.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        return new ArchiveBuild(manifest, Convert.ToHexStringLower(SHA256.HashData(json)), json.Length, entries.Count(e => !e.Missing), bytes, flagged);
    }

    /// <summary>The archive's files become read-only, its directory closed to others (0440 and 0750 where modes exist).</summary>
    public static void Seal(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            EvidencePermissions.MakeReadOnly(file);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
    }
}

/// <summary>
/// Reads an archive back from disk and holds every entry of its tar to its manifest. The same walk serves the
/// check before an archive is committed and the re-import, which also takes the bytes of each entry.
/// </summary>
public static class ArchiveReader
{
    /// <summary>
    /// Walks the tar at <paramref name="tarPath"/>. Every entry must be a regular file named exactly
    /// <c>files/&lt;digits&gt;</c>, listed in the manifest, not flagged missing, seen once, and of the size and
    /// SHA-256 the manifest gives; every listed entry must be there; and the file as a whole must have the
    /// manifest's size and SHA-256. <paramref name="sink"/> gets each entry's bytes after its name is accepted
    /// and before its hash is known: the caller keeps what it writes apart until this returns.
    /// </summary>
    /// <exception cref="ArchiveRefusedException">Any of the above does not hold.</exception>
    public static async Task ReadAsync(string tarPath, ArchiveManifest manifest, Func<ManifestEntry, Stream, CancellationToken, Task>? sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var listed = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (entry.Name != ArchiveManifest.EntryName(entry.FileId) || !listed.TryAdd(entry.Name, entry))
            {
                throw new ArchiveRefusedException($"the manifest lists '{Show(entry.Name)}' for file {entry.FileId}: an entry is named files/<file id>, once", entry.FileId, ArchiveManifest.EntryName(entry.FileId), Show(entry.Name));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var file = new FileStream(tarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var whole = new HashingStream(file, write: false);
        try
        {
            await using var zstd = new ZstdSharp.DecompressionStream(whole, leaveOpen: true);
            await using (var tar = new TarReader(zstd, leaveOpen: true))
            {
                while (await tar.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
                {
                    var name = entry.Name;
                    if (entry.EntryType != TarEntryType.RegularFile)
                    {
                        throw new ArchiveRefusedException($"the tar holds '{Show(name)}' of type {entry.EntryType}; an archive holds regular files only");
                    }

                    if (!IsEntryName(name))
                    {
                        throw new ArchiveRefusedException($"the tar holds an entry named '{Show(name)}'; an entry is named files/<file id> and nothing else");
                    }

                    if (!listed.TryGetValue(name, out var expected) || expected.Missing)
                    {
                        throw new ArchiveRefusedException($"the tar holds '{name}', which the manifest does not list");
                    }

                    if (!seen.Add(name))
                    {
                        throw new ArchiveRefusedException($"the tar holds '{name}' twice", expected.FileId, "one entry", "two entries");
                    }

                    await using var data = new HashingStream(entry.DataStream ?? Stream.Null, write: false);
                    if (sink is not null)
                    {
                        await sink(expected, data, cancellationToken).ConfigureAwait(false);
                    }

                    await data.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                    var (sha, size) = data.Finish();
                    if (size != expected.TarSize || !string.Equals(sha, expected.TarSha256, StringComparison.Ordinal))
                    {
                        throw new ArchiveRefusedException(
                            $"'{name}' in the tar is {size} bytes with sha256 {sha}; the manifest says {expected.TarSize} bytes with sha256 {expected.TarSha256}",
                            expected.FileId,
                            $"{expected.TarSha256} ({expected.TarSize} bytes)",
                            $"{sha} ({size} bytes)");
                    }
                }
            }

            await zstd.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ZstdSharp.ZstdException or InvalidDataException or EndOfStreamException or FormatException)
        {
            throw new ArchiveRefusedException($"{ArchiveManifest.TarName} cannot be read to its end ({e.Message})", e);
        }

        // A listed entry that is not there is named before the hash of the whole says only that something differs.
        var absent = manifest.Entries.FirstOrDefault(e => !e.Missing && !seen.Contains(e.Name));
        if (absent is not null)
        {
            throw new ArchiveRefusedException($"the manifest lists '{absent.Name}' and the tar does not hold it", absent.FileId, absent.TarSha256, IntegrityOperations.Missing);
        }

        await whole.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        var (tarSha, tarSize) = whole.Finish();
        if (tarSize != manifest.TarSize || !string.Equals(tarSha, manifest.TarSha256, StringComparison.Ordinal))
        {
            throw new ArchiveRefusedException($"{ArchiveManifest.TarName} is {tarSize} bytes with sha256 {tarSha}; the manifest says {manifest.TarSize} bytes with sha256 {manifest.TarSha256}", null, manifest.TarSha256, tarSha);
        }
    }

    /// <summary>True for <c>files/</c> followed by one to nineteen digits without a leading zero: a file id as this tool writes it.</summary>
    public static bool IsEntryName(string name)
    {
        if (name is null || !name.StartsWith(ArchiveManifest.EntryPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = name.AsSpan(ArchiveManifest.EntryPrefix.Length);
        if (digits.Length is 0 or > 19 || digits[0] == '0')
        {
            return false;
        }

        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>The SHA-256 and the size of a file, read once.</summary>
    public static (string Sha256, long Size) HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long size = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            size += read;
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), size);
    }

    /// <summary>A name from an archive, cut and stripped of control characters before it goes into a message.</summary>
    private static string Show(string? name)
    {
        var text = name ?? string.Empty;
        text = text.Length <= 120 ? text : text[..120] + "...";
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }
}

/// <summary>
/// A pass-through that hashes what goes through it and counts it. Reading, it also answers length and position
/// from the stream under it, which the tar writer asks of an entry's data.
/// </summary>
internal sealed class HashingStream(Stream inner, bool write) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytes;

    public override bool CanRead => !write;

    public override bool CanSeek => !write && inner.CanSeek;

    public override bool CanWrite => write;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set
        {
            if (value != inner.Position)
            {
                throw new NotSupportedException("a hashed stream is read once, from its start to its end");
            }
        }
    }

    /// <summary>The SHA-256 and the count of everything that went through.</summary>
    public (string Sha256, long Bytes) Finish() => (Convert.ToHexStringLower(_hash.GetCurrentHash()), _bytes);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        _hash.AppendData(buffer[..read]);
        _bytes += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _hash.AppendData(buffer.Span[..read]);
        _bytes += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        _bytes += buffer.Length;
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _hash.AppendData(buffer.Span);
        _bytes += buffer.Length;
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
        offset == 0 && origin == SeekOrigin.Current ? inner.Position : throw new NotSupportedException("a hashed stream is read once, from its start to its end");

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
