using System.Globalization;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;

namespace Urlog.Core.Intake.Archives;

/// <summary>What <see cref="MemberStore.BuildBody"/> learned while it wrote a container's archive bytes.</summary>
/// <param name="End">How the container's own compressed stream ended.</param>
public sealed record BodyBuild(string Path, long Bytes, InputEnd End);

/// <summary>Free space fell under the mark while a cache copy was written; the copy is gone and the job waits in the queue.</summary>
public sealed class CacheSpaceException : Exception
{
    public CacheSpaceException()
    {
    }

    public CacheSpaceException(string message)
        : base(message)
    {
    }

    public CacheSpaceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Where every file's bytes are, read-only: a file that arrived is its stored file; an archive member is read
/// through its parent container.
/// <para>
/// A zip or tar that arrived by itself and uncompressed is read in place, so a member costs no disk. A container
/// that cannot be seeked to a member as it is stored (a compressed tar, a container inside another one) gets one
/// seekable copy of its archive bytes in the cache directory, <c>&lt;cache&gt;/&lt;case&gt;/&lt;file id&gt;.body</c>.
/// </para>
/// <para>
/// The copy is derived data, never evidence. It lives outside the intake directory, is not hashed by the
/// integrity sweep, is written again when it is missing, and is removed with the case. Nothing depends on it
/// being right: every member read from it is hashed on every parse and compared with the member's own record.
/// </para>
/// </summary>
public sealed class MemberStore
{

    private readonly string? _evidenceRoot;
    private readonly string? _cacheRoot;
    private readonly IDiskProbe _disk;
    private readonly WatermarkOptions _watermark;
    private readonly Lock _buildLock = new();

    /// <param name="cacheRoot">Null puts the cache beside the evidence root (<c>&lt;root&gt;-cache</c>).</param>
    public MemberStore(string? evidenceRoot, string? cacheRoot, IDiskProbe? disk = null, WatermarkOptions? watermark = null)
    {
        _evidenceRoot = evidenceRoot;
        _cacheRoot = cacheRoot ?? (string.IsNullOrEmpty(evidenceRoot) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(evidenceRoot)) + "-cache");
        _disk = disk ?? new DriveDiskProbe();
        _watermark = watermark ?? new WatermarkOptions();
    }

    public string? CacheRoot => _cacheRoot;

    /// <summary>Free space is read again every this many bytes of a cache copy. 256 MiB; tests lower it.</summary>
    public long SpaceCheckBytes { get; init; } = 256L << 20;

    /// <summary>The stored file of a record that arrived by itself.</summary>
    public string StoredPath(EvidenceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (Path.IsPathRooted(file.StoredPath))
        {
            return file.StoredPath;
        }

        var root = _evidenceRoot ?? throw new InvalidOperationException($"stored_path '{file.StoredPath}' is relative and no evidence root is configured");
        return Path.Combine(root, file.CaseId.ToString(CultureInfo.InvariantCulture), file.StoredPath);
    }

    /// <summary>True when the container's archive bytes are its stored file as it is.</summary>
    public static bool ReadInPlace(EvidenceFile container, ContainerLayout layout)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container.ParentFileId is null && layout.Compression == CompressionKind.None;
    }

    /// <summary>The path of the cache copy of a container's archive bytes, whether or not it exists.</summary>
    public string CachePath(EvidenceFile container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var root = _cacheRoot ?? throw new InvalidOperationException("no cache directory is configured for containers that need a seekable copy");
        return Path.Combine(root, container.CaseId.ToString(CultureInfo.InvariantCulture), container.Id.ToString(CultureInfo.InvariantCulture) + ".body");
    }

    /// <summary>
    /// Opens the file's own bytes for reading: the bytes its SHA-256 is over. <paramref name="load"/> returns the
    /// record of a parent container by id.
    /// </summary>
    public Stream OpenRaw(EvidenceFile file, Func<long, EvidenceFile> load)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(load);
        if (file.ParentFileId is not { } parentId)
        {
            var path = StoredPath(file);
            return File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan)
                : throw new FileNotFoundException($"the evidence file is not where the intake record says (stored_path '{file.StoredPath}')", path);
        }

        var parent = load(parentId);
        var layout = ContainerLayout.Of(parent);
        var body = BodyPath(parent, layout, load);
        return ArchiveWalker.OpenMember(body, layout.Format, file.MemberOrdinal ?? 0, file.MemberOffset, file.Size);
    }

    /// <summary>The seekable archive bytes of a container: its stored file, or its cache copy, written now if it is missing.</summary>
    public string BodyPath(EvidenceFile container, ContainerLayout layout, Func<long, EvidenceFile> load)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (ReadInPlace(container, layout))
        {
            return StoredPath(container);
        }

        var path = CachePath(container);
        lock (_buildLock)
        {
            if (!File.Exists(path))
            {
                using var raw = OpenRaw(container, load);
                BuildBody(container, layout, raw, _ => { });
            }
        }

        return path;
    }

    /// <summary>
    /// Writes the cache copy of a container from <paramref name="raw"/>, the container's own bytes: its
    /// compression is removed, nothing else. Written under a temporary name and renamed, so a copy that exists is
    /// whole. <paramref name="progress"/> gets the bytes written so far, for the caps.
    /// </summary>
    /// <exception cref="CacheSpaceException">Free space on the cache volume fell under the watermark.</exception>
    public BodyBuild BuildBody(EvidenceFile container, ContainerLayout layout, Stream raw, Action<long> progress)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(progress);
        var path = CachePath(container);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long written = 0;
        InputEnd end;
        try
        {
            using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                // The raw stream is the caller's (it may be hashing it); the decoder must not close it.
                using var decoder = new CheckedDecodeStream(new NonClosing(raw), layout.Compression);
                var buffer = new byte[1 << 20];
                var nextCheck = SpaceCheckBytes;
                int n;
                while ((n = decoder.Read(buffer, 0, buffer.Length)) > 0)
                {
                    target.Write(buffer, 0, n);
                    written += n;
                    progress(written);
                    if (written >= nextCheck)
                    {
                        nextCheck += SpaceCheckBytes;
                        RequireSpace(directory);
                    }
                }

                end = decoder.End;
            }

            EvidencePermissions.MakeReadOnly(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            Remove(temp);
            throw;
        }

        return new BodyBuild(path, written, end);
    }

    /// <summary>Removes one container's cache copy; a missing one is fine.</summary>
    public void RemoveBody(EvidenceFile container)
    {
        if (_cacheRoot is not null)
        {
            Remove(CachePath(container));
        }
    }

    /// <summary>Removes every cache copy of a case (when the case is archived or purged).</summary>
    public void RemoveCase(long caseId)
    {
        if (_cacheRoot is null)
        {
            return;
        }

        var directory = Path.Combine(_cacheRoot, caseId.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            Remove(file);
        }

        Directory.Delete(directory, recursive: true);
    }

    private void RequireSpace(string directory)
    {
        var volume = _disk.Measure(directory);
        var mark = Math.Max(_watermark.MinFreeBytes, volume.TotalBytes / 100 * _watermark.MinFreePercent);
        if (volume.FreeBytes < mark)
        {
            throw new CacheSpaceException(string.Create(CultureInfo.InvariantCulture,
                $"free space on {volume.Name} fell to {volume.FreeBytes:N0} bytes, under the mark of {mark:N0}, while the archive's seekable copy was written; the copy was removed and the job waits for space"));
        }
    }

    private static void Remove(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        // Cache copies are read-only on disk; that is a guard against accidents, not a reason to keep them.
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    /// <summary>Passes reads through and ignores disposal.</summary>
    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
