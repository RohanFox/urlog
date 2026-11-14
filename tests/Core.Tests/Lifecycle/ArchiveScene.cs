using System.Formats.Tar;
using System.Security.Cryptography;
using Urlog.Core.Audit;
using Urlog.Core.Intake;
using Urlog.Core.Jobs;
using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Urlog.Core.Tests.Lifecycle;

/// <summary>
/// A closed case ready to be archived, and a case beside it that must stay whole. The first has three files
/// stored under the evidence root (one of them an upload that arrived) and one recorded where it was found,
/// outside the root; all four are parsed by a real runner into the fake event store. It also has three months of
/// partitions, field statistics, an unfinished upload, a cache copy, and a job that was queued before the close.
/// </summary>
internal sealed class ArchiveScene
{
    private readonly RunnerHarness _h;

    private ArchiveScene(RunnerHarness h)
    {
        _h = h;
        Evidence = Path.Combine(h.Root, "evidence");
        Cache = Path.Combine(h.Root, "evidence-cache");
        Archives = Path.Combine(h.Root, "archives");
        Backups = Path.Combine(h.Root, "backups");
    }

    private static CancellationToken Ct => RunnerHarness.Ct;

    public string Evidence { get; }

    public string Cache { get; }

    public string Archives { get; }

    public string Backups { get; }

    public long CaseId { get; private set; }

    public long Kept { get; private set; }

    public long KeptFile { get; private set; }

    /// <summary>The files stored under the evidence root, by id.</summary>
    public List<EvidenceFile> Stored { get; } = [];

    /// <summary>The file recorded outside the evidence root.</summary>
    public EvidenceFile External { get; private set; } = null!;

    public string ExternalPath { get; private set; } = "";

    /// <summary>Rows each file showed before the archive.</summary>
    public Dictionary<long, long> RowsBefore { get; } = [];

    public long HeldJob { get; private set; }

    public string CaseDirectory => CaseFolders.CaseDirectory(Evidence, CaseId);

    public LifecyclePaths Paths => new(Evidence, Cache, Backups, Archives);

    public IEnumerable<EvidenceFile> AllArrived => Stored.Append(External);

    public static async Task<ArchiveScene> BuildAsync(RunnerHarness h, bool close = true)
    {
        var scene = new ArchiveScene(h);
        scene.Kept = (await h.AddCaseAsync("kept")).Id;
        scene.CaseId = (await h.AddCaseAsync("to archive")).Id;
        scene.KeptFile = (await scene.StoreAsync(scene.Kept, "kept.log", TestData.NumberedLines(2048, seed: 90).Bytes)).Id;
        scene.Stored.Add(await scene.StoreAsync(scene.CaseId, "access.log", TestData.NumberedLines(6000, seed: 91).Bytes));
        scene.Stored.Add(await scene.StoreAsync(scene.CaseId, Path.Combine(CaseFolders.Web, "0123456789abcdef"), TestData.NumberedLines(9000, seed: 92).Bytes));
        scene.Stored.Add(await scene.StoreAsync(scene.CaseId, Path.Combine("host-b", "auth.log"), TestData.NumberedLines(3000, seed: 93).Bytes));
        var (external, _, path) = await h.AddFileAsync(scene.CaseId, TestData.NumberedLines(4000, seed: 94).Bytes, "elsewhere.log");
        scene.External = external;
        scene.ExternalPath = path;

        await scene.Runner().RunAsync(Ct);
        foreach (var file in scene.AllArrived)
        {
            Assert.Equal(FileState.Parsed, (await h.FileAsync(file.Id)).State);
            scene.RowsBefore[file.Id] = h.Events.Visible((ulong)file.Id);
            Assert.True(scene.RowsBefore[file.Id] > 0);
        }

        // What intake and the runner leave beside the stored files.
        Directory.CreateDirectory(Path.Combine(scene.CaseDirectory, CaseFolders.Uploads));
        await File.WriteAllTextAsync(Path.Combine(scene.CaseDirectory, CaseFolders.Uploads, "fedcba9876543210"), "half an upl", Ct);
        foreach (var id in new[] { scene.CaseId, scene.Kept })
        {
            Directory.CreateDirectory(CaseFolders.CaseDirectory(scene.Cache, id));
            await File.WriteAllTextAsync(Path.Combine(CaseFolders.CaseDirectory(scene.Cache, id), "1.body"), "seekable copy", Ct);
            foreach (var month in id == scene.CaseId ? new[] { 202601, 202602, 202603 } : [202601])
            {
                h.Events.SeedPartition((ulong)id, month, 1000);
            }

            h.Events.SeedFieldStats((ulong)id);
        }

        await using var db = h.Open();
        // A re-parse asked for and not yet run when the case is closed: the close holds it.
        scene.HeldJob = (await JobOperations.EnqueueAsync(db, scene.Stored[0].Id, DateTime.UtcNow, Ct)).Id;
        if (close)
        {
            await LifecycleOperations.CloseAsync(db, h.Store.Audit, new AuditActor("alice"), scene.CaseId, DateTime.UtcNow, Ct);
        }

        return scene;
    }

