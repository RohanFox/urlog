using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Ingest;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Integration.Tests;

/// <summary>
/// Archive and re-import against a real event store, through the <c>urlog</c> commands: a case of two hosts and
/// five files, one of them an archive inside an archive, goes into an archive and comes back with the same
/// records and the same rows; and a process killed inside either is finished by the next start of a runner.
/// </summary>
public sealed class ArchiveTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACaseGoesIntoAnArchiveAndComesBackWithTheSameRecordsAndTheSameRows()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var scene = await Scene.BuildAsync(h, output);
        var before = await h.VisibleAsync(scene.CaseId);
        var records = await scene.RecordsAsync();
        Assert.Equal(5, records.Count(r => r.ParentFileId is null));
        Assert.True(records.Count > 5, "the nested archive has members of its own");
        Assert.True(before.Rows > 0);
        Assert.Equal(before.Rows, records.Sum(r => r.RowCount));
        // The scene's worker computed the case's field statistics behind its parses.
        var statsBefore = await h.FieldStatsAsync(scene.CaseId);
        Assert.Equal(before.Rows, statsBefore.Single(r => r.Field.Length == 0).Count);

        // Archive.
        var archive = await scene.CliAsync("case", "archive", "--case", scene.Case);
        output.WriteLine(archive.ToString());
        Assert.Equal(0, archive.ExitCode);
        Assert.Contains($"case {scene.CaseId}: archived to", archive.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("flagged", archive.Output, StringComparison.Ordinal);
        var directory = Directory.EnumerateDirectories(CaseFolders.CaseDirectory(scene.Archives, scene.CaseId)).Single();
        Assert.Equal([ArchiveManifest.AuditName, ArchiveManifest.TarName, ArchiveManifest.ManifestName], Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var manifest = ArchiveManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, ArchiveManifest.ManifestName), Ct));
        Assert.Equal(5, manifest.Entries.Count);
        Assert.Equal(records.Count, manifest.Files.Count);
        Assert.Contains(manifest.TarSha256, archive.Output, StringComparison.Ordinal);

        // No part of the case in the store, nothing to read, its directory empty, its cache gone; the neighbour whole.
        Assert.Empty(await scene.PartitionsAsync(scene.CaseId));
        Assert.Equal(0, (await h.VisibleAsync(scene.CaseId)).Rows);
        Assert.Empty(await h.FieldStatsAsync(scene.CaseId));
        Assert.Equal(0, await h.HistogramRowsAsync(scene.CaseId));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scene.CaseDirectory));
        Assert.False(Directory.Exists(CaseFolders.CaseDirectory(scene.Evidence + "-cache", scene.CaseId)));
        Assert.Equal(Scene.KeptRows, (await h.VisibleAsync(scene.Kept)).Rows);
        Assert.Equal(CaseState.Archived, await scene.StateAsync());
        var usage = await scene.CliAsync("case", "usage", "--case", scene.Case);
        output.WriteLine(usage.Output);
        Assert.Contains(" archived ", usage.Output, StringComparison.Ordinal);

        // The sweep, by hand, reads the archive and finds it as committed.
        var sweep = await h.RunCliAsync(null, "sweep", "--db", h.Store.Path, "--evidence-root", scene.Evidence, "--archive-root", scene.Archives);
        output.WriteLine(sweep.ToString());
        Assert.Equal(0, sweep.ExitCode);

        // Re-import: verified and placed by the command, parsed and closed by a runner.
        var reimport = await scene.CliAsync("case", "reimport", "--case", scene.Case);
        output.WriteLine(reimport.ToString());
        Assert.Equal(0, reimport.ExitCode);
        Assert.Contains("every file verified and back in place", reimport.Output, StringComparison.Ordinal);
        Assert.Equal(CaseState.Archived, await scene.StateAsync());
        Assert.Equal(0, (await h.VisibleAsync(scene.CaseId)).Rows);
        var worker = h.StartWorker("--once", "--evidence-root", scene.Evidence, "--archive-root", scene.Archives);
        Assert.Equal(0, await worker.WaitForExitAsync(300));
        output.WriteLine(worker.Output);
        Assert.Contains($"case {scene.CaseId}: re-imported and closed", worker.Output, StringComparison.Ordinal);

        Assert.Equal(CaseState.Closed, await scene.StateAsync());
        var after = await scene.RecordsAsync();
        Assert.Equal(records.Select(r => (r.Id, r.Sha256, r.Size, r.StoredPath, r.ParentFileId, r.State, r.RowCount)), after.Select(r => (r.Id, r.Sha256, r.Size, r.StoredPath, r.ParentFileId, r.State, r.RowCount)));
        var visible = await h.VisibleAsync(scene.CaseId);
        Assert.Equal((before.Rows, 1L), (visible.Rows, visible.Generations));
        // The stored histogram went with the partitions, and the parses of the re-import wrote it again.
        var histogram = await h.HistogramAsync(scene.CaseId);
        Assert.Equal((0L, before.Rows), (histogram.Differing, histogram.Events));
        // The statistics went with the partitions and are rebuilt from the re-imported rows by the same runner:
        // the same values and counts, in a newer set.
        var statsAfter = await h.FieldStatsAsync(scene.CaseId);
        Assert.Equal(statsBefore.Select(r => (r.Field, r.Value, r.Count)), statsAfter.Select(r => (r.Field, r.Value, r.Count)));
        Assert.True(statsAfter[0].ComputedAt > statsBefore[0].ComputedAt, $"{statsAfter[0].ComputedAt:O} after {statsBefore[0].ComputedAt:O}");
        await using (var db = h.Store.CreateDbContext())
        {
            var state = await db.CaseStats.AsNoTracking().SingleAsync(s => s.CaseId == scene.CaseId, Ct);
            Assert.Equal((CaseStatsState.Ok, before.Rows), (state.State, state.Events));
            Assert.Null(state.DirtySince);
            var row = await db.Audit.AsNoTracking().SingleAsync(a => a.Action == AuditActions.CaseReimport, Ct);
            Assert.Contains("\"failed_files\":[]", row.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("\"row_differences\":[]", row.DetailsJson, StringComparison.Ordinal);
        }

        Assert.Equal(Scene.KeptRows, (await h.VisibleAsync(scene.Kept)).Rows);
        var chain = await AuditVerifier.VerifyAsync(h.Store.Path, Ct);
        Assert.True(chain.Ok, chain.Detail);
    }

    [Fact]
    public async Task AChangedByteInTheArchiveRefusesTheReimportAndTheCommandSaysWhichFile()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var scene = await Scene.BuildAsync(h, output);
        Assert.Equal(0, (await scene.CliAsync("case", "archive", "--case", scene.Case)).ExitCode);
        var directory = Directory.EnumerateDirectories(CaseFolders.CaseDirectory(scene.Archives, scene.CaseId)).Single();
        var tar = Path.Combine(directory, ArchiveManifest.TarName);

        // One byte of one member of the tar, the tar written anew with valid compression.
        var entries = new List<(string Name, byte[] Data)>();
        using (var file = File.OpenRead(tar))
        using (var zstd = new ZstdSharp.DecompressionStream(file))
        using (var reader = new TarReader(zstd))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                using var data = new MemoryStream();
                entry.DataStream!.CopyTo(data);
                entries.Add((entry.Name, data.ToArray()));
            }
        }

        var victim = entries[2];
        victim.Data[victim.Data.Length / 2] ^= 0x01;
        File.SetAttributes(tar, FileAttributes.Normal);
        using (var file = File.Create(tar))
        using (var zstd = new ZstdSharp.CompressionStream(file, 3))
        using (var writer = new TarWriter(zstd, TarEntryFormat.Pax))
        {
            foreach (var (name, data) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
            }
        }

        var reimport = await scene.CliAsync("case", "reimport", "--case", scene.Case);
        output.WriteLine(reimport.ToString());
        Assert.Equal(2, reimport.ExitCode);
        Assert.Contains("re-import refused, nothing was restored", reimport.Error, StringComparison.Ordinal);
        Assert.Contains($"'{victim.Name}' in the tar", reimport.Error, StringComparison.Ordinal);
        Assert.Equal(CaseState.Archived, await scene.StateAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(scene.CaseDirectory));
        Assert.Empty(await scene.PartitionsAsync(scene.CaseId));
        await using var db = h.Store.CreateDbContext();
        var fileId = long.Parse(victim.Name["files/".Length..], CultureInfo.InvariantCulture);
        var refused = await db.Audit.AsNoTracking().SingleAsync(a => a.Action == AuditActions.CaseReimportRefused, Ct);
        Assert.Contains($"\"file_id\":{fileId}", refused.DetailsJson, StringComparison.Ordinal);
        var mismatch = await db.Audit.AsNoTracking().SingleAsync(a => a.Action == AuditActions.IntegrityMismatch, Ct);
        Assert.Equal((AuditTargets.File, (long?)fileId), (mismatch.TargetType, mismatch.TargetId));
        Assert.Equal(0, await db.Jobs.CountAsync(j => j.CaseId == scene.CaseId && j.State == JobState.Queued, Ct));
    }

    [Theory]
    [InlineData("archive-built")]
    [InlineData("archive-commit")]
    [InlineData("archive-committed")]
    [InlineData("archive-store-done")]
    [InlineData("archive-files-done")]
    [InlineData("reimport-verified")]
    [InlineData("reimport-placed")]
    public async Task AProcessKilledInsideAnArchiveOrAReimportIsFinishedByTheNextRunner(string point)
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var scene = await Scene.BuildAsync(h, output, bigFileBytes: 48 << 20);
        var before = await h.VisibleAsync(scene.CaseId);
        var reimport = point.StartsWith("reimport", StringComparison.Ordinal);
        if (reimport)
        {
            Assert.Equal(0, (await scene.CliAsync("case", "archive", "--case", scene.Case)).ExitCode);
        }

        var archives = CaseFolders.CaseDirectory(scene.Archives, scene.CaseId);
        var staging = Path.Combine(scene.CaseDirectory, LifecycleExecutor.StagingFolder);
        // Where the command is to be killed is one write of its own, and that write is held from before the
        // command starts (PausePoint): everything the step did is on disk and in the store, the write is not
        // committed, and the command cannot get past it however long the test takes to look.
        var (table, when, held) = point switch
        {
            // The build is written; the verify step has not begun.
            "archive-built" => ("case_tasks", "NEW.step = 'verify' AND OLD.step IS NOT 'verify'", true),
            // Verified and renamed; the commit is not written.
            "archive-commit" => ("cases", "NEW.state = 'archived' AND OLD.state <> 'archived'", true),
            // Committed: killed as soon as the commit shows, wherever in the store step that is. The write of the
            // next step is held so that a slow test finds the command alive.
            "archive-committed" => ("case_tasks", "NEW.step = 'files' AND OLD.step IS NOT 'files'", false),
            // The partitions are dropped; the files step has not begun.
            "archive-store-done" => ("case_tasks", "NEW.step = 'files' AND OLD.step IS NOT 'files'", true),
            // The files are removed; the completion is not written.
            "archive-files-done" => ("case_tasks", "NEW.state = 'done' AND OLD.state <> 'done'", true),
            // Every file is verified into the staging folder; the place step has not begun.
            "reimport-verified" => ("case_tasks", "NEW.step = 'place' AND OLD.step IS NOT 'place'", true),
            // Every file is back in place; the parses are not queued.
            _ => ("case_tasks", "NEW.step = 'parse' AND OLD.step IS NOT 'parse'", true),
        };
        var pause = await PausePoint.ArmAsync(h.Store.Path, table, when, Ct);
        var command = h.StartCli("case", reimport ? "reimport" : "archive", "--db", h.Store.Path, "--case", scene.Case, "--evidence-root", scene.Evidence, "--archive-root", scene.Archives);
        if (held)
        {
            await pause.HeldAsync(command.Process, point, Ct);
        }
        else
        {
            await WorkerHarness.EventuallyAsync(async () => await scene.StateAsync() == CaseState.Archived || command.Process.HasExited, "the commit", pollMilliseconds: 1);
        }

        // What the step before the held write left, now a fact and not something to wait for.
        switch (point)
        {
            case "archive-built":
                Assert.True(Directory.EnumerateDirectories(archives, "*.partial").Any(), "the build still under its partial name");
                Assert.True(Directory.EnumerateFiles(archives, ArchiveManifest.ManifestName, SearchOption.AllDirectories).Any(), "the build written");
                break;
            case "archive-commit":
                Assert.True(Directory.EnumerateDirectories(archives).Any(d => !d.EndsWith(".partial", StringComparison.Ordinal)), "the archive verified and renamed");
                break;
            case "archive-store-done":
                Assert.Empty(await scene.PartitionsAsync(scene.CaseId));
                break;
            case "archive-files-done":
                Assert.Empty(Directory.EnumerateFileSystemEntries(scene.CaseDirectory));
                break;
            case "reimport-verified":
                Assert.Equal(6, Directory.EnumerateFiles(staging).Count());
                break;
            case "reimport-placed":
                Assert.True(!Directory.Exists(staging) && File.Exists(Path.Combine(scene.CaseDirectory, "big.log")), "the files placed");
                break;
        }

        Assert.False(command.Process.HasExited, "the command ended before the kill: " + command.Output);
        await command.KillAsync();
        await pause.DisarmAsync(Ct);

        // What the kill left behind.
        var state = await scene.StateAsync();
        var task = await scene.TaskAsync();
        var partitions = (await scene.PartitionsAsync(scene.CaseId)).Count;
        var filesLeft = Directory.EnumerateFiles(scene.CaseDirectory, "*", SearchOption.AllDirectories).Count();
        output.WriteLine($"{point}: killed with the case {state}, task {task?.Kind} {task?.State} at step {task?.Step}, {partitions} partition(s), {filesLeft} file(s) in the case directory");
        switch (point)
        {
            case "archive-built" or "archive-commit":
                // Not committed: nothing of the case is gone.
                Assert.Equal((CaseState.Closed, CaseTaskState.Running, 6), (state, task!.State, filesLeft));
                Assert.True(partitions > 0);
                Assert.Equal(before.Rows, (await h.VisibleAsync(scene.CaseId)).Rows);
                break;
            case "archive-committed":
                Assert.Equal((CaseState.Archived, CaseTaskState.Committed), (state, task!.State));
                break;
            case "archive-store-done":
                Assert.Equal((CaseState.Archived, CaseTaskState.Committed, 0, 6), (state, task!.State, partitions, filesLeft));
                break;
            case "archive-files-done":
                Assert.Equal((CaseState.Archived, CaseTaskState.Committed, 0, 0), (state, task!.State, partitions, filesLeft));
                break;
            default:
                Assert.Equal((CaseState.Archived, CaseTaskKind.Reimport, CaseTaskState.Running, 0), (state, task!.Kind, task.State, partitions));
                break;
        }

        if (state == CaseState.Archived)
        {
            // Whatever is gone, the archive the case was committed with is there and hashes as committed.
            var committed = await scene.ArchiveTaskAsync();
            Assert.Equal(committed.TarSha256, ArchiveReader.HashFile(Path.Combine(committed.ArchiveDirectory!, ArchiveManifest.TarName)).Sha256);
            Assert.Equal(committed.ManifestSha256, ArchiveReader.HashFile(Path.Combine(committed.ArchiveDirectory!, ArchiveManifest.ManifestName)).Sha256);
        }

        // The next start of a runner finishes it.
        var worker = h.StartWorker("--once", "--evidence-root", scene.Evidence, "--archive-root", scene.Archives);
        var exit = await worker.WaitForExitAsync(300);
        output.WriteLine(worker.Output);
        Assert.Equal(0, exit);
        if (reimport)
        {
            Assert.Equal(CaseState.Closed, await scene.StateAsync());
            Assert.Equal(before.Rows, (await h.VisibleAsync(scene.CaseId)).Rows);
            Assert.False(Directory.Exists(staging));
            Assert.Equal(6, Directory.EnumerateFiles(scene.CaseDirectory, "*", SearchOption.AllDirectories).Count());
        }
        else
        {
            Assert.Equal(CaseState.Archived, await scene.StateAsync());
            var done = await scene.ArchiveTaskAsync();
            Assert.Equal(CaseTaskState.Done, done.State);
            Assert.Empty(await scene.PartitionsAsync(scene.CaseId));
            Assert.Empty(Directory.EnumerateFileSystemEntries(scene.CaseDirectory));
            // One archive, the committed one: what an uncommitted attempt left is gone, under either name.
            Assert.Equal([done.ArchiveDirectory], Directory.EnumerateDirectories(archives));
            await using var db = h.Store.CreateDbContext();
            Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == AuditActions.CaseArchive, Ct));
            Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == AuditActions.CaseArchiveComplete, Ct));
        }

        Assert.Equal(Scene.KeptRows, (await h.VisibleAsync(scene.Kept)).Rows);
        var chain = await AuditVerifier.VerifyAsync(h.Store.Path, Ct);
        Assert.True(chain.Ok, chain.Detail);
    }

    /// <summary>
    /// A closed case with two hosts and five files under the evidence root, taken in and parsed by the real
    /// commands: four nginx logs and a zip that holds a compressed tar that holds a log. Beside it a case whose
    /// rows are put into the store directly and must be untouched throughout.
    /// </summary>
    private sealed class Scene
    {
        public const long KeptRows = 300;
        private readonly WorkerHarness _h;

        private Scene(WorkerHarness h)
        {
            _h = h;
            Evidence = Path.Combine(h.Folder, "evidence");
            Archives = Path.Combine(h.Folder, "archives");
        }

        public string Evidence { get; }

        public string Archives { get; }

        public long Kept { get; private set; }

        public long CaseId { get; private set; }

        public string Case => CaseId.ToString(CultureInfo.InvariantCulture);

        public string CaseDirectory => CaseFolders.CaseDirectory(Evidence, CaseId);

        public static async Task<Scene> BuildAsync(WorkerHarness h, ITestOutputHelper output, int bigFileBytes = 0)
        {
            var scene = new Scene(h);
            scene.Kept = (await h.AddCaseAsync("kept")).Id;
            scene.CaseId = (await h.AddCaseAsync("to archive")).Id;
            await using (var admin = h.Admin())
            {
                await admin.ExecuteAsync(
                    $"INSERT INTO events (case_id, file_id, parse_generation, ts, raw) SELECT {scene.Kept}, 4611686018427387905, 1, toDateTime64('2025-01-10 00:00:00', 3, 'UTC'), 'kept' FROM numbers({KeptRows})",
                    cancellationToken: Ct);
            }

            await h.EventStore().CommitFileGenerationsAsync([new(4611686018427387905UL, 1)], Ct);

            Directory.CreateDirectory(scene.CaseDirectory);
            var files = new List<(string Name, string? Host)>
            {
                ("access-a.log", "web-01"), ("access-b.log", "web-01"), ("access-c.log", "web-02"), ("access-d.log", "web-02"),
            };
            var seed = 0;
            foreach (var (name, _) in files)
            {
                await File.WriteAllBytesAsync(Path.Combine(scene.CaseDirectory, name), NginxLines(400 + 50 * seed, seed++), Ct);
            }

            // An archive inside an archive: bundle.zip > logs/inner.tar.gz > web-02/nginx/access.log.
            using (var inner = new MemoryStream())
            {
                using (var gzip = new GZipStream(inner, CompressionLevel.Fastest, leaveOpen: true))
                using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "web-02/nginx/access.log") { DataStream = new MemoryStream(NginxLines(700, 99)) });
                }

                await using var zipFile = File.Create(Path.Combine(scene.CaseDirectory, "bundle.zip"));
                using var zip = new ZipArchive(zipFile, ZipArchiveMode.Create);
                var entry = zip.CreateEntry("logs/inner.tar.gz", CompressionLevel.NoCompression);
                await using var stream = entry.Open();
                inner.Position = 0;
                await inner.CopyToAsync(stream, Ct);
            }

            files.Add(("bundle.zip", null));
            if (bigFileBytes > 0)
            {
                // Large enough that building, hashing and removing it takes a while a kill can land in.
                await File.WriteAllBytesAsync(Path.Combine(scene.CaseDirectory, "big.log"), NginxLines(bigFileBytes / 100, 7), Ct);
                files.Add(("big.log", "web-01"));
            }

            foreach (var (name, host) in files)
            {
                string[] hostArgs = host is null ? [] : ["--host", host];
                var enqueue = await h.RunCliAsync(null, ["enqueue", "--db", h.Store.Path, "--case", scene.Case, "--evidence-root", scene.Evidence, .. hostArgs, Path.Combine(scene.CaseDirectory, name), "--user", WorkerHarness.IngestUser, "--password", WorkerHarness.IngestPassword]);
                Assert.True(enqueue.ExitCode == 0, enqueue.ToString());
            }

            var worker = h.StartWorker("--once", "--evidence-root", scene.Evidence);
            var exit = await worker.WaitForExitAsync(300);
            Assert.True(exit == 0, worker.Output);
            var records = await scene.RecordsAsync();
            Assert.True(records.Where(r => r.State is not (FileState.Expanded or FileState.Artifact)).All(r => r.State == FileState.Parsed), string.Join(", ", records.Select(r => $"{r.OriginalName}:{r.State}")) + "\n" + worker.Output);
            output.WriteLine($"scene: {records.Count} file records, {records.Sum(r => r.RowCount)} rows");
            Assert.Equal(0, (await scene.CliAsync("case", "close", "--case", scene.Case)).ExitCode);
            return scene;
        }

        /// <summary>A lifecycle command of the case with both roots, as the ingest account.</summary>
        public Task<CliResult> CliAsync(string command, string verb, params string[] arguments) =>
            _h.RunCliAsync(null, [command, verb, "--db", _h.Store.Path, "--evidence-root", Evidence, "--archive-root", Archives, "--user", WorkerHarness.IngestUser, "--password", WorkerHarness.IngestPassword, .. arguments]);

        public async Task<List<EvidenceFile>> RecordsAsync()
        {
            await using var db = _h.Store.CreateDbContext();
            return await db.Files.AsNoTracking().Where(f => f.CaseId == CaseId).OrderBy(f => f.Id).ToListAsync(Ct);
        }

        public async Task<CaseState> StateAsync()
        {
            await using var db = _h.Store.CreateDbContext();
            return await db.Cases.AsNoTracking().Where(c => c.Id == CaseId).Select(c => c.State).SingleAsync(Ct);
        }

        /// <summary>The newest task of the case.</summary>
        public async Task<CaseTask?> TaskAsync()
        {
            await using var db = _h.Store.CreateDbContext();
            return await db.CaseTasks.AsNoTracking().Where(t => t.CaseId == CaseId).OrderByDescending(t => t.Id).FirstOrDefaultAsync(Ct);
        }

        public async Task<CaseTask> ArchiveTaskAsync()
        {
            await using var db = _h.Store.CreateDbContext();
            return await db.CaseTasks.AsNoTracking().Where(t => t.CaseId == CaseId && t.Kind == CaseTaskKind.Archive).OrderByDescending(t => t.Id).FirstAsync(Ct);
        }

        public async Task<IReadOnlyList<CasePartition>> PartitionsAsync(long caseId)
        {
            await using var admin = _h.Admin();
            return await new EventStoreAdmin(admin).CasePartitionsAsync((ulong)caseId, Ct);
        }

        /// <summary>Synthetic access-log lines in the combined format, spread over three months.</summary>
        private static byte[] NginxLines(int lines, int seed)
        {
            var text = new StringBuilder(lines * 100);
            string[] months = ["Jan", "Feb", "Mar"];
            for (var i = 0; i < lines; i++)
            {
                text.Append(CultureInfo.InvariantCulture, $"192.0.2.{(i + seed) % 250} - - [{1 + (i % 27):D2}/{months[i % 3]}/2026:10:{i % 60:D2}:{(i + seed) % 60:D2} +0000] \"GET /p/{seed}/{i} HTTP/1.1\" 200 {100 + (i % 900)} \"-\" \"fixture/1.0\"\n");
            }

            return Encoding.UTF8.GetBytes(text.ToString());
        }
    }
}
