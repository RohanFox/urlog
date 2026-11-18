using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Ingest;
using Urlog.Query;

namespace Urlog.Security.Tests;

/// <summary>
/// What the server itself refuses <c>urlog_query</c>, with the gate out of the way
/// (the driver straight). The profile constraints keep the limits unraisable; the two grants in
/// deploy/clickhouse/users.d/urlog.xml keep everything but <c>events_v</c> and <c>field_stats_v</c> unreadable.
/// The same for <c>urlog_ingest</c> over the driver's TCP client and <see cref="NativeInserter"/>, the way the
/// backend's write path connects: what the account can do, what the server refuses it, and the pinned grant list.
/// </summary>
public sealed class GrantTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Readonly = 164;
    private const int SettingConstraintViolation = 452;
    private const int AccessDenied = 497;
    private const int IntoOutfileNotAllowed = 358;
    private const int QueryIsProhibited = 392;
    private const int TooManySimultaneousQueries = 202;

    [Theory]
    [InlineData("max_execution_time", "1000")]
    [InlineData("max_memory_usage", "9000000000")]
    [InlineData("max_result_rows", "5000000")]
    [InlineData("max_concurrent_queries_for_user", "100")]
    [InlineData("cancel_http_readonly_queries_on_client_close", "0")]
    public async Task LimitsCannotBeRaisedByRequestParameter(string setting, string value)
    {
        var e = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync("SELECT 1", new() { [setting] = value }));
        output.WriteLine($"{setting}={value} -> {e.ErrorCode}: {First(e)}");
        Assert.Equal(SettingConstraintViolation, e.ErrorCode);
    }

    [Theory]
    [InlineData("SELECT 1 SETTINGS max_execution_time = 1000")]
    [InlineData("SELECT 1 SETTINGS max_memory_usage = 9000000000")]
    [InlineData("SELECT 1 SETTINGS max_result_rows = 5000000")]
    [InlineData("SELECT 1 SETTINGS max_concurrent_queries_for_user = 100")]
    public async Task LimitsCannotBeRaisedInline(string sql)
    {
        var e = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync(sql));
        Assert.Equal(SettingConstraintViolation, e.ErrorCode);
    }

    [Fact]
    public async Task LimitsAreWhatTheProfileSays()
    {
        var limits = await QueryAsync("SELECT toString(getSetting('max_execution_time')), toString(getSetting('max_memory_usage')), toString(getSetting('max_result_rows')), toString(getSetting('readonly')), toString(getSetting('allow_ddl')), toString(getSetting('max_concurrent_queries_for_user')), toString(getSetting('cancel_http_readonly_queries_on_client_close'))");
        output.WriteLine(string.Join(" / ", limits[0]));
        Assert.Equal(["60", "4000000000", "1000000", "2", "false", "16", "true"], limits[0]);
        // Lowering a limit is allowed (a client may ask for less); the constraint is readonly, so even that is refused.
        var lower = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync("SELECT 1", new() { ["max_result_rows"] = "10" }));
        Assert.Equal(SettingConstraintViolation, lower.ErrorCode);
    }

    [Theory]
    [InlineData("SELECT 1 SETTINGS readonly = 0")]
    [InlineData("SELECT 1 SETTINGS allow_ddl = 1")]
    public async Task ReadonlyAndDdlCannotBeSwitchedOff(string sql)
    {
        var e = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync(sql));
        output.WriteLine($"{sql} -> {e.ErrorCode}: {First(e)}");
        Assert.True(e.ErrorCode is Readonly or SettingConstraintViolation or QueryIsProhibited, $"code {e.ErrorCode}");
    }

    [Theory]
    [InlineData("INSERT INTO events_v SELECT * FROM events_v")]
    [InlineData("INSERT INTO field_stats VALUES (1, 'f', 'v', 1, now())")]
    [InlineData("CREATE TABLE query_denied (x UInt8) ENGINE = Memory")]
    [InlineData("CREATE TEMPORARY TABLE query_tmp (x UInt8)")]
    [InlineData("DROP TABLE events")]
    [InlineData("DROP VIEW events_v")]
    [InlineData("ALTER TABLE events DELETE WHERE 1")]
    [InlineData("TRUNCATE TABLE field_stats")]
    [InlineData("OPTIMIZE TABLE events FINAL")]
    [InlineData("SYSTEM RELOAD DICTIONARY file_generation_dict")]
    [InlineData("KILL QUERY WHERE 1")]
    public async Task WritesDdlAndControlAreRefused(string sql)
    {
        var e = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync(sql));
        output.WriteLine($"{sql,-56} -> {e.ErrorCode}: {First(e)}");
        Assert.True(e.ErrorCode is Readonly or AccessDenied, $"code {e.ErrorCode}");
    }

    [Theory]
    [InlineData("SELECT count() FROM events")]
    [InlineData("SELECT count() FROM default.events")]
    [InlineData("SELECT count() FROM file_generation")]
    [InlineData("SELECT count() FROM schema_migrations")]
    [InlineData("SELECT store_id FROM store_identity")]
    [InlineData("SELECT count() FROM system.query_log")]
    [InlineData("SELECT count() FROM system.processes")]
    [InlineData("SELECT count() FROM system.users")]
    [InlineData("SELECT count() FROM system.grants")]
    [InlineData("SELECT * FROM url('http://127.0.0.1:8123/?query=SELECT%201', CSV, 'a String')")]
    [InlineData("SELECT * FROM file('x.csv')")]
    [InlineData("SELECT * FROM s3('http://127.0.0.1/x.csv', CSV)")]
    [InlineData("SELECT * FROM remote('127.0.0.1', 'default', 'events')")]
    [InlineData("SELECT * FROM mysql('127.0.0.1:3306', 'd', 't', 'u', 'p')")]
    [InlineData("SELECT * FROM executable('ls', 'TSV', 'x String')")]
    public async Task OtherRelationsAndTableFunctionsAreDenied(string sql)
    {
        var e = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync(sql, new() { ["SQL_allowed_cases"] = "[1]" }));
        output.WriteLine($"{sql,-72} -> {e.ErrorCode}: {First(e)}");
        Assert.Equal(AccessDenied, e.ErrorCode);
    }

    [Fact]
    public async Task SetIsAcceptedByTheServerSoTheGateMustRefuseIt()
    {
        // readonly = 2 means settings may change, and the HTTP interface runs SET outside any session: the server
        // accepts it and it changes nothing for the next request. Harmless here, but it is the gate, not the
        // server, that keeps SET out of the raw tab (RawSqlGateTests), and a SET of a constrained setting is still 452.
        await QueryAsync("SET max_threads = 1");
        var next = await QueryAsync("SELECT toString(getSetting('max_threads'))");
        output.WriteLine($"SET max_threads = 1 accepted; next request sees max_threads = {next[0][0]}");
        Assert.NotEqual("1", next[0][0]);
        var constrained = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync("SET max_result_rows = 5000000"));
        Assert.Equal(SettingConstraintViolation, constrained.ErrorCode);
    }

    [Fact]
    public async Task IntoOutfileIsRefusedOverHttp()
    {
        var e = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync("SELECT 1 INTO OUTFILE '/tmp/query_probe.csv'"));
        Assert.Equal(IntoOutfileNotAllowed, e.ErrorCode);
    }

    [Fact]
    public async Task TheGrantedRelationsAreReadableAndNothingElseIsListed()
    {
        var rows = await QueryAsync("SELECT count() FROM events_v", new() { ["SQL_allowed_cases"] = "[1]" });
        Assert.Equal(ClickHouseFixture.RowsPerCase.ToString(CultureInfo.InvariantCulture), rows[0][0]);
        // Case 1: the ingest-account test of this class writes field_stats rows for cases 2 and 3, in either order.
        var stats = await QueryAsync("SELECT count() FROM field_stats_v", new() { ["SQL_allowed_cases"] = "[1]" });
        Assert.Equal("0", stats[0][0]);
        // The table under that view holds every case's statistics and has no filter: no grant on it.
        var table = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync("SELECT count() FROM field_stats", new() { ["SQL_allowed_cases"] = "[1,2,3]" }));
        Assert.Equal(AccessDenied, table.ErrorCode);

        // system.tables and system.databases are filtered by SHOW, which SELECT implies: the user sees its two
        // relations and the default database, nothing of the schema behind the view.
        var visible = await QueryAsync("SELECT database || '.' || name FROM system.tables WHERE database NOT IN ('INFORMATION_SCHEMA', 'information_schema') ORDER BY 1");
        output.WriteLine("system.tables as urlog_query: " + string.Join(", ", visible.Select(r => r[0])));
        Assert.Equal(["default.case_histogram_v", "default.events_v", "default.field_stats_v"], visible.Select(r => r[0]).ToArray());
        var databases = await QueryAsync("SELECT name FROM system.databases ORDER BY name");
        Assert.Equal(["default"], databases.Select(r => r[0]).ToArray());
    }

    [Fact]
    public async Task AQueryWhoseClientHasLeftIsStoppedWithinSeconds()
    {
        // A scan that would run to max_execution_time (60 s). The request is cancelled once the server shows the
        // query in flight, which closes the connection; cancel_http_readonly_queries_on_client_close on the
        // profile then stops it. Without the setting it stays in system.processes for the full minute.
        const string Marker = "sipHash64(number) = 7341";
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var abandoned = Task.Run(async () =>
        {
            await using var connection = new ClickHouseConnection(ch.HttpConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
            await connection.OpenAsync(leaving.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count() FROM numbers(1000000000000) WHERE {Marker}";
            return await command.ExecuteScalarAsync(leaving.Token);
        }, Ct);

        await using var admin = ch.Admin();
        async Task<ulong> InFlightAsync() => Convert.ToUInt64(
            await admin.ExecuteScalarAsync($"SELECT count() FROM system.processes WHERE user = 'urlog_query' AND query LIKE '%{Marker}%' AND query NOT LIKE '%system.processes%'", cancellationToken: Ct),
            CultureInfo.InvariantCulture);

        for (var i = 0; i < 250 && await InFlightAsync() == 0; i++)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(1UL, await InFlightAsync());
        await Task.Delay(500, Ct); // it is running, not about to finish
        Assert.Equal(1UL, await InFlightAsync());

        var left = System.Diagnostics.Stopwatch.StartNew();
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        // No ceiling on a measured time: the wait ends when the query is gone, and gives up at a third of
        // max_execution_time, the moment the query would have ended by itself with the setting off.
        while (await InFlightAsync() != 0 && left.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(50, Ct);
        }

        output.WriteLine($"gone from system.processes {left.ElapsedMilliseconds} ms after the client left");
        Assert.Equal(0UL, await InFlightAsync());
    }

    [Fact]
    public async Task TheQueryUsersConcurrentQueriesStopAtTheBackstop()
    {
        // max_concurrent_queries_for_user = 16: that many queries held open by sleep(3), seen in flight by the
        // admin, then one more. The API bounds its own statements below this (12 in all) and answers 429 there;
        // this is the server-side backstop for everything that uses the account.
        const ulong Backstop = 16;
        var held = Enumerable.Range(0, (int)Backstop).Select(_ => QueryAsync("SELECT sleep(3)")).ToArray();
        await using (var admin = ch.Admin())
        {
            var inFlight = 0UL;
            for (var i = 0; i < 100 && inFlight < Backstop; i++)
            {
                inFlight = Convert.ToUInt64(await admin.ExecuteScalarAsync("SELECT count() FROM system.processes WHERE user = 'urlog_query' AND query LIKE 'SELECT sleep(3)%'", cancellationToken: Ct), CultureInfo.InvariantCulture);
                if (inFlight < Backstop)
                {
                    await Task.Delay(20, Ct);
                }
            }

            Assert.Equal(Backstop, inFlight);
        }

        var over = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync("SELECT 1"));
        output.WriteLine($"one more query with {Backstop} in flight -> {over.ErrorCode}: {First(over)}");
        Assert.Equal(TooManySimultaneousQueries, over.ErrorCode);
        await Task.WhenAll(held);
        Assert.Equal("1", (await QueryAsync("SELECT 1"))[0][0]);
    }

    [Fact]
    public async Task IngestAccountRunsTheReparseProtocolAndThePartitionDrop()
    {
        // A case outside the seed, through the paths the backend uses: NativeInserter for the rows, EventStoreAdmin on
        // the driver's TCP client for the control plane, both as urlog_ingest; the result read through events_v as
        // urlog_query.
        const ulong caseId = 9;
        const ulong fileId = 900;
        var allowed = new Dictionary<string, string> { ["SQL_allowed_cases"] = "[9]" };
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);

        await ch.InsertAsync(caseId, fileId, generation: 1, month: 3, rows: 100);
        await store.CommitFileGenerationAsync(fileId, 1, Ct);
        Assert.Equal("100", (await QueryAsync("SELECT count() FROM events_v", allowed))[0][0]);

        await store.DeleteFileRowsAsync(fileId, Ct);
        await ch.InsertAsync(caseId, fileId, generation: 2, month: 3, rows: 60);
        Assert.Equal("0", (await QueryAsync("SELECT count() FROM events_v", allowed))[0][0]); // generation 1 deleted, generation 2 not yet current
        await store.CommitFileGenerationAsync(fileId, 2, Ct);
        var visible = await QueryAsync("SELECT toString(count()), toString(min(parse_generation)), toString(max(parse_generation)) FROM events_v", allowed);
        output.WriteLine($"after the generation-2 commit as urlog_ingest: {string.Join(" / ", visible[0])}");
        Assert.Equal(["60", "2", "2"], visible[0]);

        var months = await store.CasePartitionsAsync(caseId, Ct);
        Assert.Equal([202603], months.Select(m => m.Month));
        // The parts still count the 100 rows the lightweight delete masked, until a merge drops them.
        Assert.InRange(months[0].Rows, 60, 160);
        Assert.True(months[0].BytesOnDisk > 0);
        await store.DropCasePartitionAsync(caseId, 202603, Ct);
        Assert.Equal("0", (await QueryAsync("SELECT count() FROM events_v", allowed))[0][0]);
        Assert.Empty(await store.CasePartitionsAsync(caseId, Ct));
        // Dropping what is not there is not an error: a purge repeats its drops after a stop.
        await store.DropCasePartitionAsync(caseId, 202603, Ct);

        // The seed is untouched.
        Assert.Equal(ClickHouseFixture.RowsPerCase.ToString(CultureInfo.InvariantCulture), (await QueryAsync("SELECT count() FROM events_v", new() { ["SQL_allowed_cases"] = "[1]" }))[0][0]);
    }

    [Fact]
    public async Task IngestAccountReadsEventsForOneCaseWritesFieldStatsAndWatchesTheSystemTables()
    {
        await using var ingest = ch.Ingest();
        var sql = CaseScopedEventsQuery.Build(2, "host, count() AS c", tail: "GROUP BY host ORDER BY host");
        var hosts = new List<string>();
        await foreach (var row in ingest.QueryAsync(sql, cancellationToken: Ct))
        {
            hosts.Add($"{row[0]}={row[1]}");
        }

        output.WriteLine($"{sql} -> {string.Join(", ", hosts)}");
        Assert.Equal(["web-01=100", "web-02=100"], hosts);

        var cases = ClickHouseTcpColumn.Create("case_id", new ulong[] { 2, 3 });
        string[] fieldNames = ["host", "host"];
        string[] fieldValues = ["web-01", "web-02"];
        var fields = ClickHouseTcpColumn.Create("field", fieldNames);
        var values = ClickHouseTcpColumn.Create("value", fieldValues);
        var counts = ClickHouseTcpColumn.Create("count", new ulong[] { 100, 100 });
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        var times = ClickHouseTcpColumn.Create("computed_at", new[] { now, now });
        await ingest.InsertAsync("INSERT INTO field_stats (case_id, field, value, count, computed_at) VALUES", [cases, fields, values, counts, times], cancellationToken: Ct);
        Assert.Equal("1", (await QueryAsync("SELECT count() FROM field_stats_v", new() { ["SQL_allowed_cases"] = "[2]" }))[0][0]);

        // A case's statistics go when the case does or when they are recomputed: the delete of one case's
        // rows is allowed and leaves the other case's.
        await ingest.ExecuteAsync("DELETE FROM field_stats WHERE case_id = 2", cancellationToken: Ct);
        Assert.Equal("0", (await QueryAsync("SELECT count() FROM field_stats_v", new() { ["SQL_allowed_cases"] = "[2]" }))[0][0]);
        Assert.Equal("1", (await QueryAsync("SELECT count() FROM field_stats_v", new() { ["SQL_allowed_cases"] = "[3]" }))[0][0]);

        foreach (var table in new[] { "parts", "merges", "mutations" })
        {
            var n = await ingest.ExecuteScalarAsync($"SELECT count() FROM system.{table} WHERE database = currentDatabase()", cancellationToken: Ct);
            output.WriteLine($"system.{table}: {n}");
            Assert.NotNull(n);
        }

        // The watermark's read: every disk with its free and total bytes.
        var disks = await new EventStoreAdmin(ingest).DiskSpaceAsync(Ct);
        output.WriteLine(string.Join("; ", disks.Select(d => $"{d.Name} {d.FreeBytes}/{d.TotalBytes}")));
        Assert.NotEmpty(disks);
        Assert.All(disks, d => Assert.True(d.TotalBytes > 0 && d.FreeBytes >= 0 && d.FreeBytes <= d.TotalBytes));

        Assert.NotNull(await ingest.ExecuteScalarAsync("SELECT max(current) FROM file_generation", cancellationToken: Ct));

        // The store identity is readable by the write path (none in this fixture: nothing paired it) and by nobody below it.
        Assert.Null(await new EventStoreAdmin(ingest).ReadStoreIdentityAsync(Ct));
    }

    [Theory]
    [InlineData("SELECT count() FROM events_v")]
    [InlineData("INSERT INTO events_v (case_id) VALUES (1)")]
    [InlineData("SELECT count() FROM schema_migrations")]
    [InlineData("SELECT count() FROM field_stats")]
    [InlineData("CREATE TABLE ingest_denied (x UInt8) ENGINE = Memory")]
    [InlineData("CREATE TEMPORARY TABLE ingest_tmp (x UInt8)")]
    [InlineData("CREATE VIEW ingest_v AS SELECT * FROM events")]
    [InlineData("CREATE DATABASE ingest_db")]
    [InlineData("DROP TABLE events")]
    [InlineData("DROP VIEW events_v")]
    [InlineData("SYSTEM RELOAD DICTIONARIES")]
    [InlineData("CREATE DICTIONARY ingest_dict (k UInt64, v UInt32) PRIMARY KEY k SOURCE(CLICKHOUSE(TABLE 'file_generation')) LAYOUT(HASHED()) LIFETIME(0)")]
    [InlineData("TRUNCATE TABLE events")]
    [InlineData("RENAME TABLE events TO events_old")]
    [InlineData("ALTER TABLE events ADD COLUMN ingest_probe UInt8")]
    [InlineData("ALTER TABLE events DROP COLUMN user_agent")]
    [InlineData("ALTER TABLE events MODIFY TTL toDateTime(ts) + INTERVAL 1 DAY")]
    [InlineData("ALTER TABLE events UPDATE host = 'x' WHERE 0")]
    [InlineData("OPTIMIZE TABLE events FINAL")]
    [InlineData("TRUNCATE TABLE file_generation")]
    [InlineData("ALTER TABLE file_generation UPDATE current = 9 WHERE 1")]
    [InlineData("DROP TABLE file_generation")]
    [InlineData("ALTER TABLE file_generation ADD COLUMN ingest_probe UInt8")]
    [InlineData("ALTER TABLE field_stats UPDATE count = 0 WHERE 1")]
    [InlineData("ALTER TABLE field_stats ADD COLUMN ingest_probe UInt8")]
    [InlineData("DROP TABLE field_stats")]
    [InlineData("DELETE FROM schema_migrations WHERE 1")]
    [InlineData("INSERT INTO store_identity (store_id, created_at, created_by) VALUES ('00000000000000000000000000000000', now64(3), 'x')")]
    [InlineData("DELETE FROM store_identity WHERE 1")]
    [InlineData("ALTER TABLE store_identity DELETE WHERE 1")]
    [InlineData("TRUNCATE TABLE store_identity")]
    [InlineData("DROP TABLE store_identity")]
    [InlineData("TRUNCATE TABLE field_stats")]
    [InlineData("CREATE USER ingest_user")]
    [InlineData("GRANT SELECT ON default.events TO urlog_query")]
    [InlineData("SYSTEM DROP MARK CACHE")]
    [InlineData("SYSTEM SHUTDOWN")]
    [InlineData("KILL QUERY WHERE 1")]
    [InlineData("SELECT count() FROM system.query_log")]
    [InlineData("SELECT count() FROM system.processes")]
    [InlineData("SELECT count() FROM system.users")]
    [InlineData("SELECT count() FROM system.grants")]
    [InlineData("SELECT * FROM url('http://127.0.0.1:8123/?query=SELECT%201', CSV, 'a String')")]
    [InlineData("SELECT * FROM file('x.csv')")]
    [InlineData("SELECT * FROM s3('http://127.0.0.1/x.csv', CSV)")]
    [InlineData("SELECT * FROM remote('127.0.0.1', 'default', 'events')")]
    [InlineData("SELECT * FROM mysql('127.0.0.1:3306', 'd', 't', 'u', 'p')")]
    [InlineData("SELECT * FROM executable('ls', 'TSV', 'x String')")]
    public async Task IngestAccountIsRefusedEverythingElse(string sql)
    {
        await using var ingest = ch.Ingest();
        var e = await Assert.ThrowsAsync<ClickHouseTcpServerException>(async () =>
        {
            await foreach (var _ in ingest.QueryAsync(sql, cancellationToken: Ct))
            {
            }
        });
        output.WriteLine($"{sql,-72} -> {e.RawCode} {e.Name}");
        Assert.Equal(AccessDenied, e.RawCode);
    }

    [Fact]
    public async Task AlterDeleteAlsoLetsTheIngestAccountDetachAndReattachAPartition()
    {
        // Measured, not wanted: on 26.8 DETACH PARTITION is checked against ALTER DELETE like DROP PARTITION (there is
        // no narrower privilege), and ATTACH PARTITION against INSERT, so the write account can take a partition out
        // of the table and put it back. Nothing in the code does; the design record says so. This pins the width so
        // a server upgrade that changes it is noticed. A case of its own: the seed stays whole.
        const ulong caseId = 8;
        var allowed = new Dictionary<string, string> { ["SQL_allowed_cases"] = "[8]" };
        await ch.InsertAsync(caseId, fileId: 800, generation: 1, month: 4, rows: 10);
        await using var ingest = ch.Ingest();
        await new EventStoreAdmin(ingest).CommitFileGenerationAsync(800, 1, Ct);
        Assert.Equal("10", (await QueryAsync("SELECT count() FROM events_v", allowed))[0][0]);
        await ingest.ExecuteAsync("ALTER TABLE events DETACH PARTITION (8, 202604)", cancellationToken: Ct);
        Assert.Equal("0", (await QueryAsync("SELECT count() FROM events_v", allowed))[0][0]);
        await ingest.ExecuteAsync("ALTER TABLE events ATTACH PARTITION (8, 202604)", cancellationToken: Ct);
        Assert.Equal("10", (await QueryAsync("SELECT count() FROM events_v", allowed))[0][0]);
        await new EventStoreAdmin(ingest).DropCasePartitionAsync(caseId, 202604, Ct);
    }

    [Fact]
    public async Task IngestAccountRemovesTheGenerationRowsOfAPurgedCaseAndTheDeleteIsAppliedWhenItReturns()
    {
        // Ids of their own, far from the seed's.
        ulong[] files = [9101, 9102, 9103];
        await using var ingest = ch.Ingest();
        var store = new EventStoreAdmin(ingest);
        await store.CommitFileGenerationsAsync([new(9101, 1), new(9102, 3), new(9103, 1)], Ct);
        await store.CommitFileGenerationAsync(9101, 2, Ct);
        Assert.Equal(4, await store.CountFileGenerationsAsync(files, Ct));

        await store.ClearFileGenerationsAsync([9101, 9102], Ct);
        // No wait between the delete and the read: the statement returns with its mask applied.
        Assert.Equal(0, await store.CountFileGenerationsAsync([9101, 9102], Ct));
        Assert.Equal(1, await store.CountFileGenerationsAsync(files, Ct));
        Assert.Equal(0u, await store.ReadFileGenerationAsync(9101, Ct));

        // A repeat and an id that never had a row are not errors: a purge repeats this step after a stop.
        await store.ClearFileGenerationsAsync([9101, 9102, 9199], Ct);
        // An id handed out again starts from no row.
        await store.CommitFileGenerationAsync(9101, 1, Ct);
        Assert.Equal(1u, await store.ReadFileGenerationAsync(9101, Ct));
        await store.ClearFileGenerationsAsync(files, Ct);
        Assert.Equal(0, await store.CountFileGenerationsAsync(files, Ct));
    }

    [Fact]
    public async Task AlterDeleteOnFileGenerationAlsoLetsTheIngestAccountEmptyTheTable()
    {
        // Measured, not wanted: there is no privilege for a row delete alone, and ALTER DELETE passes a drop of
        // the table's one partition. It hides every file until a runner starts and writes the generations back
        // from the metadata file. Nothing in the code does it; this pins the width. The rows are put back here.
        await using var admin = ch.Admin();
        var before = new List<(ulong File, uint Current)>();
        await foreach (var row in admin.QueryAsync("SELECT file_id, argMax(current, updated_at) FROM file_generation GROUP BY file_id", cancellationToken: Ct))
        {
            before.Add((Convert.ToUInt64(row[0], CultureInfo.InvariantCulture), Convert.ToUInt32(row[1], CultureInfo.InvariantCulture)));
        }

        await using var ingest = ch.Ingest();
        await ingest.ExecuteAsync("ALTER TABLE file_generation DROP PARTITION tuple()", cancellationToken: Ct);
        Assert.Equal(0L, Convert.ToInt64(await ingest.ExecuteScalarAsync("SELECT count() FROM file_generation", cancellationToken: Ct), CultureInfo.InvariantCulture));
        await new EventStoreAdmin(ingest).CommitFileGenerationsAsync([.. before.Select(b => new KeyValuePair<ulong, uint>(b.File, b.Current))], Ct);
    }

    [Fact]
    public async Task IngestAccountLiftsTheDropLimitForItsOwnStatementOnly()
    {
        // The server refuses to drop a partition above max_partition_size_to_drop (50 GB by default). The purge's
        // statement carries the setting with 0; the same account with a limit of one byte is refused, which shows
        // the server takes the statement's value.
        const ulong caseId = 9;
        await ch.InsertAsync(caseId, fileId: 900, generation: 1, month: 5, rows: 10);
        await using var ingest = ch.Ingest();
        var refused = await Assert.ThrowsAsync<ClickHouseTcpServerException>(() => ingest.ExecuteAsync("ALTER TABLE events DROP PARTITION (9, 202605) SETTINGS max_partition_size_to_drop = 1", cancellationToken: Ct).AsTask());
        Assert.Equal(359, refused.RawCode);
        var store = new EventStoreAdmin(ingest);
        Assert.Single(await store.CasePartitionsAsync(caseId, Ct));
        await store.DropCasePartitionAsync(caseId, 202605, Ct);
        Assert.Empty(await store.CasePartitionsAsync(caseId, Ct));
        await store.DeleteCaseFieldStatsAsync(caseId, Ct);
    }

    [Fact]
    public async Task TheDictionaryAndItsReaderAccountAreGone()
    {
        // events_v decides visibility from file_generation itself (migration 0006), as its definer. What the
        // dictionary needed is removed with it: the dictionary, the passwordless urlog_dict account and the write
        // path's SYSTEM RELOAD DICTIONARY grant (the grant list below pins the last).
        await using var admin = ch.Admin();
        Assert.Equal(0UL, Convert.ToUInt64(await admin.ExecuteScalarAsync("SELECT count() FROM system.dictionaries WHERE database = currentDatabase()", cancellationToken: Ct), CultureInfo.InvariantCulture));
        Assert.Equal(0UL, Convert.ToUInt64(await admin.ExecuteScalarAsync("SELECT count() FROM system.users WHERE name = 'urlog_dict'", cancellationToken: Ct), CultureInfo.InvariantCulture));
        var users = new List<string>();
        await foreach (var row in admin.QueryAsync("SELECT name FROM system.users WHERE name LIKE 'urlog%' ORDER BY name", cancellationToken: Ct))
        {
            users.Add((string)row[0]!);
        }

        Assert.Equal(["urlog_ingest", "urlog_query"], users);

        // The query user reads the view and still nothing under it.
        Assert.Equal(ClickHouseFixture.RowsPerCase.ToString(CultureInfo.InvariantCulture), (await QueryAsync("SELECT count() FROM events_v", new() { ["SQL_allowed_cases"] = "[1]" }))[0][0]);
        var refused = await Assert.ThrowsAsync<ClickHouseServerException>(() => QueryAsync("SELECT count() FROM file_generation"));
        Assert.Equal(AccessDenied, refused.ErrorCode);
    }

    [Fact]
    public async Task IngestGrantsAreExactlyTheDeclaredOnes()
    {
        await using var admin = ch.Admin();
        var grants = new List<string>();
        await foreach (var row in admin.QueryAsync("SELECT toString(access_type), ifNull(database, '*'), ifNull(table, '*') FROM system.grants WHERE user_name = 'urlog_ingest' ORDER BY 2, 3, 1", cancellationToken: Ct))
        {
            grants.Add($"{row[0]} {row[1]}.{row[2]}");
        }

        output.WriteLine(string.Join("; ", grants));
        Assert.Equal(
            [
                "ALTER DELETE default.case_histogram", "INSERT default.case_histogram",
                "ALTER DELETE default.events", "INSERT default.events", "SELECT default.events",
                "ALTER DELETE default.field_stats", "INSERT default.field_stats",
                "ALTER DELETE default.file_generation", "INSERT default.file_generation", "SELECT default.file_generation",
                "SELECT default.store_identity",
                "SELECT system.disks", "SELECT system.merges", "SELECT system.mutations", "SELECT system.parts",
            ],
            grants);

        // The profile: one bound, no readonly, DDL not switched off by setting (the grants are what refuse it).
        await using var ingest = ch.Ingest();
        var profile = (string?)await ingest.ExecuteScalarAsync("SELECT concat(toString(getSetting('max_memory_usage')), ' ', toString(getSetting('readonly')), ' ', toString(getSetting('allow_ddl')))", cancellationToken: Ct);
        Assert.Equal("4000000000 0 true", profile);
    }

    [Fact]
    public async Task GrantsAreExactlyTheThreeDeclaredOnes()
    {
        await using var admin = ch.Admin();
        var grants = new List<string>();
        await foreach (var row in admin.QueryAsync("SELECT toString(access_type), database, table FROM system.grants WHERE user_name = 'urlog_query' ORDER BY table", cancellationToken: Ct))
        {
            grants.Add($"{row[0]} {row[1]}.{row[2]}");
        }

        output.WriteLine(string.Join("; ", grants));
        Assert.Equal(["SELECT default.case_histogram_v", "SELECT default.events_v", "SELECT default.field_stats_v"], grants);

        // Both views read their tables as the definer; without the clause the query user would need the tables.
        var statsView = (string?)await admin.ExecuteScalarAsync("SELECT create_table_query FROM system.tables WHERE database = currentDatabase() AND name = 'field_stats_v'", cancellationToken: Ct);
        Assert.Contains("SQL SECURITY DEFINER", statsView, StringComparison.Ordinal);

        var definer = (string?)await admin.ExecuteScalarAsync("SELECT create_table_query FROM system.tables WHERE database = currentDatabase() AND name = 'events_v'", cancellationToken: Ct);
        Assert.NotNull(definer);
        Assert.Contains("SQL SECURITY DEFINER", definer, StringComparison.Ordinal);
        Assert.Contains($"DEFINER = {ClickHouseFixture.AdminUser}", definer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRelationTheMigrationsCreateIsKnownToTheGate()
    {
        // A new migration that adds a relation must add it to RawSqlGate.DeniedRelations (or AllowedRelations with a
        // grant); otherwise the gate would take its name for a CTE and leave the refusal to the grants alone.
        await using var admin = ch.Admin();
        var names = new List<string>();
        await foreach (var row in admin.QueryAsync("SELECT name FROM system.tables WHERE database = currentDatabase() ORDER BY name", cancellationToken: Ct))
        {
            names.Add((string)row[0]!);
        }

        output.WriteLine(string.Join(", ", names));
        var unknown = names.Where(n => !RawSqlGate.AllowedRelations.Contains(n) && !RawSqlGate.DeniedRelations.Contains(n)).ToList();
        Assert.True(unknown.Count == 0, "relations the gate does not know: " + string.Join(", ", unknown));
        Assert.All(RawSqlGate.AllowedRelations.Concat(RawSqlGate.DeniedRelations), n => Assert.Contains(n, names));
    }

    private static string First(Exception e) => e.Message.Split('\n')[0];

    /// <summary>The driver straight, as <c>urlog_query</c>: the gate is not in the way, the server's own refusals are the subject.</summary>
    private async Task<List<string[]>> QueryAsync(string sql, Dictionary<string, string>? settings = null)
    {
        await using var connection = new ClickHouseConnection(ch.HttpConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (k, v) in settings ?? [])
        {
            command.CustomSettings[k] = v;
        }

        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = ClickHouseQueryClient.Text(reader.GetValue(i));
            }

            rows.Add(row);
        }

        return rows;
    }
}
