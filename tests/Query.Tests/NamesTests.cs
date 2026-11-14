using Urlog.Query;

namespace Urlog.Query.Tests;

public sealed class NamesTests
{
    [Theory]
    [InlineData("n", true)]
    [InlineData("_n9", true)]
    [InlineData("Failed_Logins", true)]
    [InlineData("", false)]
    [InlineData("9n", false)]
    [InlineData("a.b", false)]
    [InlineData("a-b", false)]
    [InlineData("a b", false)]
    [InlineData("a\\", false)]
    [InlineData("a`b", false)]
    [InlineData("a'b", false)]
    [InlineData("a\"b", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\0b", false)]
    [InlineData("aéb", false)]
    [InlineData("а", false)] // a Cyrillic letter that looks like 'a'
    public void TheStrictRule(string name, bool accepted) => Assert.Equal(accepted, Names.IsStrict(name));

    [Fact]
    public void TheStrictRuleStopsAtSixtyFourCharacters()
    {
        Assert.True(Names.IsStrict(new string('a', Names.MaxLength)));
        Assert.False(Names.IsStrict(new string('a', Names.MaxLength + 1)));
    }

    [Theory]
    [InlineData("c0", true)]
    [InlineData("c17", true)]
    [InlineData("c", false)]
    [InlineData("c1x", false)]
    [InlineData("C1", false)]
    [InlineData("count", false)]
    public void TheGeneratedForm(string name, bool generated)
    {
        Assert.Equal(generated, Names.IsGenerated(name));
        Assert.True(Names.IsGenerated(Names.Generated(0)) && Names.IsGenerated(Names.Generated(123)));
    }

    [Fact]
    public void EveryCatalogNameIsAnIdentifierAndNoneLooksGenerated()
    {
        // A catalog name is the only text from outside the code that is written into the SQL as an identifier.
        Assert.All(FieldCatalog.Embedded.Fields, f => Assert.True(Names.IsCatalogName(f.Name), f.Name));
    }

    [Theory]
    [InlineData("c0")]
    [InlineData("a`b")]
    [InlineData("a\\\\")]
    [InlineData(".a")]
    [InlineData("a b")]
    public void TheLoaderRefusesACatalogNameThatIsNotOne(string name)
    {
        var yaml = $"version: 1\nfields:\n  - name: \"{name}\"\n    column: host\n    type: keyword\n";
        var e = Assert.Throws<InvalidDataException>(() => FieldCatalog.Parse(yaml));
        Assert.Contains("is not a field name", e.Message, StringComparison.Ordinal);
    }
}
