using System.Globalization;
using ClickHouse.Driver.Tcp;
using Urlog.Tests.Shared;

namespace Urlog.Integration.Tests;

/// <summary>
/// The server a fixture starts has the fixed memory ceiling of the tests, and keeps it: the value the server
/// reports is the one in the tests' own configuration file, whatever else the machine holds at the moment.
/// </summary>
public sealed class TestServerMemoryTests(ClickHouseFixture ch) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFixturesServerReportsTheFixedCeiling()
    {
        await using var client = new ClickHouseTcpClient(ch.NativeConnectionString(ClickHouseFixture.AdminUser, ClickHouseFixture.AdminPassword));
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var row in client.QueryAsync("SELECT name, value FROM system.server_settings WHERE name IN ('max_server_memory_usage', 'max_server_memory_usage_to_ram_ratio')", cancellationToken: Ct))
        {
            settings[(string)row[0]!] = (string)row[1]!;
        }

        Assert.Equal(TestServerMemory.CeilingBytes.ToString(CultureInfo.InvariantCulture), settings["max_server_memory_usage"]);
        Assert.Equal("0.9", settings["max_server_memory_usage_to_ram_ratio"]);
    }

    [Fact]
    public void TheCeilingLeavesAMarginOverTheMeasuredPeak()
    {
        // The worker crash tests' server peaked at 3,122 MiB when it was measured.
        const long measuredPeak = 3122L << 20;
        Assert.InRange(TestServerMemory.CeilingBytes, measuredPeak * 5 / 4, measuredPeak * 3 / 2);
    }
}
