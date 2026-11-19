using System.Globalization;
using System.Security.Cryptography;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Audit;
using Urlog.Core.Jobs;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Integration.Tests;

/// <summary>
/// The changes a person makes to files and hosts, through the job runner into the event store: an override
/// replaces the visible parse of a file with the next generation and leaves the detection record alone, a host's
/// default log format parses its files without an override on each, a host merge leaves the rows of both names
/// findable, and the dashboard's figures are the file records' sums and the store's own.
/// </summary>
public sealed class CaseSettingsStoreTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static readonly AuditActor Alice = new("alice");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobRunnerOptions Options => new()
    {
        Pipeline = new PipelineOptions { ChunkBytes = 1 << 20, MaxRecordBytes = 1 << 20, BatchRows = 5_000, Parallelism = 2, InFlightChunks = 4, InFlightBatches = 2, Writers = 2 },
        ExitWhenIdle = true,
        IdlePoll = TimeSpan.FromMilliseconds(20),
        ProgressInterval = TimeSpan.FromMilliseconds(50),
        WorkerName = "test",
        Watermark = new WatermarkOptions { MinFreeBytes = 0, MinFreePercent = 0 }, // the test box's disks are not under test here
    };

    [Fact]
    public async Task AnOverrideWithALogFormatReplacesTheFallbackParseWithGenerationTwo()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("one");
        var format = (await File.ReadAllTextAsync(Fixture("nginx", "custom", "geoip-upstream.format"), Ct)).Trim();
        var expected = await File.ReadAllLinesAsync(Fixture("nginx", "custom", "geoip-upstream.expected.jsonl"), Ct);
        var (path, sha256) = await CopyAsync(h, Fixture("nginx", "custom", "geoip-upstream.log"), "access.log");
        // Taken in without its format: no built-in layout fits, and the file went to the fallback source.
        var (file, first) = await h.AddFileAsync(@case.Id, path, sha256, source: "generic");
        const string Detection = """{"detector_version":1,"registered_sources":["generic","nginx","syslog"],"kind":"log","source":"generic","layout":null}""";
        await using (var db = h.Store.CreateDbContext())
        {
            await db.Files.Where(f => f.Id == file.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.DetectedKind, Urlog.Core.Detection.FileKind.Log).SetProperty(f => f.Detection, Detection).SetProperty(f => f.DetectedConfidence, 0.4f), Ct);
        }

        await Runner(h).RunAsync(Ct);
        Assert.Equal(JobState.Done, (await h.JobAsync(first.Id)).State);
        var fallback = await h.FileAsync(file.Id);
        Assert.Equal((expected.Length, 1L, 1L), await h.VisibleAsync(@case.Id));
        Assert.Equal((0L, 0L), (fallback.RejectCount, await StatusRowsAsync(h, @case.Id)));

        FileChange change;
        await using (var db = h.Store.CreateDbContext())
        {
            change = await FileSettingsOperations.OverrideAsync(db, h.Store.Audit, Alice, Rules(), @case.Id, file.Id, 1, "nginx", new Dictionary<string, string> { ["nginx.log_format"] = format }, h.Store.Clock, Ct);
        }

        // Between the request and the commit a reader still sees the fallback parse, whole.
        Assert.Equal((JobKind.Reparse, JobState.Queued), (change.Job!.Kind, change.Job.State));
        Assert.Equal((expected.Length, 1L, 1L), await h.VisibleAsync(@case.Id));

        await Runner(h).RunAsync(Ct);

        // Generation 2 alone, with the count the layout gives for this fixture, rejects included as rows.
        Assert.Equal((expected.Length, 1L, 2L), await h.VisibleAsync(@case.Id));
        var rejects = expected.Count(l => l.Contains("reject_reason", StringComparison.Ordinal));
        var parsed = await h.FileAsync(file.Id);
        Assert.Equal((2, (long)expected.Length, (long)rejects, (int?)2), (parsed.ParseGeneration, parsed.RowCount, parsed.RejectCount, parsed.ParsedSettingsVersion));
        Assert.Equal(expected.Count(l => l.Contains("\"http_status\": 200,", StringComparison.Ordinal)), await StatusRowsAsync(h, @case.Id));
        Assert.True(await StatusRowsAsync(h, @case.Id) > 0);
        await WorkerHarness.EventuallyAsync(async () => (await h.StoredGenerationsAsync(file.Id)).Keys.SequenceEqual([2u]), "generation 1 deleted after the commit of 2", seconds: 60, pollMilliseconds: 250);

        // The original guess is still on the record, beside the override.
        Assert.Equal((Urlog.Core.Detection.FileKind.Log, "generic", Detection, 0.4f), (parsed.DetectedKind!.Value, parsed.DetectedSource, parsed.Detection, parsed.DetectedConfidence!.Value));
        Assert.Equal("nginx", parsed.OverrideSource);
        await using var read = h.Store.CreateDbContext();
        var trail = await read.Audit.AsNoTracking().Where(a => a.TargetType == AuditTargets.File && a.TargetId == file.Id).OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync(Ct);
        Assert.Equal([AuditActions.FileIntake, AuditActions.FileOverride, AuditActions.FileReparse], trail);
        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);
        output.WriteLine($"{expected.Length} rows of generation 2 visible, {rejects} of them rejects");
    }

    [Fact]
    public async Task AHostsDefaultLogFormatParsesItsFilesWithoutAnOverrideOnEach()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("one");
        var format = (await File.ReadAllTextAsync(Fixture("nginx", "custom", "geoip-upstream.format"), Ct)).Trim();
        var lines = await File.ReadAllLinesAsync(Fixture("nginx", "custom", "geoip-upstream.log"), Ct);
        // Two rotations of the same host's log: the fixture, and the fixture without its last line.
        var one = await CopyAsync(h, Fixture("nginx", "custom", "geoip-upstream.log"), "access.log");
        var twoPath = Path.Combine(h.Folder, "access.log.1");
        await File.WriteAllTextAsync(twoPath, string.Join('\n', lines[..^1]) + "\n", Ct);
        var two = (twoPath, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(twoPath, Ct))));
        var (a, _) = await h.AddFileAsync(@case.Id, one.Path, one.Sha256);
        var (b, _) = await h.AddFileAsync(@case.Id, two.twoPath, two.Item2);

        // Parsed as nginx without the format: the built-in layouts do not fit these lines.
        await Runner(h).RunAsync(Ct);
        var without = (await h.FileAsync(a.Id)).RejectCount + (await h.FileAsync(b.Id)).RejectCount;

        HostChange preview;
        HostChange made;
        await using (var db = h.Store.CreateDbContext())
        {
            var edit = new HostEdit(default, default, Change.To<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["nginx.log_format"] = format }));
            preview = await HostOperations.UpdateAsync(db, h.Store.Audit, Alice, Rules(), @case.Id, a.HostId!.Value, 1, edit, confirm: false, h.Store.Clock, Ct);
            made = await HostOperations.UpdateAsync(db, h.Store.Audit, Alice, Rules(), @case.Id, a.HostId.Value, 1, edit, confirm: true, h.Store.Clock, Ct);
        }

        Assert.Equal((false, 2, a.Size + b.Size), (preview.Applied, preview.Cost.Files, preview.Cost.Bytes));
        Assert.Equal((true, 2), (made.Applied, made.Jobs));
        await Runner(h).RunAsync(Ct);

        var expected = await File.ReadAllLinesAsync(Fixture("nginx", "custom", "geoip-upstream.expected.jsonl"), Ct);
        var rejects = expected.Count(l => l.Contains("reject_reason", StringComparison.Ordinal));
        var first = await h.FileAsync(a.Id);
        var second = await h.FileAsync(b.Id);
        Assert.Equal((2, (long)expected.Length, (long)rejects, (string?)null), (first.ParseGeneration, first.RowCount, first.RejectCount, first.OverrideOptions));
        Assert.Equal((2, (long)expected.Length - 1, (string?)null), (second.ParseGeneration, second.RowCount, second.OverrideOptions));
        Assert.True(first.RejectCount + second.RejectCount < without, $"{first.RejectCount + second.RejectCount} rejects with the host's format, {without} without");
        Assert.Equal(((expected.Length * 2) - 1, 1L, 2L), await h.VisibleAsync(@case.Id));
        output.WriteLine($"rejects without the format: {without}; with the host's default: {first.RejectCount + second.RejectCount}");
    }

    [Fact]
    public async Task AfterAHostMergeTheRowsOfBothNamesAreFoundAndTheFilesPointAtTheSurvivor()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("one");
        var one = await CopyAsync(h, Fixture("nginx", "sample.log"), "a.log");
        var twoPath = Path.Combine(h.Folder, "b.log");
        await File.WriteAllTextAsync(twoPath, string.Join('\n', (await File.ReadAllLinesAsync(Fixture("nginx", "sample.log"), Ct))[..10]) + "\n", Ct);
        var (a, _) = await h.AddFileAsync(@case.Id, one.Path, one.Sha256);
        var (b, _) = await h.AddFileAsync(@case.Id, twoPath, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(twoPath, Ct))));
        Host old;
        await using (var db = h.Store.CreateDbContext())
        {
            // The second file belongs to the same machine under the name another collector gave it.
            old = await HostOperations.CreateAsync(db, h.Store.Audit, Alice, Rules(), @case.Id, "web-01.internal", null, null, h.Store.Clock, Ct);
            await db.Files.Where(f => f.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.HostId, old.Id), Ct);
        }

        await Runner(h).RunAsync(Ct);
        var before = await HostRowsAsync(h, @case.Id);
        Assert.Equal(["web-01", "web-01.internal"], before.Keys.Order().ToArray());

        await using (var db = h.Store.CreateDbContext())
        {
            var (into, moved) = await HostOperations.MergeAsync(db, h.Store.Audit, Alice, @case.Id, old.Id, 1, a.HostId!.Value, h.Store.Clock, Ct);
            Assert.Equal((a.HostId.Value, 1), (into.Id, moved));
            Assert.Equal(0, await db.Jobs.CountAsync(j => j.State == JobState.Queued, Ct));
        }

        // Nothing is parsed again: the host column is per event, and each row keeps the name it was written with.
        Assert.Equal(before, await HostRowsAsync(h, @case.Id));
        Assert.Equal((a.HostId, a.HostId), ((await h.FileAsync(a.Id)).HostId, (await h.FileAsync(b.Id)).HostId));
        Assert.Equal(1, (await h.FileAsync(b.Id)).ParseGeneration);
    }

    [Fact]
    public async Task TheDashboardCountsEqualTheFileRecordsAndDiskUseIsTheStoresOwn()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var @case = await h.AddCaseAsync("one");
        var one = await CopyAsync(h, Fixture("nginx", "sample.log"), "a.log");
        var two = await CopyAsync(h, Fixture("nginx", "custom", "geoip-upstream.log"), "b.log");
        var (a, _) = await h.AddFileAsync(@case.Id, one.Path, one.Sha256);
        var (b, _) = await h.AddFileAsync(@case.Id, two.Path, two.Sha256);
        await Runner(h).RunAsync(Ct);

        await using var db = h.Store.CreateDbContext();
        var overview = await CaseOverview.ReadAsync(db, @case.Id, Rules().RegistryKey, Ct);
        var files = await db.Files.AsNoTracking().Where(f => f.CaseId == @case.Id).ToListAsync(Ct);
        Assert.Equal((2, files.Sum(f => f.RowCount), files.Sum(f => f.RejectCount)), (overview.Files, overview.FileRows, overview.FileRejects));
        Assert.Equal(overview.FileRows, (await h.VisibleAsync(@case.Id)).Rows);
        Assert.Equal((a.Size + b.Size, a.Size + b.Size), (overview.ParseBytesTotal, overview.ParseBytesDone));
        var host = Assert.Single(overview.Hosts);
        Assert.Equal((a.HostId, 2, overview.FileRows), (host.HostId, host.Files, host.Rows));

        var usage = (await CaseAccounting.MeasureAsync(db, h.EventStore(), new LifecyclePaths(null, null, string.Empty), @case.Id, Ct))!;
        await using var admin = h.Admin();
        var c = @case.Id.ToString(CultureInfo.InvariantCulture);
        var onDisk = Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT sum(bytes_on_disk) FROM system.parts WHERE database = currentDatabase() AND table = 'events' AND active AND partition LIKE '({c},%'", cancellationToken: Ct), CultureInfo.InvariantCulture);
        output.WriteLine($"disk use of the case: accounting {usage.StoreBytes:N0} bytes and {usage.StoreRows:N0} rows, system.parts {onDisk:N0} bytes");
        Assert.True(onDisk > 0);
        Assert.InRange(usage.StoreBytes, onDisk * 0.99, onDisk * 1.01);
        Assert.Equal(await h.StoredRowsAsync(@case.Id), usage.StoreRows);
        Assert.Equal(a.Size + b.Size, usage.RecordedBytes);
    }

    private static string Fixture(params string[] parts) => Path.Combine([RepoRoot(), "fixtures", .. parts]);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("urlog.sln was not found above the test binaries");
    }

    private static SourceCatalog Catalog()
    {
        var catalog = new SourceCatalog();
        catalog.Add(Urlog.Sources.LogSourceRegistry.Registrations);
        return catalog;
    }

    private static SourceRules Rules() => new(Catalog());

    private static JobRunner Runner(WorkerHarness h) => new(h.Store, h.EventStore(Options.Pipeline.Writers), Catalog(), Options);

    /// <summary>A copy of a fixture under the harness's folder, with LF line ends whatever the checkout made of them, and its hash.</summary>
    private static async Task<(string Path, string Sha256)> CopyAsync(WorkerHarness h, string fixture, string name)
    {
        var text = (await File.ReadAllTextAsync(fixture, Ct)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var path = Path.Combine(h.Folder, name);
        await File.WriteAllTextAsync(path, text, Ct);
        return (path, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, Ct))));
    }

    /// <summary>Rows of the case with HTTP status 200, as a member reads them.</summary>
    private async Task<long> StatusRowsAsync(WorkerHarness h, long caseId)
    {
        _ = h;
        await using var query = new ClickHouseTcpClient(ch.NativeConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        var c = caseId.ToString(CultureInfo.InvariantCulture);
        return Convert.ToInt64(await query.ExecuteScalarAsync($"SELECT count() FROM events_v WHERE case_id = {c} AND http_status = 200 SETTINGS SQL_allowed_cases = '[{c}]'", cancellationToken: Ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Rows of the case per value of the host column, as a member reads them.</summary>
    private async Task<Dictionary<string, long>> HostRowsAsync(WorkerHarness h, long caseId)
    {
        _ = h;
        await using var query = new ClickHouseTcpClient(ch.NativeConnectionString(ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
        var c = caseId.ToString(CultureInfo.InvariantCulture);
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var row in query.QueryAsync($"SELECT toString(host), count() FROM events_v WHERE case_id = {c} GROUP BY host SETTINGS SQL_allowed_cases = '[{c}]'", cancellationToken: Ct))
        {
            rows[(string)row[0]!] = Convert.ToInt64(row[1], CultureInfo.InvariantCulture);
        }

        return rows;
    }
}
