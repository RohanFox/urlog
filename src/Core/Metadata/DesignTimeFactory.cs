using Microsoft.EntityFrameworkCore.Design;

namespace Urlog.Core.Metadata;

/// <summary>
/// What <c>dotnet ef</c> instantiates: <c>dotnet ef migrations add &lt;Name&gt; --project src/Core --output-dir Metadata/Migrations</c>
/// (tool manifest in <c>.config/dotnet-tools.json</c>). The file name is irrelevant for adding a migration; it is only opened
/// by <c>dotnet ef database update</c>, which this repository never runs (the store migrates itself on open).
/// </summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MetadataDb>
{
    public MetadataDb CreateDbContext(string[] args) =>
        new(MetadataStore.BuildOptions(args is { Length: > 0 } ? args[0] : Path.Combine(Path.GetTempPath(), "urlog-design-time.db")));
}
