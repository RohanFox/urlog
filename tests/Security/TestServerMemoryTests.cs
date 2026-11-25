using System.Globalization;
using Urlog.Tests.Shared;

namespace Urlog.Security.Tests;

/// <summary>The server of the security tests has the fixed memory ceiling of the tests, as the integration tests' has.</summary>
[Collection(SeedReaders.Name)]
public sealed class TestServerMemoryTests(ClickHouseFixture ch)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFixturesServerReportsTheFixedCeiling()
    {
        await using var client = ch.Admin();
        var value = await client.ExecuteScalarAsync("SELECT value FROM system.server_settings WHERE name = 'max_server_memory_usage'", cancellationToken: Ct);
        Assert.Equal(TestServerMemory.CeilingBytes.ToString(CultureInfo.InvariantCulture), Convert.ToString(value, CultureInfo.InvariantCulture));
    }
}
