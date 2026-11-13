using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Detection;
using Urlog.Core.Intake;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Tests.Intake;

/// <summary>
/// One file through intake: hashed before anything else, the same bytes once per case, record and job in one
/// step, the hints, what is kept and not parsed, what is refused; the gate that turns new bytes away; and the
/// cache copy, which is never evidence.
/// </summary>
public sealed class IntakePipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSameBytesTwiceInOneCaseAreOneRecordOneDuplicateRowAndOneJob()
    {
        var content = Encoding.ASCII.GetBytes("1700000000001 a\n1700000000002 b\n");
        await using var h = await RunnerHarness.CreateAsync();
        var one = await h.AddCaseAsync("one");
        var two = await h.AddCaseAsync("two");

        var first = await h.Intake().AcceptAsync(Request(one.Id, await h.WriteAsync("a.log", content)), Ct);
        var again = await h.Intake().AcceptAsync(Request(one.Id, await h.WriteAsync("copy-of-a.log", content), host: "web-09"), Ct);
        var other = await h.Intake().AcceptAsync(Request(two.Id, await h.WriteAsync("a-for-two.log", content)), Ct);

        Assert.Equal((false, true, false), (first.Duplicate, again.Duplicate, other.Duplicate));
        Assert.Equal(first.File!.Id, again.File!.Id);
        Assert.Null(again.Job);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), first.Sha256);
        await using var db = h.Open();
        Assert.Equal((2, 2), (await db.Files.CountAsync(Ct), await db.Jobs.CountAsync(Ct)));
        var audit = await h.AuditAsync();
        var duplicate = Assert.Single(audit, a => a.Action == AuditActions.FileDuplicate);
        Assert.Equal(first.File.Id, duplicate.TargetId);
        Assert.Contains("\"arrival_name\":\"copy-of-a.log\"", duplicate.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"arrival_host\":\"web-09\"", duplicate.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"copy-of-a.log\"", (await h.FileAsync(first.File.Id)).SeenPaths, StringComparison.Ordinal);

        await h.Runner().RunAsync(Ct);
        Assert.Equal((2, 2), (h.Events.Visible((ulong)first.File.Id), h.Events.Visible((ulong)other.File!.Id)));
    }

    [Fact]
    public async Task TheRecordAndItsJobAreWrittenTogetherAndAFileThatIsNotALogGetsNoJob()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");

        var log = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("app.log", "1700000000001 a\n"u8.ToArray())), Ct);
        var pdf = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("report.pdf", [.. "%PDF-1.7\n"u8, 0xE2, 0xE3, 0xCF, 0xD3, 10])), Ct);
        var empty = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("empty.log", [])), Ct);

        Assert.Equal((FileState.Received, JobKind.Parse), (log.File!.State, log.Job!.Kind));
        Assert.Equal((FileState.Artifact, FileKind.Artifact), (pdf.File!.State, pdf.Detection.Kind));
        Assert.Null(pdf.Job);
        Assert.Equal((FileState.Artifact, "empty"), (empty.File!.State, empty.Detection.Format));
        Assert.Null(empty.Job);
        await using var db = h.Open();
        Assert.Equal(1, await db.Jobs.CountAsync(Ct));
        Assert.Equal(3, (await h.AuditAsync()).Count(a => a.Action == AuditActions.FileIntake));
    }

    [Fact]
    public async Task TheHostIsTheGivenOneElseInferredFromTheNameElseUnassigned()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");

        var given = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("web-04_access.log", "1700000000001 a\n"u8.ToArray()), host: "Proxy-A"), Ct);
        var inferred = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("web-05_access.log", "1700000000002 a\n"u8.ToArray())), Ct);
        var none = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("access.log", "1700000000003 a\n"u8.ToArray())), Ct);

        Assert.Equal(("proxy-a", false, false), (given.Host, given.HostInferred, given.File!.HostInferred));
        Assert.Equal(("web-05", true), (inferred.Host, inferred.File!.HostInferred));
        Assert.False(string.IsNullOrEmpty(inferred.File.HostRule));
        Assert.Null(none.Host);
        Assert.Null(none.File!.HostId);
        Assert.Equal(JobKind.Parse, none.Job!.Kind); // unassigned is flagged, never blocked

        await h.Runner().RunAsync(Ct);
        Assert.Equal(FileState.Parsed, (await h.FileAsync(none.File.Id)).State);
    }

    [Fact]
    public async Task TheYearComesFromTheFilesOwnTimeAndAnUnknownZoneIsRefusedBeforeTheFileIsRead()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var path = await h.WriteAsync("messages", "Dec 30 23:59:59 host app: old year\nJan  2 00:00:01 host app: new year\n"u8.ToArray());
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        var accepted = await h.Intake().AcceptAsync(Request(@case.Id, path) with { Timezone = "+02:00" }, Ct);
        var explicitYear = await h.Intake().AcceptAsync(Request(@case.Id, await h.WriteAsync("other", "Dec 30 23:59:59 host app: x\n"u8.ToArray())) with { Year = 2019 }, Ct);

        Assert.Equal((2023, false, "+02:00"), (accepted.File!.YearHint, accepted.File.YearAssumed, accepted.File.Timezone));
        Assert.Equal(new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), accepted.File.FileTime);
        Assert.Equal(2019, explicitYear.File!.YearHint);
        var refused = await Assert.ThrowsAsync<IntakeRefusedException>(() => h.Intake().AcceptAsync(Request(@case.Id, path) with { Timezone = "Mars/Olympus" }, Ct));
        Assert.Contains("Mars/Olympus", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<IntakeRefusedException>(() => h.Intake().AcceptAsync(Request(@case.Id, path) with { OverrideSource = "no-such-source" }, Ct));
        await Assert.ThrowsAsync<IntakeRefusedException>(() => h.Intake().AcceptAsync(Request(@case.Id, Path.Combine(h.Root, "missing.log")), Ct));
    }

    [Fact]
    public async Task AFileUnderTheEvidenceRootIsMadeReadOnlyAndRecordedRelativeToItsCase()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var root = Path.Combine(h.Root, "intake");
        var path = await h.WriteAsync(Path.Combine("intake", @case.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "drop", "a.log"), "1700000000001 a\n"u8.ToArray());
        var elsewhere = await h.WriteAsync("elsewhere.log", "1700000000002 a\n"u8.ToArray());

        var under = await h.Intake(root).AcceptAsync(Request(@case.Id, path), Ct);
        var outside = await h.Intake(root).AcceptAsync(Request(@case.Id, elsewhere), Ct);

        Assert.Equal(Path.Combine("drop", "a.log"), under.File!.StoredPath);
        Assert.True(EvidencePermissions.IsReadOnly(path));
        Assert.Throws<UnauthorizedAccessException>(() => File.OpenWrite(path).Dispose());
        Assert.Equal(elsewhere, outside.File!.StoredPath);
        Assert.False(EvidencePermissions.IsReadOnly(elsewhere));

        // The runner finds the relative one through the same root, and reads a read-only file without trouble.
        await h.Runner(RunnerHarness.Options() with { EvidenceRoot = root, CacheRoot = h.CacheRoot }).RunAsync(Ct);
        Assert.Equal(FileState.Parsed, (await h.FileAsync(under.File.Id)).State);
        Assert.Equal(FileState.Parsed, (await h.FileAsync(outside.File.Id)).State);
    }

    [Fact]
    public async Task TheCommittedTruncatedGzipFixtureIsFlaggedFromIntakeToCommit()
    {
        var fixture = Path.Combine(RepositoryRoot(), "fixtures", "detection", "adversarial", "truncated.log.gz");
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var path = await h.WriteAsync("truncated.log.gz", await File.ReadAllBytesAsync(fixture, Ct));

        var arrived = await h.Intake().AcceptAsync(Request(@case.Id, path) with { OverrideSource = "test-lines" }, Ct);
        await h.Runner().RunAsync(Ct);

        var record = await h.FileAsync(arrived.File!.Id);
        Assert.Equal((FileState.Parsed, true, new FileInfo(fixture).Length), (record.State, record.InputTruncated, record.TruncatedStoredOffset!.Value));
        Assert.True(record.TruncatedDecodedOffset > 0);
        Assert.True(record.RowCount > 0); // a reject is a row too: every line before the cut is there
        Assert.Equal(record.RowCount, h.Events.Visible((ulong)record.Id));
        Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.InputTruncated && a.TargetId == record.Id);
    }

    [Fact]
    public async Task ACaseThatIsNotActiveTakesNoFile()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        await using (var db = h.Open())
        {
            await CaseOperations.TransitionAsync(db, h.Store.Audit, new AuditActor("alice"), @case.Id, CaseState.Closed, DateTime.UtcNow, Ct);
        }

        var path = await h.WriteAsync("a.log", "1 a\n"u8.ToArray());
        await Assert.ThrowsAsync<CaseNotActiveException>(() => h.Intake().AcceptAsync(Request(@case.Id, path), Ct));
        await using var check = h.Open();
        Assert.Equal(0, await check.Files.CountAsync(Ct));
    }

    [Fact]
    public async Task TheGateTurnsNewBytesAwayBelowTheMarkWhileTheQueueIsPausedAndForACaseThatIsNotActive()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var disk = new FakeDiskProbe { TotalBytes = 1000L << 30, FreeBytes = 150L << 30 };
        var gate = new IntakeGate(disk, new WatermarkOptions()); // the larger of 20 GB and 10 %: 100 GB here
        await using var db = h.Open();

        Assert.True((await gate.CheckAsync(db, @case.Id, h.Root, 10L << 30, Ct)).Accepted);
        var tooBig = await gate.CheckAsync(db, @case.Id, h.Root, 60L << 30, Ct);
        Assert.False(tooBig.Accepted);
        Assert.Contains("under the mark", tooBig.Reason, StringComparison.Ordinal);

        disk.FreeBytes = 500L << 30;
        await JobOperations.PauseAsync(db, h.Store.Audit, "test", new Dictionary<string, object?>(), Ct);
        var paused = await gate.CheckAsync(db, @case.Id, h.Root, 1, Ct);
        Assert.False(paused.Accepted);
        Assert.Contains("paused", paused.Reason, StringComparison.Ordinal);
        await JobOperations.ResumeAsync(db, h.Store.Audit, new Dictionary<string, object?>(), Ct);
        Assert.True((await gate.CheckAsync(db, @case.Id, h.Root, 1, Ct)).Accepted);

        Assert.False((await gate.CheckAsync(db, 9999, h.Root, 1, Ct)).Accepted);
        await CaseOperations.TransitionAsync(db, h.Store.Audit, new AuditActor("alice"), @case.Id, CaseState.Closed, DateTime.UtcNow, Ct);
        Assert.False((await gate.CheckAsync(db, @case.Id, h.Root, 1, Ct)).Accepted);
    }

    [Fact]
    public async Task ACacheCopyThatRunsTheVolumeUnderTheMarkIsRemovedAndItsJobWaits()
    {
        var big = new byte[6 << 20];
        new Random(80).NextBytes(big);
        var zip = Urlog.Sources.Tests.Detection.FixtureContainers.Zip(("inner.zip", Urlog.Sources.Tests.Detection.FixtureContainers.Zip(("blob.bin", big))));
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var container = new EvidenceFile { Id = 7, CaseId = @case.Id, Sha256 = new string('a', 64), Size = zip.Length, OriginalName = "x.tar.gz", StoredPath = "x", Uploader = "alice", Encoding = "utf8", ParentFileId = 1 };
        var disk = new FakeDiskProbe { TotalBytes = 1000L << 30, FreeBytes = 500L << 30 };
        var store = new MemberStore(null, h.CacheRoot, disk, new WatermarkOptions()) { SpaceCheckBytes = 1 << 20 };
        var written = new List<long>();

        var failure = Assert.Throws<CacheSpaceException>(() => store.BuildBody(container, new ContainerLayout(CompressionKind.None, ContainerFormat.Zip), new MemoryStream(zip), bytes =>
        {
            written.Add(bytes);
            if (bytes >= 3 << 20)
            {
                disk.FreeBytes = 50L << 30; // under the 100 GB mark from here on
            }
        }));

        Assert.Contains("under the mark", failure.Message, StringComparison.Ordinal);
        Assert.InRange(written[^1], 3 << 20, zip.Length - 1);
        Assert.False(Directory.EnumerateFiles(h.CacheRoot, "*", SearchOption.AllDirectories).Any(), "nothing of the copy is left");

        // With room it is written whole, read-only, and removed with its case.
        disk.FreeBytes = 500L << 30;
        var build = store.BuildBody(container, new ContainerLayout(CompressionKind.None, ContainerFormat.Zip), new MemoryStream(zip), _ => { });
        Assert.Equal((zip.Length, InputEndKind.Complete), (build.Bytes, build.End.Kind));
        Assert.True(EvidencePermissions.IsReadOnly(build.Path));
        Assert.StartsWith(h.CacheRoot, build.Path, StringComparison.Ordinal);
        store.RemoveCase(@case.Id);
        Assert.False(File.Exists(build.Path));
        store.RemoveCase(@case.Id);
    }

    [Fact]
    public void ATarWrittenByTheRuntimeWithLongNamesIsWalkedWithTheRightDataOffsets()
    {
        // Pax entries carry an extended header before the real one; the offsets must still point at the data.
        var directory = Directory.CreateTempSubdirectory("urlog-walker-");
        try
        {
            var path = Path.Combine(directory.FullName, "a.tar");
            var longName = string.Join('/', Enumerable.Repeat("a-long-directory-name", 9)) + "/access.log";
            var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["web-01/short.log"] = Encoding.ASCII.GetBytes(new string('s', 700)),
                [longName] = Encoding.ASCII.GetBytes(new string('l', 1500)),
                ["web-01/empty.log"] = [],
                ["web-01/last.log"] = Encoding.ASCII.GetBytes("the last one"),
            };
            using (var writer = new TarWriter(File.Create(path), TarEntryFormat.Pax, leaveOpen: false))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "web-01/"));
                foreach (var (name, data) in contents)
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
                }

                writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "web-01/link") { LinkName = "/etc/shadow" });
            }

            var seen = new List<ArchiveEntry>();
            var summary = ArchiveWalker.Walk(path, ContainerFormat.Tar, "a", (entry, open) =>
            {
                seen.Add(entry);
                if (entry.Regular)
                {
                    using var stream = open();
                    var read = new MemoryStream();
                    stream.CopyTo(read);
                    Assert.Equal(contents[entry.Name], read.ToArray());
                }
            });

            Assert.Equal((6, 2, false), (summary.Entries, summary.Skipped, summary.Truncated));
            Assert.Equal(4, seen.Count(e => e.Regular));
            Assert.All(seen.Where(e => !e.Regular), e => Assert.Null(e.Offset)); // a directory and a link: listed, never opened
            foreach (var entry in seen.Where(e => e.Regular))
            {
                using var again = ArchiveWalker.OpenMember(path, ContainerFormat.Tar, entry.Ordinal, entry.Offset, entry.Size);
                var read = new MemoryStream();
                again.CopyTo(read);
                Assert.Equal(contents[entry.Name], read.ToArray());
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void EvidenceIsOnlyEverOpenedForReading()
    {
        // The code that touches evidence lives in these directories. Nothing in them may open a file for writing or
        // delete one, except the member store, which writes and removes cache copies and nothing else.
        var root = RepositoryRoot();
        string[] directories = ["src/Core/Intake", "src/Core/Jobs", "src/Core/Detection", "src/Core/Pipeline"];
        string[] writes = ["FileAccess.Write", "FileAccess.ReadWrite", "FileMode.Create", "FileMode.Append", "FileMode.Truncate", "FileMode.OpenOrCreate", "File.Create", "File.OpenWrite", "File.WriteAll", "File.AppendAll", "File.Delete", "File.Move", "File.Copy", "File.Replace", "SetLength("];
        var offenders = new List<string>();
        foreach (var file in directories.SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories)))
        {
            var name = Path.GetFileName(file);
            foreach (var (line, number) in File.ReadLines(file).Select((line, index) => (line, index + 1)))
            {
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("public override void SetLength(", StringComparison.Ordinal))
                {
                    continue;
                }

                if (writes.Any(w => code.Contains(w, StringComparison.Ordinal)) && name is not ("MemberStore.cs" or "RunnerLock.cs"))
                {
                    offenders.Add($"{name}:{number}: {code}");
                }
            }
        }

        Assert.Empty(offenders);

        // And the member store writes under the cache root only: its two write sites name the temporary copy.
        var store = File.ReadAllText(Path.Combine(root, "src/Core/Intake/Archives/MemberStore.cs"));
        Assert.Equal(1, Count(store, "FileAccess.Write"));
        Assert.Contains("new FileStream(temp, FileMode.CreateNew, FileAccess.Write", store, StringComparison.Ordinal);
        Assert.Equal(1, Count(store, "File.Move("));
        Assert.Contains("File.Move(temp, path", store, StringComparison.Ordinal);
    }

    private static int Count(string text, string what) => text.Split(what).Length - 1;

    private static IntakeRequest Request(long caseId, string path, string? host = null) =>
        new() { CaseId = caseId, Path = path, Source = FileSource.Cli, Uploader = "alice", Host = host };

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "urlog.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("urlog.sln was not found above the test binaries");
    }
}
