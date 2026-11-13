using System.Globalization;
using Urlog.Core.Audit;
using Urlog.Core.Ingest;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog pair --db &lt;metadata.db&gt; [--confirm &lt;identity&gt;]</c>: joins a metadata file to an event store by
/// hand (design record, "Consistency", Store identity). For a deployment from before the identity existed, a
/// metadata file restored from an old backup, or a store that was replaced. Never run by anything else.
/// <para>
/// The event store's identity is never changed: when it has one, the metadata file takes it. When it has none, it
/// gets the metadata file's, or a new one when neither has any. Before anything is written the command prints what
/// the two sides hold and what the next start of a runner will do to <c>file_generation</c> once they are paired,
/// and asks for the identity to be typed back. <c>--confirm</c> carries that answer for a script; without it and
/// without a terminal the command refuses. When neither side has an identity, <c>--confirm</c> names the one to
/// give them. The metadata file's change and its <c>store_paired</c> audit row are one transaction.
/// </para>
/// Runs as the admin account and holds the metadata file's runner lock: no worker or backend may be up on it.
/// </summary>
internal static class PairCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        if (args.Positional.Count != 0)
        {
            throw new CliException("urlog pair --db <metadata.db> [--confirm <identity>]");
        }

        var path = VerifyAuditCommand.MetadataPath(args, mustExist: false);
        var connection = new Connection(args);
        Console.WriteLine(connection.AnnounceAdmin());
        await using var admin = connection.Admin();
        var store = new EventStoreAdmin(admin);
        var storeId = await MigrateCommand.ReadStoreIdentityAsync(store, ct).ConfigureAwait(false);

        // A runner on this file would reconcile against the store while the identity changes under it.
        // A file that does not exist yet has no runner, and gets no lock file left beside its name.
        using var runnerLock = File.Exists(path) ? RunnerLock.TryAcquire(path) : null;
        if (runnerLock is null && File.Exists(path))
        {
            throw new CliException($"a job runner holds {RunnerLock.PathFor(path)}: stop the worker or the backend on this metadata file before pairing it");
        }

        var metadataId = await StoreIdentity.ReadAsync(path, ct).ConfigureAwait(false);
        if (metadataId is not null && string.Equals(metadataId, storeId, StringComparison.Ordinal))
        {
            Console.WriteLine($"paired    {path} and {connection.Server} already hold store identity {metadataId}; nothing to do");
            return 0;
        }

        var contents = await StoreIdentity.ReadContentsAsync(path, ct).ConfigureAwait(false);
        var mirrored = await store.ReadFileGenerationsAsync(ct).ConfigureAwait(false);
        var eventRows = await store.StoredEventRowsAsync(ct).ConfigureAwait(false);
        var report = Compare(contents, mirrored, eventRows);

        var asked = args.Option("confirm");
        var target = storeId ?? metadataId ?? (StoreIdentity.IsValid(asked) ? asked! : StoreIdentity.NewId());
        Print(path, connection.Server, metadataId, storeId, target, contents, report);

        if (asked is null)
        {
            if (Console.IsInputRedirected)
            {
                throw new CliException($"not confirmed: nothing was written. Pairing needs the store identity typed back on a terminal, or --confirm {target}");
            }

            Console.Write($"confirm   type {target} to pair these two, anything else to leave both as they are: ");
            asked = Console.ReadLine()?.Trim();
        }

        if (!string.Equals(asked, target, StringComparison.Ordinal))
        {
            throw new CliException($"not confirmed: the answer is not {target}. Nothing was written");
        }

        var actor = new AuditActor(Environment.UserName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var metadata = new MetadataStore(path);
        await metadata.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = metadata.CreateDbContext();
        if (!string.Equals(metadataId, target, StringComparison.Ordinal))
        {
            // The metadata file first: a stop here leaves a file that names the identity the next run gives the store.
            await StoreIdentityOperations.PairAsync(db, metadata.Audit, actor, target, connection.Server, report.Details(), metadata.Clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }

        if (storeId is null)
        {
            await store.WriteStoreIdentityAsync(target, connection.AdminUser, ct).ConfigureAwait(false);
        }

        Console.WriteLine($"paired    {path} and {connection.Server} hold store identity {target}");
        await UserCommand.WriteHeadAsync(db, ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>What pairing binds together, counted from the two sides as they are.</summary>
    internal static PairReport Compare(MetadataContents contents, IReadOnlyDictionary<ulong, uint> mirrored, long eventRows)
    {
        long same = 0, different = 0, missing = 0;
        foreach (var (fileId, generation) in contents.CommittedGenerations)
        {
            if (!mirrored.TryGetValue((ulong)fileId, out var current))
            {
                missing++;
            }
            else if (current == (uint)generation)
            {
                same++;
            }
            else
            {
                different++;
            }
        }

        long uncommitted = 0, unknown = 0, ingestRange = 0;
        foreach (var fileId in mirrored.Keys)
        {
            if (fileId >= (ulong)StoreIdentity.IngestFileIdFloor)
            {
                ingestRange++;
            }
            else if (!contents.FileIds.Contains((long)fileId))
            {
                unknown++;
            }
            else if (!contents.CommittedGenerations.ContainsKey((long)fileId))
            {
                uncommitted++;
            }
        }

        return new PairReport(contents.Cases, contents.FileIds.Count, contents.CommittedGenerations.Count, eventRows, mirrored.Count, same, different, missing, uncommitted, unknown, ingestRange);
    }

    private static void Print(string path, string server, string? metadataId, string? storeId, string target, MetadataContents contents, PairReport r)
    {
        var inv = CultureInfo.InvariantCulture;
        var highest = contents.FileIds.Count == 0 ? 0 : contents.FileIds.Max();
        Console.WriteLine($"metadata  {path}");
        Console.WriteLine($"          store identity {StoreIdentity.Show(metadataId)}");
        Console.WriteLine(string.Create(inv, $"          {r.Cases:N0} case(s), {r.Files:N0} file record(s) (highest id {highest}), {r.Committed:N0} with a committed generation"));
        Console.WriteLine($"store     {server}");
        Console.WriteLine($"          store identity {StoreIdentity.Show(storeId)}");
        Console.WriteLine(string.Create(inv, $"          {r.EventRows:N0} event row(s) in active parts, {r.GenerationRows:N0} file_generation row(s)"));
        Console.WriteLine("compare   files with a committed generation in the metadata file:");
        Console.WriteLine(string.Create(inv, $"            {r.Same:N0} with the same generation in the store"));
        Console.WriteLine(string.Create(inv, $"            {r.Different:N0} with a different generation in the store: the next start of a runner writes the metadata file's value over the store's"));
        Console.WriteLine(string.Create(inv, $"            {r.Missing:N0} with no row in the store: the next start of a runner writes one"));
        Console.WriteLine("          file ids with a row in the store and no committed generation in the metadata file:");
        Console.WriteLine(string.Create(inv, $"            {r.Uncommitted:N0} recorded in the metadata file"));
        Console.WriteLine(string.Create(inv, $"            {r.Unknown:N0} unknown to the metadata file, below {StoreIdentity.IngestFileIdFloor}: when the metadata file reaches such an id, the rows the store holds under it are hidden"));
        Console.WriteLine(string.Create(inv, $"            {r.IngestRange:N0} from {StoreIdentity.IngestFileIdFloor} up (written by urlog ingest; the metadata file never uses these)"));
        var storeSide = storeId is null ? $"the event store gets store identity {target}" : $"the event store keeps store identity {storeId}";
        var metadataSide = string.Equals(metadataId, target, StringComparison.Ordinal)
            ? $"the metadata file keeps {target}"
            : $"the metadata file takes {target} (it has {StoreIdentity.Show(metadataId)}), with a store_paired row in its audit chain";
        Console.WriteLine($"result    {storeSide}; {metadataSide}");
    }
}

/// <summary>The counts <c>urlog pair</c> shows before it asks, and writes into the <c>store_paired</c> audit row.</summary>
internal sealed record PairReport(long Cases, long Files, long Committed, long EventRows, long GenerationRows, long Same, long Different, long Missing, long Uncommitted, long Unknown, long IngestRange)
{
    public Dictionary<string, object?> Details() => new(StringComparer.Ordinal)
    {
        ["cases"] = Cases,
        ["files"] = Files,
        ["files_committed"] = Committed,
        ["store_event_rows"] = EventRows,
        ["store_generation_rows"] = GenerationRows,
        ["generation_same"] = Same,
        ["generation_different"] = Different,
        ["generation_missing_in_store"] = Missing,
        ["store_only_uncommitted"] = Uncommitted,
        ["store_only_unknown"] = Unknown,
        ["store_only_ingest_range"] = IngestRange,
    };
}
