using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Ingest;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Integration.Tests;

/// <summary>
/// The purge against a real event store, through the <c>urlog</c> commands: what is left of the case afterwards
/// (nothing, in either store or on disk), what is left of the case beside it (everything), and that a process
/// killed inside the purge is finished by the next start of a runner.
/// </summary>
public sealed class PurgeTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private const int Months = 24;
    private const int RowsPerMonth = 200;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APurgeLeavesNoPartNoStatisticsNoGenerationRowNoFileAndNoRecord()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var scene = await Scene.BuildAsync(h);
        Assert.Equal(Months, (await scene.PartitionsAsync(scene.Purged)).Count);

        var usage = await h.RunCliAsync(null, "case", "usage", "--db", h.Store.Path, "--case", Text(scene.Purged), "--evidence-root", scene.Evidence, "--user", WorkerHarness.IngestUser, "--password", WorkerHarness.IngestPassword);
        output.WriteLine(usage.Output);
        Assert.Equal(0, usage.ExitCode);
        Assert.Contains($" {Months * RowsPerMonth} ", usage.Output, StringComparison.Ordinal);

        var purge = await h.RunCliAsync(null, "case", "purge", "--db", h.Store.Path, "--case", Text(scene.Purged), "--evidence-root", scene.Evidence, "--user", WorkerHarness.IngestUser, "--password", WorkerHarness.IngestPassword);
        output.WriteLine(purge.ToString());
        Assert.Equal(0, purge.ExitCode);
        Assert.Contains($"case {scene.Purged}: purged", purge.Output, StringComparison.Ordinal);
        await scene.AssertPurgedAsync();

        // The backup was taken before the mark, and its hash is in the chain three times.
        await using (var db = h.Store.CreateDbContext())
        {
            var rows = await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.Backup || a.Action == AuditActions.CasePurge || a.Action == AuditActions.CasePurgeComplete).OrderBy(a => a.Seq).ToListAsync(Ct);
            Assert.Equal([AuditActions.Backup, AuditActions.CasePurge, AuditActions.CasePurgeComplete], rows.Select(r => r.Action));
            var backup = Directory.EnumerateFiles(Path.Combine(h.Folder, "backups"), "*.sqlite").Single();
            var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(backup, Ct)));
            Assert.All(rows, row => Assert.Contains(sha, row.DetailsJson, StringComparison.Ordinal));
        }

        // The ids are handed out again, and the new case and file see only their own rows.
        var next = await h.AddCaseAsync("next");
        Assert.Equal(scene.Purged, next.Id);
        var file = await scene.StoreAsync(next.Id, "next.log", "a new file\n");
        Assert.Contains(file.Id, scene.PurgedFiles);
        Assert.Equal(0, (await h.VisibleAsync(next.Id)).Rows);
        await using (var admin = h.Admin())
        {
            await admin.ExecuteAsync($"INSERT INTO events (case_id, file_id, parse_generation, ts, raw) SELECT {Text(next.Id)}, {Text(file.Id)}, 1, toDateTime64('2025-03-10 00:00:00', 3, 'UTC'), 'new' FROM numbers(7)", cancellationToken: Ct);
        }

        Assert.Equal(0, (await h.VisibleAsync(next.Id)).Rows);
        await h.EventStore().CommitFileGenerationsAsync([new((ulong)file.Id, 1)], Ct);
        Assert.Equal((7L, 1L, 1L), await h.VisibleAsync(next.Id));
    }

    [Fact]
    public async Task WithTwoPersonApprovalThePurgeWaitsForASecondAdministrator()
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        var scene = await Scene.BuildAsync(h);
        await using (var db = h.Store.CreateDbContext())
        {
            db.Users.Add(new User { Name = "ada", PasswordHash = "argon2id$placeholder", CreatedAt = DateTime.UtcNow, Role = UserRole.Admin });
            db.Users.Add(new User { Name = "bo", PasswordHash = "argon2id$placeholder", CreatedAt = DateTime.UtcNow, Role = UserRole.Admin });
            await db.SaveChangesAsync(Ct);
        }

        Task<CliResult> PurgeAs(string? name) => h.RunCliAsync(null,
        [
            "case", "purge", "--db", h.Store.Path, "--case", Text(scene.Purged), "--evidence-root", scene.Evidence, "--two-person",
            "--user", WorkerHarness.IngestUser, "--password", WorkerHarness.IngestPassword, .. name is null ? Array.Empty<string>() : ["--as", name],
        ]);

        var nobody = await PurgeAs(null);
        Assert.Equal(2, nobody.ExitCode);
        Assert.Contains("two administrator accounts", nobody.Error, StringComparison.Ordinal);
        var analyst = await PurgeAs("alice");
        Assert.Equal(2, analyst.ExitCode);
        Assert.Contains("it takes an administrator", analyst.Error, StringComparison.Ordinal);

        var first = await PurgeAs("ada");
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("it starts when a second administrator", first.Output, StringComparison.Ordinal);
        var again = await PurgeAs("ada");
        Assert.Equal(2, again.ExitCode);
        Assert.Contains("a second administrator has to confirm", again.Error, StringComparison.Ordinal);
        // Asked for and not confirmed: nothing is gone, and a runner does not take it either.
        var worker = h.StartWorker("--once", "--two-person", "--evidence-root", scene.Evidence);
        Assert.Equal(0, await worker.WaitForExitAsync(120));
        Assert.Equal(Months, (await scene.PartitionsAsync(scene.Purged)).Count);
        Assert.True(File.Exists(scene.StoredFile));

        var second = await PurgeAs("bo");
        output.WriteLine(second.ToString());
        Assert.Equal(0, second.ExitCode);
        await scene.AssertPurgedAsync();
        await using var check = h.Store.CreateDbContext();
        var mark = await check.Audit.AsNoTracking().SingleAsync(a => a.Action == AuditActions.CasePurge, Ct);
        Assert.Contains("\"requested_by\":\"ada\"", mark.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"confirmed_by\":\"bo\"", mark.DetailsJson, StringComparison.Ordinal);

        // Each request row names the account it was made as and the OS user that ran the command.
        var requests = await check.Audit.AsNoTracking().Where(a => a.Action == AuditActions.CasePurgeRequest).OrderBy(a => a.Seq).ToListAsync(Ct);
        Assert.Equal(["ada", "bo"], requests.Select(r => r.Actor));
        Assert.All(requests, row =>
        {
            Assert.NotNull(row.ActorId);
            Assert.Equal(Environment.UserName, System.Text.Json.JsonDocument.Parse(row.DetailsJson).RootElement.GetProperty("os_user").GetString());
        });
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("store-start")]
    [InlineData("store-done")]
    [InlineData("files-done")]
    public async Task AProcessKilledInsideAPurgeIsFinishedByTheNextRunner(string point)
    {
        await using var h = await WorkerHarness.CreateAsync(ch);
        // For the kill after the file step the directory holds enough files that removing them takes a while.
        var extra = point == "files-done" ? 4000 : 0;
        var scene = await Scene.BuildAsync(h, extraFiles: extra);
        // Where the command is to be killed is one write of its own, held from before the command starts
        // (PausePoint): what the step did is done, the write is not committed, and the command cannot pass it.
        var (table, when, held) = point switch
        {
            // The backup is written; the mark is not.
            "backup" => ("cases", "NEW.state = 'purging' AND OLD.state <> 'purging'", true),
            // Marked: killed as soon as the mark shows, wherever in the store step that is. The write of the next
            // step is held so that a slow test finds the command alive.
            "store-start" => ("case_tasks", "NEW.step = 'files' AND OLD.step IS NOT 'files'", false),
            // The partitions and the generation rows are gone; the files step has not begun.
            "store-done" => ("case_tasks", "NEW.step = 'files' AND OLD.step IS NOT 'files'", true),
            // The case directory is removed; the metadata step has not begun.
            _ => ("case_tasks", "NEW.step = 'metadata' AND OLD.step IS NOT 'metadata'", true),
        };
        var pause = await PausePoint.ArmAsync(h.Store.Path, table, when, Ct);
        var purge = h.StartCli("case", "purge", "--db", h.Store.Path, "--case", Text(scene.Purged), "--evidence-root", scene.Evidence);
        if (held)
        {
            await pause.HeldAsync(purge.Process, point, Ct);
        }
        else
        {
            await WorkerHarness.EventuallyAsync(async () => await scene.StateAsync() == CaseState.Purging || purge.Process.HasExited, "the mark", pollMilliseconds: 1);
        }

        // What the step before the held write left.
        switch (point)
        {
            case "backup":
                Assert.True(Directory.Exists(scene.Backups) && Directory.EnumerateFiles(scene.Backups, "*.sqlite").Any(), "the backup file written");
                break;
            case "store-done":
                Assert.Empty(await scene.PartitionsAsync(scene.Purged));
                Assert.Equal(0, await scene.GenerationRowsAsync());
                break;
            case "files-done":
                Assert.False(Directory.Exists(scene.CaseDirectory), "the case directory removed");
                break;
        }

        Assert.False(purge.Process.HasExited, "the command ended before the kill: " + purge.Output);
        await purge.KillAsync();
        await pause.DisarmAsync(Ct);

        // What the kill left behind.
        var state = await scene.StateAsync();
        var task = await scene.TaskAsync();
        var partitions = (await scene.PartitionsAsync(scene.Purged)).Count;
        var generations = await scene.GenerationRowsAsync();
        var filesLeft = Directory.Exists(scene.CaseDirectory) ? Directory.EnumerateFiles(scene.CaseDirectory, "*", SearchOption.AllDirectories).Count() : 0;
        output.WriteLine($"{point}: killed with the case {state}, task {task?.State} at step {task?.Step}, {partitions} of {Months} partitions, {generations} generation rows, {filesLeft} files in the case directory");
        await using (var db = h.Store.CreateDbContext())
        {
            Assert.Equal(2, await db.Files.CountAsync(f => f.CaseId == scene.Purged, Ct));
        }

        switch (point)
        {
            case "backup":
                Assert.Equal((CaseState.SoftDeleted, Months, 2L, 3 + extra), (state, partitions, generations, filesLeft));
                break;
            case "store-start":
                Assert.Equal((CaseState.Purging, 3), (state, filesLeft));
                break;
            case "store-done":
                Assert.Equal((CaseState.Purging, 0, 0L, 3), (state, partitions, generations, filesLeft));
                Assert.Equal(LifecycleOperations.StepStore, task!.Step);
                break;
            default:
                Assert.Equal((CaseState.Purging, 0, 0L, 0), (state, partitions, generations, filesLeft));
                break;
        }

        // The next start of a runner finishes it.
        var worker = h.StartWorker("--once", "--evidence-root", scene.Evidence);
        var exit = await worker.WaitForExitAsync(120);
        output.WriteLine(worker.Output);
        Assert.Equal(0, exit);
        if (point != "backup")
        {
            Assert.Contains($"case {scene.Purged} was left in purging by a stop", worker.Output, StringComparison.Ordinal);
        }

        Assert.Contains($"case {scene.Purged}: purged", worker.Output, StringComparison.Ordinal);
        await scene.AssertPurgedAsync();
        var chain = await AuditVerifier.VerifyAsync(h.Store.Path, Ct);
        Assert.True(chain.Ok, chain.Detail);
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A case that stays, and a newer one that is closed and soft-deleted: 24 months of rows, statistics, two
    /// stored files with their generation rows, an unfinished upload. Rows are put into the store directly; no
    /// parser is under test here.
    /// </summary>
    private sealed class Scene
    {
        private readonly WorkerHarness _h;

        private Scene(WorkerHarness h)
        {
            _h = h;
            Evidence = Path.Combine(h.Folder, "evidence");
            Backups = Path.Combine(h.Folder, "backups");
        }

        public string Evidence { get; }

        public string Backups { get; }

        public long Kept { get; private set; }

        public long KeptFile { get; private set; }

        public long Purged { get; private set; }

        public List<long> PurgedFiles { get; } = [];

        public string CaseDirectory => CaseFolders.CaseDirectory(Evidence, Purged);

        public string StoredFile => Path.Combine(CaseDirectory, "access.log");

        public static async Task<Scene> BuildAsync(WorkerHarness h, int extraFiles = 0)
        {
            var scene = new Scene(h);
            scene.Kept = (await h.AddCaseAsync("kept")).Id;
            scene.Purged = (await h.AddCaseAsync("to purge")).Id;
            scene.KeptFile = (await scene.StoreAsync(scene.Kept, "kept.log", "kept\n")).Id;
            scene.PurgedFiles.Add((await scene.StoreAsync(scene.Purged, "access.log", "one\ntwo\n")).Id);
            scene.PurgedFiles.Add((await scene.StoreAsync(scene.Purged, Path.Combine(CaseFolders.Web, "0123456789abcdef"), "an upload that arrived\n")).Id);
            Directory.CreateDirectory(Path.Combine(scene.CaseDirectory, CaseFolders.Uploads));
            await File.WriteAllTextAsync(Path.Combine(scene.CaseDirectory, CaseFolders.Uploads, "fedcba9876543210"), "half an upl", Ct);
            for (var i = 0; i < extraFiles; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(scene.CaseDirectory, CaseFolders.Uploads, $"{i:x16}"), "x", Ct);
            }

            await using (var admin = h.Admin())
            {
                await admin.ExecuteAsync("TRUNCATE TABLE field_stats", cancellationToken: Ct);
                await admin.ExecuteAsync("TRUNCATE TABLE case_histogram", cancellationToken: Ct);
                foreach (var (caseId, fileId, months) in new[] { (scene.Purged, scene.PurgedFiles[0], Months), (scene.Kept, scene.KeptFile, 2) })
                {
                    await admin.ExecuteAsync(
                        $"INSERT INTO events (case_id, file_id, parse_generation, ts, raw) SELECT {Text(caseId)}, {Text(fileId)}, 1, addMonths(toDateTime64('2024-01-10 00:00:00', 3, 'UTC'), number % {months}), 'row' FROM numbers({months * RowsPerMonth})",
                        cancellationToken: Ct);
                    await admin.ExecuteAsync($"INSERT INTO field_stats (case_id, field, value, count, computed_at) VALUES ({Text(caseId)}, 'host', 'web-01', 10, now64(3))", cancellationToken: Ct);
                    // The stored histogram of the file, as its parse would have written it.
                    await admin.ExecuteAsync(
                        "INSERT INTO case_histogram (case_id, file_id, parse_generation, severity, bucket, events) "
                        + $"SELECT case_id, file_id, parse_generation, severity, {Urlog.Core.Ingest.CaseScopedEventsQuery.HistogramBucket} AS bucket, count() FROM events "
                        + $"WHERE case_id = {Text(caseId)} AND file_id = {Text(fileId)} GROUP BY case_id, file_id, parse_generation, severity, bucket",
                        cancellationToken: Ct);
                }
            }

            await h.EventStore().CommitFileGenerationsAsync([.. scene.PurgedFiles.Append(scene.KeptFile).Select(id => new KeyValuePair<ulong, uint>((ulong)id, 1))], Ct);
            Assert.Equal(Months * RowsPerMonth, (await h.VisibleAsync(scene.Purged)).Rows);

            await using var db = h.Store.CreateDbContext();
            var actor = new AuditActor("alice");
            await LifecycleOperations.CloseAsync(db, h.Store.Audit, actor, scene.Purged, DateTime.UtcNow, Ct);
            await LifecycleOperations.SoftDeleteAsync(db, h.Store.Audit, actor, scene.Purged, "to purge", TimeSpan.FromDays(7), DateTime.UtcNow, Ct);
            return scene;
        }

        public async Task<EvidenceFile> StoreAsync(long caseId, string relative, string content)
        {
            var path = Path.Combine(CaseFolders.CaseDirectory(Evidence, caseId), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, Ct);
            Urlog.Core.Intake.EvidencePermissions.MakeReadOnly(path);
            var file = new EvidenceFile
            {
                CaseId = caseId, Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), Size = Encoding.UTF8.GetByteCount(content),
                OriginalName = Path.GetFileName(relative), StoredPath = relative.Replace('\\', '/'), Source = FileSource.Web, Uploader = "alice", ReceivedAt = DateTime.UtcNow, Encoding = "utf8",
            };
            await using var db = _h.Store.CreateDbContext();
            return (await FileOperations.AddAsync(db, _h.Store.Audit, new AuditActor("alice"), file, Ct)).File;
        }

        public async Task<CaseState?> StateAsync()
        {
            await using var db = _h.Store.CreateDbContext();
            return await db.Cases.AsNoTracking().Where(c => c.Id == Purged).Select(c => (CaseState?)c.State).SingleOrDefaultAsync(Ct);
        }

        public async Task<CaseTask?> TaskAsync()
        {
            await using var db = _h.Store.CreateDbContext();
            return await db.CaseTasks.AsNoTracking().Where(t => t.CaseId == Purged).OrderByDescending(t => t.Id).FirstOrDefaultAsync(Ct);
        }

        public async Task<IReadOnlyList<CasePartition>> PartitionsAsync(long caseId)
        {
            await using var admin = _h.Admin();
            return await new EventStoreAdmin(admin).CasePartitionsAsync((ulong)caseId, Ct);
        }

        public async Task<long> GenerationRowsAsync()
        {
            await using var admin = _h.Admin();
            return await new EventStoreAdmin(admin).CountFileGenerationsAsync(PurgedFiles.ConvertAll(id => (ulong)id), Ct);
        }

        public async Task AssertPurgedAsync()
        {
            Assert.Empty(await PartitionsAsync(Purged));
            Assert.Equal(0, await GenerationRowsAsync());
            await using (var admin = _h.Admin())
            {
                Assert.Equal(0L, Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM field_stats WHERE case_id = {Text(Purged)}", cancellationToken: Ct), CultureInfo.InvariantCulture));
                Assert.Equal(0L, Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM events WHERE case_id = {Text(Purged)}", cancellationToken: Ct), CultureInfo.InvariantCulture));
                Assert.Equal(0L, Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM case_histogram WHERE case_id = {Text(Purged)}", cancellationToken: Ct), CultureInfo.InvariantCulture));
                // The case beside it: its rows, its statistics, its generation row.
                Assert.Equal(1L, Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT count() FROM field_stats WHERE case_id = {Text(Kept)}", cancellationToken: Ct), CultureInfo.InvariantCulture));
                Assert.Equal(2L * RowsPerMonth, Convert.ToInt64(await admin.ExecuteScalarAsync($"SELECT sum(events) FROM case_histogram WHERE case_id = {Text(Kept)}", cancellationToken: Ct), CultureInfo.InvariantCulture));
            }

            Assert.Equal((2L * RowsPerMonth, 1L, 1L), await _h.VisibleAsync(Kept));
            Assert.False(Directory.Exists(CaseDirectory));
            Assert.True(File.Exists(Path.Combine(CaseFolders.CaseDirectory(Evidence, Kept), "kept.log")));
            await using var db = _h.Store.CreateDbContext();
            Assert.False(await db.Cases.AnyAsync(c => c.Id == Purged, Ct));
            Assert.Equal(0, await db.Files.CountAsync(f => f.CaseId == Purged, Ct));
            Assert.Equal(0, await db.Jobs.CountAsync(j => j.CaseId == Purged, Ct));
            Assert.Equal(0, await db.CaseMembers.CountAsync(m => m.CaseId == Purged, Ct));
            Assert.Equal(0, await db.CaseTasks.CountAsync(t => t.CaseId == Purged, Ct));
            Assert.Equal(CaseState.Active, await db.Cases.AsNoTracking().Where(c => c.Id == Kept).Select(c => c.State).SingleAsync(Ct));
            Assert.Equal(1, await db.Files.CountAsync(f => f.CaseId == Kept, Ct));
        }
    }
}
