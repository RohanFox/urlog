using System.Globalization;

namespace Urlog.Core.Lifecycle;

/// <summary>The folders intake keeps inside a case's directory under the evidence root.</summary>
public static class CaseFolders
{
    /// <summary>Uploads that are still arriving, with their sidecars.</summary>
    public const string Uploads = ".uploads";

    /// <summary>Uploads that arrived whole and are stored evidence.</summary>
    public const string Web = "web";

    /// <summary>Where the directory watcher puts a file dropped for a case that is no longer active. Nothing writes it yet.</summary>
    public const string Late = "late";

    /// <summary><c>&lt;root&gt;/&lt;case id&gt;</c>.</summary>
    public static string CaseDirectory(string root, long caseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.Combine(Path.GetFullPath(root), caseId.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>How the case lifecycle's own work runs: the purge window and approval, and the integrity sweep.</summary>
public sealed record LifecycleOptions
{
    /// <summary>A purge needs two different administrator accounts, and nothing is purged on schedule. Off by default.</summary>
    public bool TwoPersonPurge { get; init; }

    /// <summary>How long a soft-deleted case can be restored before the scheduled purge takes it.</summary>
    public TimeSpan PurgeAfter { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Purge soft-deleted cases whose window has passed, as <c>system</c>. Without effect while <see cref="TwoPersonPurge"/> is on.</summary>
    public bool ScheduledPurge { get; init; } = true;

    /// <summary>The UTC time of day from which the integrity sweep reads every stored file once; null turns the schedule off.</summary>
    public TimeOnly? SweepAtUtc { get; init; } = new TimeOnly(3, 0);

    /// <summary>How often the runner looks for lifecycle work.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Verified files written to the audit chain in one transaction.</summary>
    public int SweepBatch { get; init; } = 200;

    /// <summary>Bytes of one file the sweep reads before it asks again whether a job has started.</summary>
    public int SweepYieldBytes { get; init; } = 16 << 20;

    /// <summary>Bytes kept free on the archive volume on top of the size of the case's stored files, or the archive is not started.</summary>
    public long ArchiveHeadroomBytes { get; init; } = 1L << 30;

    public void Validate()
    {
        if (PurgeAfter < TimeSpan.Zero || Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PurgeAfter), "the purge window cannot be negative and the interval must be positive");
        }

        if (SweepBatch < 1 || SweepYieldBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SweepBatch), "the sweep's batch and yield sizes must be positive");
        }
    }
}
