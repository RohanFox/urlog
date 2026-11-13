using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Detection;
using Urlog.Core.Intake;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Metadata;
using Urlog.Sources.Tests.Detection;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Tests.Intake;

/// <summary>
/// Archives through intake and the job runner: every member its own record with its own hash, path and job;
/// archives inside archives to the depth limit; the caps that refuse a bomb and leave nothing behind; an
/// expansion that stops and goes on; the same bytes twice; members that are cut short; files that are refused.
/// </summary>
public sealed class ArchiveExpansionTests
{
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n%"u8, 0xE2, 0xE3, 0xCF, 0xD3, .. "\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"u8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AZipHoldingACompressedTarOfThreeLogsAndAnArtifactBecomesFiveMemberRecordsAndThreeParses()
    {
        var access = Lines(400, 1);
        var error = Lines(300, 2);
        var syslog = Lines(200, 3);
        var tar = FixtureContainers.Tar(("web-01/nginx/access.log", access), ("web-01/nginx/error.log", error), ("db-02/var/log/syslog", syslog), ("web-01/report.pdf", Pdf));
        var zip = FixtureContainers.Zip(("logs/inner.tar.gz", Gzip(tar)));

        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var path = await h.WriteAsync("bundle.zip", zip);
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, path), Ct);
        Assert.Equal((JobKind.Expand, FileKind.Container, false), (arrived.Job!.Kind, arrived.Detection.Kind, arrived.Duplicate));

        await h.Runner().RunAsync(Ct);

        await using var db = h.Open();
        var files = await db.Files.AsNoTracking().OrderBy(f => f.Id).ToListAsync(Ct);
        var top = files.Single(f => f.ParentFileId is null);
        var inner = files.Single(f => f.ArchivePath == "bundle.zip!logs/inner.tar.gz");
        Assert.Equal(6, files.Count); // the zip and five members: the inner archive and its four files
        Assert.Equal((FileState.Expanded, 0, (string?)null), (top.State, top.ArchiveDepth, top.CachePath)); // a plain zip is read in place
        Assert.Equal((FileState.Expanded, 1, top.Id, top.Id), (inner.State, inner.ArchiveDepth, inner.ParentFileId!.Value, inner.RootFileId!.Value));
        Assert.Equal(Sha(Gzip(tar)), inner.Sha256); // a member's hash is over its bytes as the archive holds them
        Assert.NotNull(inner.CachePath);
        Assert.True(File.Exists(Path.Combine(h.CacheRoot, @case.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), inner.CachePath!)));

        foreach (var (name, content, host) in new[] { ("web-01/nginx/access.log", access, "web-01"), ("web-01/nginx/error.log", error, "web-01"), ("db-02/var/log/syslog", syslog, "db-02") })
        {
            var member = files.Single(f => f.ArchivePath == "bundle.zip!logs/inner.tar.gz!" + name);
            Assert.Equal((FileState.Parsed, 2, inner.Id, top.Id, Sha(content), (long)content.Length), (member.State, member.ArchiveDepth, member.ParentFileId!.Value, member.RootFileId!.Value, member.Sha256, member.Size));
            Assert.Equal(name, member.OriginalName);
            Assert.True(member.HostInferred);
            Assert.Equal(host, (await db.Hosts.AsNoTracking().SingleAsync(x => x.Id == member.HostId, Ct)).Name);
            Assert.Equal(content.Count(b => b == (byte)'\n'), h.Events.Visible((ulong)member.Id));
            Assert.NotNull(member.MemberOffset);
        }

        var artifact = files.Single(f => f.ArchivePath == "bundle.zip!logs/inner.tar.gz!web-01/report.pdf");
        Assert.Equal((FileState.Artifact, FileKind.Artifact, Sha(Pdf)), (artifact.State, artifact.DetectedKind!.Value, artifact.Sha256));

        var jobs = await db.Jobs.AsNoTracking().ToListAsync(Ct);
        Assert.All(jobs, j => Assert.True(j.State == JobState.Done, $"{j.Kind} of file {j.FileId}: {j.State}, {j.Message}"));
        Assert.Equal((2, 3), (jobs.Count(j => j.Kind == JobKind.Expand), jobs.Count(j => j.Kind == JobKind.Parse)));
        Assert.DoesNotContain(jobs, j => j.FileId == artifact.Id);
        Assert.Equal(6, (await h.AuditAsync()).Count(a => a.Action == AuditActions.FileIntake));
    }

    [Fact]
    public async Task ATarThatArrivedUncompressedIsReadInPlaceAndItsMembersAreParsedFromIt()
    {
        var log = Lines(500, 4);
        var tar = FixtureContainers.Tar(("srv-9/var/log/auth.log", log), ("srv-9/var/log/old.log.gz", Gzip(Lines(100, 5))));
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("collect.tar", tar)), Ct);

        await h.Runner().RunAsync(Ct);

        await using var db = h.Open();
        var members = await db.Files.AsNoTracking().Where(f => f.ParentFileId == arrived.File!.Id).OrderBy(f => f.MemberOrdinal).ToListAsync(Ct);
        Assert.Equal(2, members.Count);
        Assert.All(members, m => Assert.Equal(FileState.Parsed, m.State));
        Assert.Equal((500, 100), (h.Events.Visible((ulong)members[0].Id), h.Events.Visible((ulong)members[1].Id)));
        Assert.Equal("gzip", members[1].Compression);
        Assert.False(Directory.Exists(h.CacheRoot) && Directory.EnumerateFiles(h.CacheRoot, "*", SearchOption.AllDirectories).Any(), "a plain tar needs no copy");
    }

    [Fact]
    public async Task AnArchiveNestedDeeperThanTheLimitIsRecordedAndLeftClosed()
    {
        // Four archives inside one another; the log is inside the fourth.
        var fourth = FixtureContainers.Zip(("deep.log", Lines(10, 6)));
        var third = FixtureContainers.Zip(("d.zip", fourth));
        var second = FixtureContainers.Zip(("c.zip", third));
        var first = FixtureContainers.Zip(("b.zip", second));
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("a.zip", first)), Ct);

        await h.Runner().RunAsync(Ct);

        await using var db = h.Open();
        var files = await db.Files.AsNoTracking().OrderBy(f => f.ArchiveDepth).ToListAsync(Ct);
        Assert.Equal(["a.zip", "b.zip", "c.zip", "d.zip"], files.Select(f => f.OriginalName).ToArray());
        Assert.Equal([FileState.Expanded, FileState.Expanded, FileState.Expanded, FileState.Artifact], files.Select(f => f.State).ToArray());
        Assert.Contains("nested 3 deep", files[3].Detection, StringComparison.Ordinal);
        Assert.Contains("Extract it and upload the contents", files[3].Detection, StringComparison.Ordinal);
        Assert.Equal(3, await db.Jobs.CountAsync(j => j.Kind == JobKind.Expand, Ct));
        Assert.Equal(0, await db.Jobs.CountAsync(j => j.Kind == JobKind.Parse, Ct));
    }

    [Fact]
    public async Task AnArchiveThatExpandsAThousandToOneIsRefusedWithTheDefaultLimitsAndLeavesNoMember()
    {
        // 70 MiB of zero bytes in a tar, gzipped: past the 64 MiB floor at about 1,000:1.
        var big = new byte[70 << 20];
        var stored = Gzip(FixtureContainers.Tar(("web-01/nginx/access.log", big)));
        Assert.True(big.Length / stored.Length > 200, $"ratio {big.Length / stored.Length}");
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("bomb.tar.gz", stored)), Ct);

        await h.Runner().RunAsync(Ct);

        var job = await h.JobAsync(arrived.Job!.Id);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("expands more than 200:1", job.Message, StringComparison.Ordinal);
        Assert.Contains($"from {stored.Length:N0} stored", job.Message, StringComparison.Ordinal);
        await using var db = h.Open();
        Assert.Equal(1, await db.Files.CountAsync(Ct));
        Assert.Equal(FileState.Failed, (await h.FileAsync(arrived.File!.Id)).State);
        Assert.False(Directory.EnumerateFiles(h.CacheRoot, "*", SearchOption.AllDirectories).Any(), "the unfinished copy is removed");
    }

    [Fact]
    public async Task ACapNamesItselfAndItsMeasureAndAnAuditedRerunWithHigherLimitsExpandsTheArchive()
    {
        var zip = FixtureContainers.Zip(Enumerable.Range(0, 5).Select(i => ($"web-01/nginx/access.log.{i}", Lines(20, 10 + i))).ToArray());
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("many.zip", zip)), Ct);
        var tight = RunnerHarness.Options() with { Archive = new ArchiveLimits { MaxMembers = 3 } };

        await h.Runner(tight).RunAsync(Ct);

        var failed = await h.JobAsync(arrived.Job!.Id);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Contains("more than 3 members (4 counted so far)", failed.Message, StringComparison.Ordinal);
        await using var db = h.Open();
        Assert.Equal(1, await db.Files.CountAsync(Ct));

        // A rerun is an administrator's act: refused for a file that did not fail, audited with every limit.
        var limits = new ArchiveLimits { MaxMembers = 10 };
        var rerun = await JobOperations.RequestExpandRerunAsync(db, h.Store.Audit, new AuditActor("root"), arrived.File!.Id, limits.ToJson(), limits.ToDetails(), DateTime.UtcNow, Ct);
        await Assert.ThrowsAsync<JobRefusedException>(() => JobOperations.RequestExpandRerunAsync(db, h.Store.Audit, new AuditActor("root"), arrived.File.Id, limits.ToJson(), limits.ToDetails(), DateTime.UtcNow, Ct));

        await h.Runner(tight).RunAsync(Ct); // the configured limit is still 3; the job carries its own

        Assert.Equal(JobState.Done, (await h.JobAsync(rerun.Id)).State);
        Assert.Equal(5, await db.Files.CountAsync(f => f.ParentFileId == arrived.File.Id && f.State == FileState.Parsed, Ct));
        var row = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.FileExpandRerun);
        Assert.Equal(("root", arrived.File.Id), (row.Actor, row.TargetId));
        Assert.Contains("\"max_members\":10", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"max_ratio\":200", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("more than 3 members", row.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASingleCompressedLogIsHeldToTheRatioToo()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1700000000000 " + new string('b', 100) + "\n", 40_000)));
        var stored = Gzip(content);
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("a.log.gz", stored)), Ct);
        Assert.Equal(JobKind.Parse, arrived.Job!.Kind);

        await h.Runner(RunnerHarness.Options() with { Archive = new ArchiveLimits { MaxRatio = 50, RatioFloorBytes = 1 << 20 } }).RunAsync(Ct);

        var job = await h.JobAsync(arrived.Job.Id);
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("expands more than 50:1", job.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.Events.Visible((ulong)arrived.File!.Id));
        Assert.Empty(h.Events.Generations((ulong)arrived.File.Id));
    }

    [Fact]
    public async Task AnExpansionThatStopsBetweenBatchesGoesOnBehindTheLastBatchAndWritesNoRowTwice()
    {
        var zip = FixtureContainers.Zip(Enumerable.Range(0, 450).Select(i => ($"web-01/nginx/part-{i:D3}.log", Encoding.ASCII.GetBytes($"{1700000000000 + i} line of file {i}\n"))).ToArray());
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("many.zip", zip)), Ct);

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var first = h.Runner();
            first.Expander.AfterBatch = done =>
            {
                if (done == ArchiveExpander.BatchSize)
                {
                    stop.Cancel();
                }
            };
            await first.RunAsync(stop.Token);
        }

        await using var db = h.Open();
        var container = await h.FileAsync(arrived.File!.Id);
        Assert.Equal((FileState.Received, ArchiveExpander.BatchSize), (container.State, container.ExpandedMembers));
        Assert.Equal(ArchiveExpander.BatchSize, await db.Files.CountAsync(f => f.ParentFileId == container.Id, Ct));
        Assert.Equal(JobState.Queued, (await h.JobAsync(arrived.Job!.Id)).State);

        await h.Runner().RunAsync(Ct);

        Assert.Equal(FileState.Expanded, (await h.FileAsync(container.Id)).State);
        var members = await db.Files.AsNoTracking().Where(f => f.ParentFileId == container.Id).ToListAsync(Ct);
        Assert.Equal(450, members.Count);
        Assert.Equal(450, members.Select(m => m.MemberOrdinal).Distinct().Count());
        Assert.All(members, m => Assert.Equal(FileState.Parsed, m.State));
        var audit = await h.AuditAsync();
        Assert.Equal(451, audit.Count(a => a.Action == AuditActions.FileIntake));
        Assert.Equal(0, audit.Count(a => a.Action == AuditActions.FileDuplicate));
        Assert.Equal(450, await db.Jobs.CountAsync(j => j.Kind == JobKind.Parse && j.State == JobState.Done, Ct));
    }

    [Fact]
    public async Task TheSameBytesUnderTwoHostsAreOneRecordThatListsBothPlaces()
    {
        var same = Lines(50, 20);
        var zip = FixtureContainers.Zip(("web-01/nginx/access.log", same), ("web-02/nginx/access.log", same), ("web-02/nginx/error.log", Lines(5, 21)));
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("two-hosts.zip", zip)), Ct);

        await h.Runner().RunAsync(Ct);

        await using var db = h.Open();
        var members = await db.Files.AsNoTracking().Where(f => f.ParentFileId == arrived.File!.Id).OrderBy(f => f.Id).ToListAsync(Ct);
        Assert.Equal(2, members.Count);
        var kept = members[0];
        Assert.Equal("two-hosts.zip!web-01/nginx/access.log", kept.ArchivePath);
        Assert.Contains("\"path\":\"two-hosts.zip!web-02/nginx/access.log\"", kept.SeenPaths, StringComparison.Ordinal);
        Assert.Contains("\"host\":\"web-02\"", kept.SeenPaths, StringComparison.Ordinal);
        var row = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.FileDuplicate);
        Assert.Equal(kept.Id, row.TargetId);
        Assert.Contains("\"arrival_archive_path\":\"two-hosts.zip!web-02/nginx/access.log\"", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"arrival_host\":\"web-02\"", row.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(1, await db.Jobs.CountAsync(j => j.FileId == kept.Id, Ct));
        Assert.Contains("1 duplicates", (await h.JobAsync(arrived.Job!.Id)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompressedMemberThatIsCutShortIsParsedUpToTheCutAndFlagged()
    {
        var content = Lines(4000, 30);
        var whole = Gzip(content);
        var cut = whole[..(whole.Length / 2)];
        var tar = FixtureContainers.Tar(("web-01/nginx/access.log.1.gz", cut), ("web-01/nginx/access.log.2.gz", Gzip(Lines(30, 31))));
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("logs.tar", tar)), Ct);

        await h.Runner().RunAsync(Ct);

        await using var db = h.Open();
        var members = await db.Files.AsNoTracking().Where(f => f.ParentFileId == arrived.File!.Id).OrderBy(f => f.MemberOrdinal).ToListAsync(Ct);
        Assert.Equal((FileState.Parsed, true, (long)cut.Length), (members[0].State, members[0].InputTruncated, members[0].TruncatedStoredOffset!.Value));
        Assert.InRange(members[0].RowCount, 1000, 3999);
        Assert.Equal(members[0].RowCount, h.Events.Visible((ulong)members[0].Id));
        Assert.Equal((FileState.Parsed, false, 30L), (members[1].State, members[1].InputTruncated, members[1].RowCount));
        var row = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.InputTruncated);
        Assert.Equal(members[0].Id, row.TargetId);
        Assert.Contains("\"archive_path\":\"logs.tar!web-01/nginx/access.log.1.gz\"", row.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompressedTarThatIsCutShortYieldsTheMembersBeforeTheCutAndSaysSo()
    {
        var tar = FixtureContainers.Tar(("web-01/nginx/a.log", Lines(3000, 40)), ("web-01/nginx/b.log", Lines(3000, 41)), ("web-01/nginx/c.log", Lines(3000, 42)));
        var whole = Gzip(tar);
        var cut = whole[..(whole.Length * 5 / 6)]; // well inside the third member's data
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("cut.tar.gz", cut)), Ct);

        await h.Runner().RunAsync(Ct);

        var job = await h.JobAsync(arrived.Job!.Id);
        Assert.True(job.State == JobState.Done, job.Message);
        var container = await h.FileAsync(arrived.File!.Id);
        Assert.Equal((FileState.Expanded, true, (long)cut.Length), (container.State, container.InputTruncated, container.TruncatedStoredOffset!.Value));
        Assert.Contains("truncated", job.Message, StringComparison.Ordinal);
        await using var db = h.Open();
        var members = await db.Files.AsNoTracking().Where(f => f.ParentFileId == container.Id).OrderBy(f => f.MemberOrdinal).ToListAsync(Ct);
        Assert.Equal(3, members.Count);
        Assert.All(members.SkipLast(1), whole => Assert.Equal((FileState.Parsed, false, false, 3000L), (whole.State, whole.InputTruncated, whole.ContainerCut, whole.RowCount)));

        // The member the cut falls in is there with the bytes that are there, and says it is not whole.
        var last = members[^1];
        Assert.Equal((FileState.Parsed, true, true), (last.State, last.ContainerCut, last.InputTruncated));
        Assert.Equal(last.Size, last.TruncatedStoredOffset);
        Assert.InRange(last.RowCount, 1, 2999);
        Assert.Equal(last.RowCount, h.Events.Visible((ulong)last.Id));
        Assert.Contains("ends inside the data of entry", job.Message, StringComparison.Ordinal);
        Assert.Contains(await h.AuditAsync(), a => a.Action == AuditActions.InputTruncated && a.TargetId == last.Id && a.DetailsJson.Contains("\"cut_by\":\"container\"", StringComparison.Ordinal));

        var audit = await h.AuditAsync();
        Assert.Contains(audit, a => a.Action == AuditActions.InputTruncated && a.TargetId == container.Id);
    }

    [Fact]
    public async Task APacketCaptureIsRefusedAloneAndInsideAnArchiveAndLeavesOnlyAnAuditRow()
    {
        byte[] pcap = [0xD4, 0xC3, 0xB2, 0xA1, 2, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 1, 0, 0, 0, .. new byte[64]];
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");

        var alone = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("capture.pcap", pcap)), Ct);
        var zip = FixtureContainers.Zip(("net/capture.pcap", [.. pcap, 1]), ("web-01/nginx/access.log", Lines(5, 50)));
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("mixed.zip", zip)), Ct);
        await h.Runner().RunAsync(Ct);

        Assert.True(alone.Rejected);
        Assert.Null(alone.File);
        await using var db = h.Open();
        Assert.Equal(2, await db.Files.CountAsync(Ct)); // the zip and its log
        var rows = (await h.AuditAsync()).Where(a => a.Action == AuditActions.FileRejected).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((AuditTargets.Case, @case.Id), (r.TargetType, r.TargetId)));
        Assert.Contains($"\"sha256\":\"{Sha(pcap)}\"", rows[0].DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"archive_path\":\"mixed.zip!net/capture.pcap\"", rows[1].DetailsJson, StringComparison.Ordinal);
        Assert.Contains("1 refused", (await h.JobAsync(arrived.Job!.Id)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContainerChangedAfterIntakeIsQuarantinedAndNoMemberIsRecorded()
    {
        var zip = FixtureContainers.Zip(("web-01/nginx/access.log", Lines(5, 60)));
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var path = await h.WriteAsync("a.zip", zip);
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, path), Ct);
        var tampered = (byte[])zip.Clone();
        tampered[40] ^= 0x20; // a byte of the stored member's content
        await File.WriteAllBytesAsync(path, tampered, Ct);

        await h.Runner().RunAsync(Ct);

        Assert.Equal(FileState.Quarantined, (await h.FileAsync(arrived.File!.Id)).State);
        await using var db = h.Open();
        Assert.Equal(1, await db.Files.CountAsync(Ct));
        Assert.Contains(await h.AuditAsync(), a => a.Action == AuditActions.IntegrityMismatch);
    }

    [Fact]
    public async Task ACacheCopyThatIsGoneIsWrittenAgainAndAChangedOneIsCaughtByTheMembersOwnHash()
    {
        var tar = FixtureContainers.Tar(("web-01/nginx/access.log", Lines(200, 70)));
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("a.tar.gz", Gzip(tar))), Ct);
        var runner = h.Runner();
        runner.Expander.AfterBatch = _ =>
        {
            // The members are recorded and their parses are queued: lose the copy before any of them runs.
            foreach (var copy in Directory.EnumerateFiles(h.CacheRoot, "*.body", SearchOption.AllDirectories))
            {
                File.SetAttributes(copy, FileAttributes.Normal);
                File.Delete(copy);
            }
        };

        await runner.RunAsync(Ct);

        await using var db = h.Open();
        var member = await db.Files.AsNoTracking().SingleAsync(f => f.ParentFileId == arrived.File!.Id, Ct);
        Assert.Equal((FileState.Parsed, 200L), (member.State, member.RowCount));
        var body = Assert.Single(Directory.EnumerateFiles(h.CacheRoot, "*.body", SearchOption.AllDirectories));
        Assert.True(EvidencePermissions.IsReadOnly(body));

        // The copy is not evidence and nobody trusts it: a changed byte fails the member's parse at its own hash.
        File.SetAttributes(body, FileAttributes.Normal);
        var bytes = await File.ReadAllBytesAsync(body, Ct);
        bytes[600] ^= 0x01;
        await File.WriteAllBytesAsync(body, bytes, Ct);
        var again = await JobOperations.EnqueueAsync(db, member.Id, DateTime.UtcNow, Ct);
        await h.Runner().RunAsync(Ct);
        Assert.Equal(JobState.Failed, (await h.JobAsync(again.Id)).State);
        Assert.Equal(FileState.Quarantined, (await h.FileAsync(member.Id)).State);
    }

    [Fact]
    public async Task AnEntryTheZipCannotDeliverIsRecordedAsUnreadableWithAHashOfItsStoredBytes()
    {
        // The runtime's zip reader checks no CRC and hands out an encrypted entry's bytes as if they were content,
        // so all three of these would otherwise be parsed or silently dropped.
        var good = Lines(20, 90);
        var secret = Lines(21, 91);
        var odd = Lines(22, 92);
        var broken = Lines(23, 93);
        var zip = FixtureContainers.Zip(("web-01/nginx/good.log", good), ("web-01/nginx/secret.log", secret), ("web-01/nginx/odd.log", odd), ("web-01/nginx/broken.log", broken), ("web-01/nginx/last.log", Lines(5, 94)));
        var locals = Offsets(zip, [0x50, 0x4B, 3, 4]);
        var centrals = Offsets(zip, [0x50, 0x4B, 1, 2]);
        zip[locals[1] + 6] |= 1;        // general purpose flag bit 0: encrypted
        zip[centrals[1] + 8] |= 1;
        zip[locals[2] + 8] = 99;        // a compression method the runtime does not have
        zip[centrals[2] + 10] = 99;
        var brokenAt = locals[3] + 30 + "web-01/nginx/broken.log".Length + 7;
        zip[brokenAt] ^= 0x01;          // one content byte: only the CRC can tell
        var brokenStored = (byte[])broken.Clone();
        brokenStored[7] ^= 0x01;

        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("mixed.zip", zip)), Ct);

        await h.Runner().RunAsync(Ct);

        var job = await h.JobAsync(arrived.Job!.Id);
        Assert.True(job.State == JobState.Done, job.Message);
        Assert.Contains("3 unreadable", job.Message, StringComparison.Ordinal);
        await using var db = h.Open();
        var members = await db.Files.AsNoTracking().Where(f => f.ParentFileId == arrived.File!.Id).OrderBy(f => f.MemberOrdinal).ToListAsync(Ct);
        Assert.Equal([FileState.Parsed, FileState.Unreadable, FileState.Unreadable, FileState.Unreadable, FileState.Parsed], members.Select(m => m.State).ToArray());
        foreach (var (member, stored, reason) in new[] { (members[1], secret, "encrypted"), (members[2], odd, "unsupported compression method"), (members[3], brokenStored, "CRC-32 mismatch") })
        {
            Assert.True(member.HashOfStoredBytes);
            Assert.Equal((Sha(stored), (long)stored.Length, (long?)stored.Length), (member.Sha256, member.Size, member.StatedSize));
            Assert.Contains(reason, member.UnreadableReason, StringComparison.Ordinal);
            Assert.StartsWith("mixed.zip!web-01/nginx/", member.ArchivePath, StringComparison.Ordinal);
            Assert.Equal(0, await db.Jobs.CountAsync(j => j.FileId == member.Id, Ct));
            Assert.Equal(0, h.Events.Visible((ulong)member.Id));
            await Assert.ThrowsAsync<JobRefusedException>(() => JobOperations.EnqueueAsync(db, member.Id, DateTime.UtcNow, Ct));
        }

        Assert.Contains("zip method 99", members[2].UnreadableReason, StringComparison.Ordinal);
        Assert.All([members[0], members[4]], whole => Assert.False(whole.HashOfStoredBytes));
        var rows = (await h.AuditAsync()).Where(a => a.Action == AuditActions.FileIntake && a.DetailsJson.Contains("\"hash_of\":\"stored_bytes\"", StringComparison.Ordinal)).ToList();
        Assert.Equal(members.Skip(1).Take(3).Select(m => m.Id).ToArray(), rows.Select(r => r.TargetId!.Value).ToArray());
        Assert.All(rows, r => Assert.Contains("\"unreadable\":", r.DetailsJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AYearGivenForAnArchiveHoldsForItsMembersAndADerivedOneDoesNot()
    {
        byte[] Archive(int seed) => Gzip(FixtureContainers.Tar(("web-01/var/log/messages", Lines(10, seed)), ("web-01/var/log/secure", Lines(10, seed + 1))));
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var givenPath = await h.WriteAsync("given.tar.gz", Archive(100));
        var derivedPath = await h.WriteAsync("derived.tar.gz", Archive(110));
        File.SetLastWriteTimeUtc(derivedPath, new DateTime(2020, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var given = await h.Intake().AcceptAsync(Request(@case.Id, givenPath) with { Year = 2019, Timezone = "+03:00" }, Ct);
        var derived = await h.Intake().AcceptAsync(Request(@case.Id, derivedPath), Ct);
        await h.Runner().RunAsync(Ct);

        Assert.Equal((2019, true, false), (given.File!.YearHint, given.File.YearGiven, given.File.YearAssumed));
        Assert.Equal((2020, false, false), (derived.File!.YearHint, derived.File.YearGiven, derived.File.YearAssumed));
        await using var db = h.Open();
        var ofGiven = await db.Files.AsNoTracking().Where(f => f.ParentFileId == given.File.Id).ToListAsync(Ct);
        var ofDerived = await db.Files.AsNoTracking().Where(f => f.ParentFileId == derived.File.Id).ToListAsync(Ct);
        Assert.Equal(2, ofGiven.Count);
        Assert.All(ofGiven, m => Assert.Equal((2019, true, "+03:00"), (m.YearHint, m.YearGiven, m.Timezone)));

        // Each member of the other archive has the year of its own entry time, not the 2020 of the archive's file.
        Assert.Equal(2, ofDerived.Count);
        Assert.All(ofDerived, m => Assert.Equal((m.FileTime!.Value.Year, false), (m.YearHint, m.YearGiven)));
        Assert.All(ofDerived, m => Assert.NotEqual(2020, m.YearHint));
    }

    private static int[] Offsets(byte[] bytes, byte[] signature)
    {
        var found = new List<int>();
        for (var i = 0; i + signature.Length <= bytes.Length; i++)
        {
            if (bytes.AsSpan(i, signature.Length).SequenceEqual(signature))
            {
                found.Add(i);
            }
        }

        return [.. found];
    }

    private static IntakeRequest Request(long caseId, string path, string? host = null) =>
        new() { CaseId = caseId, Path = path, Source = FileSource.Cli, Uploader = "alice", Host = host };

    /// <summary>Lines the numbered-line source parses: a millisecond time and some text.</summary>
    private static byte[] Lines(int count, int seed)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append(1_700_000_000_000 + (seed * 1_000_000L) + i).Append(" line ").Append(i).Append(" of file ").Append(seed).Append('\n');
        }

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static byte[] Gzip(byte[] content)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
