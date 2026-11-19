namespace Urlog.Core.Metadata;

/// <summary>
/// A host of one case. The same hostname in two cases is two rows (design record, "Data model"); within a case
/// <see cref="HostOperations.GetOrCreateAsync"/> deduplicates on the lowercased name, which is what the events column carries.
/// </summary>
public sealed class Host
{
    public long Id { get; init; }

    public long CaseId { get; init; }

    /// <summary>Lowercase, trimmed; unique within the case. Changed by a rename only (<see cref="HostOperations.UpdateAsync"/>).</summary>
    public required string Name { get; set; }

    /// <summary>IANA zone for naive timestamps of this host's files; a per-file <see cref="EvidenceFile.Timezone"/> overrides it.</summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>True until an analyst sets the zone: the UI shows the "assumed" badge.</summary>
    public bool TimezoneAssumed { get; set; } = true;

    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Default source options for this host's files, a JSON object with the keys a file's own options use
    /// (<c>nginx.log_format</c>, ...); null when none. A file's own option wins over the host's.
    /// </summary>
    public string? SourceOptions { get; set; }

    /// <summary>
    /// Set when this host was merged into another one of the case. The row stays: events keep its id, and an id
    /// that is deleted can be handed out again. A file that names this host by name is given the surviving one.
    /// </summary>
    public long? MergedIntoId { get; set; }

    /// <summary>Raised by every change a person makes to the host; a write that names an older value is refused.</summary>
    public int Version { get; set; } = 1;
}
