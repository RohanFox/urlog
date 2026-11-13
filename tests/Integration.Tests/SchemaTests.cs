using System.Globalization;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Ingest;
using Urlog.Core.Ingest.Native;
using Urlog.Core.Sources;

namespace Urlog.Integration.Tests;

/// <summary>
/// The migrations and what they promise: the migrator records and verifies hashes, <c>events_v</c> hides other
/// cases and stale generations, the delete-and-reparse protocol is idempotent through the view, and a case+time
/// query prunes partitions and uses the primary key (<c>EXPLAIN indexes = 1</c>).
/// </summary>
public sealed class SchemaTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MigrationsApplyOnceRecordHashesAndRefuseEditedFiles()
    {
        await using var client = Admin();
        await Reset(client);
        var migrator = new ClickHouseMigrator(client);
        var embedded = MigrationSet.Embedded();
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], embedded.Select(m => m.Version));

        var first = await migrator.ApplyAsync(embedded, Ct);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], first.Applied);
        Assert.Empty(first.AlreadyApplied);

        var second = await migrator.ApplyAsync(embedded, Ct);
        Assert.Empty(second.Applied);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], second.AlreadyApplied);

        var tables = new List<string>();
        await foreach (var row in client.QueryAsync("SELECT name FROM system.tables WHERE database = currentDatabase() ORDER BY name", cancellationToken: Ct))
        {
            tables.Add((string)row[0]!);
        }

        output.WriteLine(string.Join(", ", tables));
        Assert.Contains("events", tables);
        Assert.Contains("events_v", tables);
        Assert.Contains("file_generation", tables);
        Assert.DoesNotContain("file_generation_dict", tables); // created by 0002, retired by 0006
        Assert.Contains("field_stats", tables);
        Assert.Contains("schema_migrations", tables);
        Assert.Contains("store_identity", tables);

        var recorded = Convert.ToInt64(await client.ExecuteScalarAsync("SELECT count() FROM schema_migrations FINAL", cancellationToken: Ct), CultureInfo.InvariantCulture);
        Assert.Equal(7, recorded);

        var edited = embedded.Select(m => m.Version == 2 ? m with { Sql = m.Sql + "\n-- edited after it shipped" } : m).ToList();
        var error = await Assert.ThrowsAsync<MigrationException>(() => migrator.ApplyAsync(edited, Ct));
        output.WriteLine(error.Message);
        Assert.Contains("0002_file_generation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsViewHidesOtherCasesAndStaleGenerationsAndReparseIsIdempotent()
    {
        await using var client = Admin();
        await Reset(client);
        await new ClickHouseMigrator(client).ApplyAsync(MigrationSet.Embedded(), Ct);
        var admin = new EventStoreAdmin(client);

        // Case 1, file 1, generation 1: 50 rows; case 2, file 2: 20 rows.
        await Insert(caseId: 1, fileId: 1, generation: 1, rows: 50);
        await Insert(caseId: 2, fileId: 2, generation: 1, rows: 20);

        // Nothing is committed yet: a file without a file_generation row shows no rows, to anyone.
        Assert.Equal(0, await CountVisible(client, "[1, 2]"));
        await admin.CommitFileGenerationAsync(1, 1, Ct);
        await admin.CommitFileGenerationAsync(2, 1, Ct);

        Assert.Equal(50, await CountVisible(client, "[1]"));
        Assert.Equal(20, await CountVisible(client, "[2]"));
        Assert.Equal(70, await CountVisible(client, "[1, 2]"));
        Assert.Equal(0, await CountVisible(client, "[3]"));
        Assert.Equal(0, await CountVisible(client, null)); // no setting: an unauthorised user sees nothing

        // Re-parse file 1: delete, insert generation 2, commit. The old rows are hidden by the generation filter
        // whether or not the lightweight delete has applied yet; the view never shows both generations.
        await admin.DeleteFileRowsAsync(1, Ct);
        await Insert(caseId: 1, fileId: 1, generation: 2, rows: 50);
        // Before the commit the view shows generation 1 (50 rows) or, once the lightweight delete has applied,
        // nothing for the file: never generation 2, never both. A crash here is recovered by a full re-parse.
        var beforeCommit = await CountVisible(client, "[1]");
        Assert.True(beforeCommit is 50 or 0, $"visible rows before the commit: {beforeCommit}");
        var generationsBefore = (string?)await client.ExecuteScalarAsync("SELECT arrayStringConcat(arraySort(groupUniqArray(toString(parse_generation))), ',') FROM events_v WHERE case_id = 1 SETTINGS SQL_allowed_cases = '[1]'", cancellationToken: Ct);
        Assert.True(generationsBefore is "1" or "", $"generations visible before the commit: {generationsBefore}");
        await admin.CommitFileGenerationAsync(1, 2, Ct);
        Assert.Equal(50, await CountVisible(client, "[1]"));
        var generations = (string?)await client.ExecuteScalarAsync("SELECT arrayStringConcat(arraySort(groupUniqArray(toString(parse_generation))), ',') FROM events_v WHERE case_id = 1 SETTINGS SQL_allowed_cases = '[1]'", cancellationToken: Ct);
        Assert.Equal("2", generations);

        // A crash after the delete and before the commit: re-run the whole file with the same generation.
        await admin.DeleteFileRowsAsync(1, Ct);
        await Insert(caseId: 1, fileId: 1, generation: 3, rows: 50);
        await admin.CommitFileGenerationAsync(1, 3, Ct);
        Assert.Equal(50, await CountVisible(client, "[1]"));
        Assert.Equal(20, await CountVisible(client, "[2]"));

        var raw = Convert.ToInt64(await client.ExecuteScalarAsync("SELECT count() FROM events WHERE file_id = 1", cancellationToken: Ct), CultureInfo.InvariantCulture);
        output.WriteLine($"events table holds {raw} rows of file 1 (generations awaiting the lightweight delete), events_v shows 50");

        async Task Insert(ulong caseId, ulong fileId, uint generation, int rows)
        {
            using var builder = new BatchBuilder(batchRows: rows);
            for (var i = 0; i < rows; i++)
            {
                builder.Writer.AppendUInt64(EventColumn.FileOffset, (ulong)i);
                builder.Writer.AppendTimestamp(1_700_000_000_000 + (i * 1000L));
                builder.Writer.AppendString(EventColumn.Raw, "r"u8);
                builder.Writer.CommitRow();
            }

            var batch = Assert.Single(builder.Flush());
            batch.CaseId.AsSpan(0, rows).Fill(caseId);
            batch.FileId.AsSpan(0, rows).Fill(fileId);
            batch.ParseGeneration.AsSpan(0, rows).Fill(generation);
            await using var inserter = new NativeInserter(BatchBuilder.Options(ch, "events") with { Writers = 1 }, returnBatches: false);
            inserter.Submit(batch);
            await inserter.CompleteAsync(Ct);
        }
    }

    [Fact]
    public async Task CaseAndTimeQueryPrunesPartitionsAndUsesThePrimaryKey()
    {
        await using var client = Admin();
        await Reset(client);
        await new ClickHouseMigrator(client).ApplyAsync(MigrationSet.Embedded(), Ct);

        // Two cases, two months each: four partitions.
        foreach (var (caseId, month) in new[] { (1UL, 1), (1UL, 2), (2UL, 1), (2UL, 2) })
        {
            using var builder = new BatchBuilder(batchRows: 100);
            var start = new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            for (var i = 0; i < 100; i++)
            {
                builder.Writer.AppendUInt64(EventColumn.FileOffset, (ulong)i);
                builder.Writer.AppendTimestamp(start + (i * 60_000L));
                builder.Writer.AppendString(EventColumn.Raw, "r"u8);
                builder.Writer.CommitRow();
            }

            var batch = Assert.Single(builder.Flush());
            batch.CaseId.AsSpan(0, 100).Fill(caseId);
            await using var inserter = new NativeInserter(BatchBuilder.Options(ch, "events") with { Writers = 1 }, returnBatches: false);
            inserter.Submit(batch);
            await inserter.CompleteAsync(Ct);
        }

        var plan = new List<string>();
        await foreach (var row in client.QueryAsync(
            "EXPLAIN indexes = 1 SELECT count() FROM events_v WHERE case_id = 1 AND ts BETWEEN '2026-02-01 00:00:00' AND '2026-02-01 00:30:00' SETTINGS SQL_allowed_cases = '[1]'",
            cancellationToken: Ct))
        {
            plan.Add((string)row[0]!);
        }

        var text = string.Join("\n", plan);
        output.WriteLine(text);
        Assert.Contains("Partition", text, StringComparison.Ordinal);
        Assert.Contains("PrimaryKey", text, StringComparison.Ordinal);
        Assert.Contains("Parts: 1/4", text, StringComparison.Ordinal); // the month and case partition alone
        Assert.Matches(@"Keys:\s*\n?\s*case_id", text);
    }

    private static async Task<long> CountVisible(ClickHouseTcpClient client, string? allowedCases)
    {
        var sql = allowedCases is null
            ? "SELECT count() FROM events_v"
            : $"SELECT count() FROM events_v SETTINGS SQL_allowed_cases = '{allowedCases}'";
        return Convert.ToInt64(await client.ExecuteScalarAsync(sql, cancellationToken: Ct), CultureInfo.InvariantCulture);
    }

    private static async Task Reset(ClickHouseTcpClient client)
    {
        foreach (var statement in new[]
                 {
                     "DROP VIEW IF EXISTS events_v", "DROP DICTIONARY IF EXISTS file_generation_dict", "DROP TABLE IF EXISTS file_generation",
                     "DROP TABLE IF EXISTS field_stats", "DROP TABLE IF EXISTS events", "DROP TABLE IF EXISTS store_identity", "DROP TABLE IF EXISTS schema_migrations",
                 })
        {
            await client.ExecuteAsync(statement, cancellationToken: Ct);
        }
    }

    private ClickHouseTcpClient Admin() => new(ch.NativeConnectionString(ClickHouseFixture.AdminUser, ClickHouseFixture.AdminPassword));
}
