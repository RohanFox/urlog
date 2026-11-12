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

    public MetadataStore(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Options = BuildOptions(Path);
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

    /// <summary>The connection string for <paramref name="path"/>: read-write-create, foreign keys on, the busy timeout.</summary>
    public static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        ForeignKeys = true,
        DefaultTimeout = BusyTimeoutSeconds,
        Pooling = true,
    }.ToString();

    public static DbContextOptions<MetadataDb> BuildOptions(string path) => Configure(new DbContextOptionsBuilder<MetadataDb>(), path).Options;

    /// <summary>SQLite provider, the snake_case history table, and the pragmas applied on every open.</summary>
    public static DbContextOptionsBuilder<MetadataDb> Configure(DbContextOptionsBuilder<MetadataDb> builder, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .UseSqlite(ConnectionString(path), o => o.MigrationsHistoryTable(MetadataDb.MigrationsHistoryTable))
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
