using System.Globalization;
using System.Security.Cryptography;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Intake.Archives;

/// <summary>How one expansion ended. A mismatch means the container's bytes are not the ones intake recorded.</summary>
/// <param name="Entries">Entries of the container that were listed.</param>
/// <param name="Truncated">The container ends before its own end structure; null when it is whole.</param>
public sealed record ExpandOutcome(IntegrityMismatch? Mismatch, long Entries, string Summary, InputEnd? Truncated, string? CachePath);

/// <summary>
/// The body of an <c>expand</c> job (design record, "Intake"): one container becomes one record per member.
/// <list type="number">
/// <item>The container is read once through SHA-256 and compared with its own record before anything else. The
/// same read writes its seekable copy when it needs one.</item>
/// <item>Every entry is listed, and every regular file among them is read once through SHA-256 and detected like
/// a file that arrived by itself. Nothing is written to the metadata store yet, and the caps are checked on the
/// bytes that actually come out, so an archive that outgrows a cap leaves no member record at all.</item>
/// <item>The members are recorded in batches, each batch one transaction with its audit rows and jobs and the
/// container's resume point. A stop between batches loses nothing and repeats nothing.</item>
/// </list>
/// A member that is a log gets a parse job, a member that is a container an expansion of its own while the depth
/// allows, anything else is recorded and kept. Entry names are data: no member is ever written to disk under a
/// name from an archive.
/// </summary>
public sealed class ArchiveExpander
{
    /// <summary>Members per transaction.</summary>
    public const int BatchSize = 200;

    private readonly MetadataStore _store;
    private readonly MemberStore _members;
    private readonly IReadOnlyList<ILogSource> _sources;
    private readonly HostInference _hosts;

    public ArchiveExpander(MetadataStore store, MemberStore members, IReadOnlyList<ILogSource> sources, HostInference? hosts = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(sources);
        _store = store;
        _members = members;
        _sources = sources;
        _hosts = hosts ?? HostInference.Default;
    }

    /// <summary>Called after every batch of members is committed, with the number of entries dealt with so far. For tests that stop an expansion half way.</summary>
    public Action<int>? AfterBatch { get; set; }

