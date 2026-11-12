using Urlog.Core.Metadata;

namespace Urlog.Metadata.Tests;

public sealed class NamesTests
{
    [Theory]
    [InlineData("SoftDeleted", "soft_deleted")]
    [InlineData("TsUtc", "ts_utc")]
    [InlineData("Sha256", "sha256")]
    [InlineData("Id", "id")]
    [InlineData("ParentFileId", "parent_file_id")]
    [InlineData("Rw", "rw")]
    [InlineData("DetailsJson", "details_json")]
    public void SnakeCase(string pascal, string expected) => Assert.Equal(expected, Names.Snake(pascal));

    [Fact]
    public void EnumsRoundTripThroughTheirText()
    {
        foreach (var state in Enum.GetValues<CaseState>())
        {
            Assert.Equal(state, Names.Parse<CaseState>(Names.Of(state)));
        }

        Assert.Equal("soft_deleted", Names.Of(CaseState.SoftDeleted));
        Assert.Throws<ArgumentOutOfRangeException>(() => Names.Parse<CaseState>("deleted"));
        Assert.Equal("role IN ('owner', 'rw', 'ro')", Names.EnumCheck<MemberRole>("role"));
    }
}
