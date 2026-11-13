using System.Net;
using Urlog.Api.Uploads;
using Urlog.Core.Intake;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Urlog.Api.Tests.UploadHarness;

namespace Urlog.Api.Tests;

/// <summary>
/// The metadata file is locked by another writer at the moment a complete upload is to be recorded. Inside the
/// retry the last request waits and answers 204. Past it the request answers 503, the upload stays
/// <c>finalising</c> and is recorded seconds later without the sweep. Either way the file is recorded once.
/// The hosts here wait one second for a lock, set in code, so no test waits the store's five.
/// </summary>
public sealed class UploadHandOverRetryTests
{
    private const string RecordWaits = "the record waits for it";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALockThatOutlastsTheBusyTimeoutIsWaitedForAndTheLastRequestAnswers204()
    {
        await using var h = await StartAsync(busyTimeoutSeconds: 1);
        var bytes = Log(200);
        var half = bytes.Length / 2;
        var url = await h.CreatedAsync(bytes.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..half])).StatusCode);
        Assert.Equal(("receiving", (long)half, (long)bytes.Length), await HeadStateAsync(h.Alice, url));

        await using var blocker = await Blocker.HoldAsync(h.Factory.MetadataPath);
        var last = PatchAsync(h.Alice, url, half, bytes[half..]);

        // One attempt at the record has ended busy: the lock has outlived the store's busy timeout.
        await WaitAsync(() => h.Factory.Logs.Lines.Any(l => l.Contains(RecordWaits, StringComparison.Ordinal)), "the record never met the lock");
        Assert.False(last.IsCompleted);

        // While the last request is in flight the protocol answers a HEAD 423; the state is on that answer too, and on the status route.
        var busy = await HeadAsync(h.Alice, url);
        Assert.Equal(HttpStatusCode.Locked, busy.StatusCode);
        Assert.Equal("finalising", busy.Headers.GetValues(UploadEndpoints.StateHeader).Single());
        Assert.Equal("finalising", (await StatusAsync(h.Alice, url)).State);
        await using (var db = h.Factory.Open())
        {
            Assert.Equal(0, await db.Files.CountAsync(Ct));
        }

        await blocker.ReleaseAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await last).StatusCode);

        var status = await StatusAsync(h.Alice, url);
        Assert.Equal(("done", Sha256(bytes)), (status.State, status.Sha256));
        Assert.Equal(("done", (long)bytes.Length, (long)bytes.Length), await HeadStateAsync(h.Alice, url));
        await AssertRecordedOnceAsync(h, bytes);
        // The record was tried more than once and the file was read for its hash once.
        Assert.Equal(1, h.Factory.Services.GetRequiredService<IntakePipeline>().HashPasses);
        Assert.Equal(0, h.Factory.Services.GetRequiredService<PendingHandOvers>().Count);
    }

    [Fact]
    public async Task ALockThatOutlastsTheRetryIs503AndTheRecordFollowsInSecondsWithoutTheSweep()
    {
        // The retry gives up at the first busy answer; the hand-over is tried again every 50 ms.
        await using var h = await StartAsync(
            new Dictionary<string, string?> { ["Urlog:Upload:HandOverBusyTimeout"] = "00:00:00.001", ["Urlog:Upload:HandOverRetry"] = "00:00:00.050" },
            busyTimeoutSeconds: 1);
        var pending = h.Factory.Services.GetRequiredService<PendingHandOvers>();
        var bytes = Log(200);
        var half = bytes.Length / 2;
        var url = await h.CreatedAsync(bytes.Length);
        var id = Id(url);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..half])).StatusCode);

        await using var blocker = await Blocker.HoldAsync(h.Factory.MetadataPath);
        var last = await PatchAsync(h.Alice, url, half, bytes[half..]);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, last.StatusCode);
        Assert.Equal("1", last.Headers.GetValues("Retry-After").Single());
        Assert.Equal("application/problem+json", last.Content.Headers.ContentType!.MediaType);
        Assert.Contains("upload-pending", await last.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // What the client is told: every byte is here, the record is not.
        Assert.Equal(("finalising", (long)bytes.Length, (long)bytes.Length), await HeadStateAsync(h.Alice, url));
        Assert.Equal("finalising", (await StatusAsync(h.Alice, url)).State);
        Assert.True(File.Exists(h.Folders.Finished(CaseId, id)));
        Assert.True(pending.Contains(CaseId, id));
        Assert.Equal(1, pending.Count);
        await using (var db = h.Factory.Open())
        {
            Assert.Equal(0, await db.Files.CountAsync(Ct));
        }

        // Sending the last chunk again is refused; the upload is not listed a second time.
        Assert.False((await PatchAsync(h.Alice, url, half, bytes[half..])).IsSuccessStatusCode);
        Assert.Equal(1, pending.Count);

        await blocker.ReleaseAsync();

        // No sweep runs here: its interval is 15 minutes and its pass at start came before the upload.
        await WaitAsync(() => !pending.Contains(CaseId, id), "the upload was never handed over again");
        var status = await StatusAsync(h.Alice, url);
        Assert.Equal(("done", Sha256(bytes)), (status.State, status.Sha256));
        Assert.Equal(("done", (long)bytes.Length, (long)bytes.Length), await HeadStateAsync(h.Alice, url));
        await AssertRecordedOnceAsync(h, bytes);

        // The retry is over with the upload: nothing is listed, a round does nothing, and neither does a sweep.
        var intake = h.Factory.Services.GetRequiredService<IntakePipeline>();
        var passes = intake.HashPasses;
        Assert.Equal(0, pending.Count);
        Assert.Equal(0, await h.Factory.Services.GetRequiredService<UploadHandOverRetryService>().RunOnceAsync(Ct));
        Assert.Equal(new UploadSweepResult(0, 0, 0), await h.Sweeper.RunOnceAsync(Ct));
        Assert.Equal(passes, intake.HashPasses);
        await AssertRecordedOnceAsync(h, bytes);
    }

    [Fact]
    public async Task TwoHandOversOfOneUploadAtOnceAreOneHashPassAndOneRecord()
    {
        await using var h = await StartAsync();
        // The host's store waits the five seconds every deployment has; only a test's own store is given less.
        Assert.Equal(TimeSpan.FromSeconds(MetadataStore.BusyTimeoutSeconds), h.Factory.Store.BusyTimeout);
        var finaliser = h.Factory.Services.GetRequiredService<UploadFinaliser>();
        var intake = h.Factory.Services.GetRequiredService<IntakePipeline>();
        var bytes = Log(80);
        var url = await h.CreatedAsync(bytes.Length);
        var id = Id(url);
        // Every byte is on disk and nothing has handed the upload over yet.
        await File.WriteAllBytesAsync(h.Folders.Data(CaseId, id), bytes, Ct);

        // The first hand-over stands still at the record, after its hash, while another writer holds the file.
        await using var blocker = await Blocker.HoldAsync(h.Factory.MetadataPath);
        var first = finaliser.FinaliseAsync(CaseId, id);
        await WaitAsync(() => intake.HashPasses == 1, "the first hand-over never reached the hash");
        Assert.True(finaliser.IsRunning(CaseId, id));

        // A second caller, as a repeated request or the retry would be, and a sweep pass, all while the first is at work.
        var second = finaliser.FinaliseAsync(CaseId, id);
        Assert.Equal(new UploadSweepResult(0, 0, 0), await h.Sweeper.RunOnceAsync(Ct));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        await blocker.ReleaseAsync();
        var (a, b) = (await first, await second);

        // Both callers were answered by the one hand-over.
        Assert.Same(a, b);
        Assert.Equal(UploadState.Done, a!.State);
        Assert.Equal(1, intake.HashPasses);
        Assert.False(finaliser.IsRunning(CaseId, id));
        await AssertRecordedOnceAsync(h, bytes);

        // A caller that comes after it finds the upload done and reads nothing.
        Assert.Equal(UploadState.Done, (await finaliser.FinaliseAsync(CaseId, id))!.State);
        Assert.Equal(1, intake.HashPasses);
        await AssertRecordedOnceAsync(h, bytes);
    }

    [Fact]
    public void AnUploadIsListedOnce()
    {
        var pending = new PendingHandOvers();
        Assert.True(pending.Add(1, "a"));
        Assert.False(pending.Add(1, "a"));
        Assert.True(pending.Add(2, "a"));
        Assert.Equal(2, pending.Count);
        Assert.True(pending.Remove(1, "a"));
        Assert.False(pending.Remove(1, "a"));
        Assert.Equal([(2L, "a")], pending.Snapshot());
    }

    private static async Task AssertRecordedOnceAsync(UploadHarness h, byte[] bytes)
    {
        await using var db = h.Factory.Open();
        var file = await db.Files.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((Sha256(bytes), (long)bytes.Length), (file.Sha256, file.Size));
        Assert.Equal(1, await db.Jobs.CountAsync(Ct));
        Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == "file_intake", Ct));
        Assert.Equal(0, await db.Audit.CountAsync(a => a.Action == "file_duplicate", Ct));
    }

    private static async Task WaitAsync(Func<bool> condition, string otherwise)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        Assert.Fail(otherwise);
    }

    /// <summary>Another writer of the metadata file: it holds the write lock from <see cref="HoldAsync"/> to <see cref="ReleaseAsync"/>.</summary>
    private sealed class Blocker(SqliteConnection connection) : IAsyncDisposable
    {
        private bool _held = true;

        public static async Task<Blocker> HoldAsync(string path)
        {
            var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync(Ct);
            await using var begin = connection.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync(Ct);
            return new Blocker(connection);
        }

        public async Task ReleaseAsync()
        {
            if (_held)
            {
                _held = false;
                await using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await connection.DisposeAsync();
        }
    }
}