    public JobRunner Runner() => _h.Runner(RunnerHarness.Options() with { EvidenceRoot = Evidence, CacheRoot = Cache, ArchiveRoot = Archives, BackupDirectory = Backups });

    public LifecycleExecutor Executor(Action<string>? onStep = null, Action<string>? beforeRemoval = null, Action<string>? beforeStoreCall = null, Action<string>? beforeRead = null, LifecycleOptions? options = null)
    {
        _h.Events.BeforePurgeCall = beforeStoreCall;
        return new LifecycleExecutor(_h.Store, _h.Events, options ?? new LifecycleOptions { ArchiveHeadroomBytes = 0 }, Paths, NullLogger.Instance)
        {
            OnStep = onStep,
            BeforeRemoval = beforeRemoval,
            BeforeArchiveRead = beforeRead,
        };
    }

    public async Task<CaseTask> RequestArchiveAsync()
    {
        await using var db = _h.Open();
        return await ArchiveOperations.RequestArchiveAsync(db, new AuditActor("alice"), CaseId, _h.Store.Clock, Ct);
    }

    public async Task<CaseTask> RequestReimportAsync()
    {
        await using var db = _h.Open();
        return await ArchiveOperations.RequestReimportAsync(db, new AuditActor("alice"), CaseId, _h.Store.Clock, Ct);
    }

    /// <summary>Asks for the archive and carries it out to its end.</summary>
    public async Task<CaseTask> ArchiveAsync()
    {
        var task = await RequestArchiveAsync();
        await Executor().ArchiveAsync(task, Ct);
        return await TaskAsync(task.Id);
    }

    public async Task<CaseTask> TaskAsync(long id)
    {
        await using var db = _h.Open();
        return await db.CaseTasks.AsNoTracking().SingleAsync(t => t.Id == id, Ct);
    }

    public async Task<CaseState> StateAsync()
    {
        await using var db = _h.Open();
        return await db.Cases.AsNoTracking().Where(c => c.Id == CaseId).Select(c => c.State).SingleAsync(Ct);
    }

    public string PathOf(EvidenceFile file) => Path.IsPathRooted(file.StoredPath) ? file.StoredPath : Path.Combine(CaseDirectory, file.StoredPath);

    public bool StoreWhole => _h.Events.Partitions((ulong)CaseId).Length == 3 && _h.Events.HasFieldStats((ulong)CaseId);

    public bool StoreEmpty => _h.Events.Partitions((ulong)CaseId).Length == 0 && !_h.Events.HasFieldStats((ulong)CaseId);

    /// <summary>Every stored file where its record puts it with the bytes it was recorded with, the unfinished upload and the cache copy.</summary>
    public bool FilesWhole =>
        Stored.All(f => File.Exists(PathOf(f)) && Sha(File.ReadAllBytes(PathOf(f))) == f.Sha256)
        && File.Exists(Path.Combine(CaseDirectory, CaseFolders.Uploads, "fedcba9876543210"))
        && File.Exists(Path.Combine(CaseFolders.CaseDirectory(Cache, CaseId), "1.body"));

    /// <summary>True when the archive on the task's record is there and hashes to what the task was committed with.</summary>
    public static bool ArchiveVerifies(CaseTask task) =>
        task.ArchiveDirectory is { } directory && !directory.EndsWith(".partial", StringComparison.Ordinal)
        && File.Exists(Path.Combine(directory, ArchiveManifest.TarName)) && File.Exists(Path.Combine(directory, ArchiveManifest.ManifestName)) && File.Exists(Path.Combine(directory, ArchiveManifest.AuditName))
        && ArchiveReader.HashFile(Path.Combine(directory, ArchiveManifest.TarName)).Sha256 == task.TarSha256
        && ArchiveReader.HashFile(Path.Combine(directory, ArchiveManifest.ManifestName)).Sha256 == task.ManifestSha256;

