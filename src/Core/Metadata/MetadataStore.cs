using System.Data.Common;
using Urlog.Core.Audit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Urlog.Core.Metadata;

/// <summary>
/// Opens the SQLite metadata file. WAL on open (a reader never waits for the writer), <c>foreign_keys</c> on,
/// a 5 s busy timeout, one <see cref="MetadataDb"/> per request or job through <see cref="CreateDbContext"/>.
/// The API host registers an instance as the <see cref="IDbContextFactory{TContext}"/>; the CLI builds one from its arguments.
/// </summary>
public sealed class MetadataStore : IDbContextFactory<MetadataDb>
{
    /// <summary>How long a writer waits for the lock before SQLITE_BUSY surfaces (design record, "Consistency", Job runner).</summary>
    public const int BusyTimeoutSeconds = 5;

    /// <param name="busyTimeoutSeconds">
    /// For a test that stages a locked file and cannot wait five seconds for it. Whole seconds, at least one (the
    /// provider counts in seconds and reads zero as no limit). No setting reaches it: every host and command
    /// runs on <see cref="BusyTimeoutSeconds"/>.
    /// </param>
    public MetadataStore(string path, TimeProvider? clock = null, int busyTimeoutSeconds = BusyTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(busyTimeoutSeconds, 1);
        Path = System.IO.Path.GetFullPath(path);
        BusyTimeout = TimeSpan.FromSeconds(busyTimeoutSeconds);
        Options = BuildOptions(Path, busyTimeoutSeconds);
        Clock = clock ?? TimeProvider.System;
        Audit = new AuditChain(Clock);
    }

    /// <summary>Absolute path of the database file.</summary>
    public string Path { get; }

    /// <summary>The clock the audit chain stamps rows with (a test passes a fake).</summary>
    public TimeProvider Clock { get; }

    /// <summary>The audit writer for this store: every state change appends through it in its own transaction.</summary>
    public IAuditWriter Audit { get; }

    public DbContextOptions<MetadataDb> Options { get; }

    /// <summary>How long a writer of this store waits for the lock: <see cref="BusyTimeoutSeconds"/> outside tests.</summary>
    public TimeSpan BusyTimeout { get; }

