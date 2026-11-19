using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Tests;

/// <summary>
/// The settings file of one box: optional, read after the tracked appsettings files and before whatever follows
/// them, so it changes a tracked default and the environment still has the last word.
/// </summary>
public sealed class LocalSettingsTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("urlog-local-settings-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void TheFileIsOptionalAndSitsBetweenTheTrackedFilesAndWhatFollowsThem()
    {
        File.WriteAllText(Path.Combine(_directory.FullName, "appsettings.json"), """{"Urlog":{"A":"tracked","B":"tracked","C":"tracked"}}""");
        File.WriteAllText(Path.Combine(_directory.FullName, "appsettings.Development.json"), """{"Urlog":{"B":"development"}}""");

        ConfigurationManager Build()
        {
            var configuration = new ConfigurationManager();
            configuration.SetBasePath(_directory.FullName);
            configuration.AddJsonFile("appsettings.json");
            configuration.AddJsonFile("appsettings.Development.json");
            // What comes after the files in a host: the environment.
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Urlog:C"] = "environment" });
            ApiHost.AddLocalSettings(configuration, _directory.FullName, ApiHost.LocalSettingsFile);
            return configuration;
        }

        // No file: no error, nothing changes.
        using (var without = Build())
        {
            Assert.Equal(("tracked", "development", "environment"), (without["Urlog:A"], without["Urlog:B"], without["Urlog:C"]));
        }

        File.WriteAllText(Path.Combine(_directory.FullName, ApiHost.LocalSettingsFile), """{"Urlog":{"A":"local","B":"local","C":"local","D":"local"}}""");
        using var with = Build();
        // Over both tracked files, under the environment.
        Assert.Equal(("local", "local", "environment", "local"), (with["Urlog:A"], with["Urlog:B"], with["Urlog:C"], with["Urlog:D"]));
        var files = with.Sources.Select(s => s is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource json ? Path.GetFileName(json.Path) : "other").Where(n => n != "other").ToArray();
        Assert.Equal(["appsettings.json", "appsettings.Development.json", ApiHost.LocalSettingsFile], files);
        Assert.IsNotType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>(with.Sources[^1]);

        // An empty name adds nothing.
        using var off = new ConfigurationManager();
        ApiHost.AddLocalSettings(off, _directory.FullName, string.Empty);
        Assert.DoesNotContain(off.Sources, s => s is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource);
    }

    [Fact]
    public async Task TheHostStartsWithoutTheFileAndTakesAValueFromItWhenItIsThere()
    {
        var missing = Path.Combine(_directory.FullName, "none.json");
        await using (var factory = new ApiFactory(new Dictionary<string, string?> { [ApiHost.LocalSettingsKey] = missing }))
        {
            var options = factory.Services.GetRequiredService<IOptions<UrlogOptions>>().Value;
            Assert.Equal((10, 20L << 30), (options.Ingest.Watermark.MinFreePercent, options.Ingest.Watermark.MinFreeBytes));
        }

        var local = Path.Combine(_directory.FullName, "box.json");
        await File.WriteAllTextAsync(local, """{"Urlog":{"Ingest":{"Watermark":{"MinFreePercent":0}},"Upload":{"MaxFileBytes":4096}}}""", TestContext.Current.CancellationToken);
        // A setting given to the host directly comes after the file and wins.
        await using var with = new ApiFactory(new Dictionary<string, string?> { [ApiHost.LocalSettingsKey] = local, ["Urlog:Upload:MaxFileBytes"] = "8192" });
        var read = with.Services.GetRequiredService<IOptions<UrlogOptions>>().Value;
        Assert.Equal((0, 20L << 30, 8192L), (read.Ingest.Watermark.MinFreePercent, read.Ingest.Watermark.MinFreeBytes, read.Upload.MaxFileBytes));
    }
}
