using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Urlog.Bench.Fixtures;

/// <summary>What a generated fixture is. Stored next to the file as <c>&lt;name&gt;.meta.json</c>.</summary>
public sealed record FixtureMeta(
    string Name,
    string Path,
    string Generator,
    int GeneratorVersion,
    ulong Seed,
    long Bytes,
    long Lines,
    long OddityLines,
    string Sha256,
    DateTime GeneratedUtc,
    double GenerationSeconds,
    Dictionary<string, long>? Counts = null)
{
    public double MeanLineBytes => Lines == 0 ? 0 : (double)Bytes / Lines;
}

/// <summary>
/// The fixed-seed fixtures under <c>bench/data/</c> (gitignored). Generated on first use; reused when the
/// metadata matches the file on disk and the generator version has not changed.
/// </summary>
public static class FixtureStore
{
    public const long OneGiB = 1L << 30;

    // Seeds are part of the contract: changing one changes every downstream number.
    public const ulong NginxSeed = 0x4C4F47544F4F4C01UL;
    public const ulong SyslogSeed = 0x4C4F47544F4F4C02UL;
    public const ulong GenericSeed = 0x4C4F47544F4F4C03UL;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static string DataDir => Path.Combine(RepoPaths.Root, "bench", "data");

    public static FixtureMeta Nginx1G() => Ensure("nginx-1g.log", () => new NginxGen(NginxSeed), OneGiB);

    public static FixtureMeta Syslog1G() => Ensure("syslog-1g.log", () => new SyslogGen(SyslogSeed), OneGiB);

    public static FixtureMeta Generic1G() => Ensure("generic-1g.log", () => new GenericGen(GenericSeed), OneGiB);

    public static void EnsureAll()
    {
        Nginx1G();
        Syslog1G();
        Generic1G();
    }

    /// <summary>Generates a fixture of at least <paramref name="targetBytes"/>, stopping at the first line boundary past it.</summary>
    public static FixtureMeta Generate(string path, LineGenerator generator, long targetBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sw = Stopwatch.StartNew();
        string sha;
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
        using (var sink = new ByteSink(fs))
        {
            while (sink.Written < targetBytes)
            {
                generator.WriteLine(sink);
            }

            sha = sink.Finish();
        }

        var meta = new FixtureMeta(
            Path.GetFileName(path),
            path,
            generator.Name,
            generator.Version,
            generator.Seed,
            new FileInfo(path).Length,
            generator.Lines,
            generator.OddityLines,
            sha,
            DateTime.UtcNow,
            sw.Elapsed.TotalSeconds,
            generator.Counts is null ? null : new Dictionary<string, long>(generator.Counts, StringComparer.Ordinal));
        File.WriteAllText(MetaPath(path), JsonSerializer.Serialize(meta, JsonOptions));
        return meta;
    }

    public static FixtureMeta? TryReadMeta(string path)
    {
        var metaPath = MetaPath(path);
        if (!File.Exists(metaPath) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var meta = JsonSerializer.Deserialize<FixtureMeta>(File.ReadAllText(metaPath), JsonOptions);
            return meta is not null && meta.Bytes == new FileInfo(path).Length ? meta with { Path = path } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FixtureMeta Ensure(string fileName, Func<LineGenerator> factory, long targetBytes)
    {
        var path = Path.Combine(DataDir, fileName);
        var generator = factory();
        var existing = TryReadMeta(path);
        if (existing is not null && existing.GeneratorVersion == generator.Version && existing.Seed == generator.Seed && existing.Bytes >= targetBytes)
        {
            return existing;
        }

        Console.WriteLine($"fixture: generating {fileName} ({generator.Name} v{generator.Version}, seed 0x{generator.Seed:X}, {targetBytes >> 20} MiB) ...");
        var meta = Generate(path, generator, targetBytes);
        Console.WriteLine($"fixture: {fileName}: {meta.Bytes:N0} B, {meta.Lines:N0} lines, {meta.MeanLineBytes:F1} B/line, {meta.OddityLines * 100.0 / meta.Lines:F2} % oddities, sha256 {meta.Sha256[..16]}..., {meta.GenerationSeconds:F1} s");
        return meta;
    }

    private static string MetaPath(string path) => path + ".meta.json";
}
