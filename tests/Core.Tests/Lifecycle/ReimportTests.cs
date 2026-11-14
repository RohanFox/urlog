using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Urlog.Core.Audit;
using Urlog.Core.Intake;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Urlog.Core.Tests.Lifecycle;

/// <summary>
/// The re-import: the round trip back to a closed case with the same rows, a stop at every call, and every way
/// an archive can be wrong. Each refusal leaves the case archived, restores nothing, and writes nothing outside
/// the staging folder, which is removed.
/// </summary>
public sealed class ReimportTests
{
    private static CancellationToken Ct => RunnerHarness.Ct;

    [Fact]
    public async Task ARoundTripBringsBackEveryFileUnderItsRecordedNameAndTheSameRows()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var archive = await scene.ArchiveAsync();

        var task = await scene.RequestReimportAsync();
        Assert.Equal((archive.ArchiveDirectory, archive.TarSha256, archive.ManifestSha256), (task.ArchiveDirectory, task.TarSha256, task.ManifestSha256));
        var steps = new List<string>();
        Assert.False(await scene.Executor(onStep: steps.Add).ReimportAsync(task, Ct));
        Assert.Equal(["verify", "place", "queue"], steps);

        // Verified and in place, still archived: nothing of the case can be read while it is half back.
        Assert.Equal(CaseState.Archived, await scene.StateAsync());
        Assert.True(scene.Stored.All(f => File.Exists(scene.PathOf(f)) && ArchiveScene.Sha(File.ReadAllBytes(scene.PathOf(f))) == f.Sha256));
        Assert.All(scene.Stored, f => Assert.True(EvidencePermissions.IsReadOnly(scene.PathOf(f))));
        Assert.Equal(scene.Stored.Select(f => f.StoredPath.Replace('/', Path.DirectorySeparatorChar)).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(scene.CaseDirectory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(scene.CaseDirectory, p)).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(scene.CaseDirectory, LifecycleExecutor.StagingFolder)));
        // The file recorded outside the root was still there and still the same: it is the file, and no copy is made.
        Assert.False(Directory.Exists(Path.Combine(scene.CaseDirectory, LifecycleExecutor.RestoredFolder)));
        await using (var db = h.Open())
        {
            Assert.Equal(CaseTaskState.Committed, (await db.CaseTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id, Ct)).State);
            Assert.Equal(4, await db.Jobs.CountAsync(j => j.CaseId == scene.CaseId && j.State == JobState.Queued && j.Kind == JobKind.Reparse, Ct));
            Assert.Empty(await CaseAcl.AllowedCaseIdsAsync(db, (await db.Cases.AsNoTracking().SingleAsync(c => c.Id == scene.CaseId, Ct)).OwnerId, Ct).ContinueWith(t => t.Result.Where(id => id == scene.CaseId), Ct));
        }

        // A runner parses them, although the case is archived, because the re-import is under way; then it closes the case.
        await scene.Runner().RunAsync(Ct);
        Assert.Equal(CaseState.Closed, await scene.StateAsync());
        foreach (var file in scene.AllArrived)
        {
            Assert.Equal(scene.RowsBefore[file.Id], h.Events.Visible((ulong)file.Id));
            var record = await h.FileAsync(file.Id);
            Assert.Equal((FileState.Parsed, scene.RowsBefore[file.Id]), (record.State, record.RowCount));
            Assert.True(h.Events.Current((ulong)file.Id) > 1, "a re-import's parse is numbered above the generation the archive left");
        }

        var audit = await h.AuditAsync();
        var row = audit.Single(a => a.Action == AuditActions.CaseReimport);
        using var details = JsonDocument.Parse(row.DetailsJson);
        Assert.Equal(("archived", "closed", 4, 4, 0, 0, true),
            (details.RootElement.GetProperty("from").GetString(), details.RootElement.GetProperty("to").GetString(), details.RootElement.GetProperty("files").GetInt32(), details.RootElement.GetProperty("parsed").GetInt32(),
             details.RootElement.GetProperty("failed_files").GetArrayLength(), details.RootElement.GetProperty("row_differences").GetArrayLength(), details.RootElement.GetProperty("rows_compared").GetBoolean()));
        Assert.Equal(archive.TarSha256, details.RootElement.GetProperty("tar_sha256").GetString());
        Assert.DoesNotContain(audit, a => a.Action == AuditActions.CaseReimportRefused);
        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);

        // Closed, never active: evidence that was frozen stays frozen. The archive is kept until a purge.
        await using var check = h.Open();
        await Assert.ThrowsAsync<CaseNotActiveException>(() => JobOperations.EnqueueAsync(check, scene.Stored[0].Id, DateTime.UtcNow, Ct));
        Assert.True(ArchiveScene.ArchiveVerifies(archive));
        Assert.True((await CaseAccounting.MeasureAsync(check, h.Events, scene.Paths, scene.CaseId, Ct))!.ArchiveBytes > 0);
        // And it can be archived again, into a second archive beside the first.
        var second = await scene.ArchiveAsync();
        Assert.NotEqual(archive.ArchiveDirectory, second.ArchiveDirectory);
        Assert.Equal(2, Directory.EnumerateDirectories(CaseFolders.CaseDirectory(scene.Archives, scene.CaseId)).Count());
    }

    [Fact]
    public async Task AFileRecordedOutsideTheRootThatIsGoneComesBackUnderTheCaseDirectoryAndItsRecordSaysSo()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        await scene.ArchiveAsync();
        File.Delete(scene.ExternalPath);

        Assert.False(await scene.Executor().ReimportAsync(await scene.RequestReimportAsync(), Ct));
        var restored = Path.Combine(scene.CaseDirectory, LifecycleExecutor.RestoredFolder, scene.External.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(scene.External.Sha256, ArchiveScene.Sha(await File.ReadAllBytesAsync(restored, Ct)));
        Assert.Equal($"restored/{scene.External.Id}", (await h.FileAsync(scene.External.Id)).StoredPath);
        await scene.Runner().RunAsync(Ct);
        Assert.Equal(CaseState.Closed, await scene.StateAsync());
        Assert.Equal(scene.RowsBefore[scene.External.Id], h.Events.Visible((ulong)scene.External.Id));
    }

    [Fact]
    public async Task AStopAtAnyCallOfAReimportIsFinishedByTheNextExecutor()
    {
        int total;
        await using (var clean = await RunnerHarness.CreateAsync())
        {
            var scene = await ArchiveScene.BuildAsync(clean);
            await scene.ArchiveAsync();
            var ticks = 0;
            await scene.Executor(_ => ticks++, _ => ticks++).ReimportAsync(await scene.RequestReimportAsync(), Ct);
            total = ticks;
        }

        Assert.InRange(total, 10, 60);
        for (var stopAt = 0; stopAt < total; stopAt++)
        {
            await using var h = await RunnerHarness.CreateAsync();
            var scene = await ArchiveScene.BuildAsync(h);
            var archive = await scene.ArchiveAsync();
            var outside = scene.OutsideListing();
            var seen = 0;
            void Tick(string what)
            {
                if (seen++ == stopAt)
                {
                    throw new IOException($"stopped by the test at call {stopAt}");
                }
            }

            var task = await scene.RequestReimportAsync();
            await Assert.ThrowsAsync<IOException>(() => scene.Executor(Tick, Tick).ReimportAsync(task, Ct));
            // Whatever the stop left is inside the case's directory; the case is archived and its archive untouched.
            Assert.Equal(CaseState.Archived, await scene.StateAsync());
            Assert.True(ArchiveScene.ArchiveVerifies(archive));
            Assert.Equal(outside, scene.OutsideListing());

            Assert.Equal(0, await scene.Executor().RunPendingAsync(Ct));
            Assert.Equal(CaseTaskState.Committed, (await scene.TaskAsync(task.Id)).State);
            Assert.False(Directory.Exists(Path.Combine(scene.CaseDirectory, LifecycleExecutor.StagingFolder)));
            Assert.True(scene.Stored.All(f => ArchiveScene.Sha(File.ReadAllBytes(scene.PathOf(f))) == f.Sha256), $"after a stop at call {stopAt}");
            await scene.Runner().RunAsync(Ct);
            Assert.Equal(CaseState.Closed, await scene.StateAsync());
            Assert.All(scene.AllArrived, f => Assert.Equal(scene.RowsBefore[f.Id], h.Events.Visible((ulong)f.Id)));
            Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.CaseReimport);
        }
    }

    [Theory]
    [InlineData("member-byte", true)]
    [InlineData("zstd-byte", false)]
    [InlineData("tar-truncated", false)]
    [InlineData("manifest-byte", false)]
    [InlineData("member-and-manifest", false)]
    [InlineData("entry-dotdot", false)]
    [InlineData("entry-absolute", false)]
    [InlineData("entry-backslash", false)]
    [InlineData("entry-symlink", false)]
    [InlineData("entry-hardlink", false)]
    [InlineData("entry-directory", false)]
    [InlineData("entry-unlisted", false)]
    [InlineData("entry-duplicate", true)]
    [InlineData("entry-absent", true)]
    [InlineData("record-path-outside", true)]
    [InlineData("record-path-staging", true)]
    [InlineData("record-hash", true)]
    [InlineData("tar-missing", false)]
    public async Task AnArchiveThatIsWrongInAnyWayRefusesTheWholeReimportAndRestoresNothing(string fault, bool namesAFile)
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var archive = await scene.ArchiveAsync();
        var directory = archive.ArchiveDirectory!;
        var tar = Path.Combine(directory, ArchiveManifest.TarName);
        var manifestPath = Path.Combine(directory, ArchiveManifest.ManifestName);
        var entries = TarForge.Read(tar);
        var victim = scene.Stored[1];
        var name = ArchiveManifest.EntryName(victim.Id);
        var index = entries.FindIndex(e => e.Name == name);
        // A place outside the case's directory that a careless extraction would write to.
        var evil = Path.Combine(h.Root, "evil.log");

        switch (fault)
        {
            case "member-byte":
                // One byte of one member; the tar is written anew and its compression is valid.
                var data = (byte[])entries[index].Data.Clone();
                data[data.Length / 2] ^= 0x01;
                entries[index] = entries[index] with { Data = data };
                TarForge.Write(tar, entries);
                break;
            case "zstd-byte":
                ArchiveScene.Unlock(tar);
                var raw = await File.ReadAllBytesAsync(tar, Ct);
                raw[raw.Length / 2] ^= 0x40;
                await File.WriteAllBytesAsync(tar, raw, Ct);
                break;
            case "tar-truncated":
                ArchiveScene.Unlock(tar);
                var whole = await File.ReadAllBytesAsync(tar, Ct);
                await File.WriteAllBytesAsync(tar, whole[..(whole.Length * 2 / 3)], Ct);
                break;
            case "tar-missing":
                ArchiveScene.Unlock(tar);
                File.Delete(tar);
                break;
            case "manifest-byte":
                ArchiveScene.Unlock(manifestPath);
                await File.WriteAllTextAsync(manifestPath, (await File.ReadAllTextAsync(manifestPath, Ct)).Replace("to archive", "to archivf", StringComparison.Ordinal), Ct);
                break;
            case "member-and-manifest":
                // The member and everything in the archive that names its hash, changed together and consistently.
                var forged = Encoding.UTF8.GetBytes("1 forged line\n");
                entries[index] = entries[index] with { Data = forged };
                TarForge.Write(tar, entries);
                ArchiveScene.Unlock(manifestPath);
                var json = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, Ct))!;
                var entry = json["entries"]!.AsArray().Single(e => e!["file_id"]!.GetValue<long>() == victim.Id)!;
                entry["tar_sha256"] = ArchiveScene.Sha(forged);
                entry["tar_size"] = forged.Length;
                entry["record_sha256"] = ArchiveScene.Sha(forged);
                entry["record_size"] = forged.Length;
                json["tar_sha256"] = ArchiveReader.HashFile(tar).Sha256;
                json["tar_size"] = new FileInfo(tar).Length;
                await File.WriteAllTextAsync(manifestPath, json.ToJsonString(), Ct);
                break;
            case "entry-dotdot":
                entries.Add(new TarForge.Entry(TarEntryType.RegularFile, "files/../../../evil.log", "evil"u8.ToArray()));
                TarForge.Write(tar, entries);
                break;
            case "entry-absolute":
                entries.Insert(0, new TarForge.Entry(TarEntryType.RegularFile, evil.Replace('\\', '/'), "evil"u8.ToArray()));
                TarForge.Write(tar, entries);
                break;
            case "entry-backslash":
                entries.Insert(0, new TarForge.Entry(TarEntryType.RegularFile, "files\\..\\..\\..\\evil.log", "evil"u8.ToArray()));
                TarForge.Write(tar, entries);
                break;
            case "entry-symlink":
                // In place of a listed file: its name, pointing out of the case's directory.
                entries[index] = new TarForge.Entry(TarEntryType.SymbolicLink, name, [], evil);
                TarForge.Write(tar, entries);
                break;
            case "entry-hardlink":
                entries[index] = new TarForge.Entry(TarEntryType.HardLink, name, [], ArchiveManifest.EntryName(scene.Stored[0].Id));
                TarForge.Write(tar, entries);
                break;
            case "entry-directory":
                entries.Insert(0, new TarForge.Entry(TarEntryType.Directory, "files/", []));
                TarForge.Write(tar, entries);
                break;
            case "entry-unlisted":
                entries.Add(TarForge.File_(987654321, "nobody listed this"));
                TarForge.Write(tar, entries);
                break;
            case "entry-duplicate":
                entries.Add(entries[index]);
                TarForge.Write(tar, entries);
                break;
            case "entry-absent":
                entries.RemoveAt(index);
                TarForge.Write(tar, entries);
                break;
            case "record-path-outside":
                await ExecuteAsync(h, $"UPDATE files SET stored_path = '../../evil.log' WHERE id = {victim.Id}");
                break;
            case "record-path-staging":
                await ExecuteAsync(h, $"UPDATE files SET stored_path = '.reimport/../web/x' WHERE id = {victim.Id}");
                break;
            default:
                await ExecuteAsync(h, $"UPDATE files SET sha256 = '{new string('e', 64)}' WHERE id = {victim.Id}");
                break;
        }

        var outside = scene.OutsideListing();
        var task = await scene.RequestReimportAsync();
        var staged = new List<string>();
        Assert.False(await scene.Executor(beforeRemoval: staged.Add).ReimportAsync(task, Ct));

        // Refused as a whole.
        var row = await scene.TaskAsync(task.Id);
        Assert.Equal(CaseTaskState.Failed, row.State);
        Assert.Equal(CaseState.Archived, await scene.StateAsync());
        var audit = await h.AuditAsync();
        var refusal = Assert.Single(audit, a => a.Action == AuditActions.CaseReimportRefused);
        using var details = JsonDocument.Parse(refusal.DetailsJson);
        Assert.Equal(row.Message, details.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain(audit, a => a.Action == AuditActions.CaseReimport);
        await using (var db = h.Open())
        {
            Assert.Equal(0, await db.Jobs.CountAsync(j => j.CaseId == scene.CaseId && (j.State == JobState.Queued || j.State == JobState.Parsing), Ct));
            Assert.Equal(victim.StoredPath, fault.StartsWith("record-path", StringComparison.Ordinal) ? victim.StoredPath : (await db.Files.AsNoTracking().SingleAsync(f => f.Id == victim.Id, Ct)).StoredPath);
        }

        // Nothing restored, nothing left, and nothing anywhere else: every path written was a number in the staging folder.
        Assert.Empty(Directory.EnumerateFileSystemEntries(scene.CaseDirectory));
        Assert.Equal(outside, scene.OutsideListing());
        Assert.False(File.Exists(evil));
        var staging = Path.Combine(scene.CaseDirectory, LifecycleExecutor.StagingFolder) + Path.DirectorySeparatorChar;
        Assert.All(staged.Where(p => File.Exists(p) || !Directory.Exists(p)), path =>
        {
            Assert.StartsWith(scene.CaseDirectory + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            if (path.StartsWith(staging, StringComparison.Ordinal))
            {
                Assert.True(long.TryParse(Path.GetFileName(path), out _), $"a name in the staging folder that is not a file id: {path}");
            }
        });

        // When the fault is in one file the rows name it, with what was expected and what the archive holds.
        if (namesAFile)
        {
            Assert.Equal(victim.Id, details.RootElement.GetProperty("file_id").GetInt64());
            var mismatch = Assert.Single(audit, a => a.Action == AuditActions.IntegrityMismatch);
            Assert.Equal((AuditTargets.File, (long?)victim.Id), (mismatch.TargetType, mismatch.TargetId));
            Assert.Contains("\"check\":\"reimport\"", mismatch.DetailsJson, StringComparison.Ordinal);
        }

        // The refusal is the end of that request; the next one is judged anew and refused the same way.
        Assert.Equal(0, await scene.Executor().RunPendingAsync(Ct));
        Assert.True((await AuditVerifier.VerifyAsync(h.Store.Path, Ct)).Ok);
    }

    public static TheoryData<string> HostileNames => new(Hostile.Keys);

    private static Dictionary<string, TarForge.Entry[]> Hostile { get; } = new()
    {
        { "dot-dot", [TarForge.File_(1, "one"), new(TarEntryType.RegularFile, "files/../2", "two"u8.ToArray())] },
        { "dot-dot-inside", [new(TarEntryType.RegularFile, "files/1/../1", "one"u8.ToArray())] },
        { "absolute", [new(TarEntryType.RegularFile, "/files/1", "one"u8.ToArray())] },
        { "drive", [new(TarEntryType.RegularFile, "C:/files/1", "one"u8.ToArray())] },
        { "backslash", [new(TarEntryType.RegularFile, "files\\1", "one"u8.ToArray())] },
        { "no-prefix", [new(TarEntryType.RegularFile, "1", "one"u8.ToArray())] },
        { "leading-zero", [new(TarEntryType.RegularFile, "files/01", "one"u8.ToArray())] },
        { "not-a-number", [new(TarEntryType.RegularFile, "files/1a", "one"u8.ToArray())] },
        { "empty-id", [new(TarEntryType.RegularFile, "files/", "one"u8.ToArray())] },
        { "trailing-slash", [new(TarEntryType.RegularFile, "files/1/", "one"u8.ToArray())] },
        { "too-long", [new(TarEntryType.RegularFile, "files/" + new string('9', 20), "one"u8.ToArray())] },
        { "symlink", [new(TarEntryType.SymbolicLink, "files/1", [], "/etc/passwd")] },
        { "hardlink", [TarForge.File_(2, "two"), new(TarEntryType.HardLink, "files/1", [], "files/2")] },
        { "directory", [new(TarEntryType.Directory, "files/1", [])] },
        { "unlisted", [TarForge.File_(1, "one"), TarForge.File_(2, "two"), TarForge.File_(3, "three")] },
        { "duplicate", [TarForge.File_(1, "one"), TarForge.File_(2, "two"), TarForge.File_(1, "one")] },
        { "absent", [TarForge.File_(1, "one")] },
        { "other-bytes", [TarForge.File_(1, "one"), TarForge.File_(2, "twO")] },
        { "longer", [TarForge.File_(1, "one"), TarForge.File_(2, "two and more")] },
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task TheReaderHoldsEveryEntryToTheManifestEvenWhenTheTarHashesAsTheManifestSays(string fault)
    {
        var entries = Hostile[fault];
        // A manifest that lists files 1 and 2, and a tar the manifest's own hash agrees with: only the walk over
        // the entries stands between this tar and the disk.
        var directory = Directory.CreateTempSubdirectory("urlog-hostile-");
        try
        {
            var tar = Path.Combine(directory.FullName, ArchiveManifest.TarName);
            TarForge.Write(tar, entries);
            var (sha, size) = ArchiveReader.HashFile(tar);
            var manifest = Manifest(sha, size, (1, "one"), (2, "two"));
            var given = new List<string>();
            var refused = await Assert.ThrowsAsync<ArchiveRefusedException>(() => ArchiveReader.ReadAsync(tar, manifest, async (entry, data, ct) =>
            {
                given.Add(entry.Name);
                await data.CopyToAsync(Stream.Null, ct);
            }, Ct));
            Assert.False(string.IsNullOrWhiteSpace(refused.Message), fault);
            // The sink is only ever handed an entry of the manifest, under the manifest's own name for it.
            Assert.All(given, name => Assert.True(name is "files/1" or "files/2", name));
            Assert.True(given.Count <= 2 || fault == "duplicate", $"{fault}: {given.Count} entries were handed out");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TheReaderAcceptsATarThatIsWhatTheManifestSays()
    {
        var directory = Directory.CreateTempSubdirectory("urlog-good-");
        try
        {
            var tar = Path.Combine(directory.FullName, ArchiveManifest.TarName);
            TarForge.Write(tar, [TarForge.File_(1, "one"), TarForge.File_(2, "two")]);
            var (sha, size) = ArchiveReader.HashFile(tar);
            var given = new Dictionary<string, string>();
            await ArchiveReader.ReadAsync(tar, Manifest(sha, size, (1, "one"), (2, "two")), async (entry, data, ct) =>
            {
                using var copy = new MemoryStream();
                await data.CopyToAsync(copy, ct);
                given[entry.Name] = Encoding.UTF8.GetString(copy.ToArray());
            }, Ct);
            Assert.Equal(new Dictionary<string, string> { ["files/1"] = "one", ["files/2"] = "two" }, given);
            // And the whole-file hash is its own check: the same tar under another hash in the manifest is refused.
            await Assert.ThrowsAsync<ArchiveRefusedException>(() => ArchiveReader.ReadAsync(tar, Manifest(new string('0', 64), size, (1, "one"), (2, "two")), null, Ct));
            await Assert.ThrowsAsync<ArchiveRefusedException>(() => ArchiveReader.ReadAsync(tar, Manifest(sha, size + 1, (1, "one"), (2, "two")), null, Ct));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("files/1", true)]
    [InlineData("files/9223372036854775807", true)]
    [InlineData("files/9223372036854775808", false)]
    [InlineData("files/0", false)]
    [InlineData("files/-1", false)]
    [InlineData("files/+1", false)]
    [InlineData("files/ 1", false)]
    [InlineData("files/1 ", false)]
    [InlineData("files/١", false)]
    [InlineData("Files/1", false)]
    [InlineData("files//1", false)]
    [InlineData("./files/1", false)]
    public void AnEntryNameIsFilesSlashAFileIdAndNothingElse(string name, bool accepted)
    {
        Assert.Equal(accepted, ArchiveReader.IsEntryName(name));
    }

    [Fact]
    public async Task TheSweepReadsTheArchiveOfAnArchivedCaseAndAChangedOneCannotBeReimported()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var scene = await ArchiveScene.BuildAsync(h);
        var archive = await scene.ArchiveAsync();
        IntegritySweep Sweep() => new(h.Store, new MemberStore(scene.Evidence, scene.Cache), scene.Evidence, new LifecycleOptions(), NullLogger.Instance);

        // The kept case's one file and the archive.
        var first = await Sweep().RunAsync(DateTime.UtcNow.AddMinutes(1), () => SweepSignal.Go, Ct);
        Assert.Equal((2, 0, 0, 0, true), (first.Ok, first.Mismatched, first.Missing, first.Unreadable, first.Complete));
        var ok = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.IntegrityOk && a.TargetType == AuditTargets.Case);
        Assert.Equal(scene.CaseId, ok.TargetId);
        Assert.Contains("\"check\":\"archive\"", ok.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"tar_sha256\":\"{archive.TarSha256}\"", ok.DetailsJson, StringComparison.Ordinal);
        Assert.True(first.Bytes >= new FileInfo(Path.Combine(archive.ArchiveDirectory!, ArchiveManifest.TarName)).Length);

        // The archive root away (a volume that is not mounted): no result, nothing failed.
        Directory.Move(scene.Archives, scene.Archives + "-unmounted");
        var blind = await Sweep().RunAsync(DateTime.UtcNow.AddMinutes(2), () => SweepSignal.Go, Ct);
        Assert.Equal((1, 0, 1), (blind.Ok, blind.Mismatched, blind.Unreadable));
        Directory.Move(scene.Archives + "-unmounted", scene.Archives);
        Assert.Equal(CaseTaskState.Done, (await scene.TaskAsync(archive.Id)).State);

        // One byte of the tar changed on the archive volume.
        var tar = Path.Combine(archive.ArchiveDirectory!, ArchiveManifest.TarName);
        ArchiveScene.Unlock(tar);
        var bytes = await File.ReadAllBytesAsync(tar, Ct);
        bytes[bytes.Length / 3] ^= 0x04;
        await File.WriteAllBytesAsync(tar, bytes, Ct);
        var second = await Sweep().RunAsync(DateTime.UtcNow.AddMinutes(3), () => SweepSignal.Go, Ct);
        Assert.Equal((1, 1), (second.Ok, second.Mismatched));
        var mismatch = Assert.Single(await h.AuditAsync(), a => a.Action == AuditActions.IntegrityMismatch);
        Assert.Equal((AuditTargets.Case, (long?)scene.CaseId), (mismatch.TargetType, mismatch.TargetId));
        Assert.Contains($"\"actual_tar_sha256\":\"{ArchiveScene.Sha(bytes)}\"", mismatch.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"actual_manifest_sha256\":\"{archive.ManifestSha256}\"", mismatch.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(CaseTaskState.Failed, (await scene.TaskAsync(archive.Id)).State);

        // Refused at the request, before a byte of it is read; and the failed archive is not read again.
        var refused = await Assert.ThrowsAsync<LifecycleRefusedException>(() => scene.RequestReimportAsync());
        Assert.Contains("the integrity sweep found the archive changed or gone", refused.Message, StringComparison.Ordinal);
        Assert.Equal((1, 0), ((await Sweep().RunAsync(DateTime.UtcNow.AddMinutes(4), () => SweepSignal.Go, Ct)).Ok, 0));
    }

    private static ArchiveManifest Manifest(string tarSha, long tarSize, params (long Id, string Content)[] files) => new()
    {
        StoreIdentity = new string('a', 32),
        CreatedAt = DateTime.UtcNow,
        Case = new ManifestCase(1, "c", 1, DateTime.UtcNow, null),
        Hosts = [],
        Files = [],
        Entries = [.. files.Select(f => new ManifestEntry(f.Id, ArchiveManifest.EntryName(f.Id), Sha(f.Content), f.Content.Length, Sha(f.Content), f.Content.Length, false, false, false))],
        TarSha256 = tarSha,
        TarSize = tarSize,
        AuditSha256 = new string('0', 64),
        AuditLines = 0,
        AuditHeadSeq = 1,
        AuditHeadHash = new string('0', 64),
    };

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static async Task ExecuteAsync(RunnerHarness h, string sql)
    {
        await using var connection = new SqliteConnection(MetadataStore.ConnectionString(h.Store.Path));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
