using System.Globalization;
using System.Security.Cryptography;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Urlog.Core.Audit;

/// <summary>A backup taken: where it is, its own SHA-256 and size, and the audit row that records it in the live store.</summary>
public sealed record BackupResult(string Path, string Sha256, long Size, string SidecarPath, AuditRow AuditRow);

/// <summary>
/// The metadata backup of the design record ("SQLite backup"): the file holds the evidence hashes and the audit chain, so
/// losing it loses the proof. <c>VACUUM INTO</c> on its own connection (it cannot run inside a transaction; under WAL it is
/// a read transaction and blocks no writer) to <c>&lt;dir&gt;/metadata-&lt;yyyyMMddTHHmmssZ&gt;.sqlite</c>, never over an
/// existing file; SHA-256 streamed over the backup's bytes; one <c>backup</c> audit row in the live store with
/// <c>{path, reason, sha256, size}</c>; and a sidecar <c>&lt;name&gt;.sha256</c> in <c>sha256sum</c> format so the file can be
/// checked without this tool. The backup cannot contain its own row: its head is the live row just before it, which is
/// what <see cref="AuditVerifier.VerifyExtendsAsync"/> checks. The job runner calls this nightly, the case lifecycle immediately before every purge.
/// </summary>
public static class SqliteBackup
{
    public const string ReasonManual = "manual";
    public const string ReasonNightly = "nightly";
    public const string ReasonPrePurge = "pre_purge";

    /// <summary><c>&lt;directory of the database&gt;/backups</c>: the default when no directory is configured.</summary>
    public static string DefaultDirectory(string databasePath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(databasePath)) ?? ".", "backups");

    public static async Task<BackupResult> TakeAsync(MetadataStore store, string directory, string reason, string actor, long? actorId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        Directory.CreateDirectory(directory);
        var stamp = store.Clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), "metadata-" + stamp + ".sqlite");
        var sidecar = path + ".sha256";
        if (File.Exists(path) || File.Exists(sidecar))
        {
            throw new IOException($"a backup already exists at {path}: backups are never overwritten");
        }

        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder(MetadataStore.ConnectionString(store.Path)) { Pooling = false }.ToString()))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $path";
                vacuum.Parameters.AddWithValue("$path", path);
                await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            string sha256;
            long size;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                size = stream.Length;
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            AuditRow row;
            await using (var db = store.CreateDbContext())
            await using (var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false))
            {
                var details = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = path,
                    ["reason"] = reason,
                    ["sha256"] = sha256,
                    ["size"] = size,
                };
                row = await store.Audit.AppendAsync(db, new AuditEntry(actor, actorId, AuditActions.Backup, AuditTargets.Backup, null, details), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            // sha256sum format: hash, two spaces, the file name; `sha256sum -c` beside the backup checks it.
            await File.WriteAllTextAsync(sidecar, sha256 + "  " + System.IO.Path.GetFileName(path) + "\n", cancellationToken).ConfigureAwait(false);
            return new BackupResult(path, sha256, size, sidecar, row);
        }
        catch
        {
            // No backup without its audit row: a file nobody recorded proves nothing.
            TryDelete(path);
            TryDelete(sidecar);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