    /// <summary>The end state of an archive: the verified archive, nothing of the case in the store but its generation rows, its directory empty, the neighbour whole.</summary>
    public async Task AssertArchivedAsync(CaseTask task)
    {
        Assert.Equal(CaseTaskState.Done, task.State);
        Assert.Equal(CaseState.Archived, await StateAsync());
        Assert.True(ArchiveVerifies(task), "the archive on record does not hash to what was committed");
        Assert.StartsWith(CaseFolders.CaseDirectory(Archives, CaseId) + Path.DirectorySeparatorChar, task.ArchiveDirectory, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(CaseFolders.CaseDirectory(Archives, CaseId), "*.partial"));
        Assert.True(StoreEmpty);
        // The generation rows stay: they are the floor for the parses of a re-import.
        Assert.All(AllArrived, f => Assert.NotEqual(0u, _h.Events.Current((ulong)f.Id)));
        Assert.True(Directory.Exists(CaseDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(CaseDirectory));
        Assert.False(Directory.Exists(CaseFolders.CaseDirectory(Cache, CaseId)));
        Assert.True(File.Exists(ExternalPath), "a file recorded outside the evidence root is never removed");
        await using var db = _h.Open();
        Assert.Equal(4, await db.Files.CountAsync(f => f.CaseId == CaseId, Ct));
        Assert.Equal(JobState.Cancelled, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == HeldJob, Ct)).State);
        await AssertKeptAsync(db);
    }

    public async Task AssertKeptAsync(MetadataDb db)
    {
        Assert.Equal(CaseState.Active, await db.Cases.AsNoTracking().Where(c => c.Id == Kept).Select(c => c.State).SingleAsync(Ct));
        Assert.Equal([202601], _h.Events.Partitions((ulong)Kept));
        Assert.True(_h.Events.HasFieldStats((ulong)Kept));
        Assert.True(File.Exists(Path.Combine(CaseFolders.CaseDirectory(Evidence, Kept), "kept.log")));
        Assert.True(File.Exists(Path.Combine(CaseFolders.CaseDirectory(Cache, Kept), "1.body")));
    }

    /// <summary>Every path under the harness directory that a re-import has no business creating or changing, with sizes.</summary>
    public string OutsideListing()
    {
        var own = CaseDirectory + Path.DirectorySeparatorChar;
        return string.Join("\n", Directory.EnumerateFileSystemEntries(_h.Root, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(own, StringComparison.Ordinal) && p != CaseDirectory && !Path.GetFileName(p).StartsWith("metadata.db", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p) ? $"{p} {new FileInfo(p).Length}" : p + "/"));
    }

    public async Task<EvidenceFile> StoreAsync(long caseId, string relative, byte[] content)
    {
        var path = Path.Combine(CaseFolders.CaseDirectory(Evidence, caseId), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, Ct);
        EvidencePermissions.MakeReadOnly(path);
        await using var db = _h.Open();
        var file = new EvidenceFile
        {
            CaseId = caseId, Sha256 = Sha(content), Size = content.Length, OriginalName = Path.GetFileName(relative), StoredPath = relative.Replace('\\', '/'),
            Source = FileSource.Web, Uploader = "alice", ReceivedAt = DateTime.UtcNow, Encoding = "utf8", DetectedSource = "test-lines",
        };
        var added = (await FileOperations.AddAsync(db, _h.Store.Audit, new AuditActor("alice"), file, Ct)).File;
        await JobOperations.EnqueueAsync(db, added.Id, DateTime.UtcNow, Ct);
        return added;
    }

    public static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Makes a stored or archived file writable for a test that changes it behind the tool's back.</summary>
    public static void Unlock(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>Reads and rewrites the tar of an archive the way someone with access to the archive volume could.</summary>
internal static class TarForge
{
    public sealed record Entry(TarEntryType Type, string Name, byte[] Data, string? Link = null);

    public static List<Entry> Read(string tarZst)
    {
        var entries = new List<Entry>();
        using var file = File.OpenRead(tarZst);
        using var zstd = new ZstdSharp.DecompressionStream(file);
        using var tar = new TarReader(zstd);
        while (tar.GetNextEntry() is { } entry)
        {
            using var data = new MemoryStream();
            entry.DataStream?.CopyTo(data);
            entries.Add(new Entry(entry.EntryType, entry.Name, data.ToArray()));
        }

        return entries;
    }

    /// <summary>Writes a valid zstd-compressed tar with exactly these entries, whatever their names and types.</summary>
    public static void Write(string tarZst, IEnumerable<Entry> entries)
    {
        if (File.Exists(tarZst))
        {
            ArchiveScene.Unlock(tarZst);
        }

        using var file = File.Create(tarZst);
        using var zstd = new ZstdSharp.CompressionStream(file, 3);
        using var tar = new TarWriter(zstd, TarEntryFormat.Pax);
        foreach (var entry in entries)
        {
            var written = new PaxTarEntry(entry.Type, entry.Name);
            if (entry.Link is not null)
            {
                written.LinkName = entry.Link;
            }

            if (entry.Type == TarEntryType.RegularFile)
            {
                written.DataStream = new MemoryStream(entry.Data);
            }

            tar.WriteEntry(written);
        }
    }

    public static Entry File_(long id, string content) => new(TarEntryType.RegularFile, ArchiveManifest.EntryName(id), System.Text.Encoding.UTF8.GetBytes(content));
}
