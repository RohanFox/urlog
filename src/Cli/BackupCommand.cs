using System.Globalization;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog backup --db &lt;metadata.db&gt; [--dir &lt;dir&gt;] [--reason manual] [--actor &lt;name&gt;]</c>: <see cref="SqliteBackup"/>
/// from the command line. <c>VACUUM INTO &lt;dir&gt;/metadata-&lt;utc&gt;.sqlite</c>, the backup's SHA-256 as the next audit row and
/// in <c>&lt;name&gt;.sha256</c> beside it. The directory is <c>--dir</c>, else <c>URLOG_BACKUP_DIR</c>, else <c>backups</c> next to the
/// database. The actor defaults to the OS user. Prints the new head, to be recorded with the backup.
/// </summary>
internal static class BackupCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var path = VerifyAuditCommand.MetadataPath(args);
        var directory = args.Option("dir") ?? Environment.GetEnvironmentVariable("URLOG_BACKUP_DIR") ?? SqliteBackup.DefaultDirectory(path);
        var reason = args.Option("reason") ?? SqliteBackup.ReasonManual;
        var actor = args.Option("actor") ?? Environment.UserName;
        var store = new MetadataStore(path);
        BackupResult backup;
        try
        {
            backup = await SqliteBackup.TakeAsync(store, directory, reason, actor, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or MetadataException or UnauthorizedAccessException)
        {
            throw new CliException("backup failed: " + e.Message);
        }

        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine($"backup    {backup.Path}");
        Console.WriteLine(string.Create(inv, $"bytes     {backup.Size:N0}"));
        Console.WriteLine($"sha256    {backup.Sha256}  (also in {Path.GetFileName(backup.SidecarPath)})");
        Console.WriteLine(string.Create(inv, $"audit     seq {backup.AuditRow.Seq}, action backup, actor {actor}, reason {reason}"));
        Console.WriteLine(string.Create(inv, $"audit head seq {backup.AuditRow.Seq} hash {backup.AuditRow.Hash}"));
        return 0;
    }
}
