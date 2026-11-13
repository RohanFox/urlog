using Urlog.Core.Metadata;

namespace Urlog.Core.Jobs;

/// <summary>
/// The check that a metadata file and an event store belong together (design record, "Consistency", Store
/// identity). The runner, the backend and every command that writes run it before their first write to either
/// store, and before the metadata migrations: a file that fails it is left as it was found.
/// </summary>
public static class StoreIdentityGuard
{
    /// <summary>
    /// Reads both identities and returns the common value.
    /// </summary>
    /// <exception cref="StoreIdentityException">They differ, or one side has none. The message names both.</exception>
    public static async Task<string> RequireAsync(string metadataPath, IStoreIdentitySource events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        var metadataId = await StoreIdentity.ReadAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        var storeId = await events.ReadStoreIdentityAsync(cancellationToken).ConfigureAwait(false);
        return Compare(metadataPath, metadataId, events.Endpoint, storeId);
    }

    /// <summary>Returns the common value of two identities already read, or refuses.</summary>
    public static string Compare(string metadataPath, string? metadataId, string server, string? storeId)
    {
        if (metadataId is not null && string.Equals(metadataId, storeId, StringComparison.Ordinal))
        {
            return metadataId;
        }

        var advice = metadataId is null && storeId is null
            ? "A new deployment gets its identity from 'urlog migrate --db <file>'; a metadata file and a store that already hold data are joined once with 'urlog pair --db <file>'."
            : "If this file is meant for this store (an upgrade, a restored backup), 'urlog pair --db <file>' joins them after a confirmation.";
        throw new StoreIdentityException(
            $"metadata file {metadataPath} has store identity {StoreIdentity.Show(metadataId)}, event store {server} has {StoreIdentity.Show(storeId)}: they do not belong together and nothing was written. {advice}",
            metadataId,
            storeId);
    }

    /// <summary>
    /// For a command that writes the metadata file only: the file must have been given an identity, and the event
    /// store is not asked. Returns the identity.
    /// </summary>
    /// <exception cref="StoreIdentityException">The file has no identity.</exception>
    public static async Task<string> RequireLocalAsync(string metadataPath, CancellationToken cancellationToken = default)
    {
        var metadataId = await StoreIdentity.ReadAsync(metadataPath, cancellationToken).ConfigureAwait(false);
        return metadataId ?? throw new StoreIdentityException(
            $"metadata file {metadataPath} has store identity none: it was never joined to an event store and nothing was written. A new deployment gets its identity from 'urlog migrate --db <file>'; a file from before the identity existed is joined to its store once with 'urlog pair --db <file>'.",
            null,
            null);
    }
}
