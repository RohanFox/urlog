using System.Globalization;
using ClickHouse.Driver.Tcp;
using Urlog.Core;

namespace Urlog.Integration.Tests;

/// <summary>
/// Driver acceptance: the pinned driver reaches the pinned ClickHouse LTS over the native TCP protocol,
/// inserts columnar blocks, and the read-only query user is really read-only.
/// Outcome recorded in docs/adr/0002-clickhouse-driver.md and docs/design-record.md.
/// </summary>
public sealed class ClickHouseNativeProtocolTests(ClickHouseFixture ch, ITestOutputHelper output)
    : IClassFixture<ClickHouseFixture>
{
    [Fact]
    public async Task ConnectsOverNativeProtocolToPinnedLts()
    {
        await using var client = new ClickHouseTcpClient(ch.NativeConnectionString(ClickHouseFixture.AdminUser, ClickHouseFixture.AdminPassword));

        await client.PingAsync(TestContext.Current.CancellationToken);
        var info = await client.GetServerInfoAsync(TestContext.Current.CancellationToken);
        var version = (string?)await client.ExecuteScalarAsync("SELECT version()", cancellationToken: TestContext.Current.CancellationToken);

        output.WriteLine($"native TCP {ch.Host}:{ch.NativeHostPort} -> {info.Name} {info.Version} (protocol {info.ProtocolRevision}, tz {info.Timezone}); SELECT version() = {version}");

        Assert.NotNull(version);
        Assert.StartsWith(CoreInfo.ClickHouseLts + ".", version, StringComparison.Ordinal);
        Assert.Equal(CoreInfo.ClickHouseLts, $"{info.VersionMajor}.{info.VersionMinor}");
    }

    [Fact]
    public async Task ColumnarInsertRoundTripsWithCompressedBlocks()
    {
        await using var client = new ClickHouseTcpClient(ch.NativeConnectionString(ClickHouseFixture.AdminUser, ClickHouseFixture.AdminPassword));
        var ct = TestContext.Current.CancellationToken;

        await client.ExecuteAsync("DROP TABLE IF EXISTS driver_probe", cancellationToken: ct);
        await client.ExecuteAsync(
            "CREATE TABLE driver_probe (id UInt64, host LowCardinality(String), message String) ENGINE = MergeTree ORDER BY id",
            cancellationToken: ct);

        const int rows = 10_000;
        var ids = new ulong[rows];
        var hosts = new string[rows];
        var messages = new string[rows];
        for (var i = 0; i < rows; i++)
        {
            ids[i] = (ulong)i;
            hosts[i] = i % 2 == 0 ? "web-01" : "web-02";
            messages[i] = string.Create(CultureInfo.InvariantCulture, $"GET /path/{i} 200");
        }

        var written = new List<ClickHouseTcpBlockWritten>();
        var options = new ClickHouseTcpInsertOptions
        {
            Callbacks = new ClickHouseTcpQueryCallbacks { OnBlockWritten = written.Add },
        };
        var columns = new IColumn[]
        {
            ClickHouseTcpColumn.Create("id", ids),
            ClickHouseTcpColumn.Create("host", hosts),
            ClickHouseTcpColumn.Create("message", messages),
        };

        await client.InsertAsync("INSERT INTO driver_probe (id, host, message) VALUES", columns, options, ct);

        var count = Convert.ToInt64(await client.ExecuteScalarAsync("SELECT count() FROM driver_probe", cancellationToken: ct), CultureInfo.InvariantCulture);
        var sum = Convert.ToUInt64(await client.ExecuteScalarAsync("SELECT sum(id) FROM driver_probe", cancellationToken: ct), CultureInfo.InvariantCulture);
        var last = (string?)await client.ExecuteScalarAsync("SELECT message FROM driver_probe ORDER BY id DESC LIMIT 1", cancellationToken: ct);
        var hostsSeen = (string?)await client.ExecuteScalarAsync("SELECT arrayStringConcat(arraySort(groupUniqArray(host)), ',') FROM driver_probe", cancellationToken: ct);

        foreach (var b in written)
        {
            output.WriteLine($"block {b.BlockIndex}: {b.RowCount} rows, {b.UncompressedBytes} B raw -> {b.CompressedBytes} B on the wire");
        }

        Assert.Equal(rows, count);
        Assert.Equal((ulong)rows * (rows - 1) / 2, sum);
        Assert.Equal("GET /path/9999 200", last);
        Assert.Equal("web-01,web-02", hostsSeen);
        Assert.NotEmpty(written);
        Assert.All(written, b => Assert.True(b.CompressedBytes > 0 && b.CompressedBytes < b.UncompressedBytes, "LZ4 should shrink the block on the wire"));

        await client.ExecuteAsync("DROP TABLE driver_probe", cancellationToken: ct);
    }

    [Fact]
    public async Task ReadOnlyQueryUserCannotWriteButCanSendCustomSettings()
    {
        await using var client = new ClickHouseTcpClient(ch.NativeConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        var ct = TestContext.Current.CancellationToken;

        var ddl = await Assert.ThrowsAsync<ClickHouseTcpServerException>(
            () => client.ExecuteAsync("CREATE TABLE driver_denied (x UInt8) ENGINE = Memory", cancellationToken: ct).AsTask());
        var dml = await Assert.ThrowsAsync<ClickHouseTcpServerException>(
            () => client.ExecuteAsync("INSERT INTO system.one SELECT 0", cancellationToken: ct).AsTask());
        output.WriteLine($"DDL denied: {ddl.RawCode} {ddl.Name}; DML denied: {dml.RawCode} {dml.Name}");
        // 497 ACCESS_DENIED since the user has explicit grants (the access check runs before the readonly one);
        // 164 READONLY was the profile-only stub's answer. Either way the write never happens.
        Assert.True(ddl.RawCode is 164 or 497, $"ddl code {ddl.RawCode}");
        Assert.True(dml.RawCode is 164 or 497, $"dml code {dml.RawCode}");

        // readonly=2 must still accept per-query settings; a known setting goes through.
        var threads = (string?)await client.ExecuteScalarAsync(
            "SELECT toString(getSetting('max_threads'))",
            new ClickHouseTcpQueryOptions { Settings = new Dictionary<string, string> { ["max_threads"] = "3" } },
            ct);
        Assert.Equal("3", threads);

        // The server honours custom_settings_prefixes = SQL_ (deploy/clickhouse/config.d/urlog.xml).
        var inline = (string?)await client.ExecuteScalarAsync(
            "SELECT getSetting('SQL_allowed_cases') SETTINGS SQL_allowed_cases = '[7]'", cancellationToken: ct);
        Assert.Equal("[7]", inline);

        // KNOWN LIMITATION (ClickHouse.Driver 1.5.0 TCP client): a custom-prefixed setting sent out-of-band in
        // ClickHouseTcpQueryOptions.Settings never reaches the server, although the same request works over
        // HTTP and from clickhouse-client. This assertion is a tripwire: when a driver upgrade makes it pass,
        // revisit docs/adr/0002-clickhouse-driver.md and the case isolation mechanism.
        var dropped = await Assert.ThrowsAsync<ClickHouseTcpServerException>(
            () => client.ExecuteScalarAsync(
                "SELECT getSetting('SQL_allowed_cases')",
                new ClickHouseTcpQueryOptions { Settings = new Dictionary<string, string> { ["SQL_allowed_cases"] = "[7]" } },
                ct).AsTask());
        output.WriteLine($"custom setting out-of-band over TCP: {dropped.RawCode} {dropped.Message.Split('\n')[0]}");
        Assert.Equal(115, dropped.RawCode); // UNKNOWN_SETTING

        // The user cannot raise its own limits (profile constraints).
        var raised = await Assert.ThrowsAsync<ClickHouseTcpServerException>(
            () => client.ExecuteScalarAsync(
                "SELECT 1",
                new ClickHouseTcpQueryOptions { Settings = new Dictionary<string, string> { ["max_result_rows"] = "5000000" } },
                ct).AsTask());
        output.WriteLine($"limit raise denied: {raised.RawCode} {raised.Name}");
        Assert.Equal(452, raised.RawCode); // SETTING_CONSTRAINT_VIOLATION
    }
}
