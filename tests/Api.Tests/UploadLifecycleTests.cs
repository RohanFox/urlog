using System.Net;
using Urlog.Api.Uploads;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using static Urlog.Api.Tests.UploadHarness;

namespace Urlog.Api.Tests;

/// <summary>
/// Uploads and the case lifecycle: an upload that is under way when its case closes takes no more bytes and says
/// why, its partial bytes go with the ordinary expiry, and a soft-deleted case answers like one that is not there.
/// </summary>
public sealed class UploadLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUploadInFlightWhenTheCaseClosesIsRefusedAndItsBytesExpireAsUsual()
    {
        await using var h = await StartAsync();
        var bytes = Log(40);
        var url = await h.CreatedAsync(bytes.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..300])).StatusCode);

        var owner = new AuditActor("alice", 1);
        await using (var db = h.Factory.Open())
        {
            await LifecycleOperations.CloseAsync(db, h.Factory.Store.Audit, owner, CaseId, h.Factory.Clock, Ct);
        }

        var next = await PatchAsync(h.Alice, url, 300, bytes[300..]);
        Assert.Equal(HttpStatusCode.Conflict, next.StatusCode);
        Assert.Contains("case 1 is closed; only an active case takes files", await next.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var created = await CreateAsync(h.Alice, 10, Metadata(("filename", "late.log")));
        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);
        Assert.Contains("case 1 is closed; only an active case takes files", await created.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // The bytes that arrived before the close are still there, untouched, and no file was recorded.
        var data = h.Folders.Data(CaseId, Id(url));
        Assert.Equal(300, new FileInfo(data).Length);
        Assert.Equal(300, await OffsetAsync(h.Alice, url));
        await using (var db = h.Factory.Open())
        {
            Assert.Empty(db.Files);
        }

        // A sweep before the expiry leaves them; the one after it removes them with the sidecar, as for any upload.
        await h.AdvanceAsync(TimeSpan.FromHours(23));
        Assert.Equal(new UploadSweepResult(0, 0, 0), await h.Sweeper.RunOnceAsync(Ct));
        Assert.True(File.Exists(data));
        await h.AdvanceAsync(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.Equal(new UploadSweepResult(0, 1, 0), await h.Sweeper.RunOnceAsync(Ct));
        Assert.False(File.Exists(data));
        Assert.False(File.Exists(h.Folders.Info(CaseId, Id(url))));

        // Reopened, the case takes files again.
        await using (var db = h.Factory.Open())
        {
            await LifecycleOperations.ReopenAsync(db, h.Factory.Store.Audit, owner, CaseId, h.Factory.Clock, Ct);
        }

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(h.Alice, 10, Metadata(("filename", "again.log")))).StatusCode);
    }

    [Fact]
    public async Task ASoftDeletedCaseAnswersUploadsLikeACaseThatIsNotThere()
    {
        await using var h = await StartAsync();
        var bytes = Log(20);
        var url = await h.CreatedAsync(bytes.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..100])).StatusCode);
        await using (var db = h.Factory.Open())
        {
            var name = db.Cases.Single(c => c.Id == CaseId).Name;
            await LifecycleOperations.SoftDeleteAsync(db, h.Factory.Store.Audit, new AuditActor("alice", 1), CaseId, name, TimeSpan.FromDays(7), h.Factory.Clock, Ct);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await CreateAsync(h.Alice, 10, Metadata(("filename", "x.log")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(h.Alice, url, 100, bytes[100..])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(h.Alice, url)).StatusCode);
        Assert.Equal(100, new FileInfo(h.Folders.Data(CaseId, Id(url))).Length);
    }
}
