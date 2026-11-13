using System.Globalization;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Cli;

/// <summary>What <see cref="FileDetection.Run"/> found: the detection record and the source that will parse the file.</summary>
/// <param name="Source">Null when the file is not a log and no <c>--source</c> named a parser for it.</param>
/// <param name="Overridden">True when <c>--source</c> chose the parser; the record still says what the detector found.</param>
internal sealed record Detected(DetectionResult Record, ILogSource? Source, bool Overridden)
{
    public CompressionKind Compression => Record.Compression;

    public InputEncoding Encoding => Record.Encoding;
}

/// <summary>
/// Detection for the commands that take a file from the command line (<c>ingest</c>, <c>enqueue</c>): the core
/// detector over the first 64 KB and the last 4 KB, the printed <c>detect</c>, <c>source</c> and <c>note</c> lines, and
/// what a command line can and cannot take (a packet capture and an archive are refused here). Called after the
/// file's SHA-256 exists.
/// </summary>
internal static class FileDetection
{
    public static SourceCatalog Catalog()
    {
        var catalog = new SourceCatalog();
        catalog.Add(Urlog.Sources.LogSourceRegistry.Registrations);
        return catalog;
    }

    public static Detected Run(string path, string? wanted, SourceCatalog catalog, IReadOnlyDictionary<string, string>? options)
    {
        if (wanted is not null && !catalog.TryGet(wanted, out _))
        {
            throw new CliException($"no log source called '{wanted}'; known: {string.Join(", ", catalog.All.Select(r => r.Id))}");
        }

        var record = DetectionSample.DetectFile(path, Path.GetFileName(path), FormatDetector.Instantiate(catalog), options);
        if (!record.Scores.IsEmpty)
        {
            Console.WriteLine($"detect    {string.Join(", ", record.Scores.OrderByDescending(s => s.Score).Select(Describe))}");
        }

        var wrapping = string.Create(CultureInfo.InvariantCulture, $"compression {Names.Of(record.Compression)}, encoding {Names.Of(record.Encoding)}");
        switch (record.Kind)
        {
            case FileKind.Rejected:
                throw new CliException($"{record.Format}: {record.Message}");
            case FileKind.Container:
                throw new CliException($"{string.Join(" > ", record.Containers)}: an archive is expanded by intake, not by this command; use 'urlog enqueue', which records each member");
            case FileKind.Log when wanted is null:
                var layout = record.Layout is null ? (record.LayoutKnown ? string.Empty : ", layout unknown") : $", layout {record.Layout}";
                Console.WriteLine($"source    {record.SourceId} ({Names.Of(record.Band)}{layout}, {wrapping})");
                break;
            case FileKind.Log:
                break;
            default:
                Console.WriteLine($"kind      {Names.Of(record.Kind)}: {record.Format} (by {Names.Of(record.Basis)}); not a log, not parsed");
                break;
        }

        if (record.Message is not null)
        {
            Console.WriteLine($"note      {record.Message}");
        }

        if (wanted is not null)
        {
            var named = catalog.Create(wanted);
            Console.WriteLine($"source    {named.Info.Id} (--source, {wrapping})");
            return new Detected(record, named, Overridden: true);
        }

        return new Detected(record, record.Kind == FileKind.Log ? catalog.Create(record.SourceId!) : null, Overridden: false);
    }

    /// <summary>SHA-256 of the bytes as they are on disk, streamed (evidence rule 1): 64 lowercase hex characters.</summary>
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var raw = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = await raw.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, n);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Describe(SourceScore score)
    {
        var layout = score.Layout is null ? string.Empty : $" ({score.Layout})";
        return string.Create(CultureInfo.InvariantCulture, $"{score.SourceId} {score.Score:F2}{layout}");
    }
}
