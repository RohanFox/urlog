using System.Buffers;
using Urlog.Core.Audit;
using Urlog.Core.Detection;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Urlog.Core.Sources;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Tests;

/// <summary>
/// What the job runner does with the settings a person changes after intake: a parse reads the host's default
/// options under the file's own, a change that arrives while a file is parsing is followed by another parse, and
/// a file that is detected again gets its new record before any row is written.
/// </summary>
public sealed class JobRunnerSettingsTests
{
    private static CancellationToken Ct => RunnerHarness.Ct;

    [Fact]
    public async Task AParseReadsTheHostsDefaultOptionsUnderTheFilesOwn()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("incident 1");
        var (file, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(20_000, seed: 1).Bytes, "a.log", source: HookedSource.Id);
        await using (var db = h.Open())
        {
            await db.Hosts.Where(x => x.Id == file.HostId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceOptions, """{"hooked.format":"of the host","hooked.width":"80"}"""), Ct);
            await db.Files.Where(x => x.Id == file.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.OverrideOptions, """{"hooked.format":"of the file"}"""), Ct);
        }

        IReadOnlyDictionary<string, string>? seen = null;
        h.BeforeChunk = ctx => seen = ctx.Options;

        await h.Runner().RunAsync(Ct);

        Assert.Equal(JobState.Done, (await h.JobAsync(job.Id)).State);
        Assert.NotNull(seen);
        Assert.Equal(("of the file", "80", 2), (seen["hooked.format"], seen["hooked.width"], seen.Count));
    }

    [Fact]
    public async Task ASettingsChangeDuringAParseIsFollowedByAnotherParseOfTheFile()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("incident 1");
        var (file, job, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(50_000, seed: 4).Bytes, "a.log", source: HookedSource.Id);
        var changed = 0;
        h.BeforeChunk = _ =>
        {
            // A person changes the file's settings while its first parse is reading it.
            if (Interlocked.Exchange(ref changed, 1) == 0)
            {
                using var db = h.Open();
                db.Files.Where(x => x.Id == file.Id).ExecuteUpdate(s => s.SetProperty(x => x.SettingsVersion, 2));
            }
        };

        await h.Runner().RunAsync(Ct);

        await using var after = h.Open();
        var jobs = await after.Jobs.AsNoTracking().Where(j => j.FileId == file.Id).OrderBy(j => j.Id).ToListAsync(Ct);
        Assert.Equal([(job.Id, JobKind.Parse, JobState.Done, 1), (jobs[^1].Id, JobKind.Reparse, JobState.Done, 2)], jobs.Select(j => (j.Id, j.Kind, j.State, j.SettingsVersion)).ToArray());
        var stored = await h.FileAsync(file.Id);
        Assert.Equal((2, (int?)2, 2), (stored.ParseGeneration, stored.ParsedSettingsVersion, stored.SettingsVersion));
        Assert.Equal(2u, h.Events.Current((ulong)file.Id));
        Assert.Equal(new uint[] { 2 }, h.Events.Generations((ulong)file.Id));
    }

    [Fact]
    public async Task AFileDetectedAgainByABuildThatKnowsItsFormatIsParsedByThatSource()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("incident 1");
        var (bytes, lines) = TestData.NumberedLines(30_000, seed: 5);
        var (file, first, _) = await h.AddFileAsync(@case.Id, bytes, "a.log", source: FormatDetector.GenericSourceId);
        await using (var db = h.Open())
        {
            await db.Files.Where(x => x.Id == file.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DetectedKind, FileKind.Log).SetProperty(x => x.DetectionRegistry, "0:generic"), Ct);
        }

        await h.Runner().RunAsync(Ct);
        Assert.Equal(JobState.Done, (await h.JobAsync(first.Id)).State);
        var before = await h.FileAsync(file.Id);

        // This build knows the format.
        h.ExtraSources.Add(new SourceRegistration(Sniffing.Id, [], () => new Sniffing()));
        var rules = new SourceRules(h.Catalog());
        await using (var db = h.Open())
        {
            var preview = await FileSettingsOperations.RedetectAsync(db, h.Store.Audit, new AuditActor("alice"), rules, @case.Id, new FileSelection { StaleDetection = true }, false, null, h.Store.Clock, Ct);
            Assert.Equal((1, 1), (preview.Matched, preview.Changed));
            await FileSettingsOperations.RedetectAsync(db, h.Store.Audit, new AuditActor("alice"), rules, @case.Id, new FileSelection { StaleDetection = true }, true, preview.Digest, h.Store.Clock, Ct);
        }

        await h.Runner().RunAsync(Ct);

        var stored = await h.FileAsync(file.Id);
        Assert.Equal((FileKind.Log, Sniffing.Id, rules.RegistryKey, 2, FileState.Parsed), (stored.DetectedKind!.Value, stored.DetectedSource, stored.DetectionRegistry, stored.ParseGeneration, stored.State));
        Assert.NotEqual(before.Detection, stored.Detection);
        Assert.Equal(new uint[] { 2 }, h.Events.Generations((ulong)file.Id));
        Assert.Equal(lines, h.Events.Visible((ulong)file.Id));
        var rows = (await h.AuditAsync()).Where(a => a.TargetType == AuditTargets.File && a.TargetId == file.Id).Select(a => a.Action).ToArray();
        Assert.Equal([AuditActions.FileIntake, AuditActions.FileRedetect, AuditActions.FileReparse], rows);
        var redetect = (await h.AuditAsync()).Single(a => a.Action == AuditActions.FileRedetect);
        Assert.Contains("\"changed\":true", redetect.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"registry\":\"0:generic\",\"source\":\"generic\"", redetect.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"source\":\"{Sniffing.Id}\"", redetect.DetailsJson, StringComparison.Ordinal);
        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);
    }

    [Fact]
    public async Task AFileDetectedAgainWithTheSameResultKeepsItsRowsAndOneThatIsNoLogStaysKept()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("incident 1");
        var (text, firstParse, _) = await h.AddFileAsync(@case.Id, TestData.NumberedLines(10_000, seed: 6).Bytes, "a.log", source: FormatDetector.GenericSourceId);
        var binary = new byte[4096];
        new Random(7).NextBytes(binary);
        Array.Clear(binary, 0, 64);
        var (blob, blobJob, _) = await h.AddFileAsync(@case.Id, binary, "system.journal", source: null);
        await using (var db = h.Open())
        {
            await db.Files.Where(x => x.Id == text.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DetectedKind, FileKind.Log).SetProperty(x => x.DetectionRegistry, "0:generic"), Ct);
            // Recorded and kept by an older build that knew the format and had no parser for it.
            await JobOperations.CancelAsync(db, blobJob.Id, DateTime.UtcNow, Ct);
            await db.Files.Where(x => x.Id == blob.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DetectedKind, FileKind.Unparsed).SetProperty(x => x.State, FileState.Artifact).SetProperty(x => x.DetectionRegistry, "0:generic"), Ct);
        }

        await h.Runner().RunAsync(Ct);
        Assert.Equal(JobState.Done, (await h.JobAsync(firstParse.Id)).State);
        var rules = new SourceRules(h.Catalog());
        long[] queued;
        await using (var db = h.Open())
        {
            var preview = await FileSettingsOperations.RedetectAsync(db, h.Store.Audit, new AuditActor("alice"), rules, @case.Id, new FileSelection { StaleDetection = true }, false, null, h.Store.Clock, Ct);
            Assert.Equal(2, preview.Changed);
            await FileSettingsOperations.RedetectAsync(db, h.Store.Audit, new AuditActor("alice"), rules, @case.Id, new FileSelection { StaleDetection = true }, true, preview.Digest, h.Store.Clock, Ct);
            queued = await db.Jobs.AsNoTracking().Where(j => j.State == JobState.Queued).OrderBy(j => j.FileId).Select(j => j.Id).ToArrayAsync(Ct);
        }

        await h.Runner().RunAsync(Ct);

        var sameAgain = await h.JobAsync(queued[0]);
        Assert.Equal((JobState.Done, 0), (sameAgain.State, sameAgain.Generation));
        Assert.Contains("the rows stay as they are", sameAgain.Message, StringComparison.Ordinal);
        Assert.Equal(new uint[] { 1 }, h.Events.Generations((ulong)text.Id));
        Assert.Equal((1, rules.RegistryKey), ((await h.FileAsync(text.Id)).ParseGeneration, (await h.FileAsync(text.Id)).DetectionRegistry));

        var stillKept = await h.JobAsync(queued[1]);
        Assert.Equal(JobState.Done, stillKept.State);
        Assert.Contains("still not a log", stillKept.Message, StringComparison.Ordinal);
        var kept = await h.FileAsync(blob.Id);
        Assert.Equal((FileState.Artifact, rules.RegistryKey), (kept.State, kept.DetectionRegistry));
        Assert.NotEqual(FileKind.Log, kept.DetectedKind);
        Assert.Empty(h.Events.Generations((ulong)blob.Id));
        Assert.Equal(2, (await h.AuditAsync()).Count(a => a.Action == AuditActions.FileRedetect));
        // Neither file is a candidate any more.
        await using var again = h.Open();
        Assert.Equal(0, (await FileSettingsOperations.RedetectAsync(again, h.Store.Audit, new AuditActor("alice"), rules, @case.Id, new FileSelection { StaleDetection = true }, false, null, h.Store.Clock, Ct)).Matched);
    }

    /// <summary>The numbered-line source as a format a detector recognises with confidence.</summary>
    private sealed class Sniffing : ILogSource
    {
        public const string Id = "sniffing-lines";

        private readonly NumberedLineSource _inner = new();

        public SourceInfo Info { get; } = new(Id, [], "numbered lines that are recognised", 1);

        public Framing Framing => _inner.Framing;

        public FieldSchema Schema => _inner.Schema;

        public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => 0.95f;

        public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch) => _inner.Parse(framedChunk, ctx, batch);
    }
}
