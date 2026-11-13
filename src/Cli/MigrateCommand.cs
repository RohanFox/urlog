using ClickHouse.Driver.Tcp;
using Urlog.Core.Audit;
using Urlog.Core.Ingest;
using Urlog.Core.Metadata;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog migrate [--db &lt;metadata.db&gt;]</c>: applies the embedded ClickHouse migrations as the <b>admin</b>
/// account (<c>--admin-user</c>/<c>--admin-password</c> or <c>URLOG_CH_ADMIN_USER</c>/<c>URLOG_CH_ADMIN_PASSWORD</c>).
/// The only command that changes the schema: <c>urlog ingest</c> and the backend run as <c>urlog_ingest</c>,
/// which holds no DDL grant and cannot read <c>schema_migrations</c>. The account that runs this is the
/// definer of <c>events_v</c> (ADR 0001): never drop or rename it afterwards.
/// <para>
/// With <c>--db</c> it is also where a new deployment gets its store identity (design record, "Consistency", Store
/// identity): when the metadata file has no file record and the event store holds nothing, one value is generated
/// and written to both, the metadata file first. A pair that already agrees is left alone. Every other combination
/// is refused and named: joining a metadata file and a store that hold data is <c>urlog pair</c>, by hand.
/// </para>
/// </summary>
internal static class MigrateCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        if (args.Positional.Count != 0)
        {
            throw new CliException("migrate takes no argument");
        }

        var connection = new Connection(args);
        Console.WriteLine(connection.AnnounceAdmin());
        await using var admin = connection.Admin();
        MigrationReport report;
        try
        {
            report = await new ClickHouseMigrator(admin).ApplyAsync(MigrationSet.Embedded(), ct).ConfigureAwait(false);
        }
        catch (ClickHouseTcpServerException e) when (e.RawCode == 497)
        {
            throw new CliException($"the account '{connection.AdminUser}' may not change the schema (server code 497); migrate runs as the admin account: --admin-user/--admin-password or URLOG_CH_ADMIN_USER/URLOG_CH_ADMIN_PASSWORD");
        }

        Console.WriteLine(report.Applied.Count > 0
            ? $"schema    applied migrations {string.Join(", ", report.Applied)}; already applied: {report.AlreadyApplied.Count}"
            : $"schema    up to date ({report.AlreadyApplied.Count} migrations)");

        var store = new EventStoreAdmin(admin);
        var storeId = await ReadStoreIdentityAsync(store, ct).ConfigureAwait(false);
        var option = args.Option("db") ?? Environment.GetEnvironmentVariable("URLOG_DB");
        if (option is null)
        {
            Console.WriteLine(storeId is null
                ? "identity  the event store has none; 'urlog migrate --db <metadata file>' gives a new deployment its identity, 'urlog pair --db <metadata file>' joins a store that holds data to its metadata file"
                : $"identity  {storeId} (event store)");
            return 0;
        }

        var path = Path.GetFullPath(option);
        var metadataId = await StoreIdentity.ReadAsync(path, ct).ConfigureAwait(false);
        if (metadataId is not null && string.Equals(metadataId, storeId, StringComparison.Ordinal))
        {
            await new MetadataStore(path).MigrateAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"identity  {metadataId}: {path} and {connection.Server} agree");
            return 0;
        }

        var contents = await StoreIdentity.ReadContentsAsync(path, ct).ConfigureAwait(false);
        var generationRows = (await store.ReadFileGenerationsAsync(ct).ConfigureAwait(false)).Count;
        var eventRows = await store.StoredEventRowsAsync(ct).ConfigureAwait(false);
        if (storeId is not null || contents.FileIds.Count > 0 || generationRows > 0 || eventRows > 0)
        {
            throw new CliException(
                $"metadata file {path} has store identity {StoreIdentity.Show(metadataId)} and {contents.FileIds.Count:N0} file record(s); event store {connection.Server} has {StoreIdentity.Show(storeId)}, {eventRows:N0} event row(s) and {generationRows:N0} file_generation row(s). " +
                "migrate gives an identity only to a metadata file without file records and a store that holds nothing: no identity was written and the metadata file was not touched. " +
                "If these two belong together (an upgrade, a restored backup), 'urlog pair --db <file>' joins them after a confirmation.");
        }

        // The metadata file first: a stop between the two leaves a file that names an identity the empty store takes
        // on the next run.
        var id = metadataId ?? StoreIdentity.NewId();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var metadata = new MetadataStore(path);
        await metadata.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = metadata.CreateDbContext();
        if (metadataId is null)
        {
            await StoreIdentityOperations.CreateAsync(db, metadata.Audit, new AuditActor(Environment.UserName), id, connection.Server, metadata.Clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }

        await store.WriteStoreIdentityAsync(id, connection.AdminUser, ct).ConfigureAwait(false);
        Console.WriteLine($"identity  {id} created: written to {path} and to {connection.Server}");
        await UserCommand.WriteHeadAsync(db, ct).ConfigureAwait(false);
        return 0;
    }

    internal static async Task<string?> ReadStoreIdentityAsync(EventStoreAdmin store, CancellationToken ct)
    {
        try
        {
            return await store.ReadStoreIdentityAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EventStoreSchemaException or StoreIdentityException)
        {
            throw new CliException(e.Message);
        }
    }
}
