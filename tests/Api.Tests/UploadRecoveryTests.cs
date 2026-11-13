using System.Net;
using Urlog.Api.Uploads;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static Urlog.Api.Tests.UploadHarness;

namespace Urlog.Api.Tests;

/// <summary>
/// A process that stops between an upload's last byte and its record: the next pass finishes the hand-over to
/// intake from wherever it stopped, and a file that was already recorded is not handed over twice.
/// </summary>
public sealed class UploadRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AConnectionCutWhileTheLastRequestIsStillAtWorkLeavesOneRecordAndACompleteUpload()
    {
        await using var h = await StartAsync();
        var bytes = Log(200);
        var half = bytes.Length / 2;
        var url = await h.CreatedAsync(bytes.Length);
        var id = Id(url);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..half])).StatusCode);

        // Another writer holds the metadata file, so the last request stands still inside intake: every byte is
        // received, the file has its final name, and the record is not written yet.
        await using var blocker = new SqliteConnection($"Data Source={h.Factory.MetadataPath}");
        await blocker.OpenAsync(Ct);
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync(Ct);
        }

        using var cut = new CancellationTokenSource();
        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = new ByteArrayContent(bytes[half..]) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/offset+octet-stream");
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Offset", half.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(ApiFactory.ClientAddressHeader, h.Alice.Address);
        request.Headers.Add("Cookie", h.Alice.Cookie);
        request.Headers.Add(Auth.CsrfMiddleware.HeaderName, h.Alice.Csrf);
        var last = h.Alice.Http.SendAsync(request, cut.Token);

        var finished = h.Folders.Finished(CaseId, id);
        await WaitAsync(() => Task.FromResult(File.Exists(finished)), "the file never got its final name");
        Assert.False(last.IsCompleted);

        // The client, or a proxy in front of it, gives up on the request.
        await cut.CancelAsync();
        await Task.Delay(300, Ct);
        await using (var db = h.Factory.Open())
        {
            Assert.Equal(0, await db.Files.CountAsync(Ct));
        }

        await using (var release = blocker.CreateCommand())
        {
            release.CommandText = "ROLLBACK";
            await release.ExecuteNonQueryAsync(Ct);
        }

        // Nobody is waiting for the answer and the file is recorded all the same.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => last);
        await WaitAsync(async () => (await StatusAsync(h.Alice, url)).State == "done", "the upload was never recorded");
        var status = await StatusAsync(h.Alice, url);
        Assert.Equal(Sha256(bytes), status.Sha256);

        // What a client does after a lost answer: ask how far the upload is. It is complete.
        Assert.Equal(bytes.Length, await OffsetAsync(h.Alice, url));

        // And what a cruder client does: send the last chunk again, at its old offset and at the end.
        Assert.Equal(HttpStatusCode.Conflict, (await PatchAsync(h.Alice, url, half, bytes[half..])).StatusCode);
        Assert.False((await PatchAsync(h.Alice, url, bytes.Length, [])).IsSuccessStatusCode);
        Assert.False((await PatchAsync(h.Alice, url, bytes.Length, bytes[half..])).IsSuccessStatusCode);
        await h.Sweeper.RunOnceAsync(Ct);

        await using var after = h.Factory.Open();
        var file = await after.Files.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((status.FileId, Sha256(bytes), (long)bytes.Length), (file.Id, file.Sha256, file.Size));
        Assert.Equal(1, await after.Jobs.CountAsync(Ct));
        Assert.Equal(1, await after.Audit.CountAsync(a => a.Action == "file_intake", Ct));
        Assert.Equal(0, await after.Audit.CountAsync(a => a.Action == "file_duplicate", Ct));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(finished, Ct));
        Assert.Equal("done", (await StatusAsync(h.Alice, url)).State);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, string otherwise)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail(otherwise);
    }

    [Fact]
    public async Task AllBytesOnDiskAndNothingElseDoneIsFinishedByTheNextPass()
    {
        await using var h = await StartAsync();
        var bytes = Log(80);
        var url = await h.CreatedAsync(bytes.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..1000])).StatusCode);

        // The rest reaches the disk and the process dies before it answers.
        await File.AppendAllBytesAsync(h.Folders.Data(CaseId, Id(url)), bytes[1000..], Ct);
        Assert.Equal("receiving", (await StatusAsync(h.Alice, url)).State);

        Assert.Equal(new UploadSweepResult(1, 0, 0), await h.Sweeper.RunOnceAsync(Ct));
        var status = await StatusAsync(h.Alice, url);
        Assert.Equal(("done", Sha256(bytes)), (status.State, status.Sha256));
        await using var db = h.Factory.Open();
        var file = await db.Files.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(("alice", FileSource.Web, "access.log"), (file.Uploader, file.Source, file.OriginalName));
        Assert.Equal(1, await db.Jobs.CountAsync(Ct));
    }

    [Fact]
    public async Task AFileThatWasMovedButNotRecordedIsRecordedOnce()
    {
        await using var h = await StartAsync();
        var bytes = Log(80);
        var url = await h.CreatedAsync(bytes.Length);
        var id = Id(url);
        await File.WriteAllBytesAsync(h.Folders.Data(CaseId, id), bytes, Ct);
        var info = (await h.Folders.ReadAsync(CaseId, id, Ct))!;
        await h.Folders.WriteAsync(info with { State = UploadState.Finalising }, Ct);
        Directory.CreateDirectory(Path.GetDirectoryName(h.Folders.Finished(CaseId, id))!);
        File.Move(h.Folders.Data(CaseId, id), h.Folders.Finished(CaseId, id));

        Assert.Equal(new UploadSweepResult(1, 0, 0), await h.Sweeper.RunOnceAsync(Ct));
        Assert.Equal(new UploadSweepResult(0, 0, 0), await h.Sweeper.RunOnceAsync(Ct));
        Assert.Equal("done", (await StatusAsync(h.Alice, url)).State);
        await using var db = h.Factory.Open();
        Assert.Equal(Sha256(bytes), (await db.Files.AsNoTracking().SingleAsync(Ct)).Sha256);
        Assert.Equal(0, await db.Audit.CountAsync(a => a.Action == "file_duplicate", Ct));
    }

    [Fact]
    public async Task AFileThatWasRecordedBeforeTheStopIsNotHandedToIntakeAgain()
    {
        await using var h = await StartAsync();
        var bytes = Log(80);
        var url = await h.UploadAsync(bytes);
        var id = Id(url);
        var done = (await h.Folders.ReadAsync(CaseId, id, Ct))!;
        Assert.Equal(UploadState.Done, done.State);

        // The record was committed and the sidecar never learned of it.
        await h.Folders.WriteAsync(done with { State = UploadState.Finalising, FileId = null, Sha256 = null, JobId = null }, Ct);
        Assert.Equal(new UploadSweepResult(1, 0, 0), await h.Sweeper.RunOnceAsync(Ct));

        var status = await StatusAsync(h.Alice, url);
        Assert.Equal(("done", done.FileId, done.Sha256), (status.State, status.FileId, status.Sha256));
        Assert.True(File.Exists(h.Folders.Finished(CaseId, id)));
        await using var db = h.Factory.Open();
        Assert.Equal(1, await db.Files.CountAsync(Ct));
        Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == "file_intake", Ct));
        Assert.Equal(0, await db.Audit.CountAsync(a => a.Action == "file_duplicate", Ct));
    }

    [Fact]
    public async Task ACaseClosedBeforeTheHandOverLeavesTheBytesInTheUploadsFolderWithTheReason()
    {
        await using var h = await StartAsync();
        var bytes = Log(80);
        var url = await h.CreatedAsync(bytes.Length);
        var id = Id(url);
        await File.WriteAllBytesAsync(h.Folders.Data(CaseId, id), bytes, Ct);
        await using (var db = h.Factory.Open())
        {
            await CaseOperations.TransitionAsync(db, h.Factory.Store.Audit, new Core.Audit.AuditActor("alice", 1), CaseId, CaseState.Closed, h.Factory.Clock.GetUtcNow().UtcDateTime, Ct);
        }

        await h.Sweeper.RunOnceAsync(Ct);
        var status = await StatusAsync(h.Alice, url);
        Assert.Equal("failed", status.State);
        Assert.Contains("closed", status.Message, StringComparison.Ordinal);
        Assert.Equal(bytes.Length, new FileInfo(h.Folders.Data(CaseId, id)).Length);
        Assert.False(File.Exists(h.Folders.Finished(CaseId, id)));
        await using var after = h.Factory.Open();
        Assert.Equal(0, await after.Files.CountAsync(Ct));

        // It waits for the sweep like any other upload that will not finish.
        h.Factory.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(new UploadSweepResult(0, 1, 0), await h.Sweeper.RunOnceAsync(Ct));
        Assert.Empty(Directory.EnumerateFiles(h.Folders.Uploads(CaseId)));
    }
}