    /// <summary>The connection string for <paramref name="path"/>: read-write-create, foreign keys on, the busy timeout.</summary>
    public static string ConnectionString(string path, int busyTimeoutSeconds = BusyTimeoutSeconds) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        DefaultTimeout = busyTimeoutSeconds,
        Pooling = true,
    }.ToString();

    public static DbContextOptions<MetadataDb> BuildOptions(string path, int busyTimeoutSeconds = BusyTimeoutSeconds) =>
        Configure(new DbContextOptionsBuilder<MetadataDb>(), path, busyTimeoutSeconds).Options;

    /// <summary>SQLite provider, the snake_case history table, and the pragmas applied on every open.</summary>
    public static DbContextOptionsBuilder<MetadataDb> Configure(DbContextOptionsBuilder<MetadataDb> builder, string path, int busyTimeoutSeconds = BusyTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .UseSqlite(ConnectionString(path, busyTimeoutSeconds), o => o.MigrationsHistoryTable(MetadataDb.MigrationsHistoryTable))
            .AddInterceptors(SqlitePragmas.Instance)
            // No Sqlite:Autoincrement (-> AUTOINCREMENT + sqlite_sequence) in any migration: see PlainRelationalAnnotationProvider.
            .ReplaceService<IRelationalAnnotationProvider, PlainRelationalAnnotationProvider>();
    }

    public MetadataDb CreateDbContext() => new(Options);

    /// <summary>Applies every pending EF migration (creates the file on first use), then writes the audit genesis row when the table is empty.</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await ((AuditChain)Audit).EnsureGenesisAsync(db, cancellationToken).ConfigureAwait(false);
        await BackfillDetectionRegistryAsync(db, cancellationToken).ConfigureAwait(false);
        await BackfillDetectedFormatAsync(db, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Files recorded before <c>detected_format</c> and <c>detected_control_bytes</c> existed get both from their
    /// detection record, once: the format of a file that is not a log, and whether a log's record carries the
    /// control-bytes flag. Copies of the record, like the registry key; nothing a file's trail states changes.
    /// </summary>
    private static async Task BackfillDetectedFormatAsync(MetadataDb db, CancellationToken cancellationToken)
    {
        // A file that is not a log always has a format in its record, so a row is read here at most once.
        var missing = db.Files.Where(f => f.DetectedFormat == null && f.Detection != null
            && (f.DetectedKind == Urlog.Core.Detection.FileKind.Artifact || f.DetectedKind == Urlog.Core.Detection.FileKind.Unparsed || f.DetectedKind == Urlog.Core.Detection.FileKind.Rejected));
        if (await missing.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            var byFormat = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            await foreach (var row in missing.AsNoTracking().Select(f => new { f.Id, f.Detection }).AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                // A record without a format is marked with the empty text, so it is not read again.
                var format = string.Empty;
                try
                {
                    using var record = System.Text.Json.JsonDocument.Parse(row.Detection!);
                    if (record.RootElement.TryGetProperty("format", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        format = value.GetString() ?? string.Empty;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                }

                if (!byFormat.TryGetValue(format, out var ids))
                {
                    byFormat[format] = ids = [];
                }

                ids.Add(row.Id);
            }

            foreach (var (format, ids) in byFormat)
            {
                foreach (var chunk in ids.Chunk(500))
                {
                    await db.Files.Where(f => chunk.Contains(f.Id)).ExecuteUpdateAsync(s => s.SetProperty(f => f.DetectedFormat, format), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // The flag of a log is one word in its record; setting the column twice changes nothing.
        var flag = "\"" + Urlog.Core.Detection.FormatDetector.FlagBinaryBytes + "\"";
        await db.Files
            .Where(f => !f.DetectedControlBytes && f.DetectedKind == Urlog.Core.Detection.FileKind.Log && f.Detection != null && f.Detection.Contains(flag))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.DetectedControlBytes, true), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Files recorded before <c>detection_registry</c> existed get it from their detection record, once. The value
    /// is a copy of two members of that record, so writing it changes nothing a file's trail states.
    /// </summary>
    private static async Task BackfillDetectionRegistryAsync(MetadataDb db, CancellationToken cancellationToken)
    {
        if (!await db.Files.AnyAsync(f => f.DetectionRegistry == null && f.Detection != null, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var byKey = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        await foreach (var row in db.Files.AsNoTracking().Where(f => f.DetectionRegistry == null && f.Detection != null).Select(f => new { f.Id, f.Detection }).AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // A record without the two members is from before they were kept: the empty key marks it as read, and stale.
            var key = Urlog.Core.Detection.DetectionResult.RegistryKeyOfJson(row.Detection) ?? string.Empty;
            if (!byKey.TryGetValue(key, out var ids))
            {
                byKey[key] = ids = [];
            }

            ids.Add(row.Id);
        }

        foreach (var (key, ids) in byKey)
        {
            foreach (var chunk in ids.Chunk(500))
            {
                await db.Files.Where(f => chunk.Contains(f.Id)).ExecuteUpdateAsync(s => s.SetProperty(f => f.DetectionRegistry, key), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <c>journal_mode=WAL</c> is persistent in the file but checked on every open (cheap: one PRAGMA read);
    /// <c>synchronous=NORMAL</c> is per connection and the right level under WAL (durable across a crash of the process,
    /// not of the OS, which the nightly VACUUM INTO backup covers).
    /// </summary>
    private sealed class SqlitePragmas : DbConnectionInterceptor
    {
        public static readonly SqlitePragmas Instance = new();

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Apply(connection);
            return Task.CompletedTask;
        }

        private static void Apply(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode";
            if (command.ExecuteScalar() is not string mode || !string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                command.CommandText = "PRAGMA journal_mode=WAL";
                command.ExecuteNonQuery();
            }

            command.CommandText = "PRAGMA synchronous=NORMAL";
            command.ExecuteNonQuery();
        }
    }
}
