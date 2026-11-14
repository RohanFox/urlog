using System.Globalization;
using Urlog.Core.Audit;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Jobs;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog case create|close|reopen|soft-delete|restore|purge|usage</c>: a case and its lifecycle from the box
/// (design record, "Case lifecycle"). Every verb writes its audit row in the transaction that changes the case.
/// The actor is the OS user; with <c>--as &lt;user name&gt;</c> it is that account, held to the role the operation
/// asks for (an owner of the case, or an administrator), and the row also names the OS user that ran the command
/// (<c>os_user</c>).
/// </summary>
internal static class CaseCommand
{
    private const string Usage = """
        urlog case create --db <metadata.db> --name <case name> --owner <user name>
        urlog case close|reopen|restore --db <metadata.db> --case <id> [--as <user name>]
        urlog case soft-delete --db <metadata.db> --case <id> --confirm-name <case name> [--as <user name>]
        urlog case purge --db <metadata.db> --case <id> [--as <admin name>] [--two-person] [--evidence-root dir] [--cache-root dir]
        urlog case archive|reimport --db <metadata.db> --case <id> --evidence-root dir --archive-root dir [--as <user name>] [--cache-root dir]
        urlog case usage --db <metadata.db> [--case <id>] [--evidence-root dir] [--cache-root dir] [--archive-root dir]
        """;

    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var verb = args.Positional.Count > 0 ? args.Positional[0].ToLowerInvariant() : "";
        if (verb is not ("create" or "close" or "reopen" or "soft-delete" or "restore" or "purge" or "usage" or "archive" or "reimport"))
        {
            throw new CliException(Usage);
        }

