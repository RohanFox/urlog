using System.Diagnostics;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>Migrations on an empty file in under 2 s; WAL on open; a reader never waits for the writer.</summary>
public sealed class MigrationTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MigratesAnEmptyFileInUnderTwoSeconds()
    {
        await using var temp = TempDb.CreateUnmigrated();
        var clock = Stopwatch.StartNew();
        await temp.Store.MigrateAsync(Ct);
        clock.Stop();
        output.WriteLine($"migrate on an empty file: {clock.Elapsed.TotalMilliseconds:F0} ms");
        if (Environment.GetEnvironmentVariable("URLOG_SCHEMA_DUMP") is { Length: > 0 } timingPath)
        {
            await File.AppendAllTextAsync(timingPath + ".timing", $"migrate on an empty file: {clock.Elapsed.TotalMilliseconds:F0} ms\n", Ct);
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"migrate took {clock.Elapsed.TotalSeconds:F2} s");

        await using var db = temp.Open();
        var pending = await db.Database.GetPendingMigrationsAsync(Ct);
        Assert.Empty(pending);
        Assert.Equal(["20261106171503_Initial", "20261106175518_AuditTriggers", "20261111172457_Sessions", "20261112151235_DetectionRecord"], await db.Database.GetAppliedMigrationsAsync(Ct));
    }

    [Fact]
    public async Task MigrateIsIdempotent()
    {
        await using var temp = await TempDb.CreateAsync();
        await temp.Store.MigrateAsync(Ct);
        await using var db = temp.Open();
        Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync(Ct)).Count());
    }

    [Fact]
    public async Task SchemaHasEveryTableAndNothingSqliteSpecific()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var ddl = await SchemaAsync(db);
        // __EFMigrationsLock is EF Core's own migration lock table (created by Migrate, kept empty), like its history table.
        var tables = ddl.Where(d => d.Type == "table" && d.Name != "__EFMigrationsLock").Select(d => d.Name).Order().ToList();
        Assert.Equal(["audit", "case_members", "cases", MetadataDb.MigrationsHistoryTable, "files", "hosts", "jobs", "sessions", "users"], tables);
        Assert.DoesNotContain(ddl, d => d.Name.StartsWith("sqlite_", StringComparison.Ordinal));
        var all = string.Join("\n", ddl.Select(d => d.Sql));
        Assert.DoesNotContain("AUTOINCREMENT", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WITHOUT ROWID", all, StringComparison.OrdinalIgnoreCase);

        // The schema listing for the ticket: URLOG_SCHEMA_DUMP=<path> writes it; the output helper always shows it.
        var listing = string.Join("\n", ddl.Select(d => d.Sql + ";"));
        output.WriteLine(listing);
        if (Environment.GetEnvironmentVariable("URLOG_SCHEMA_DUMP") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, listing + "\n", Ct);
        }
    }

    [Fact]
    public async Task JournalModeIsWalAndForeignKeysAreOn()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        // synchronous is per connection and set by the store's interceptor, which runs on an open made through EF.
        // Clearing the pool first makes this a new physical connection every time. Another test's TempDb disposal
        // clears every pool at an arbitrary moment, and a reused connection would hide a missing pragma.
        SqliteConnection.ClearAllPools();
        await db.Database.OpenConnectionAsync(Ct);
        var connection = db.Database.GetDbConnection();
        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode"));
        Assert.Equal("1", await ScalarAsync(connection, "PRAGMA foreign_keys"));
        Assert.Equal("1", await ScalarAsync(connection, "PRAGMA synchronous")); // 1 = NORMAL
    }

    [Fact]
    public async Task ForeignKeysAreEnforced()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        db.Hosts.Add(new Host { CaseId = 999, Name = "web-01", CreatedAt = DateTime.UtcNow });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.IsType<SqliteException>(error.InnerException);
    }

    [Fact]
    public async Task ASecondConnectionReadsWhileTheFirstWrites()
    {
        await using var temp = await TempDb.CreateAsync();
        const int iterations = 1000;
        var errors = new List<Exception>();
        var start = new TaskCompletionSource();

        async Task WriterAsync()
        {
            await start.Task;
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    await using var db = temp.Open();
                    db.Users.Add(new User { Name = $"writer-{i}", PasswordHash = "x", CreatedAt = DateTime.UtcNow });
                    await db.SaveChangesAsync(Ct);
                }
                catch (Exception e) when (e is DbUpdateException or SqliteException)
                {
                    lock (errors)
                    {
                        errors.Add(e);
                    }
                }
            }
        }

        async Task ReaderAsync()
        {
            await start.Task;
            for (var i = 0; i < iterations; i++)
            {
                try
                {
                    await using var db = temp.Open();
                    _ = await db.Users.CountAsync(Ct);
                }
                catch (SqliteException e)
                {
                    lock (errors)
                    {
                        errors.Add(e);
                    }
                }
            }
        }

        var writer = Task.Run(WriterAsync, Ct);
        var reader = Task.Run(ReaderAsync, Ct);
        start.SetResult();
        await Task.WhenAll(writer, reader);

        Assert.Empty(errors);
        await using var check = temp.Open();
        Assert.Equal(iterations, await check.Users.CountAsync(Ct));
    }

    [Fact]
    public async Task TwoWritersShareTheFileUnderTheBusyTimeout()
    {
        await using var temp = await TempDb.CreateAsync();
        const int perWriter = 500;
        var errors = new List<Exception>();

        async Task WriterAsync(string prefix)
        {
            for (var i = 0; i < perWriter; i++)
            {
                try
                {
                    await using var db = temp.Open();
                    db.Users.Add(new User { Name = $"{prefix}-{i}", PasswordHash = "x", CreatedAt = DateTime.UtcNow });
                    await db.SaveChangesAsync(Ct);
                }
                catch (Exception e) when (e is DbUpdateException or SqliteException)
                {
                    lock (errors)
                    {
                        errors.Add(e);
                    }
                }
            }
        }

        await Task.WhenAll(Task.Run(() => WriterAsync("a"), Ct), Task.Run(() => WriterAsync("b"), Ct));

        Assert.Empty(errors);
        await using var check = temp.Open();
        Assert.Equal(2 * perWriter, await check.Users.CountAsync(Ct));
    }

    private static async Task<string?> ScalarAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync(Ct))?.ToString();
    }

    private static async Task<List<(string Type, string Name, string Sql)>> SchemaAsync(MetadataDb db)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type DESC, name";
        var rows = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }
}
