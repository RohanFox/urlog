using System.Globalization;
using Urlog.Core.Ingest;

namespace Urlog.Core.Jobs;

/// <summary>Free and total bytes of the volume holding a local path.</summary>
public interface IDiskProbe
{
    DiskSpace Measure(string path);
}

/// <summary>The operating system's figures for the volume a path is on.</summary>
public sealed class DriveDiskProbe : IDiskProbe
{
    public DiskSpace Measure(string path)
    {
        var full = Path.GetFullPath(path);
        // Windows wants the root; elsewhere DriveInfo resolves any path to its mount.
        var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full);
        return new DiskSpace(full, drive.AvailableFreeSpace, drive.TotalSize);
    }
}

/// <summary>The mark: ingest pauses while any watched volume has less free space than the larger of the two.</summary>
public sealed record WatermarkOptions
{
    /// <summary>Absolute floor. Default 20 GB.</summary>
    public long MinFreeBytes { get; init; } = 20L << 30;

    /// <summary>Floor as a share of the volume. Default 10 %.</summary>
    public int MinFreePercent { get; init; } = 10;

    /// <summary>Local paths whose volumes are watched (the metadata directory is always one of them).</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];
}

/// <summary>One reading of the watched volumes.</summary>
/// <param name="Below">The volume furthest below its mark, or null when all are above.</param>
/// <param name="MarkBytes">The mark of that volume.</param>
public sealed record WatermarkReading(DiskSpace? Below, long MarkBytes, IReadOnlyList<DiskSpace> Volumes)
{
    public bool Paused => Below is not null;

    /// <summary>What a queued job's message says while the queue is paused.</summary>
    public string Describe() => Below is null
        ? "free space is above the mark on every watched volume"
        : string.Create(CultureInfo.InvariantCulture, $"{Gb(Below.FreeBytes)} GB free on {Below.Name}, below the mark of {Gb(MarkBytes)} GB; ingest resumes when space is freed");

    public Dictionary<string, object?> Details() => new(StringComparer.Ordinal)
    {
        ["volume"] = Below?.Name,
        ["free_bytes"] = Below?.FreeBytes,
        ["total_bytes"] = Below?.TotalBytes,
        ["mark_bytes"] = Below is null ? null : MarkBytes,
    };

    private static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>
/// The global disk watermark (design record, "Data model"): the local volumes the runner can see plus the event
/// store's own disks, each against <c>max(MinFreeBytes, MinFreePercent of the volume)</c>.
/// </summary>
public sealed class DiskWatermark(WatermarkOptions options, IDiskProbe probe, IReadOnlyList<string> localPaths)
{
    public long MarkFor(DiskSpace volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return Math.Max(options.MinFreeBytes, volume.TotalBytes / 100 * options.MinFreePercent);
    }

    /// <summary>Reads every volume. <paramref name="storeDisks"/> are the event store's, already read (empty when it could not be asked).</summary>
    public WatermarkReading Read(IReadOnlyList<DiskSpace> storeDisks)
    {
        ArgumentNullException.ThrowIfNull(storeDisks);
        var volumes = new List<DiskSpace>();
        foreach (var path in localPaths.Concat(options.Paths).Distinct(StringComparer.Ordinal))
        {
            volumes.Add(probe.Measure(path));
        }

        volumes.AddRange(storeDisks);
        DiskSpace? worst = null;
        long worstShortfall = 0;
        foreach (var volume in volumes)
        {
            var shortfall = MarkFor(volume) - volume.FreeBytes;
            if (shortfall > worstShortfall)
            {
                worst = volume;
                worstShortfall = shortfall;
            }
        }

        return new WatermarkReading(worst, worst is null ? 0 : MarkFor(worst), volumes);
    }
}
