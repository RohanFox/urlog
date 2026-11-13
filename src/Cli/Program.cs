using System.Diagnostics;
using Urlog.Cli;
using Urlog.Core;

// urlog migrate | ingest | enqueue | expand | worker | query | bench | verify-audit | backup | user | case.
if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    Usage();
    return args.Length == 0 ? 2 : 0;
}

var cli = new CliArgs(args, 1);
if (cli.Flag("slo"))
{
    // The SLO box has 8 cores: pin to 8 threads like the benches' -Slo switch (bench/Program.cs).
    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
    {
        Process.GetCurrentProcess().ProcessorAffinity = (nint)0xFF;
    }
    else
    {
        Console.Error.WriteLine("urlog: --slo (processor affinity) is not available on this platform");
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return args[0].ToLowerInvariant() switch
    {
        "migrate" => await MigrateCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "pair" => await PairCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "ingest" => await IngestCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "enqueue" => await EnqueueCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "expand" => await ExpandCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "worker" => await WorkerCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "query" => await QueryCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "bench" => await BenchCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "verify-audit" => await VerifyAuditCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "backup" => await BackupCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "user" => await UserCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "case" => await CaseCommand.RunAsync(cli, cts.Token).ConfigureAwait(false),
        "version" => Version(),
        _ => Unknown(args[0]),
    };
}
catch (CliException e)
{
    Console.Error.WriteLine("urlog: " + e.Message);
    return 2;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("urlog: cancelled");
    return 130;
}

static int Version()
{
    Console.WriteLine($"urlog {typeof(CoreInfo).Assembly.GetName().Version} targeting ClickHouse {CoreInfo.ClickHouseLts} LTS");
    return 0;
}

static int Unknown(string verb)
{
    Console.Error.WriteLine($"urlog: unknown command '{verb}'");
    Usage();
    return 2;
}

static void Usage()
{
    Console.WriteLine("""
        urlog migrate [--db <metadata.db>]
        urlog pair --db <metadata.db> [--confirm <identity>]
        urlog ingest --case <id> --host <name> [--host-id n] [--file-id n] [--generation n] [--source id] [--store-id <identity>]
                     [--format <nginx log_format or layout>] [--tz zone] [--year yyyy] [--parallelism n] [--writers n] [--slo] <file>
        urlog enqueue --db <metadata.db> --case <id> [--host name] [--source id] [--format <nginx log_format or layout>]
                     [--tz zone] [--year yyyy] [--evidence-root dir] <file>
        urlog expand --db <metadata.db> --file <id> [--max-depth n] [--max-ratio n] [--max-total-gb n] [--max-members n]
        urlog worker --db <metadata.db> [--once] [--jobs n] [--parallelism n] [--writers n] [--small-box]
                     [--evidence-root dir] [--cache-root dir] [--progress-interval ms] [--commit-timeout s]
                     [--min-free-gb 20] [--min-free-percent 10]
        urlog query  --case <id> [--from t --to t] [--tz zone] [--now t] [--sql] [--explain] '<spl>'
        urlog bench  query --case <id> [--runs 20] [--cold] '<spl>'
        urlog verify-audit --db <metadata.db> [--head] [--expect <seq>:<hash>] [--backup <file>] [--json]
        urlog backup --db <metadata.db> [--dir <dir>] [--reason manual] [--actor <name>]
        urlog user create --db <metadata.db> --name <name> [--admin] [--password-stdin]
        urlog user unlock --db <metadata.db> --name <name>
        urlog user passwd --db <metadata.db> --name <name> [--password-stdin]
        urlog case create --db <metadata.db> --name <case name> --owner <user name>
        urlog version

        verify-audit walks the audit hash chain read-only and reports the first broken link (exit 1), then prints
        the head on one line: "audit head seq N hash H" (--head prints only that line). Record the head outside this
        machine (case notebook, ticket): the chain proves nothing before the head was changed, but rows cut off the
        end leave a shorter chain that still verifies. Only a head written down earlier, passed back as
        --expect N:H (or the whole line, quoted), or a backup passed as --backup, detects that truncation.
        backup runs VACUUM INTO <dir>/metadata-<utc>.sqlite (default dir: backups next to the database, or
        URLOG_BACKUP_DIR), writes the backup's SHA-256 as the next audit row and into <file>.sha256, and prints
        the new head. --db may come from URLOG_DB.
        enqueue records a file in the metadata store (SHA-256 first, then detection; the file stays where it is) and
        queues its parse; the same bytes in the same case are a duplicate and are not queued again. Text no parser
        claims is queued for the generic source. A file that is not a log (a document, an image, a binary journal, an
        empty file) is recorded as an artifact and not queued; --source parses it anyway. A packet capture is refused
        and leaves an audit row. An archive (zip, tar, a compressed tar, archives inside archives to a depth of 3)
        is queued for expansion: every member gets its own record, hash and job. Without --host the file name and
        each member's path propose a host; a file no pattern fits stays unassigned and is parsed all the same.
        An archive that would expand more than 200:1, to more than 250 GB or to more than 100,000 members fails its
        job and records none of its members; expand queues it again with limits given by hand, and that request is
        in the audit chain with the limits. A compressed input that is cut short is parsed as far as it reads and
        its record says "input truncated" with the position. worker runs the
        queue as the ingest account: one runner per metadata file, each file hashed again as it is read and compared
        with its intake record before anything is committed, a file visible only after its commit. --once leaves when
        the queue is empty; without it the worker runs until Ctrl-C, which puts the running jobs back in the queue.
        A worker that was killed leaves its job in parsing; the next start removes that attempt's rows and runs it again.
        A starting worker tries for the metadata file's lock for five seconds before it reports another runner.
        The queue pauses while a watched volume (the metadata directory, the evidence root, the event store's disks)
        has less free space than the larger of --min-free-gb and --min-free-percent of the volume.
        user create makes a local account (the first admin: --admin, on an empty store); unlock ends a lockout early
        and passwd sets a new password and ends the user's sessions, both the break-glass for a locked or forgotten sole
        admin. The password is read from stdin (--password-stdin), URLOG_PASSWORD, or a no-echo prompt; never from an
        argument. Every command writes its audit row as the OS user and prints the new head.

        Store identity: a metadata file belongs to one event store, and both hold the same identity (32 hex
        characters). migrate --db gives a new deployment its identity: a metadata file without file records and a
        store that holds nothing. worker, enqueue, expand and case compare the two before they write and refuse,
        naming both values, when they differ or one side has none. user and backup need only that the metadata
        file has an identity; the event store need not be up. verify-audit reads only and runs on any metadata
        file, with an identity or without. pair joins a metadata file and a store that
        already hold data (an upgrade from before the identity existed, a metadata file restored from an old
        backup, a replaced store): it prints what each side holds and what the next start of a runner would write,
        and asks for the identity to be typed back (--confirm for a script). The store's identity never changes;
        the metadata file's change is in its audit chain. No job runner may be up on the file while it runs.
        ingest keeps no record: into a store that has an identity it needs --store-id with that identity, and a
        --case and a --file-id from 4611686018427387904 (2^62) up, the ranges the metadata file never allocates.

        ClickHouse: --clickhouse host --native-port 9000 --http-port 8123, or URLOG_CH_HOST / URLOG_CH_TCP_PORT /
        URLOG_CH_HTTP_PORT. Three accounts, one per role; each command prints the account it connected as (the name,
        never the password); the defaults are compose.dev.yml's development stubs:
          ingest  --user / --password              URLOG_CH_USER / URLOG_CH_PASSWORD              urlog_ingest
                  ingest: inserts, the re-parse delete, the generation commit. No DDL, cannot read events_v.
          query   --query-user / --query-password  URLOG_CH_QUERY_USER / URLOG_CH_QUERY_PASSWORD  urlog_query
                  query, bench query: read-only, events_v with the case filter.
          admin   --admin-user / --admin-password  URLOG_CH_ADMIN_USER / URLOG_CH_ADMIN_PASSWORD  default
                  migrate (the schema; run it before the first ingest and after an upgrade), bench query --cold.
        URLOG_CH_USER named the admin account before the per-role accounts; it now names the ingest account.
        """);
}