        var path = VerifyAuditCommand.MetadataPath(args, mustExist: verb != "create");
        var connection = new Connection(args);
        // A case id is a partition key in one event store: both identities are compared.
        await StoreCheck.FullAsync(path, connection, ct).ConfigureAwait(false);
        var store = new MetadataStore(path);
        await store.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = store.CreateDbContext();
        var now = store.Clock.GetUtcNow().UtcDateTime;
        try
        {
            switch (verb)
            {
                case "create":
                    await CreateAsync(args, store, db, now, ct).ConfigureAwait(false);
                    break;
                case "usage":
                    await UsageAsync(args, store, db, connection, ct).ConfigureAwait(false);
                    return 0;
                case "purge":
                    await PurgeAsync(args, store, db, connection, now, ct).ConfigureAwait(false);
                    break;
                case "archive":
                    await ArchiveAsync(args, store, db, connection, ct).ConfigureAwait(false);
                    break;
                case "reimport":
                    await ReimportAsync(args, store, db, connection, ct).ConfigureAwait(false);
                    break;
                default:
                    await MoveAsync(verb, args, store, db, now, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (MetadataException e)
        {
            throw new CliException(e.Message);
        }

        await UserCommand.WriteHeadAsync(db, ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The lifecycle settings a command line gives: <c>--two-person</c> or <c>URLOG_TWO_PERSON_PURGE=1</c>.</summary>
    internal static LifecycleOptions Lifecycle(CliArgs args) => new()
    {
        TwoPersonPurge = args.Flag("two-person") || Environment.GetEnvironmentVariable("URLOG_TWO_PERSON_PURGE") is "1" or "true",
    };

    /// <summary>Where a command finds the case directories and puts the backup, by the worker's rules.</summary>
    internal static LifecyclePaths Paths(CliArgs args, MetadataStore store)
    {
        var evidence = args.Option("evidence-root");
        var cache = args.Option("cache-root") ?? (evidence is null ? Path.Combine(Path.GetDirectoryName(store.Path)!, "archive-cache") : null);
        var paths = new LifecyclePaths(evidence, new MemberStore(evidence, cache).CacheRoot, Environment.GetEnvironmentVariable("URLOG_BACKUP_DIR") ?? SqliteBackup.DefaultDirectory(store.Path), args.Option("archive-root") ?? Environment.GetEnvironmentVariable("URLOG_ARCHIVE_ROOT"));
        try
        {
            paths.CheckedArchiveRoot();
        }
        catch (ArgumentException e)
        {
            throw new CliException(e.Message);
        }

        return paths;
    }

    private static async Task ArchiveAsync(CliArgs args, MetadataStore store, MetadataDb db, Connection connection, CancellationToken ct)
    {
        var caseId = CaseId(args);
        var actor = await ActorAsync(args, db, ct).ConfigureAwait(false);
        var paths = Paths(args, store);
        if (paths.ArchiveRoot is null || paths.EvidenceRoot is null)
        {
            throw new CliException("case archive needs --evidence-root (where the stored files are) and --archive-root (where the archive goes)");
        }

        var events = new ClickHouseJobEventStore(connection.Ingest, connection.Inserter(1));
        var executor = new LifecycleExecutor(store, events, Lifecycle(args), paths, NullLogger.Instance);
        var stray = await executor.StrayEntriesAsync(caseId, ct).ConfigureAwait(false);
        if (stray.Count > 0)
        {
            throw new CliException($"case {caseId} is not archived: its directory holds {stray.Count} entr{(stray.Count == 1 ? "y" : "ies")} that no file record accounts for, and an archive removes the directory:\n  " + string.Join("\n  ", stray.Take(20)));
        }

        var task = await ArchiveOperations.RequestArchiveAsync(db, actor, caseId, store.Clock, ct).ConfigureAwait(false);
        using var runnerLock = RunnerLock.TryAcquire(store.Path);
        if (runnerLock is null)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {caseId}: archive asked for; the runner that holds {RunnerLock.PathFor(store.Path)} carries it out (it needs the archive root)"));
            return;
        }

        try
        {
            var flagged = await executor.ArchiveAsync(task, ct).ConfigureAwait(false);
            var done = await db.CaseTasks.AsNoTracking().FirstAsync(t => t.Id == task.Id, ct).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {caseId}: archived to {done.ArchiveDirectory}"));
            Console.WriteLine($"tar       sha256 {done.TarSha256}");
            Console.WriteLine($"manifest  sha256 {done.ManifestSha256}");
            foreach (var flag in flagged)
            {
                // Archived as found, and said so: here, in the manifest and in the case_archive row.
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"flagged   file {flag.FileId} '{flag.OriginalName}': recorded {flag.ExpectedSha256} ({flag.ExpectedSize} bytes), found {flag.ActualSha256}{(flag.ActualSize is { } size ? $" ({size} bytes)" : "")}"));
            }
        }
        catch (Exception e) when (e is ArchiveRefusedException or StoreIdentityException or DeletionRefusedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            var gaveUp = await executor.AfterArchiveFailureAsync(task, e).ConfigureAwait(false);
            throw new CliException($"case {caseId}: the archive stopped: {e.Message}. {(gaveUp ? "It is given up; the case is as it was and nothing of it was dropped or removed." : "Nothing is dropped or removed before the archive is verified and committed; 'urlog worker --once --archive-root <dir>' or the backend takes it up again.")}");
        }
    }

    private static async Task ReimportAsync(CliArgs args, MetadataStore store, MetadataDb db, Connection connection, CancellationToken ct)
    {
        var caseId = CaseId(args);
        var actor = await ActorAsync(args, db, ct).ConfigureAwait(false);
        var paths = Paths(args, store);
        if (paths.ArchiveRoot is null || paths.EvidenceRoot is null)
        {
            throw new CliException("case reimport needs --archive-root (where the archive is) and --evidence-root (where the files go back)");
        }

        var task = await ArchiveOperations.RequestReimportAsync(db, actor, caseId, store.Clock, ct).ConfigureAwait(false);
        using var runnerLock = RunnerLock.TryAcquire(store.Path);
        if (runnerLock is null)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {caseId}: re-import asked for; the runner that holds {RunnerLock.PathFor(store.Path)} carries it out"));
            return;
        }

        var events = new ClickHouseJobEventStore(connection.Ingest, connection.Inserter(1));
        var executor = new LifecycleExecutor(store, events, Lifecycle(args), paths, NullLogger.Instance);
        try
        {
            var closed = await executor.ReimportAsync(task, ct).ConfigureAwait(false);
            var after = await db.CaseTasks.AsNoTracking().FirstAsync(t => t.Id == task.Id, ct).ConfigureAwait(false);
            if (after.State == CaseTaskState.Failed)
            {
                throw new CliException($"case {caseId}: re-import refused, nothing was restored: {after.Message}");
            }

            Console.WriteLine(closed
                ? string.Create(CultureInfo.InvariantCulture, $"case {caseId}: re-imported; it had nothing to parse and is closed")
                : string.Create(CultureInfo.InvariantCulture, $"case {caseId}: every file verified and back in place; the parses are queued. 'urlog worker --once --evidence-root <dir> --archive-root <dir>' or the backend runs them, and the case is closed when the last has ended"));
        }
        catch (Exception e) when (e is StoreIdentityException or DeletionRefusedException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new CliException($"case {caseId}: the re-import stopped: {e.Message}. The case stays archived and its archive is untouched; run the command's work again with 'urlog worker --once'.");
        }
    }

    private static async Task CreateAsync(CliArgs args, MetadataStore store, MetadataDb db, DateTime now, CancellationToken ct)
    {
        var name = args.Required("name");
        var ownerName = args.Required("owner");
        var owner = await UserOperations.FindByNameAsync(db, UserOperations.NormalizeName(ownerName), ct).ConfigureAwait(false)
            ?? throw new CliException($"no user named '{ownerName}'");
        var @case = await CaseOperations.CreateAsync(db, store.Audit, new AuditActor(Environment.UserName), name, owner.Id, now, ct).ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {@case.Id} '{@case.Name}' created, owner {owner.Name} (id {owner.Id})"));
    }

