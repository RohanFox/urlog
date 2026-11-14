using Urlog.Core.Lifecycle;
using Urlog.Core.Metadata;

namespace Urlog.Core.Tests.Lifecycle;

/// <summary>Per-case disk accounting: recorded bytes from the intake records, bytes on disk from the directories, rows and bytes from the event store's partitions.</summary>
public sealed class CaseAccountingTests
{
    private static CancellationToken Ct => RunnerHarness.Ct;

    [Fact]
    public async Task UsageCountsTheCaseAndNothingOfItsNeighbour()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var evidence = Path.Combine(h.Root, "evidence");
        var cache = Path.Combine(h.Root, "evidence-cache");
        var one = await h.AddCaseAsync("one");
        var two = await h.AddCaseAsync("two");
        // One file recorded where it was found (outside the root) counts as recorded bytes, not as bytes under the root.
        var (recorded, _, _) = await h.AddFileAsync(one.Id, new byte[1000], "outside.log");

        var directory = Directory.CreateDirectory(CaseFolders.CaseDirectory(evidence, one.Id)).FullName;
        await File.WriteAllBytesAsync(Path.Combine(directory, "a.log"), new byte[300], Ct);
        Directory.CreateDirectory(Path.Combine(directory, CaseFolders.Web));
        await File.WriteAllBytesAsync(Path.Combine(directory, CaseFolders.Web, "b"), new byte[200], Ct);
        Directory.CreateDirectory(Path.Combine(directory, CaseFolders.Uploads));
        await File.WriteAllBytesAsync(Path.Combine(directory, CaseFolders.Uploads, "c"), new byte[50], Ct);
        await File.WriteAllBytesAsync(Path.Combine(directory, CaseFolders.Uploads, "c.json"), new byte[7], Ct);
        Directory.CreateDirectory(CaseFolders.CaseDirectory(cache, one.Id));
        await File.WriteAllBytesAsync(Path.Combine(CaseFolders.CaseDirectory(cache, one.Id), "1.body"), new byte[4000], Ct);
        Directory.CreateDirectory(CaseFolders.CaseDirectory(evidence, two.Id));
        await File.WriteAllBytesAsync(Path.Combine(CaseFolders.CaseDirectory(evidence, two.Id), "other.log"), new byte[9999], Ct);
        h.Events.SeedPartition((ulong)one.Id, 202601, 100);
        h.Events.SeedPartition((ulong)one.Id, 202602, 50);
        h.Events.SeedPartition((ulong)two.Id, 202601, 7);

        await using var db = h.Open();
        var paths = new LifecyclePaths(evidence, cache, Path.Combine(h.Root, "backups"));
        var usage = await CaseAccounting.MeasureAsync(db, h.Events, paths, one.Id, Ct);
        Assert.NotNull(usage);
        Assert.Equal(("one", CaseState.Active, 1, 1000L, 500L, 57L, 4000L, 150L, 6000L), (usage.Name, usage.State, usage.Files, usage.RecordedBytes, usage.IntakeBytes, usage.UploadBytes, usage.CacheBytes, usage.StoreRows, usage.StoreBytes));
        Assert.Equal([202601, 202602], usage.Partitions.Select(p => p.Month));
        Assert.Equal(recorded.Size, usage.RecordedBytes);

        var other = await CaseAccounting.MeasureAsync(db, h.Events, paths, two.Id, Ct);
        Assert.Equal((0, 0L, 9999L, 0L, 0L, 7L), (other!.Files, other.RecordedBytes, other.IntakeBytes, other.UploadBytes, other.CacheBytes, other.StoreRows));
        Assert.Null(await CaseAccounting.MeasureAsync(db, h.Events, paths, 4242, Ct));
        // Without roots only the records and the store are counted.
        var bare = await CaseAccounting.MeasureAsync(db, h.Events, new LifecyclePaths(null, null, paths.BackupDirectory), one.Id, Ct);
        Assert.Equal((1000L, 0L, 0L, 0L, 150L), (bare!.RecordedBytes, bare.IntakeBytes, bare.UploadBytes, bare.CacheBytes, bare.StoreRows));
    }
}
