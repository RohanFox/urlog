using System.Globalization;
using Urlog.Core.Ingest;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// A file is visible only after its generation is committed (migration 0006). Rows of a file that is still being
/// parsed, or whose hash has not been compared with its intake record yet, are in <c>events</c> and must not be
/// readable by anyone: not through SPL, not through the raw-SQL tab, not by naming the file or the generation.
/// The same holds for the next generation of a file while its re-parse runs.
/// </summary>
public sealed class UncommittedFileTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private const ulong CaseId = 1;
    private const ulong FileId = 500;
    private const int Rows = 40;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> RawTexts() =>
    [
        "SELECT count() FROM events_v WHERE file_id = 500",
        "SELECT count() FROM events_v WHERE file_id = 500 AND parse_generation = 1",
        "SELECT count() FROM events_v WHERE case_id = 1 AND file_id = 500 AND parse_generation > 0",
        "SELECT count() FROM (SELECT * FROM events_v WHERE file_id = 500)",
        "WITH f AS (SELECT file_id FROM events_v WHERE file_id = 500) SELECT count() FROM f",
        "SELECT count() FROM events_v WHERE file_id IN (SELECT file_id FROM events_v WHERE file_id = 500)",
    ];

    [Fact]
    public async Task RowsOfAnUncommittedFileAreInvisibleThroughSplAndRawSqlUntilTheCommit()
    {
        var client = ch.QueryClient();
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);
        var seed = ClickHouseFixture.RowsPerCase;

        // The rows are written (as urlog_ingest, as a parse writes them) and not committed.
        await ch.InsertAsync(CaseId, FileId, generation: 1, month: 1, Rows);
        await using (var admin = ch.Admin())
        {
            var stored = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM events WHERE file_id = {FileId}", cancellationToken: Ct), CultureInfo.InvariantCulture);
            Assert.Equal(Rows, stored); // they are in the table
        }

        Assert.Equal(seed, await SplCountAsync(client, "| stats count"));
        Assert.Equal(seed, await SplCountAsync(client, "program=nginx | stats count"));
        Assert.Equal(seed, await SplCountAsync(client, "GET | stats count")); // the text index does not find them either
        foreach (var text in RawTexts())
        {
            var raw = await client.RunSqlAsync(text, [CaseId], Ct);
            output.WriteLine($"uncommitted: {text} -> {raw.Rows[0][0]}");
            Assert.Equal("0", raw.Rows[0][0]);
        }

        Assert.Equal(seed.ToString(CultureInfo.InvariantCulture), (await client.RunSqlAsync("SELECT count() FROM events_v", [CaseId], Ct)).Rows[0][0]);

        // The commit makes them visible, to the case's users only.
        await store.CommitFileGenerationAsync(FileId, 1, Ct);
        Assert.Equal(seed + Rows, await SplCountAsync(client, "| stats count"));
        Assert.Equal(Rows.ToString(CultureInfo.InvariantCulture), (await client.RunSqlAsync("SELECT count() FROM events_v WHERE file_id = 500", [CaseId], Ct)).Rows[0][0]);
        Assert.Equal("0", (await client.RunSqlAsync("SELECT count() FROM events_v WHERE file_id = 500", [2, 3], Ct)).Rows[0][0]);

        // A re-parse in flight: generation 2 is written beside generation 1 and stays invisible; the first parse stays whole.
        await ch.InsertAsync(CaseId, FileId, generation: 2, month: 1, Rows + 7);
        Assert.Equal(seed + Rows, await SplCountAsync(client, "| stats count"));
        Assert.Equal("0", (await client.RunSqlAsync("SELECT count() FROM events_v WHERE file_id = 500 AND parse_generation = 2", [CaseId], Ct)).Rows[0][0]);
        var generations = await client.RunSqlAsync("SELECT toString(min(parse_generation)), toString(max(parse_generation)), toString(count()) FROM events_v WHERE file_id = 500", [CaseId], Ct);
        Assert.Equal(["1", "1", Rows.ToString(CultureInfo.InvariantCulture)], generations.Rows[0]);

        // Its commit switches the file to the new generation at once, never a mix.
        await store.CommitFileGenerationAsync(FileId, 2, Ct);
        generations = await client.RunSqlAsync("SELECT toString(min(parse_generation)), toString(max(parse_generation)), toString(count()) FROM events_v WHERE file_id = 500", [CaseId], Ct);
        Assert.Equal(["2", "2", (Rows + 7).ToString(CultureInfo.InvariantCulture)], generations.Rows[0]);
    }

    [Theory]
    [MemberData(nameof(RawTexts))]
    public async Task AnUnauthorisedUserSeesNoRowsOfTheFileCommittedOrNot(string text)
    {
        // Whatever the other test has done to file 500 by now, a user without case 1 reads nothing of it.
        var client = ch.QueryClient();
        Assert.Equal("0", (await client.RunSqlAsync(text, [2, 3], Ct)).Rows[0][0]);
        Assert.Equal("0", (await client.RunSqlAsync(text, [], Ct)).Rows[0][0]);
    }

    private static async Task<int> SplCountAsync(ClickHouseQueryClient client, string spl)
    {
        var options = new TranspileOptions(CaseId, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var table = await client.RunAsync(Transpiler.Transpile(spl, options), [CaseId], Ct);
        return table.Rows.Sum(r => int.Parse(r[^1], CultureInfo.InvariantCulture));
    }
}
