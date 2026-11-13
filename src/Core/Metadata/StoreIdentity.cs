using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Urlog.Core.Metadata;

/// <summary>
/// The store identity as a value, and the read of a metadata file's copy that changes nothing in the file.
/// </summary>
public static class StoreIdentity
{
    /// <summary>
    /// File ids from here up belong to <c>urlog ingest</c>, which chooses its own ids and has no metadata file. The
    /// metadata file never allocates one (a trigger on <c>files</c> refuses it), so a file written by hand can never
    /// meet a recorded file's id in <c>file_generation</c>. 2^62.
    /// </summary>
    public const long IngestFileIdFloor = 1L << 62;

    /// <summary>
    /// Case ids from here up belong to <c>urlog ingest</c> for the same reason: <c>events</c> is partitioned by
    /// case id, so a row written without a record under a recorded case's id would show in that case, and would go
    /// with it when the case is purged. A trigger on <c>cases</c> refuses an id at or above it. 2^62.
    /// </summary>
    public const long IngestCaseIdFloor = 1L << 62;

    public const int Length = 32;

    /// <summary>128 random bits as 32 lowercase hex characters.</summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsValid(string? id)
    {
        if (id is not { Length: Length })
        {
            return false;
        }

        foreach (var c in id)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How an identity is printed: the value, or <c>none</c>.</summary>
    public static string Show(string? id) => id ?? "none";

    /// <summary>
    /// The identity the metadata file at <paramref name="path"/> holds, or null: no file, a file from before the
    /// identity existed, or a file that was never paired. Opened read-only and never migrated, so a file that turns
    /// out not to belong to the store is left exactly as it was found.
    /// </summary>
    public static async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        var connection = OpenReadOnly(path);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (!await TableExistsAsync(connection, "store_identity", cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT store_id FROM store_identity WHERE id = 1";
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
    }

    /// <summary>
    /// What the metadata file holds that a pairing would bind to a store, read without changing the file: its
    /// cases, its file records and the committed generation of every parsed file. All zero for a missing file.
    /// </summary>
    public static async Task<MetadataContents> ReadContentsAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var committed = new Dictionary<long, int>();
        var ids = new HashSet<long>();
        if (!File.Exists(path))
        {
            return new MetadataContents(0, ids, committed);
        }

        var connection = OpenReadOnly(path);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (!await TableExistsAsync(connection, "files", cancellationToken).ConfigureAwait(false))
            {
                return new MetadataContents(0, ids, committed);
            }

            var cases = await CountAsync(connection, "SELECT count(*) FROM cases", cancellationToken).ConfigureAwait(false);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id FROM files";
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        ids.Add(reader.GetInt64(0));
                    }
                }
            }

            // The same rule as JobOperations.CommittedGenerationsAsync, which is what the runner's sync writes from.
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT file_id, max(generation) FROM jobs WHERE state = 'done' GROUP BY file_id";
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        committed[reader.GetInt64(0)] = reader.GetInt32(1);
                    }
                }
            }

            return new MetadataContents(cases, ids, committed);
        }
    }

    private static SqliteConnection OpenReadOnly(string path) =>
        // Not pooled: a refused file must not stay open in this process.
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}

/// <summary>What a metadata file records: its cases, the id of every file record, and the committed generation per file that has one.</summary>
public sealed record MetadataContents(long Cases, IReadOnlySet<long> FileIds, IReadOnlyDictionary<long, int> CommittedGenerations);

/// <summary>
/// A metadata file and an event store do not carry the same identity, or one of them carries none. Nothing was
/// written to either when this is thrown.
/// </summary>
public sealed class StoreIdentityException : Exception
{
    public StoreIdentityException()
    {
    }

    public StoreIdentityException(string message)
        : base(message)
    {
    }

    public StoreIdentityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StoreIdentityException(string message, string? metadataId, string? storeId)
        : base(message)
    {
        MetadataId = metadataId;
        StoreId = storeId;
    }

    /// <summary>What the metadata file holds; null for none.</summary>
    public string? MetadataId { get; }

    /// <summary>What the event store holds; null for none or when it was not read.</summary>
    public string? StoreId { get; }
}
