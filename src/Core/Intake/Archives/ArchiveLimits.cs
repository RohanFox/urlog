using System.Globalization;
using System.Text.Json;

namespace Urlog.Core.Intake.Archives;

/// <summary>
/// What an archive may expand to before its job is failed (design record, "Intake"). The limits hold for a file
/// that arrived and everything inside it, to any depth: a small archive of archives is one tree.
/// </summary>
public sealed record ArchiveLimits
{
    /// <summary>Containers that are expanded, counted from the file that arrived. Default 3; one nested deeper is recorded and left closed.</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Decoded bytes per stored byte of the file that arrived. Default 200.</summary>
    public int MaxRatio { get; init; } = 200;

    /// <summary>The ratio is not looked at below this many decoded bytes: a small file of padding is not a bomb. Default 64 MiB.</summary>
    public long RatioFloorBytes { get; init; } = 64L << 20;

    /// <summary>Decoded bytes in the whole tree. Default 250 GiB.</summary>
    public long MaxTotalBytes { get; init; } = 250L << 30;

    /// <summary>Members in the whole tree. Default 100,000.</summary>
    public int MaxMembers { get; init; } = 100_000;

    public static ArchiveLimits Default { get; } = new();

    public void Validate()
    {
        if (MaxDepth < 0 || MaxRatio < 1 || RatioFloorBytes < 0 || MaxTotalBytes < 1 || MaxMembers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ArchiveLimits), ToString(), "archive limits are a depth of 0 or more and positive caps");
        }
    }

    /// <summary>Throws when the tree has outgrown a cap.</summary>
    /// <param name="decodedBytes">Decoded bytes of the tree so far.</param>
    /// <param name="members">Members of the tree so far.</param>
    /// <param name="storedBytes">Stored size of the file that arrived.</param>
    /// <exception cref="ArchiveLimitException">A cap is exceeded; the message names it and the measured value.</exception>
    public void Check(long decodedBytes, long members, long storedBytes)
    {
        if (members > MaxMembers)
        {
            throw new ArchiveLimitException("members", members, MaxMembers,
                string.Create(CultureInfo.InvariantCulture, $"the archive holds more than {MaxMembers:N0} members ({members:N0} counted so far); nothing of it was recorded"));
        }

        if (decodedBytes > MaxTotalBytes)
        {
            throw new ArchiveLimitException("total_bytes", decodedBytes, MaxTotalBytes,
                string.Create(CultureInfo.InvariantCulture, $"the archive expands to more than {MaxTotalBytes:N0} bytes ({decodedBytes:N0} decoded so far); nothing of it was recorded"));
        }

        if (decodedBytes > RatioFloorBytes && decodedBytes / MaxRatio > Math.Max(storedBytes, 1))
        {
            throw new ArchiveLimitException("ratio", decodedBytes / Math.Max(storedBytes, 1), MaxRatio,
                string.Create(CultureInfo.InvariantCulture, $"the input expands more than {MaxRatio}:1 ({decodedBytes:N0} bytes decoded so far from {storedBytes:N0} stored); nothing of it was committed"));
        }
    }

    /// <summary>The limits as the audit row and the job row carry them.</summary>
    public Dictionary<string, object?> ToDetails() => new(StringComparer.Ordinal)
    {
        ["max_depth"] = MaxDepth,
        ["max_ratio"] = MaxRatio,
        ["ratio_floor_bytes"] = RatioFloorBytes,
        ["max_total_bytes"] = MaxTotalBytes,
        ["max_members"] = MaxMembers,
    };

    public string ToJson() => JsonSerializer.Serialize(ToDetails());

    /// <summary>Reads what <see cref="ToJson"/> wrote; a key that is missing keeps the value of <paramref name="fallback"/>.</summary>
    public static ArchiveLimits FromJson(string? json, ArchiveLimits fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        long Read(string key, long otherwise) => root.TryGetProperty(key, out var value) && value.TryGetInt64(out var number) ? number : otherwise;
        var limits = new ArchiveLimits
        {
            MaxDepth = (int)Read("max_depth", fallback.MaxDepth),
            MaxRatio = (int)Read("max_ratio", fallback.MaxRatio),
            RatioFloorBytes = Read("ratio_floor_bytes", fallback.RatioFloorBytes),
            MaxTotalBytes = Read("max_total_bytes", fallback.MaxTotalBytes),
            MaxMembers = (int)Read("max_members", fallback.MaxMembers),
        };
        limits.Validate();
        return limits;
    }
}

/// <summary>An archive outgrew a cap. <see cref="Cap"/> is <c>members</c>, <c>total_bytes</c> or <c>ratio</c>.</summary>
public sealed class ArchiveLimitException : Exception
{
    public ArchiveLimitException()
    {
    }

    public ArchiveLimitException(string message)
        : base(message)
    {
    }

    public ArchiveLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ArchiveLimitException(string cap, long measured, long limit, string message)
        : base(message)
    {
        Cap = cap;
        Measured = measured;
        Limit = limit;
    }

    public string Cap { get; } = string.Empty;

    public long Measured { get; }

    public long Limit { get; }
}
