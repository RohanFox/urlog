using System.Diagnostics;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;
using Urlog.Core.Sources;

namespace Urlog.Sources.Tests.Detection;

/// <summary>
/// The detector over <c>fixtures/detection/&lt;group&gt;/</c> with the real sources: every file must come out as its
/// group's <c>expected.tsv</c> says (kind, source, band, layout, encoding, container chain, format, flags). The
/// groups are found by directory, so a new source adds a directory and is covered.
/// </summary>
public sealed class DetectorCorpusTests
{
    private static readonly string CorpusRoot = Path.Combine(Fixtures.Root, "fixtures", "detection");

    private static readonly IReadOnlyList<ILogSource> Sources = FormatDetector.Instantiate(Catalog());

    public static TheoryData<string, string> Samples()
    {
        var data = new TheoryData<string, string>();
        foreach (var (group, row) in Rows())
        {
            data.Add(group, row.File);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void TheFileIsDetectedAsItsTableSays(string group, string file)
    {
        var expected = Rows().Single(r => r.Group == group && r.Row.File == file).Row;
        var result = DetectionSample.DetectFile(Path.Combine(CorpusRoot, group, file), file, Sources);
        var actual = new Expectation(
            file,
            Names.Of(result.Kind),
            result.SourceId ?? "-",
            Names.Of(result.Band),
            result.Layout ?? (result.Kind == FileKind.Log && !result.LayoutKnown ? "unknown" : "-"),
            Names.Of(result.Encoding),
            result.Containers.IsEmpty ? "-" : string.Join('>', result.Containers),
            result.Format ?? "-",
            result.Flags.IsEmpty ? "-" : string.Join(',', result.Flags.Order(StringComparer.Ordinal)));
        Assert.True(expected == actual, $"{group}/{file}\nexpected: {expected}\nactual:   {actual}\nscores:   {string.Join(", ", result.Scores.Select(s => $"{s.SourceId} {s.Score:F3}"))}\nmessage:  {result.Message}");

        switch (result.Band)
        {
            case ConfidenceBand.Confident:
                Assert.InRange(result.Confidence, FormatDetector.ConfidentAt, 1f);
                break;
            case ConfidenceBand.Probable:
                Assert.InRange(result.Confidence, FormatDetector.ProbableAt, 1f);
                break;
            case ConfidenceBand.Fallback:
                Assert.True(result.Confidence < FormatDetector.ProbableAt, $"fallback with a score of {result.Confidence}");
                Assert.Equal(FormatDetector.GenericSourceId, result.SourceId);
                break;
            default:
                Assert.NotEqual(FileKind.Log, result.Kind);
                break;
        }

        // The record says which detector and which sources produced it, and survives its own serialisation.
        Assert.Equal(FormatDetector.Version, result.DetectorVersion);
        Assert.Equal(Sources.Select(s => s.Info.Id), result.RegisteredSources);
        using var json = System.Text.Json.JsonDocument.Parse(result.ToJson());
        Assert.Equal(Names.Of(result.Kind), json.RootElement.GetProperty("kind").GetString());
        Assert.Equal(Sources.Count, json.RootElement.GetProperty("registered_sources").GetArrayLength());

        if (result.Kind is FileKind.Unparsed or FileKind.Rejected || result.Flags.Length > 0)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Message), "a file the analyst must act on carries a message");
        }
    }

    [Fact]
    public void EveryFileInTheCorpusIsInATableAndEveryRowHasItsFile()
    {
        var groups = Directory.GetDirectories(CorpusRoot);
        Assert.True(groups.Length >= 8, "nginx, syslog, generic, adversarial, artifact, journal, rejected, container");
        foreach (var dir in groups)
        {
            var group = Path.GetFileName(dir);
            var onDisk = Directory.GetFiles(dir).Select(Path.GetFileName).Where(n => n is not ("expected.tsv" or "README.md")).Order(StringComparer.Ordinal).ToArray();
            var listed = Rows().Where(r => r.Group == group).Select(r => r.Row.File).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(onDisk, listed);
        }
    }

    [Fact]
    public void EveryRegisteredSourceHasAtLeastTwentySamples()
    {
        foreach (var source in Sources)
        {
            var dir = Path.Combine(CorpusRoot, source.Info.Id);
            Assert.True(Directory.Exists(dir), $"fixtures/detection/{source.Info.Id}/ is missing: a source brings its detection samples");
            var own = Rows().Count(r => r.Group == source.Info.Id && r.Row.Source == source.Info.Id);
            Assert.True(own >= 20, $"fixtures/detection/{source.Info.Id}/ has {own} samples of its own format, 20 are required");
        }
    }

    [Fact]
    public void ArchivesTheIntakeDoesNotExpandSayWhatToDo()
    {
        foreach (var file in new[] { "bundle.rar", "bundle.7z", "bundle.cab" })
        {
            var result = DetectionSample.DetectFile(Path.Combine(CorpusRoot, "artifact", file), file, Sources);
            Assert.Equal(FileKind.Artifact, result.Kind);
            Assert.Contains("extract and upload the contents", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ALogWithBinaryBytesRecordsTheirShare()
    {
        var result = DetectionSample.DetectFile(Path.Combine(CorpusRoot, "adversarial", "syslog-nul-block-in-the-middle.log"), "syslog-nul-block-in-the-middle.log", Sources);
        Assert.Equal("syslog", result.SourceId);
        Assert.InRange(result.BinaryShare, 0.3, 0.6); // 4,096 NUL bytes in a file of about 10 KB
        Assert.Contains("binary bytes", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogWithAnArtifactNameSaysWhichNameAndWhichSource()
    {
        var result = DetectionSample.DetectFile(Path.Combine(CorpusRoot, "adversarial", "access-log-named.php"), "access-log-named.php", Sources);
        Assert.Equal(FileKind.Log, result.Kind);
        Assert.Equal("nginx", result.SourceId);
        Assert.Equal("php", result.MismatchedExtension);
        Assert.Contains(".php", result.Message, StringComparison.Ordinal);
        Assert.Contains("nginx", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWholeCorpusIsDetectedInUnderTwoSeconds()
    {
        var rows = Rows().ToList();
        foreach (var (group, row) in rows.Take(5))
        {
            DetectionSample.DetectFile(Path.Combine(CorpusRoot, group, row.File), row.File, Sources); // JIT
        }

        var watch = Stopwatch.StartNew();
        foreach (var (group, row) in rows)
        {
            DetectionSample.DetectFile(Path.Combine(CorpusRoot, group, row.File), row.File, Sources);
        }

        watch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"{rows.Count} files in {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{rows.Count} files took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void NoFileTakesMoreThanFiftyMilliseconds()
    {
        // The budget of the design record: detection runs on every upload and every dropped file. Samples are read
        // first so the time is the detector's; the slowest of three passes over each file must fit, and the pass
        // before them pays for the JIT.
        var samples = Rows().Select(r => (r.Group, r.Row.File, Bytes: File.ReadAllBytes(Path.Combine(CorpusRoot, r.Group, r.Row.File)))).ToList();
        foreach (var (_, file, bytes) in samples)
        {
            DetectionSample.DetectBytes(bytes, file, Sources);
        }

        var slowest = TimeSpan.Zero;
        var slowestFile = string.Empty;
        foreach (var (group, file, bytes) in samples)
        {
            var best = TimeSpan.MaxValue;
            for (var pass = 0; pass < 3; pass++)
            {
                var watch = Stopwatch.StartNew();
                DetectionSample.DetectBytes(bytes, file, Sources);
                watch.Stop();
                best = watch.Elapsed < best ? watch.Elapsed : best;
            }

            if (best > slowest)
            {
                (slowest, slowestFile) = (best, group + "/" + file);
            }
        }

        Assert.True(slowest < TimeSpan.FromMilliseconds(50), $"{slowestFile} took {slowest.TotalMilliseconds:F2} ms");
    }

    private static SourceCatalog Catalog()
    {
        var catalog = new SourceCatalog();
        catalog.Add(LogSourceRegistry.Registrations);
        return catalog;
    }

    private static IEnumerable<(string Group, Expectation Row)> Rows()
    {
        foreach (var dir in Directory.GetDirectories(CorpusRoot).Order(StringComparer.Ordinal))
        {
            var table = Path.Combine(dir, "expected.tsv");
            Assert.True(File.Exists(table), $"{dir} has no expected.tsv");
            foreach (var line in File.ReadLines(table).Skip(1))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var c = line.Split('\t');
                Assert.True(c.Length == 9, $"{table}: nine tab-separated columns, got {c.Length} in '{line}'");
                yield return (Path.GetFileName(dir), new Expectation(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8]));
            }
        }
    }

    private sealed record Expectation(string File, string Kind, string Source, string Band, string Layout, string Encoding, string Containers, string Format, string Flags);
}
