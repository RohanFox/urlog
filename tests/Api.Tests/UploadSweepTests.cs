using System.Net;
using Urlog.Api.Uploads;
using Microsoft.EntityFrameworkCore;
using static Urlog.Api.Tests.UploadHarness;

namespace Urlog.Api.Tests;

/// <summary>
/// The expiry sweep: an upload older than 24 hours goes, a younger one stays, and nothing outside the uploads
/// folder is opened, created, changed or removed by it.
/// </summary>
public sealed class UploadSweepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSweepRemovesWhatExpiredKeepsWhatDidNotAndTouchesNothingOutsideTheUploadsFolder()
    {
        await using var h = await StartAsync();
        var bytes = Log(60);

        // Evidence of the case, by upload and as a drop beside it, and files of another case.
        var recorded = await h.UploadAsync(bytes, "recorded.log");
        var caseFolder = h.Folders.CaseDirectory(CaseId);
        await File.WriteAllTextAsync(Path.Combine(caseFolder, "dropped.log"), "a file that arrived another way", Ct);
        Directory.CreateDirectory(Path.Combine(caseFolder, "late"));
        await File.WriteAllTextAsync(Path.Combine(caseFolder, "late", "x.log.part"), "half", Ct);
        var other = Directory.CreateDirectory(Path.Combine(h.Folders.Root, "2", ".uploads"));
        await File.WriteAllTextAsync(Path.Combine(h.Folders.Root, "2", "other.log"), "another case", Ct);
        await File.WriteAllTextAsync(Path.Combine(h.Folders.Root, "not-a-case.txt"), "beside the cases", Ct);

        var old = await h.CreatedAsync(bytes.Length, "old.log");
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, old, 0, bytes[..500])).StatusCode);
        await h.AdvanceAsync(TimeSpan.FromHours(12));
        var young = await h.CreatedAsync(bytes.Length, "young.log");
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, young, 0, bytes[..700])).StatusCode);

        // Files in the uploads folder that are not this server's, and leftovers that are.
        var uploads = h.Folders.Uploads(CaseId);
        await File.WriteAllTextAsync(Path.Combine(uploads, "notes.txt"), "someone put this here", Ct);
        await File.WriteAllTextAsync(Path.Combine(other.FullName, "README"), "nor this", Ct);
        var orphan = Path.Combine(uploads, UploadFolders.NewId());
        await File.WriteAllBytesAsync(orphan, new byte[10], Ct);
        File.SetLastWriteTimeUtc(orphan, h.Factory.Clock.GetUtcNow().UtcDateTime.AddHours(-30));
        var fresh = Path.Combine(uploads, UploadFolders.NewId());
        await File.WriteAllBytesAsync(fresh, new byte[10], Ct);
        File.SetLastWriteTimeUtc(fresh, h.Factory.Clock.GetUtcNow().UtcDateTime.AddHours(-1));

        static bool Outside(string path) => !path.Contains(".uploads/", StringComparison.Ordinal);
        var outsideBefore = Listing(h.Folders.Root, Outside);
        var strangersBefore = Listing(h.Folders.Root, p => p.EndsWith("notes.txt", StringComparison.Ordinal) || p.EndsWith("README", StringComparison.Ordinal));

        // 12 hours and a bit later: the first upload is past its 24 hours, the second is not.
        await h.AdvanceAsync(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        var result = await h.Sweeper.RunOnceAsync(Ct);
        Assert.Equal(new UploadSweepResult(0, 2, 1), result);

        Assert.False(File.Exists(h.Folders.Data(CaseId, Id(old))));
        Assert.False(File.Exists(h.Folders.Info(CaseId, Id(old))));
        Assert.False(File.Exists(h.Folders.Info(CaseId, Id(recorded))), "the marker of a finished upload expires like any other");
        Assert.True(File.Exists(h.Folders.Finished(CaseId, Id(recorded))), "the recorded file is evidence and stays");
        Assert.Equal(700, new FileInfo(h.Folders.Data(CaseId, Id(young))).Length);
        Assert.True(File.Exists(h.Folders.Info(CaseId, Id(young))));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(fresh));
        Assert.Equal(outsideBefore, Listing(h.Folders.Root, Outside));
        Assert.Equal(strangersBefore, Listing(h.Folders.Root, p => p.EndsWith("notes.txt", StringComparison.Ordinal) || p.EndsWith("README", StringComparison.Ordinal)));

        // The younger upload goes on and is recorded; a second pass finds nothing to do.
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, young, 700, bytes[700..])).StatusCode);
        Assert.Equal("duplicate", (await StatusAsync(h.Alice, young)).State);
        Assert.Equal(new UploadSweepResult(0, 0, 0), await h.Sweeper.RunOnceAsync(Ct));
        await using var db = h.Factory.Open();
        Assert.Equal(1, await db.Files.CountAsync(Ct));
    }

    [Fact]
    public async Task ASweepOfAnEvidenceRootThatDoesNotExistYetDoesNothingAndCreatesNothing()
    {
        await using var factory = new ApiFactory();
        _ = factory.Services;
        var sweeper = (UploadSweeper)factory.Services.GetService(typeof(UploadSweeper))!;
        Assert.Equal(new UploadSweepResult(0, 0, 0), await sweeper.RunOnceAsync(Ct));
        Assert.False(Directory.Exists(factory.EvidenceRoot));
    }
}
