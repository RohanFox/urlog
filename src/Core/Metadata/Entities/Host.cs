namespace Urlog.Core.Metadata;

/// <summary>
/// A host of one case. The same hostname in two cases is two rows (design record, "Data model"); within a case
/// <see cref="HostOperations.GetOrCreateAsync"/> deduplicates on the lowercased name, which is what the events column carries.
/// </summary>
public sealed class Host
{
    public long Id { get; init; }

    public long CaseId { get; init; }

    /// <summary>Lowercase, trimmed; unique within the case.</summary>
    public required string Name { get; init; }

    /// <summary>IANA zone for naive timestamps of this host's files; a per-file <see cref="EvidenceFile.Timezone"/> overrides it.</summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>True until an analyst sets the zone: the UI shows the "assumed" badge.</summary>
    public bool TimezoneAssumed { get; set; } = true;

    public DateTime CreatedAt { get; init; }
}
