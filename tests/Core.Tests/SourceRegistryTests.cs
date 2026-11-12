using Urlog.Core.Sources;

namespace Urlog.Core.Tests;

/// <summary>The generator runs over this test assembly: <see cref="NumberedLineSource"/> carries the attribute.</summary>
public sealed class SourceRegistryTests
{
    [Fact]
    public void GeneratedRegistryListsTheAttributedSources()
    {
        var registration = Assert.Single(LogSourceRegistry.Registrations, r => r.Id == "test-lines");
        Assert.Equal(["tl", "numbered"], registration.Aliases);
        var instance = registration.Factory();
        Assert.IsType<NumberedLineSource>(instance);
        Assert.Equal("test-lines", instance.Info.Id);
        Assert.NotSame(instance, registration.Factory());
    }

    [Fact]
    public void CatalogResolvesIdsAndAliasesCaseInsensitively()
    {
        var catalog = new SourceCatalog();
        catalog.Add(LogSourceRegistry.Registrations);

        Assert.True(catalog.TryGet("TEST-LINES", out var byId));
        Assert.True(catalog.TryGet("numbered", out var byAlias));
        Assert.Same(byId, byAlias);
        Assert.IsType<NumberedLineSource>(catalog.Create("tl"));
        Assert.False(catalog.TryGet("nginx", out _));
        Assert.Throws<KeyNotFoundException>(() => catalog.Create("nginx"));

        var clash = new SourceCatalog();
        clash.Add([new SourceRegistration("other", ["tl"], static () => new NumberedLineSource())]);
        Assert.Throws<InvalidOperationException>(() => clash.Add(LogSourceRegistry.Registrations));
    }
}
