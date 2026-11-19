using Urlog.Core.Ingest;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Lifecycle;

/// <summary>
/// What one case occupies. The recorded figures come from the intake records, the disk figures from the
/// directories as they are now, the store figures from <c>system.parts</c> by partition.
/// </summary>
/// <param name="Files">File records, archive members included.</param>
/// <param name="RecordedBytes">The sizes the intake records give for the files that arrived (members are inside their containers).</param>
/// <param name="IntakeBytes">Bytes under the case's directory in the evidence root, without unfinished uploads.</param>
/// <param name="UploadBytes">Bytes of unfinished uploads and their sidecars.</param>
/// <param name="CacheBytes">Bytes of seekable copies of containers: derived, rebuilt when missing.</param>
/// <param name="ArchiveBytes">Bytes of the case's archives under the archive root: what is left of an archived case, and kept after a re-import until a purge.</param>
/// <param name="StoreRows">Rows in the active parts of the case's partitions: every generation, and rows a delete has masked until a merge drops them.</param>
/// <param name="StoreBytes">Compressed bytes on the event store's disks.</param>
public sealed record CaseUsage(long CaseId, string Name, CaseState State, int Files, long RecordedBytes, long IntakeBytes, long UploadBytes, long CacheBytes, long ArchiveBytes, long StoreRows, long StoreBytes, IReadOnlyList<CasePartition> Partitions);

/// <summary>Disk accounting per case (design record, "Case lifecycle"), for the case dashboard and <c>urlog case usage</c>.</summary>
public static class CaseAccounting
{
    /// <summary>The usage of one case, or null when there is no such case.</summary>
    public static async Task<CaseUsage?> MeasureAsync(MetadataDb db, IJobEventStore events, LifecyclePaths paths, long caseId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(paths);
        var @case = await db.Cases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken).ConfigureAwait(false);
        if (@case is null)
        {
            return null;
        }

        var files = await db.Files.AsNoTracking().CountAsync(f => f.CaseId == caseId, cancellationToken).ConfigureAwait(false);
        var recorded = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId && f.ParentFileId == null).SumAsync(f => (long?)f.Size, cancellationToken).ConfigureAwait(false) ?? 0;
        long intake = 0, uploads = 0, cache = 0, archives = 0;
        if (!string.IsNullOrWhiteSpace(paths.ArchiveRoot))
        {
            archives = DirectoryBytes(CaseFolders.CaseDirectory(paths.ArchiveRoot, caseId));
        }

        if (!string.IsNullOrWhiteSpace(paths.EvidenceRoot))
        {
            var directory = CaseFolders.CaseDirectory(paths.EvidenceRoot, caseId);
            uploads = DirectoryBytes(Path.Combine(directory, CaseFolders.Uploads));
            intake = DirectoryBytes(directory) - uploads;
        }

        if (!string.IsNullOrWhiteSpace(paths.CacheRoot))
        {
            cache = DirectoryBytes(CaseFolders.CaseDirectory(paths.CacheRoot, caseId));
        }

        var partitions = await events.CasePartitionsAsync((ulong)caseId, cancellationToken).ConfigureAwait(false);
        return new CaseUsage(caseId, @case.Name, @case.State, files, recorded, intake, uploads, cache, archives, partitions.Sum(p => p.Rows), partitions.Sum(p => p.BytesOnDisk), partitions);
    }

    /// <summary>The bytes of the files under a directory; links are neither followed nor counted. 0 when it is not there.</summary>
    public static long DirectoryBytes(string directory)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists || GuardedDelete.IsLink(root))
        {
            return 0;
        }

        return Walk(root);
    }

    /// <summary>
    /// The walk below a directory that is known to be no link. An entry is taken for a link by the attributes the
    /// listing of its directory gave, without asking the file system about it again: a count reads nothing through
    /// an entry and removes nothing, and the second question costs more than the listing (measured on 10,000
    /// files: 2.3 s with it against the figures in the design record without).
    /// </summary>
    private static long Walk(DirectoryInfo directory)
    {
        long total = 0;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            total += entry is FileInfo file ? file.Length : Walk((DirectoryInfo)entry);
        }

        return total;
    }
}
