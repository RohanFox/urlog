namespace Urlog.Bench;

/// <summary>Locates the repository root from wherever the binary runs, so artifacts and fixtures have one home.</summary>
public static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(Find);

    public static string Root => RootLazy.Value;

    public static string Artifacts(string suffix = "") => Path.Combine(Root, "bench", "BenchmarkDotNet.Artifacts" + suffix);

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