    private static async Task MoveAsync(string verb, CliArgs args, MetadataStore store, MetadataDb db, DateTime now, CancellationToken ct)
    {
        var caseId = CaseId(args);
        var actor = await ActorAsync(args, db, ct).ConfigureAwait(false);
        var @case = verb switch
        {
            "close" => await LifecycleOperations.CloseAsync(db, store.Audit, actor, caseId, store.Clock, ct).ConfigureAwait(false),
            "reopen" => await LifecycleOperations.ReopenAsync(db, store.Audit, actor, caseId, store.Clock, ct).ConfigureAwait(false),
            "restore" => await LifecycleOperations.RestoreAsync(db, store.Audit, actor, caseId, store.Clock, ct).ConfigureAwait(false),
            _ => await LifecycleOperations.SoftDeleteAsync(db, store.Audit, actor, caseId, args.Required("confirm-name"), Lifecycle(args).PurgeAfter, store.Clock, ct).ConfigureAwait(false),
        };
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {@case.Id} '{@case.Name}' is {Names.Of(@case.State)} (by {actor.Name})"));
    }

    private static async Task PurgeAsync(CliArgs args, MetadataStore store, MetadataDb db, Connection connection, DateTime now, CancellationToken ct)
    {
        var caseId = CaseId(args);
        var actor = await ActorAsync(args, db, ct).ConfigureAwait(false);
        var options = Lifecycle(args);
        var task = await LifecycleOperations.RequestPurgeAsync(db, store.Audit, actor, caseId, options.TwoPersonPurge, store.Clock, ct).ConfigureAwait(false);
        if (task.State == CaseTaskState.Requested)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {caseId}: purge asked for by {task.RequestedBy}; it starts when a second administrator runs the same command with --as"));
            return;
        }

        using var runnerLock = RunnerLock.TryAcquire(store.Path);
        if (runnerLock is null)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {caseId}: purge released; the runner that holds {RunnerLock.PathFor(store.Path)} carries it out"));
            return;
        }

        var events = new ClickHouseJobEventStore(connection.Ingest, connection.Inserter(1));
        var executor = new LifecycleExecutor(store, events, options, Paths(args, store), NullLogger.Instance);
        try
        {
            var purged = await executor.PurgeAsync(caseId, task, ct).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {caseId}: {(purged ? "purged" : "not purged: the request was withdrawn")}"));
        }
        catch (Exception e) when (e is StoreIdentityException or DeletionRefusedException or InvalidOperationException or IOException)
        {
            throw new CliException($"case {caseId}: the purge stopped: {e.Message}. What is done stays done; 'urlog worker --once' or the backend takes it up again.");
        }
    }

    private static async Task UsageAsync(CliArgs args, MetadataStore store, MetadataDb db, Connection connection, CancellationToken ct)
    {
        var events = new ClickHouseJobEventStore(connection.Ingest, connection.Inserter(1));
        var paths = Paths(args, store);
        var ids = args.Option("case") is null
            ? await db.Cases.AsNoTracking().OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false)
            : [CaseId(args)];
        Console.WriteLine("case  state         files  recorded_bytes  intake_bytes  upload_bytes  cache_bytes  archive_bytes  store_rows  store_bytes  months  name");
        foreach (var id in ids)
        {
            var usage = await CaseAccounting.MeasureAsync(db, events, paths, id, ct).ConfigureAwait(false) ?? throw new CliException($"no case {id}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{usage.CaseId,-5} {Names.Of(usage.State),-13} {usage.Files,-6} {usage.RecordedBytes,-15} {usage.IntakeBytes,-13} {usage.UploadBytes,-13} {usage.CacheBytes,-12} {usage.ArchiveBytes,-14} {usage.StoreRows,-11} {usage.StoreBytes,-12} {usage.Partitions.Count,-7} {usage.Name}"));
        }
    }

    private static long CaseId(CliArgs args) =>
        long.TryParse(args.Required("case"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : throw new CliException("--case takes a case id");

    private static async Task<AuditActor> ActorAsync(CliArgs args, MetadataDb db, CancellationToken ct)
    {
        if (args.Option("as") is not { } name)
        {
            return new AuditActor(Environment.UserName);
        }

        var user = await UserOperations.FindByNameAsync(db, UserOperations.NormalizeName(name), ct).ConfigureAwait(false) ?? throw new CliException($"no user named '{name}'");
        return new AuditActor(user.Name, user.Id) { OsUser = Environment.UserName };
    }
}
