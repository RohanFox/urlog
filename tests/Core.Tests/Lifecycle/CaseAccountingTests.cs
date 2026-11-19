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

    [Fact]
    public async Task TheWalkCountsNothingBehindALink()
    {
        var root = Directory.CreateTempSubdirectory("urlog-walk-links-");
        var outside = Directory.CreateTempSubdirectory("urlog-walk-outside-");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(outside.FullName, "big.bin"), new byte[50_000], Ct);
            var web = Directory.CreateDirectory(Path.Combine(root.FullName, "7", "web"));
            await File.WriteAllBytesAsync(Path.Combine(web.FullName, "a"), new byte[300], Ct);
            var linked = TryDirectoryLink(Path.Combine(root.FullName, "7", "elsewhere"), outside.FullName);
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(web.FullName, "file-link"), Path.Combine(outside.FullName, "big.bin"));
                linked = true;
            }

            if (!linked)
            {
                Assert.Skip("this account cannot create a link here");
            }

            // The directory link is not entered and the file link is not followed: only the stored file counts.
            Assert.Equal(300, CaseAccounting.DirectoryBytes(Path.Combine(root.FullName, "7")));
            // A case directory that is itself a link counts as nothing.
            Assert.Equal(0, CaseAccounting.DirectoryBytes(Path.Combine(root.FullName, "7", "elsewhere")));
        }
        finally
        {
            // The link first, so removing the tree does not reach through it.
            var link = Path.Combine(root.FullName, "7", "elsewhere");
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    /// <summary>A directory link: a symbolic link where the account may make one, a junction on Windows otherwise.</summary>
    private static bool TryDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    /// <summary>
    /// The walk the dashboard's disk use costs for a case of 10,000 stored files: counted right, and timed over 20
    /// passes for the record (written out, and appended to the file URLOG_API_TIMING names when set; no bound is asserted).
    /// </summary>
    [Fact]
    public async Task TheWalkOverTenThousandStoredFilesCountsEveryByte()
    {
        const int Files = 10_000;
        var root = Directory.CreateTempSubdirectory("urlog-walk-");
        try
        {
            var web = Directory.CreateDirectory(Path.Combine(root.FullName, "7", "web"));
            var content = new byte[100];
            for (var i = 0; i < Files; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(web.FullName, i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)), content, Ct);
            }

            var times = new List<double>();
            for (var pass = 0; pass < 21; pass++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Assert.Equal(Files * 100L, CaseAccounting.DirectoryBytes(Path.Combine(root.FullName, "7")));
                if (pass > 0)
                {
                    times.Add(clock.Elapsed.TotalMilliseconds);
                }
            }

            times.Sort();
            var line = $"disk-use walk over {Files:N0} stored files, 20 passes after one unmeasured: p50 {times[9]:F1} ms, p95 {times[18]:F1} ms";
            TestContext.Current.TestOutputHelper?.WriteLine(line);
            if (Environment.GetEnvironmentVariable("URLOG_API_TIMING") is { Length: > 0 } path)
            {
                await File.AppendAllTextAsync(path, line + Environment.NewLine, Ct);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
