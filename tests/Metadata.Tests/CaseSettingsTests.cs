using System.Buffers;
using System.Collections.Immutable;
using Urlog.Core.Audit;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;
using Urlog.Core.Sources;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The changes a person makes to hosts, files and jobs after intake: each is one transaction with its audit row,
/// names the version it was made against, is refused outside an active case, and queues the parse it calls for
/// without ever putting a second job beside a running one.
/// </summary>
public sealed class CaseSettingsTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // With invariant globalization Windows resolves its own zone ids only.
    private static readonly string Berlin = OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin";
    private static readonly string Tokyo = OperatingSystem.IsWindows() ? "Tokyo Standard Time" : "Asia/Tokyo";

    private static JobOutcome Whole => new(Urlog.Core.Pipeline.InputEnd.Complete(100, 100));

    [Fact]
    public async Task AHostChangeOnAFileQueuesItsReparseAndIsOnTheTrail()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var file = await scene.ParsedAsync(db, "a", "access.log", source: "nginx");
        var before = await db.Audit.CountAsync(Ct);

        var change = await FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, new FileEdit(Change.To<string?>("Web-02"), false, default, default), scene.Clock, Ct);

        Assert.True(change.Changed);
        Assert.Equal((JobKind.Reparse, JobState.Queued), (change.Job!.Kind, change.Job.State));
        Assert.Equal(new ReparseCost(1, file.Size), change.Cost);
        var stored = await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct);
        var host = await db.Hosts.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(("web-02", (long?)host.Id, 2, false), (host.Name, stored.HostId, stored.SettingsVersion, stored.HostInferred));
        var rows = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Skip(before).ToListAsync(Ct);
        Assert.Equal([AuditActions.HostCreate, AuditActions.FileUpdate], rows.Select(r => r.Action).ToArray());
        Assert.Equal((AuditTargets.Case, (long?)scene.CaseId), (rows[0].TargetType, rows[0].TargetId));
        Assert.Equal((AuditTargets.File, (long?)file.Id), (rows[1].TargetType, rows[1].TargetId));
        Assert.Contains("\"host\":{\"from\":null,\"to\":\"web-02\"}", rows[1].DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"job_id\":{change.Job.Id},", rows[1].DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"reparse_bytes\":{file.Size},\"reparse_files\":1", rows[1].DetailsJson, StringComparison.Ordinal);

        // The same value again changes nothing and writes nothing.
        var again = await FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 2, new FileEdit(Change.To<string?>("web-02"), false, default, default), scene.Clock, Ct);
        Assert.False(again.Changed);
        Assert.Equal(before + 2, await db.Audit.CountAsync(Ct));
        Assert.True((await AuditVerifier.VerifyAsync(scene.Path, Ct)).Ok);
    }

    [Fact]
    public async Task ZoneAndYearReparseOnlyAFileWhoseSourceReadsThem()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var nginx = await scene.ParsedAsync(db, "a", "access.log", source: "nginx");
        var syslog = await scene.ParsedAsync(db, "b", "messages", source: "syslog");

        var offsetCarrying = await FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, nginx.Id, 1, new FileEdit(default, false, Change.To<string?>(Berlin), default), scene.Clock, Ct);
        Assert.True(offsetCarrying.Changed);
        Assert.Null(offsetCarrying.Job);
        Assert.Equal(2, (await db.Files.AsNoTracking().SingleAsync(f => f.Id == nginx.Id, Ct)).SettingsVersion);

        var naive = await FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, syslog.Id, 1, new FileEdit(default, false, Change.To<string?>(Berlin), Change.To<int?>(2025)), scene.Clock, Ct);
        Assert.NotNull(naive.Job);
        var stored = await db.Files.AsNoTracking().SingleAsync(f => f.Id == syslog.Id, Ct);
        Assert.Equal((Berlin, (int?)2025, true, false), (stored.Timezone, stored.YearHint, stored.YearGiven, stored.YearAssumed));

        await Assert.ThrowsAsync<ArgumentException>(() => FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, syslog.Id, 2, new FileEdit(default, false, Change.To<string?>("Mars/Olympus"), default), scene.Clock, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, syslog.Id, 2, new FileEdit(default, false, default, Change.To<int?>(1492)), scene.Clock, Ct));
    }

    [Fact]
    public async Task AWriteThatNamesAnOlderVersionIsRefusedAndChangesNothing()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var file = await scene.ParsedAsync(db, "a", "access.log", source: "nginx");
        await FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, new FileEdit(Change.To<string?>("web-01"), false, default, default), scene.Clock, Ct);
        var dump = await scene.DumpAsync();

        var stale = await Assert.ThrowsAsync<StaleWriteException>(() => FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, new FileEdit(Change.To<string?>("web-09"), false, default, default), scene.Clock, Ct));
        Assert.Equal("2", stale.Current);
        await Assert.ThrowsAsync<StaleWriteException>(() => FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, "syslog", null, scene.Clock, Ct));
        var host = await db.Hosts.AsNoTracking().SingleAsync(Ct);
        await Assert.ThrowsAsync<StaleWriteException>(() => HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, host.Id, 7, new HostEdit(Change.To("web-x"), default, default), true, scene.Clock, Ct));
        await Assert.ThrowsAsync<RecordNotFoundException>(() => FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, 4242, 1, new FileEdit(default, true, default, default), scene.Clock, Ct));
        Assert.Equal(dump, await scene.DumpAsync());
    }

    [Fact]
    public async Task AnOverrideLeavesTheDetectionRecordAsItIsAndIsOnTheTrail()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var file = await scene.ParsedAsync(db, "a", "access.log", source: "generic", detection: """{"detector_version":1,"registered_sources":["generic","nginx","syslog"],"kind":"log","source":"generic"}""");
        var options = new Dictionary<string, string> { ["nginx.log_format"] = "$remote_addr [$time_local] \"$request\"" };

        var change = await FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, "NGINX", options, scene.Clock, Ct);

        var stored = await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct);
        Assert.Equal(("nginx", 2), (stored.OverrideSource, stored.SettingsVersion));
        Assert.Equal(options, SourceOptionSet.Parse(stored.OverrideOptions));
        Assert.Equal((file.DetectedKind, file.DetectedSource, file.DetectedConfidence, file.DetectedLayout, file.Detection), (stored.DetectedKind, stored.DetectedSource, stored.DetectedConfidence, stored.DetectedLayout, stored.Detection));
        Assert.Equal(JobKind.Reparse, change.Job!.Kind);
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal(AuditActions.FileOverride, row.Action);
        Assert.Contains("\"from\":{\"options\":null,\"source\":null}", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"nginx\"", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"detected_source\":\"generic\"", row.DetailsJson, StringComparison.Ordinal);

        // Unchanged: no row. Cleared: a row, and the file is parsed by what the detector chose again.
        var count = await db.Audit.CountAsync(Ct);
        Assert.False((await FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 2, "nginx", options, scene.Clock, Ct)).Changed);
        Assert.Equal(count, await db.Audit.CountAsync(Ct));
        var cleared = await FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 2, null, null, scene.Clock, Ct);
        Assert.True(cleared.Changed);
        Assert.Null((await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct)).OverrideSource);

        await Assert.ThrowsAsync<ArgumentException>(() => FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 3, "dovecot", null, scene.Clock, Ct));
        var bad = await Assert.ThrowsAsync<ArgumentException>(() => FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 3, "nginx", new Dictionary<string, string> { ["nginx.log_format"] = "bad" }, scene.Clock, Ct));
        Assert.Contains("nginx.log_format", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOverrideMakesAnArtifactALogAndAFileThatIsNeverParsedTakesNone()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var artifact = await scene.AddAsync(db, "pdf-like", "notes.txt", f =>
        {
            f.DetectedKind = FileKind.Artifact;
            f.State = FileState.Artifact;
        });
        var quarantined = await scene.AddAsync(db, "q", "q.log", f => f.State = FileState.Quarantined);
        var container = await scene.AddAsync(db, "zip", "bundle.zip", f => f.DetectedKind = FileKind.Container);

        var change = await FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, artifact.Id, 1, "generic", null, scene.Clock, Ct);
        Assert.Equal(JobKind.Parse, change.Job!.Kind);
        var stored = await db.Files.AsNoTracking().SingleAsync(f => f.Id == artifact.Id, Ct);
        Assert.Equal((FileState.Received, FileKind.Artifact), (stored.State, stored.DetectedKind!.Value));

        var clear = await Assert.ThrowsAsync<SettingsRefusedException>(() => FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, artifact.Id, 2, null, null, scene.Clock, Ct));
        Assert.Contains("name one", clear.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SettingsRefusedException>(() => FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, quarantined.Id, 1, "nginx", null, scene.Clock, Ct));
        await Assert.ThrowsAsync<SettingsRefusedException>(() => FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, container.Id, 1, "nginx", null, scene.Clock, Ct));
    }

    [Fact]
    public async Task AChangeWhileTheFileIsParsingIsFollowedByAnotherParseWhenThatJobEnds()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var file = await scene.AddAsync(db, "a", "access.log", f => f.DetectedSource = "nginx", queue: true);
        var running = (await JobOperations.ClaimAsync(db, "w:1", Now, Ct))!.Job;
        Assert.Equal(1, running.SettingsVersion);

        var change = await FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, "syslog", null, scene.Clock, Ct);
        Assert.Null(change.Job);
        Assert.Equal(new ReparseCost(1, file.Size), change.Cost);
        Assert.Equal(1, await db.Jobs.CountAsync(Ct));

        await JobOperations.CompleteAsync(db, scene.Audit, running.Id, "w:1", new JobCounts(1, 1, 10, 0, 100), Whole, Now, Ct);
        var jobs = await db.Jobs.AsNoTracking().OrderBy(j => j.Id).ToListAsync(Ct);
        Assert.Equal([(JobKind.Parse, JobState.Done), (JobKind.Reparse, JobState.Queued)], jobs.Select(j => (j.Kind, j.State)).ToArray());
        var afterFirst = await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct);
        Assert.Equal(((int?)1, 2), (afterFirst.ParsedSettingsVersion, afterFirst.SettingsVersion));

        // The follow-up reads the settings as they are now and is the last one.
        var second = (await JobOperations.ClaimAsync(db, "w:1", Now, Ct))!.Job;
        Assert.Equal(2, second.SettingsVersion);
        await JobOperations.CompleteAsync(db, scene.Audit, second.Id, "w:1", new JobCounts(1, 1, 10, 0, 100), Whole, Now, Ct);
        Assert.Equal(2, await db.Jobs.CountAsync(Ct));
        Assert.Equal(2, (await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct)).ParsedSettingsVersion);
    }

    [Fact]
    public async Task AJobThatFailsUnderOldSettingsIsFollowedTooAndAWaitingJobNeedsNoSecondOne()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var file = await scene.AddAsync(db, "a", "access.log", f => f.DetectedSource = "nginx", queue: true);

        // Waiting: the job reads the settings when it is claimed.
        var waiting = await FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, "syslog", null, scene.Clock, Ct);
        Assert.Null(waiting.Job);
        Assert.Equal(ReparseCost.None, waiting.Cost);

        var running = (await JobOperations.ClaimAsync(db, "w:1", Now, Ct))!.Job;
        await FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 2, "generic", null, scene.Clock, Ct);
        await JobOperations.FailAsync(db, running.Id, "w:1", "bad timestamp on every line", Now, Ct);
        var jobs = await db.Jobs.AsNoTracking().OrderBy(j => j.Id).ToListAsync(Ct);
        Assert.Equal([(JobKind.Parse, JobState.Failed), (JobKind.Parse, JobState.Queued)], jobs.Select(j => (j.Kind, j.State)).ToArray());

        // A failure under the current settings is not repeated.
        var again = (await JobOperations.ClaimAsync(db, "w:1", Now, Ct))!.Job;
        await JobOperations.FailAsync(db, again.Id, "w:1", "still bad", Now, Ct);
        Assert.Equal(2, await db.Jobs.CountAsync(Ct));
    }

    [Fact]
    public async Task NothingOfACaseThatIsNotActiveChangesButAHeldJobCanBeCancelled()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var file = await scene.ParsedAsync(db, "a", "access.log", source: "nginx");
        var held = await scene.AddAsync(db, "b", "b.log", queue: true);
        var host = await HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-01", null, null, scene.Clock, Ct);
        var other = await HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-02", null, null, scene.Clock, Ct);
        await LifecycleOperations.CloseAsync(db, scene.Audit, scene.Alice, scene.CaseId, Now, Ct);
        var dump = await scene.DumpAsync();

        var refused = await Assert.ThrowsAsync<CaseFrozenException>(() => FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, new FileEdit(Change.To<string?>("web-01"), false, default, default), scene.Clock, Ct));
        Assert.Contains("is closed", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CaseFrozenException>(() => FileSettingsOperations.OverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, "syslog", null, scene.Clock, Ct));
        await Assert.ThrowsAsync<CaseFrozenException>(() => FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, new FileSelection { Pattern = "*.log" }, "web-01", false, null, scene.Clock, Ct));
        await Assert.ThrowsAsync<CaseFrozenException>(() => FileSettingsOperations.BulkOverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, new FileSelection { Extension = "log" }, "syslog", null, false, null, scene.Clock, Ct));
        await Assert.ThrowsAsync<CaseFrozenException>(() => FileSettingsOperations.RedetectAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, new FileSelection { StaleDetection = true }, false, null, scene.Clock, Ct));
        await Assert.ThrowsAsync<CaseFrozenException>(() => HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-03", null, null, scene.Clock, Ct));
        await Assert.ThrowsAsync<CaseFrozenException>(() => HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, host.Id, 1, new HostEdit(default, Change.To(Berlin), default), true, scene.Clock, Ct));
        await Assert.ThrowsAsync<CaseFrozenException>(() => HostOperations.MergeAsync(db, scene.Audit, scene.Alice, scene.CaseId, host.Id, 1, other.Id, scene.Clock, Ct));
        var heldJob = await db.Jobs.AsNoTracking().SingleAsync(j => j.FileId == held.Id, Ct);
        var failed = await db.Jobs.AsNoTracking().FirstAsync(j => j.FileId == file.Id, Ct);
        await Assert.ThrowsAsync<CaseFrozenException>(() => JobOperations.RetryAsync(db, scene.Audit, scene.Alice, scene.CaseId, failed.Id, scene.Clock, Ct));
        Assert.Equal(dump, await scene.DumpAsync());

        var cancelled = await JobOperations.CancelAsync(db, scene.Audit, scene.Alice, scene.CaseId, heldJob.Id, scene.Clock, Ct);
        Assert.Equal(JobState.Cancelled, cancelled.State);
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal((AuditActions.JobCancelled, AuditTargets.File, (long?)held.Id, "alice"), (row.Action, row.TargetType, row.TargetId, row.Actor));
    }

    [Fact]
    public async Task AFailedAuditAppendLeavesNoChangeAndNoJob()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var file = await scene.ParsedAsync(db, "a", "web-02/access.log", source: "nginx");
        var queued = await scene.AddAsync(db, "b", "b.log", queue: true);
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.FileId == queued.Id, Ct);
        var host = await HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-01", null, null, scene.Clock, Ct);
        var other = await HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-09", null, null, scene.Clock, Ct);
        await FileSettingsOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, queued.Id, 1, new FileEdit(Change.To<string?>("web-01"), false, default, default), scene.Clock, Ct);
        var preview = await FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, new FileSelection { Pattern = "web-02/*" }, "web-02", false, null, scene.Clock, Ct);
        var bulk = await FileSettingsOperations.BulkOverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, new FileSelection { Extension = "log" }, "syslog", null, false, null, scene.Clock, Ct);
        Assert.Equal(2, bulk.Changed);
        var dump = await scene.DumpAsync();
        var failing = new ThrowingWriter(scene.Audit);

        Func<Task>[] attempts =
        [
            () => FileSettingsOperations.UpdateAsync(db, failing, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, new FileEdit(Change.To<string?>("web-01"), false, default, default), scene.Clock, Ct),
            () => FileSettingsOperations.UpdateAsync(db, failing, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, new FileEdit(Change.To<string?>("brand-new"), false, default, default), scene.Clock, Ct),
            () => FileSettingsOperations.OverrideAsync(db, failing, scene.Alice, scene.Rules, scene.CaseId, file.Id, 1, "syslog", null, scene.Clock, Ct),
            () => FileSettingsOperations.AssignHostAsync(db, failing, scene.Alice, scene.Rules, scene.CaseId, new FileSelection { Pattern = "web-02/*" }, "web-02", true, preview.Digest, scene.Clock, Ct),
            () => FileSettingsOperations.BulkOverrideAsync(db, failing, scene.Alice, scene.Rules, scene.CaseId, new FileSelection { Extension = "log" }, "syslog", null, true, bulk.Digest, scene.Clock, Ct),
            () => HostOperations.CreateAsync(db, failing, scene.Alice, scene.Rules, scene.CaseId, "web-03", null, null, scene.Clock, Ct),
            () => HostOperations.UpdateAsync(db, failing, scene.Alice, scene.Rules, scene.CaseId, host.Id, 1, new HostEdit(Change.To("web-1"), default, default), true, scene.Clock, Ct),
            () => HostOperations.MergeAsync(db, failing, scene.Alice, scene.CaseId, host.Id, 1, other.Id, scene.Clock, Ct),
            () => JobOperations.CancelAsync(db, failing, scene.Alice, scene.CaseId, job.Id, scene.Clock, Ct),
        ];
        foreach (var attempt in attempts)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(attempt);
            Assert.Equal(dump, await scene.DumpAsync());
        }

        Assert.True((await AuditVerifier.VerifyAsync(scene.Path, Ct)).Ok);
    }

    [Fact]
    public async Task ABulkHostAssignmentSaysWhatItCostsFirstAndIsOneRowWhenItIsMade()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var files = new List<EvidenceFile>();
        for (var i = 0; i < 10; i++)
        {
            files.Add(await scene.ParsedAsync(db, "w" + i, "access.log." + i, source: "nginx", archivePath: $"bundle.zip!web-02/nginx/access.log.{i}"));
        }

        var elsewhere = await scene.ParsedAsync(db, "x", "access.log", source: "nginx", archivePath: "bundle.zip!web-01/nginx/access.log");
        var selection = new FileSelection { Pattern = "bundle.zip!web-02/**" };
        var dump = await scene.DumpAsync();

        var preview = await FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection, "web-02", false, null, scene.Clock, Ct);
        Assert.Equal((false, 10, 10, 0), (preview.Applied, preview.Matched, preview.Changed, preview.Jobs));
        Assert.Equal(new ReparseCost(10, files.Sum(f => f.Size)), preview.Cost);
        Assert.Equal(dump, await scene.DumpAsync());

        // Confirmed against another selection than the one that was shown: refused.
        await Assert.ThrowsAsync<StaleWriteException>(() => FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection, "web-02", true, new string('0', 64), scene.Clock, Ct));
        Assert.Equal(dump, await scene.DumpAsync());

        var before = await db.Audit.CountAsync(Ct);
        var made = await FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection, "web-02", true, preview.Digest, scene.Clock, Ct);
        Assert.Equal((true, 10, 10, 10), (made.Applied, made.Matched, made.Changed, made.Jobs));
        var host = await db.Hosts.AsNoTracking().SingleAsync(h => h.Name == "web-02", Ct);
        Assert.Equal(10, await db.Files.CountAsync(f => f.HostId == host.Id, Ct));
        Assert.Null((await db.Files.AsNoTracking().SingleAsync(f => f.Id == elsewhere.Id, Ct)).HostId);
        var rows = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Skip(before).ToListAsync(Ct);
        Assert.Equal([AuditActions.HostCreate, AuditActions.FileBulkHost], rows.Select(r => r.Action).ToArray());
        Assert.Equal((AuditTargets.Case, (long?)scene.CaseId), (rows[1].TargetType, rows[1].TargetId));
        foreach (var file in files)
        {
            Assert.Contains($"{{\"id\":{file.Id},\"sha256\":\"{file.Sha256}\"}}", rows[1].DetailsJson, StringComparison.Ordinal);
        }

        Assert.Contains($"\"count\":10,\"digest\":\"{preview.Digest}\"", rows[1].DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"reparse_bytes\":{files.Sum(f => f.Size)},\"reparse_files\":10", rows[1].DetailsJson, StringComparison.Ordinal);

        // The same call again: every file is where it should be, nothing is written.
        var again = await FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection, "web-02", false, null, scene.Clock, Ct);
        Assert.Equal((0, 10), (again.Changed, again.Skipped["unchanged"]));
        Assert.NotEqual(preview.Digest, again.Digest);
    }

    [Fact]
    public async Task ABulkCallTouchesAtMostAThousandFilesAndALargerSelectionIsWalked()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        for (var i = 0; i < FileSettingsOperations.BulkLimit + 5; i++)
        {
            db.Files.Add(Scene.NewFile(scene.CaseId, "m" + i, $"big.zip!web-07/app/{i}.log"));
        }

        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
        var selection = new FileSelection { Pattern = "big.zip!web-07/**" };

        var tooMany = await Assert.ThrowsAsync<SelectionTooLargeException>(() => FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection, "web-07", false, null, scene.Clock, Ct));
        Assert.Equal(FileSettingsOperations.BulkLimit + 5, tooMany.Matched);
        await Assert.ThrowsAsync<ArgumentException>(() => FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection with { Limit = 1001, Paged = true }, "web-07", false, null, scene.Clock, Ct));

        var first = await FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection with { Paged = true }, "web-07", false, null, scene.Clock, Ct);
        Assert.Equal(FileSettingsOperations.BulkLimit, first.Matched);
        Assert.NotNull(first.NextAfterId);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var made = await FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection with { Paged = true }, "web-07", true, first.Digest, scene.Clock, Ct);
        await Timing.RecordAsync(TestContext.Current.TestOutputHelper!, $"bulk host assignment of {made.Changed} files with {made.Jobs} jobs, one transaction: {clock.ElapsedMilliseconds} ms");
        Assert.Equal((FileSettingsOperations.BulkLimit, FileSettingsOperations.BulkLimit), (made.Changed, made.Jobs));
        var rest = await FileSettingsOperations.AssignHostAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection with { Paged = true, AfterId = made.NextAfterId!.Value }, "web-07", false, null, scene.Clock, Ct);
        Assert.Equal((5, (long?)null), (rest.Matched, rest.NextAfterId));
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.InRange(row.DetailsJson.Length, 80_000, 110_000);
    }

    [Fact]
    public async Task ABulkOverrideTakesFilesByExtensionAndDetectorResultAndCountsWhatItLeaves()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var a = await scene.ParsedAsync(db, "a", "access.log.3.gz", source: "generic");
        var b = await scene.ParsedAsync(db, "b", "access.log", source: "generic");
        var named = await scene.ParsedAsync(db, "c", "other.log", source: "nginx");
        var text = await scene.ParsedAsync(db, "d", "notes.txt", source: "generic");
        var quarantined = await scene.AddAsync(db, "e", "broken.log", f =>
        {
            f.DetectedSource = "generic";
            f.State = FileState.Quarantined;
        });
        var options = new Dictionary<string, string> { ["nginx.log_format"] = "combined" };
        var selection = new FileSelection { Extension = ".LOG", DetectedSource = "generic" };

        var preview = await FileSettingsOperations.BulkOverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection, "nginx", options, false, null, scene.Clock, Ct);
        Assert.Equal((3, 2, 1), (preview.Matched, preview.Changed, preview.Skipped["quarantined"]));
        var made = await FileSettingsOperations.BulkOverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection, "nginx", options, true, preview.Digest, scene.Clock, Ct);
        Assert.Equal((true, 2, 2), (made.Applied, made.Changed, made.Jobs));
        Assert.Equal(["nginx", "nginx", null, null, null], (await db.Files.AsNoTracking().OrderBy(f => f.Id).Select(f => f.OverrideSource).ToListAsync(Ct)).ToArray());
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal(AuditActions.FileBulkOverride, row.Action);
        Assert.Contains("\"skipped\":{\"quarantined\":1}", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"extension\":\".LOG\"", row.DetailsJson, StringComparison.Ordinal);
        Assert.Equal([a.Id, b.Id], new[] { a.Id, b.Id }.Where(id => row.DetailsJson.Contains($"{{\"id\":{id},", StringComparison.Ordinal)).ToArray());
        Assert.DoesNotContain($"{{\"id\":{named.Id},", row.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain($"{{\"id\":{text.Id},", row.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain($"{{\"id\":{quarantined.Id},", row.DetailsJson, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => FileSettingsOperations.BulkOverrideAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, new FileSelection(), "nginx", null, false, null, scene.Clock, Ct));
    }

    [Theory]
    [InlineData("access.log", "log")]
    [InlineData("access.log.3", "log")]
    [InlineData("access.log.3.gz", "log")]
    [InlineData("access.log-20261001.zst", "log")]
    [InlineData("bundle.zip!web-01/nginx/error.LOG.2.xz", "log")]
    [InlineData("export.csv.bz2", "csv")]
    [InlineData("messages", "")]
    [InlineData("messages.1", "")]
    public void TheExtensionOfASelectionIsTheOneBehindCompressionAndRotation(string name, string expected)
    {
        Assert.Equal(expected, FileSettingsOperations.ExtensionOf(name));
    }

    [Fact]
    public async Task AHostChangeThatWouldReparseAnswersWithItsCostAndWaitsForTheConfirmation()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var host = await HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-01", null, null, scene.Clock, Ct);
        var nginx = await scene.ParsedAsync(db, "a", "access.log", source: "nginx", hostId: host.Id);
        var syslog = await scene.ParsedAsync(db, "b", "messages", source: "syslog", hostId: host.Id);
        var zoned = await scene.ParsedAsync(db, "c", "auth.log", source: "syslog", hostId: host.Id, edit: f => f.Timezone = Tokyo);
        var ownFormat = await scene.ParsedAsync(db, "d", "custom.log", source: "nginx", hostId: host.Id, edit: f => f.OverrideOptions = """{"nginx.log_format":"common"}""");
        var dump = await scene.DumpAsync();

        // A zone: the files without one of their own whose source reads it.
        var zone = await HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, host.Id, 1, new HostEdit(default, Change.To(Berlin), default), false, scene.Clock, Ct);
        Assert.Equal((false, false, new ReparseCost(1, syslog.Size)), (zone.Applied, zone.Changed, zone.Cost));
        Assert.Equal(dump, await scene.DumpAsync());

        // Default options: the files of that source that set no value of their own.
        var format = new Dictionary<string, string> { ["nginx.log_format"] = "combined" };
        var options = await HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, host.Id, 1, new HostEdit(default, default, Change.To<IReadOnlyDictionary<string, string>?>(format)), false, scene.Clock, Ct);
        Assert.Equal((false, new ReparseCost(1, nginx.Size)), (options.Applied, options.Cost));

        // A rename: every file of the host.
        var rename = await HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, host.Id, 1, new HostEdit(Change.To("WEB-1"), default, default), false, scene.Clock, Ct);
        Assert.Equal((false, 4), (rename.Applied, rename.Cost.Files));
        Assert.Equal(dump, await scene.DumpAsync());

        var made = await HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, host.Id, 1, new HostEdit(default, Change.To(Berlin), Change.To<IReadOnlyDictionary<string, string>?>(format)), true, scene.Clock, Ct);
        Assert.Equal((true, true, 2, 2), (made.Applied, made.Changed, made.Cost.Files, made.Jobs));
        var stored = await db.Hosts.AsNoTracking().SingleAsync(h => h.Id == host.Id, Ct);
        Assert.Equal((Berlin, false, 2), (stored.Timezone, stored.TimezoneAssumed, stored.Version));
        Assert.Equal("2,2,1,1", string.Join(",", await db.Files.AsNoTracking().OrderBy(f => f.Id).Select(f => f.SettingsVersion).ToListAsync(Ct)));
        Assert.Equal([nginx.Id, syslog.Id], (await db.Jobs.AsNoTracking().Where(j => j.State == JobState.Queued).OrderBy(j => j.FileId).Select(j => j.FileId).ToListAsync(Ct)).ToArray());
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal((AuditActions.HostUpdate, AuditTargets.Case, (long?)scene.CaseId), (row.Action, row.TargetType, row.TargetId));
        Assert.Contains("\"timezone\":{\"from\":\"UTC\",\"to\":\"" + Berlin + "\",\"was_assumed\":true}", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"reparse_bytes\":{nginx.Size + syslog.Size},\"reparse_files\":2", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"jobs_queued\":2", row.DetailsJson, StringComparison.Ordinal);
        _ = (zoned, ownFormat);

        // What a parse reads: the host's option, and the file's own wherever the file has one.
        Assert.Equal("combined", SourceOptionSet.Effective(null, stored.SourceOptions)["nginx.log_format"]);
        Assert.Equal("common", SourceOptionSet.Effective("""{"nginx.log_format":"common"}""", stored.SourceOptions)["nginx.log_format"]);
    }

    [Fact]
    public async Task AHostChangeThatReparsesNothingIsMadeWithoutAConfirmation()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var host = await HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-01", Berlin, null, scene.Clock, Ct);
        Assert.False(host.TimezoneAssumed);
        await scene.ParsedAsync(db, "a", "access.log", source: "nginx", hostId: host.Id);

        var change = await HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, host.Id, 1, new HostEdit(default, Change.To(Tokyo), default), false, scene.Clock, Ct);
        Assert.Equal((true, true, ReparseCost.None), (change.Applied, change.Changed, change.Cost));
        var same = await HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, host.Id, 2, new HostEdit(Change.To("web-01"), Change.To(Tokyo), default), false, scene.Clock, Ct);
        Assert.Equal((true, false), (same.Applied, same.Changed));
        await Assert.ThrowsAsync<SettingsRefusedException>(() => HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "WEB-01", null, null, scene.Clock, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web 01", null, null, scene.Clock, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-05", null, new Dictionary<string, string> { ["dovecot.format"] = "x" }, scene.Clock, Ct));
    }

    [Fact]
    public async Task AMergedHostKeepsItsRowAndItsNameLeadsToTheSurvivor()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var old = await HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-01.internal", null, null, scene.Clock, Ct);
        var keep = await HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web-01", null, null, scene.Clock, Ct);
        var older = await HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web01", null, null, scene.Clock, Ct);
        var file = await scene.ParsedAsync(db, "a", "access.log", source: "nginx", hostId: old.Id);
        await HostOperations.MergeAsync(db, scene.Audit, scene.Alice, scene.CaseId, older.Id, 1, old.Id, scene.Clock, Ct);

        var (into, moved) = await HostOperations.MergeAsync(db, scene.Audit, scene.Alice, scene.CaseId, old.Id, 1, keep.Id, scene.Clock, Ct);

        Assert.Equal((keep.Id, 1), (into.Id, moved));
        var stored = await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct);
        // No parse is queued and the file's version does not move: its rows keep the host they were written with.
        Assert.Equal((keep.Id, 1, 1), (stored.HostId!.Value, stored.SettingsVersion, await db.Jobs.CountAsync(Ct)));
        Assert.Equal([keep.Id, keep.Id], (await db.Hosts.AsNoTracking().Where(h => h.MergedIntoId != null).OrderBy(h => h.Id).Select(h => h.MergedIntoId!.Value).ToListAsync(Ct)).ToArray());
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal(AuditActions.HostMerge, row.Action);
        Assert.Contains("\"files_moved\":1,\"host_id\":" + old.Id + ",\"host_name\":\"web-01.internal\",\"into_host_id\":" + keep.Id + ",\"into_host_name\":\"web-01\"", row.DetailsJson, StringComparison.Ordinal);

        // A later arrival under either old name lands on the survivor, and neither name can be taken again.
        Assert.Equal(keep.Id, (await HostOperations.GetOrCreateAsync(db, scene.CaseId, "WEB-01.internal", Now, Ct)).Id);
        Assert.Equal(keep.Id, (await HostOperations.GetOrCreateAsync(db, scene.CaseId, "web01", Now, Ct)).Id);
        await Assert.ThrowsAsync<SettingsRefusedException>(() => HostOperations.CreateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, "web01", null, null, scene.Clock, Ct));
        await Assert.ThrowsAsync<SettingsRefusedException>(() => HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, keep.Id, 1, new HostEdit(Change.To("web01"), default, default), true, scene.Clock, Ct));
        await Assert.ThrowsAsync<SettingsRefusedException>(() => HostOperations.UpdateAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, old.Id, 2, new HostEdit(default, Change.To(Berlin), default), true, scene.Clock, Ct));
        await Assert.ThrowsAsync<SettingsRefusedException>(() => HostOperations.MergeAsync(db, scene.Audit, scene.Alice, scene.CaseId, keep.Id, 1, old.Id, scene.Clock, Ct));
        await Assert.ThrowsAsync<SettingsRefusedException>(() => HostOperations.MergeAsync(db, scene.Audit, scene.Alice, scene.CaseId, keep.Id, 1, keep.Id, scene.Clock, Ct));
    }

    [Fact]
    public async Task AFailedOrCancelledJobIsRunAgainAsANewJobAndOnlyTheNewestOne()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var file = await scene.AddAsync(db, "a", "access.log", queue: true);
        var first = (await JobOperations.ClaimAsync(db, "w:1", Now, Ct))!.Job;
        await Assert.ThrowsAsync<JobStateException>(() => JobOperations.RetryAsync(db, scene.Audit, scene.Alice, scene.CaseId, first.Id, scene.Clock, Ct));
        await Assert.ThrowsAsync<JobStateException>(() => JobOperations.CancelAsync(db, scene.Audit, scene.Alice, scene.CaseId, first.Id, scene.Clock, Ct));
        await JobOperations.FailAsync(db, first.Id, "w:1", "the store refused the batch", Now, Ct);
        Assert.Equal(FileState.Failed, (await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct)).State);

        var second = await JobOperations.RetryAsync(db, scene.Audit, scene.Alice, scene.CaseId, first.Id, scene.Clock, Ct);
        Assert.Equal((JobKind.Parse, JobState.Queued), (second.Kind, second.State));
        Assert.Equal(FileState.Received, (await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct)).State);
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal((AuditActions.JobRetry, AuditTargets.File, (long?)file.Id), (row.Action, row.TargetType, row.TargetId));
        Assert.Contains($"\"after_job_id\":{first.Id},\"after_message\":\"the store refused the batch\",\"after_state\":\"failed\"", row.DetailsJson, StringComparison.Ordinal);

        // The first job is no longer the newest; the second is cancelled and run again.
        await Assert.ThrowsAsync<JobStateException>(() => JobOperations.RetryAsync(db, scene.Audit, scene.Alice, scene.CaseId, first.Id, scene.Clock, Ct));
        await JobOperations.CancelAsync(db, scene.Audit, scene.Alice, scene.CaseId, second.Id, scene.Clock, Ct);
        var third = await JobOperations.RetryAsync(db, scene.Audit, scene.Alice, scene.CaseId, second.Id, scene.Clock, Ct);
        Assert.Equal(JobState.Queued, third.State);
        await Assert.ThrowsAsync<RecordNotFoundException>(() => JobOperations.CancelAsync(db, scene.Audit, scene.Alice, scene.CaseId, 4242, scene.Clock, Ct));
        await Assert.ThrowsAsync<CaseNotFoundException>(() => JobOperations.RetryAsync(db, scene.Audit, scene.Alice, scene.CaseId + 1, third.Id, scene.Clock, Ct));
    }

    [Fact]
    public async Task APurgeIsAskedForAndConfirmedAsTwoSeparateSteps()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var ada = new AuditActor("ada", await Scene.AddAdminAsync(db, "ada"));
        var bo = new AuditActor("bo", await Scene.AddAdminAsync(db, "bo"));
        await LifecycleOperations.SoftDeleteAsync(db, scene.Audit, scene.Alice, scene.CaseId, "incident 1", TimeSpan.FromDays(7), Now, Ct);

        // Two-person approval on.
        var nothing = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.RequestPurgeAsync(db, scene.Audit, bo, scene.CaseId, true, PurgeIntent.Confirm, scene.Clock, Ct));
        Assert.Contains("nothing to confirm", nothing.Message, StringComparison.Ordinal);
        var asked = await LifecycleOperations.RequestPurgeAsync(db, scene.Audit, ada, scene.CaseId, true, PurgeIntent.Request, scene.Clock, Ct);
        Assert.Equal(CaseTaskState.Requested, asked.State);
        // A second administrator who asks does not confirm by doing so.
        var second = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.RequestPurgeAsync(db, scene.Audit, bo, scene.CaseId, true, PurgeIntent.Request, scene.Clock, Ct));
        Assert.Contains("already asked for by ada", second.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.RequestPurgeAsync(db, scene.Audit, ada, scene.CaseId, true, PurgeIntent.Confirm, scene.Clock, Ct));
        Assert.Equal(CaseTaskState.Requested, (await db.CaseTasks.AsNoTracking().SingleAsync(Ct)).State);
        var released = await LifecycleOperations.RequestPurgeAsync(db, scene.Audit, bo, scene.CaseId, true, PurgeIntent.Confirm, scene.Clock, Ct);
        Assert.Equal((CaseTaskState.Running, "ada", "bo"), (released.State, released.RequestedBy, released.ConfirmedBy));
        Assert.Equal(["requested", "confirmed"], (await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.CasePurgeRequest).OrderBy(a => a.Seq).Select(a => a.DetailsJson).ToListAsync(Ct)).Select(d => d.Contains("\"step\":\"requested\"", StringComparison.Ordinal) ? "requested" : "confirmed").ToArray());
    }

    [Fact]
    public async Task WithTwoPersonApprovalOffThereIsNothingToConfirm()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var ada = new AuditActor("ada", await Scene.AddAdminAsync(db, "ada"));
        await LifecycleOperations.SoftDeleteAsync(db, scene.Audit, scene.Alice, scene.CaseId, "incident 1", TimeSpan.FromDays(7), Now, Ct);

        var off = await Assert.ThrowsAsync<LifecycleRefusedException>(() => LifecycleOperations.RequestPurgeAsync(db, scene.Audit, ada, scene.CaseId, false, PurgeIntent.Confirm, scene.Clock, Ct));
        Assert.Contains("two-person approval is off", off.Message, StringComparison.Ordinal);
        Assert.Equal(CaseTaskState.Running, (await LifecycleOperations.RequestPurgeAsync(db, scene.Audit, ada, scene.CaseId, false, PurgeIntent.Request, scene.Clock, Ct)).State);
    }

    [Fact]
    public async Task DetectingAgainIsQueuedForTheFilesThatCouldReachAParserAndKeepsTheOldResultOnTheTrail()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        const string OldRegistry = "1:generic,syslog";
        var fallback = await scene.ParsedAsync(db, "a", "access.log", source: "generic", edit: f => f.DetectionRegistry = OldRegistry);
        var unparsed = await scene.AddAsync(db, "b", "system.journal", f =>
        {
            f.DetectedKind = FileKind.Unparsed;
            f.State = FileState.Artifact;
            f.DetectionRegistry = OldRegistry;
        });
        var current = await scene.ParsedAsync(db, "c", "app.log", source: "generic", edit: f => f.DetectionRegistry = scene.Rules.RegistryKey);
        var overridden = await scene.ParsedAsync(db, "d", "x.log", source: "generic", edit: f =>
        {
            f.DetectionRegistry = OldRegistry;
            f.OverrideSource = "syslog";
        });
        var parsed = await scene.ParsedAsync(db, "e", "messages", source: "syslog", edit: f => f.DetectionRegistry = OldRegistry);
        var selection = new FileSelection { StaleDetection = true };

        var preview = await FileSettingsOperations.RedetectAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection, false, null, scene.Clock, Ct);
        Assert.Equal((2, 2), (preview.Matched, preview.Changed));
        Assert.Empty(preview.Skipped);
        // Named one by one, the files that are no candidates are counted with the reason.
        var named = await FileSettingsOperations.RedetectAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, new FileSelection { FileIds = [fallback.Id, overridden.Id, parsed.Id] }, false, null, scene.Clock, Ct);
        Assert.Equal((3, 1, 1, 1), (named.Matched, named.Changed, named.Skipped["has_override"], named.Skipped["has_a_parser"]));
        Assert.Equal(new ReparseCost(2, fallback.Size + unparsed.Size), preview.Cost);
        var made = await FileSettingsOperations.RedetectAsync(db, scene.Audit, scene.Alice, scene.Rules, scene.CaseId, selection, true, preview.Digest, scene.Clock, Ct);
        Assert.Equal(2, made.Jobs);
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.State == JobState.Queued).OrderBy(j => j.FileId).ToListAsync(Ct);
        Assert.Equal([fallback.Id, unparsed.Id], jobs.Select(j => j.FileId).ToArray());
        Assert.All(jobs, j => Assert.True(JobOperations.IsRedetect(j)));
        Assert.Equal(AuditActions.FileBulkRedetect, (await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct)).Action);
        _ = (current, overridden, parsed);

        // The runner's two writes: the new record with the old one beside it, and the end of a job that parsed nothing.
        var claimed = (await JobOperations.ClaimAsync(db, "w:1", Now, Ct))!;
        var committed = await JobOperations.CommittedGenerationAsync(db, fallback.Id, Ct);
        var detection = new DetectionResult { Kind = FileKind.Log, SourceId = "nginx", Confidence = 0.93f, Layout = "combined", RegisteredSources = ["generic", "nginx", "syslog"] };
        var after = await JobOperations.RecordRedetectAsync(db, scene.Audit, claimed.Job.Id, "w:1", detection, Ct);
        Assert.Equal((FileKind.Log, "nginx", "combined", scene.Rules.RegistryKey), (after.DetectedKind!.Value, after.DetectedSource, after.DetectedLayout, after.DetectionRegistry));
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal((AuditActions.FileRedetect, AuditActions.SystemActor), (row.Action, row.Actor));
        Assert.Contains("\"from\":{\"confidence_per_mille\":null,\"kind\":\"log\",\"layout\":null,\"registry\":\"1:generic,syslog\",\"source\":\"generic\"}", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"to\":{\"confidence_per_mille\":930,\"kind\":\"log\",\"layout\":\"combined\",\"registry\":\"" + scene.Rules.RegistryKey + "\",\"source\":\"nginx\"}", row.DetailsJson, StringComparison.Ordinal);

        await JobOperations.CompleteWithoutParseAsync(db, claimed.Job.Id, "w:1", "detected again: nothing to parse", scene.Clock, Ct);
        Assert.Equal(committed, await JobOperations.CommittedGenerationAsync(db, fallback.Id, Ct));
        Assert.DoesNotContain(0, (await JobOperations.CommittedGenerationsAsync(db, Ct)).Values);
    }

    [Fact]
    public async Task AUserChangesTheirOwnPasswordAndEveryOtherSessionEnds()
    {
        await using var scene = await Scene.CreateAsync();
        await using var db = scene.Open();
        var here = SessionOperations.Create(scene.Alice.UserId!.Value, "203.0.113.10", Now, TimeSpan.FromHours(24));
        db.Sessions.AddRange(here.Row, SessionOperations.Create(scene.Alice.UserId.Value, "203.0.113.11", Now, TimeSpan.FromHours(24)).Row, SessionOperations.Create(scene.Alice.UserId.Value, "203.0.113.12", Now, TimeSpan.FromHours(24)).Row);
        await db.SaveChangesAsync(Ct);
        var verified = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == scene.Alice.UserId, Ct)).PasswordHash;

        var ended = await UserOperations.ChangeOwnPasswordAsync(db, scene.Audit, scene.Alice, scene.Alice.UserId.Value, verified, "$argon2id$new-hash", here.Row.Id, Now, Ct);

        Assert.Equal(2, ended);
        Assert.Equal([here.Row.Id], (await db.Sessions.AsNoTracking().Select(s => s.Id).ToListAsync(Ct)).ToArray());
        Assert.Equal("$argon2id$new-hash", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == scene.Alice.UserId, Ct)).PasswordHash);
        var row = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).LastAsync(Ct);
        Assert.Equal((AuditActions.UserPasswordChange, AuditTargets.User, scene.Alice.UserId), (row.Action, row.TargetType, row.TargetId));
        Assert.Equal($$"""{"session":"{{here.Row.Id[..12]}}","sessions_ended":2,"user_name":"alice"}""", row.DetailsJson);

        // The hash that was verified is no longer the stored one: a reset came in between, and it is not undone.
        await Assert.ThrowsAsync<StaleWriteException>(() => UserOperations.ChangeOwnPasswordAsync(db, scene.Audit, scene.Alice, scene.Alice.UserId.Value, verified, "$argon2id$third", here.Row.Id, Now, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => UserOperations.ChangeOwnPasswordAsync(db, new ThrowingWriter(scene.Audit), scene.Alice, scene.Alice.UserId.Value, "$argon2id$new-hash", "$argon2id$fourth", here.Row.Id, Now, Ct));
        Assert.Equal("$argon2id$new-hash", (await db.Users.AsNoTracking().SingleAsync(u => u.Id == scene.Alice.UserId, Ct)).PasswordHash);
    }

    [Fact]
    public async Task MigrateGivesOlderRecordsTheKeyOfTheDetectorThatMadeThem()
    {
        await using var scene = await Scene.CreateAsync();
        await using (var db = scene.Open())
        {
            await scene.AddAsync(db, "a", "a.log", f => f.Detection = """{"detector_version":1,"registered_sources":["syslog","generic","nginx"],"kind":"log"}""");
            await scene.AddAsync(db, "b", "b.log", f => f.Detection = """{"kind":"log"}""");
            await scene.AddAsync(db, "c", "c.log");
        }

        await scene.Store.MigrateAsync(Ct);
        await using var after = scene.Open();
        Assert.Equal(["1:generic,nginx,syslog", string.Empty, null], (await after.Files.AsNoTracking().OrderBy(f => f.Id).Select(f => f.DetectionRegistry).ToListAsync(Ct)).ToArray());
        Assert.Equal("1:generic,nginx,syslog", scene.Rules.RegistryKey);
    }

    /// <summary>Writes the row, then fails: what a crash between the append and the commit looks like to the caller.</summary>
    private sealed class ThrowingWriter(IAuditWriter inner) : IAuditWriter
    {
        public async Task<AuditRow> AppendAsync(MetadataDb db, AuditEntry entry, CancellationToken cancellationToken = default)
        {
            await inner.AppendAsync(db, entry, cancellationToken);
            throw new InvalidOperationException("audit write failed");
        }
    }

    /// <summary>A source that is only asked what it is.</summary>
    private sealed class NamedSource(string id, bool naive) : ILogSource
    {
        public SourceInfo Info { get; } = new(id, [], id, 1, NaiveTimestamps: naive);

        public Framing Framing => Framing.Line;

        public FieldSchema Schema { get; } = new(1, [EventColumn.Ts, EventColumn.Raw]);

        public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => 0f;

        public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch) => throw new NotSupportedException();
    }

    /// <summary>One active case owned by alice, three registered sources, and the helpers that put files into a state.</summary>
    private sealed class Scene : IAsyncDisposable
    {
        private readonly TempDb _temp;

        private Scene(TempDb temp, long caseId, AuditActor alice)
        {
            _temp = temp;
            CaseId = caseId;
            Alice = alice;
            var catalog = new SourceCatalog();
            catalog.Add(
            [
                new SourceRegistration("nginx", ImmutableArray<string>.Empty, () => new NamedSource("nginx", naive: false)),
                new SourceRegistration("syslog", ImmutableArray<string>.Empty, () => new NamedSource("syslog", naive: true)),
                new SourceRegistration("generic", ["raw"], () => new NamedSource("generic", naive: true)),
            ]);
            Rules = new SourceRules(catalog, (source, options) => source == "nginx" && options.TryGetValue("nginx.log_format", out var format) && format == "bad" ? "nginx.log_format: no variable in the format" : null);
        }

        public long CaseId { get; }

        public AuditActor Alice { get; }

        public SourceRules Rules { get; }

        public MetadataStore Store => _temp.Store;

        public IAuditWriter Audit => _temp.Store.Audit;

        public TimeProvider Clock => _temp.Store.Clock;

        public string Path => _temp.Path;

        public static async Task<Scene> CreateAsync()
        {
            var temp = await TempDb.CreateAsync();
            await using var db = temp.Open();
            var owner = await TempDb.AddUserAsync(db);
            var @case = await TempDb.AddCaseAsync(db, owner);
            return new Scene(temp, @case.Id, new AuditActor("alice", owner));
        }

        public static async Task<long> AddAdminAsync(MetadataDb db, string name)
        {
            var user = new User { Name = name, PasswordHash = "argon2id$placeholder", CreatedAt = DateTime.UtcNow, Role = UserRole.Admin };
            db.Users.Add(user);
            await db.SaveChangesAsync(Ct);
            return user.Id;
        }

        public static EvidenceFile NewFile(long caseId, string content, string path)
        {
            var bang = path.LastIndexOf('!');
            var slash = path.LastIndexOfAny(['/', '!']);
            var file = TempDb.NewFile(caseId, content, slash >= 0 ? path[(slash + 1)..] : path);
            return bang < 0 ? file : Copy(file, path);
        }

        public MetadataDb Open() => _temp.Open();

        public async Task<EvidenceFile> AddAsync(MetadataDb db, string content, string name, Action<EvidenceFile>? edit = null, bool queue = false, string? archivePath = null)
        {
            var file = archivePath is null ? TempDb.NewFile(CaseId, content, name) : Copy(TempDb.NewFile(CaseId, content, name), archivePath);
            edit?.Invoke(file);
            return (await FileOperations.AddAsync(db, Audit, Alice, file, queue, arrivalHost: null, Ct)).File;
        }

        /// <summary>A file whose first parse is committed.</summary>
        public async Task<EvidenceFile> ParsedAsync(MetadataDb db, string content, string name, string source, string? detection = null, string? archivePath = null, long? hostId = null, Action<EvidenceFile>? edit = null)
        {
            var file = await AddAsync(db, content, name, f =>
            {
                f.DetectedKind = FileKind.Log;
                f.DetectedSource = source;
                f.Detection = detection;
                f.HostId = hostId;
                edit?.Invoke(f);
            }, queue: true, archivePath);
            var claimed = (await JobOperations.ClaimAsync(db, "w:seed", Now, Ct))!;
            Assert.Equal(file.Id, claimed.File.Id);
            await JobOperations.CompleteAsync(db, Audit, claimed.Job.Id, "w:seed", new JobCounts(1, 1, 10, 1, file.Size), Whole, Now, Ct);
            db.ChangeTracker.Clear();
            return await db.Files.AsNoTracking().SingleAsync(f => f.Id == file.Id, Ct);
        }

        /// <summary>Every row of the file but the sessions, to assert that a refused call wrote nothing.</summary>
        public Task<string> DumpAsync() => Urlog.Tests.Shared.MetadataDump.TakeAsync(Path);

        public ValueTask DisposeAsync() => _temp.DisposeAsync();

        private static EvidenceFile Copy(EvidenceFile file, string archivePath) => new()
        {
            CaseId = file.CaseId,
            Sha256 = file.Sha256,
            Size = file.Size,
            OriginalName = file.OriginalName,
            StoredPath = file.StoredPath,
            Source = file.Source,
            Uploader = file.Uploader,
            ReceivedAt = file.ReceivedAt,
            Encoding = file.Encoding,
            ArchivePath = archivePath,
        };
    }
}
