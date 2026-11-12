using Urlog.Core;

namespace Urlog.Core.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void PinnedClickHouseLineIsAMajorMinor()
    {
        Assert.Matches(@"^\d{2}\.\d{1,2}$", CoreInfo.ClickHouseLts);
    }
}
