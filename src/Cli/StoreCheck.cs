using Urlog.Core.Ingest;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;

namespace Urlog.Cli;

/// <summary>
/// The store identity check of the commands (design record, "Consistency", Store identity), run before a command's
/// first write and before the metadata migrations. <see cref="FullAsync"/> for a command whose records mean
/// something only in one event store (cases, files, jobs): both identities are read and compared.
/// <see cref="LocalAsync"/> for a command that writes the metadata file alone (accounts, a backup): the file must
/// have an identity, and the event store need not be up. <c>verify-audit</c> writes nothing and runs neither: the
/// chain of any metadata file can be verified, paired or not.
/// </summary>
internal static class StoreCheck
{
    public static async Task FullAsync(string metadataPath, Connection connection, CancellationToken ct)
    {
        Console.WriteLine(connection.AnnounceIngest());
        var events = new ClickHouseJobEventStore(connection.Ingest, connection.Inserter(1));
        try
        {
            await StoreIdentityGuard.RequireAsync(metadataPath, events, ct).ConfigureAwait(false);
        }
        catch (StoreIdentityException e)
        {
            throw new CliException(e.Message);
        }
        catch (EventStoreSchemaException e)
        {
            throw new CliException($"{e.Message} (connected as '{connection.IngestUser}')");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new CliException($"the event store at {connection.Server} did not answer as '{connection.IngestUser}' ({e.Message}); this command compares the store identity of {metadataPath} with the store's before it writes, and wrote nothing");
        }
    }

    public static async Task LocalAsync(string metadataPath, CancellationToken ct)
    {
        try
        {
            await StoreIdentityGuard.RequireLocalAsync(metadataPath, ct).ConfigureAwait(false);
        }
        catch (StoreIdentityException e)
        {
            throw new CliException(e.Message);
        }
    }
}