    /// <exception cref="ArchiveLimitException">The tree outgrew a cap; no member of this container was recorded.</exception>
    /// <exception cref="CacheSpaceException">The cache volume fell under the watermark while the seekable copy was written.</exception>
    public async Task<ExpandOutcome> RunAsync(EvidenceFile container, ArchiveLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(limits);
        var layout = ContainerLayout.Of(container);
        var root = container.RootFileId is { } rootId ? Load(rootId) : container;
        long treeBytes;
        long treeMembers;
        await using (var db = _store.CreateDbContext())
        {
            // What the rest of the tree already holds; this container's own members are counted as they are walked.
            var others = db.Files.AsNoTracking().Where(f => f.RootFileId == root.Id && f.ParentFileId != container.Id);
            treeBytes = await others.SumAsync(f => (long?)f.Size, cancellationToken).ConfigureAwait(false) ?? 0;
            treeMembers = await others.LongCountAsync(cancellationToken).ConfigureAwait(false);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();

        // 1. The container against its own record, and its seekable copy in the same read.
        string bodyPath;
        BodyBuild? build = null;
        string sha256;
        long size;
        using (var raw = _members.OpenRaw(container, Load))
        using (var hashing = new HashingReadStream(raw))
        {
            if (!MemberStore.ReadInPlace(container, layout))
            {
                build = _members.BuildBody(container, layout, hashing, bytes =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    limits.Check(treeBytes + bytes, treeMembers, root.Size);
                });
            }

            sha256 = hashing.DrainAndFinish(new byte[1 << 20]);
            size = hashing.BytesHashed;
        }

        if (!string.Equals(sha256, container.Sha256, StringComparison.Ordinal) || size != container.Size)
        {
            _members.RemoveBody(container);
            return new ExpandOutcome(new IntegrityMismatch(sha256, size), 0, string.Empty, null, null);
        }

        bodyPath = build?.Path ?? _members.StoredPath(container);
        var verified = clock.Elapsed;

        // 2. Every member hashed and detected; nothing recorded yet.
        var drafts = new List<MemberDraft>();
        long walkedBytes = 0;
        var unreadable = 0;
        var prefix = (container.ArchivePath ?? container.OriginalName) + "!";
        var memberName = Path.GetFileNameWithoutExtension(container.OriginalName);
        var walk = ArchiveWalker.Walk(bodyPath, layout.Format, memberName, (entry, open) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Regular)
            {
                return;
            }

            limits.Check(treeBytes + walkedBytes, treeMembers + drafts.Count + 1, root.Size);
            var archivePath = prefix + entry.Name;
            byte[] head;
            string memberSha;
            long memberSize;
            try
            {
                if (entry.Encrypted)
                {
                    // The runtime would hand out the ciphertext as if it were the content.
                    throw new InvalidDataException("the entry is encrypted");
                }

                (memberSha, memberSize, head) = Hash(open, entry.Crc32, bytes => limits.Check(treeBytes + walkedBytes + bytes, treeMembers + drafts.Count + 1, root.Size), cancellationToken);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or EndOfStreamException)
            {
                // An entry the archive lists and cannot deliver. It is still a member: recorded with the reason and
                // with a hash of its bytes as the archive stores them, never parsed.
                unreadable++;
                if (layout.Format == ContainerFormat.Zip && Unreadable(container, root, entry, archivePath, bodyPath, e.Message, cancellationToken) is { } kept)
                {
                    walkedBytes += kept.File.Size;
                    drafts.Add(kept);
                }

                return;
            }

            walkedBytes += memberSize;
            var detection = DetectionSample.Detect(open, memberSize, archivePath, _sources);
            drafts.Add(Draft(container, root, limits, entry, archivePath, memberSha, memberSize, head, detection));
        });

        var walked = clock.Elapsed;

        // 3. The records, a batch at a time, from where an earlier run of this job stopped.
        int recorded = 0, duplicates = 0, rejected = 0, parseJobs = 0, expandJobs = 0;
        for (var done = Math.Min(container.ExpandedMembers, drafts.Count); done < drafts.Count; done += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = drafts.GetRange(done, Math.Min(BatchSize, drafts.Count - done));
            await using var db = _store.CreateDbContext();
            var result = await FileOperations.AddMembersAsync(db, _store.Audit, container.Id, batch, done + batch.Count, _store.Clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
            recorded += result.Recorded;
            duplicates += result.Duplicates;
            rejected += result.Rejected;
            parseJobs += result.ParseJobs;
            expandJobs += result.ExpandJobs;
            AfterBatch?.Invoke(done + batch.Count);
        }

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{walk.Entries} entries: {recorded} recorded ({parseJobs} logs queued, {expandJobs} archives queued, {recorded - parseJobs - expandJobs} kept and not parsed), {duplicates} duplicates of bytes the case already holds, {rejected} refused, {walk.Skipped} directories and links skipped, {unreadable} unreadable (recorded with a hash of their stored bytes, among those kept)");
        summary += string.Create(CultureInfo.InvariantCulture,
            $"; {verified.TotalSeconds:F2} s to verify the archive{(build is null ? string.Empty : $" and write its {build.Bytes:N0}-byte seekable copy")}, {(walked - verified).TotalSeconds:F2} s to hash and detect {walkedBytes:N0} bytes of members, {(clock.Elapsed - walked).TotalSeconds:F2} s to record them");
        if (walk.Note is not null)
        {
            summary += "; " + walk.Note;
        }

        InputEnd? truncated = null;
        if (build is { End.IsTruncated: true })
        {
            truncated = build.End;
            summary += string.Create(CultureInfo.InvariantCulture, $"; the {Names.Of(layout.Compression)} stream around the archive is truncated at stored byte {build.End.StoredOffset}");
        }
        else if (walk.Truncated)
        {
            truncated = InputEnd.Truncated(new FileInfo(bodyPath).Length, container.Size);
        }

        return new ExpandOutcome(null, walk.Entries, summary, truncated, build is null ? null : Path.GetFileName(build.Path));
    }

    private MemberDraft Draft(EvidenceFile container, EvidenceFile root, ArchiveLimits limits, ArchiveEntry entry, string archivePath, string sha256, long size, byte[] head, DetectionResult detection)
    {
        var depth = container.ArchiveDepth + 1;
        var state = FileState.Artifact;
        if (detection.Kind == FileKind.Log)
        {
            state = FileState.Received;
        }
        else if (detection.Kind == FileKind.Container)
        {
            if (depth < limits.MaxDepth)
            {
                state = FileState.Received;
            }
            else
            {
                detection = detection with
                {
                    Message = string.Create(CultureInfo.InvariantCulture, $"an archive nested {depth} deep; archives are opened to a depth of {limits.MaxDepth}. Extract it and upload the contents"),
                };
            }
        }

        // A host the container was given by a person holds for everything inside it. Otherwise the member's own
        // path is asked, and failing that the container's inferred host is the best guess there is.
        long? hostId = null;
        string? hostName = null;
        var inferred = false;
        string? rule = null;
        if (container.HostId is not null && !container.HostInferred)
        {
            hostId = container.HostId;
        }
        else if (_hosts.Infer(archivePath) is { } proposal)
        {
            (hostName, inferred, rule) = (proposal.Host, true, proposal.Rule);
        }
        else if (container.HostId is not null)
        {
            (hostId, inferred, rule) = (container.HostId, true, container.HostRule);
        }

        // A year a person gave for the archive holds for everything in it; a derived one is each member's own.
        var year = IntakeHints.Year(container.YearGiven ? container.YearHint : null, entry.ModifiedUtc, head, container.ReceivedAt);
        var name = entry.Name.Length <= 1024 ? entry.Name : entry.Name[^1024..];
        var file = new EvidenceFile
        {
            CaseId = container.CaseId,
            HostId = hostId,
            ParentFileId = container.Id,
            RootFileId = root.Id,
            ArchiveDepth = depth,
            ArchivePath = archivePath.Length <= 4096 ? archivePath : archivePath[^4096..],
            MemberOrdinal = entry.Ordinal,
            MemberOffset = entry.Offset,
            Sha256 = sha256,
            Size = size,
            OriginalName = name,
            StoredPath = container.StoredPath,
            Source = container.Source,
            Uploader = container.Uploader,
            UploaderUserId = container.UploaderUserId,
            ReceivedAt = container.ReceivedAt,
            Timezone = container.Timezone,
            YearHint = year.Year,
            YearAssumed = year.Assumed,
            YearGiven = container.YearGiven,
            HostInferred = inferred,
            HostRule = rule,
            Encoding = Names.Of(detection.Encoding),
            Compression = detection.Compression == CompressionKind.None ? null : Names.Of(detection.Compression),
            DetectedKind = detection.Kind,
            DetectedSource = detection.SourceId,
            DetectedConfidence = detection.Confidence,
            DetectedLayout = detection.Layout,
            DetectedFormat = detection.Format,
            DetectedControlBytes = detection.Kind == Urlog.Core.Detection.FileKind.Log && detection.Flags.Contains(Urlog.Core.Detection.FormatDetector.FlagBinaryBytes),
            Detection = detection.ToJson(),
            DetectionRegistry = detection.RegistryKey,
            FileTime = entry.ModifiedUtc,
            State = state,
            ContainerCut = entry.Cut,
            InputTruncated = entry.Cut,
            TruncatedStoredOffset = entry.Cut ? size : null,
        };
        return new MemberDraft(file, hostName);
    }

    /// <summary>The record of a zip entry whose content cannot be read: its stored bytes hashed, the reason kept. Null when the zip's directory cannot be followed to it.</summary>
    private static MemberDraft? Unreadable(EvidenceFile container, EvidenceFile root, ArchiveEntry entry, string archivePath, string bodyPath, string reason, CancellationToken cancellationToken)
    {
        ZipStoredEntry? located = null;
        string sha256;
        long size;
        try
        {
            (sha256, size, _) = Hash(() => ArchiveWalker.OpenStoredZipEntry(bodyPath, entry.Ordinal, out located) ?? throw new InvalidDataException("the entry's stored bytes were not found"), null, _ => { }, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var file = new EvidenceFile
        {
            CaseId = container.CaseId,
            HostId = container.HostId,
            ParentFileId = container.Id,
            RootFileId = root.Id,
            ArchiveDepth = container.ArchiveDepth + 1,
            ArchivePath = archivePath.Length <= 4096 ? archivePath : archivePath[^4096..],
            MemberOrdinal = entry.Ordinal,
            MemberOffset = located!.DataOffset,
            Sha256 = sha256,
            Size = size,
            HashOfStoredBytes = true,
            StatedSize = located.StatedSize,
            UnreadableReason = string.Create(CultureInfo.InvariantCulture, $"{reason} (zip method {located.Method}{(located.Encrypted ? ", encrypted" : string.Empty)})"),
            OriginalName = entry.Name.Length <= 1024 ? entry.Name : entry.Name[^1024..],
            StoredPath = container.StoredPath,
            Source = container.Source,
            Uploader = container.Uploader,
            UploaderUserId = container.UploaderUserId,
            ReceivedAt = container.ReceivedAt,
            HostInferred = container.HostInferred,
            HostRule = container.HostRule,
            Encoding = Names.Of(InputEncoding.Utf8),
            FileTime = entry.ModifiedUtc,
            State = FileState.Unreadable,
        };
        return new MemberDraft(file, null);
    }

    /// <param name="expectedCrc">The CRC-32 the archive states for the content, checked here when it states one.</param>
    private static (string Sha256, long Size, byte[] Head) Hash(Func<Stream> open, uint? expectedCrc, Action<long> progress, CancellationToken cancellationToken)
    {
        var crc = expectedCrc is null ? null : new System.IO.Hashing.Crc32();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = open();
        var buffer = new byte[1 << 20];
        var head = Array.Empty<byte>();
        long size = 0;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (size == 0)
            {
                head = buffer.AsSpan(0, Math.Min(n, 256)).ToArray();
            }

            hash.AppendData(buffer, 0, n);
            crc?.Append(buffer.AsSpan(0, n));
            size += n;
            progress(size);
        }

        if (crc is not null && crc.GetCurrentHashAsUInt32() != expectedCrc)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"CRC-32 mismatch: the content read has {crc.GetCurrentHashAsUInt32():x8}, the archive states {expectedCrc:x8}"));
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), size, head);
    }

    private EvidenceFile Load(long id)
    {
        using var db = _store.CreateDbContext();
        return db.Files.AsNoTracking().First(f => f.Id == id);
    }
}
