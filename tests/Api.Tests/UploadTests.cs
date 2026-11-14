using System.Globalization;
using System.Net;
using Urlog.Api.Uploads;
using Urlog.Core.Intake;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using static Urlog.Api.Tests.UploadHarness;

namespace Urlog.Api.Tests;

/// <summary>
/// Web upload, tus 1.0, through the production host: a file sent in chunks becomes a record by the intake pipeline
/// and nothing else; who may upload; what is refused before a byte is taken; what each outcome of intake leaves on
/// disk; and that the evidence root is never served.
/// </summary>
public sealed class UploadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFileSentInChunksBecomesARecordWithItsAuditRowAndItsJob()
    {
        await using var h = await StartAsync();
        var bytes = Log(400);
        var created = await CreateAsync(h.Alice, bytes.Length, Metadata(("filename", "access.log"), ("host", "Web-01"), ("timezone", "+02:00"), ("year", "2026"), ("nginx_log_format", "combined")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var url = created.Headers.Location!.OriginalString;
        Assert.StartsWith(Base + "/", url, StringComparison.Ordinal);
        Assert.True(UploadFolders.IsId(Id(url)));
        Assert.True(created.Headers.Contains("Upload-Expires"));

        var third = bytes.Length / 3;
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..third])).StatusCode);
        Assert.Equal(third, await OffsetAsync(h.Alice, url));
        var before = await StatusAsync(h.Alice, url);
        Assert.Equal(("receiving", (long)third, (long)bytes.Length, "access.log"), (before.State, before.Offset, before.Length, before.Name));
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, third, bytes[third..(2 * third)])).StatusCode);

        // An offset that is not the upload's is a conflict and changes nothing.
        Assert.Equal(HttpStatusCode.Conflict, (await PatchAsync(h.Alice, url, 5, bytes[..10])).StatusCode);
        Assert.Equal(2 * third, await OffsetAsync(h.Alice, url));

        var last = await PatchAsync(h.Alice, url, 2 * third, bytes[(2 * third)..]);
        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.Equal(bytes.Length.ToString(CultureInfo.InvariantCulture), last.Headers.GetValues("Upload-Offset").Single());

        // The 204 of the last chunk means recorded.
        var status = await StatusAsync(h.Alice, url);
        Assert.Equal("done", status.State);
        Assert.Equal(Sha256(bytes), status.Sha256);
        await using var db = h.Factory.Open();
        var file = await db.Files.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(status.FileId, file.Id);
        Assert.Equal((Sha256(bytes), (long)bytes.Length, "access.log", FileSource.Web, "alice"), (file.Sha256, file.Size, file.OriginalName, file.Source, file.Uploader));
        Assert.Equal(UploadFolders.StoredPath(Id(url)), file.StoredPath);
        Assert.Equal("+02:00", file.Timezone);
        Assert.Equal((2026, true), (file.YearHint, file.YearGiven));
        Assert.Equal("web-01", (await db.Hosts.AsNoTracking().SingleAsync(x => x.Id == file.HostId, Ct)).Name);
        Assert.Contains("combined", file.OverrideOptions, StringComparison.Ordinal);
        var job = await db.Jobs.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((file.Id, JobState.Queued, status.JobId), (job.FileId, job.State, job.Id));
        var intake = await db.Audit.AsNoTracking().SingleAsync(a => a.Action == "file_intake", Ct);
        Assert.Equal(("alice", file.Id), (intake.Actor, intake.TargetId));

        // On disk: the bytes under web/<id>, read-only; the uploads folder holds the sidecar alone.
        var finished = h.Folders.Finished(CaseId, Id(url));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(finished, Ct));
        Assert.True(EvidencePermissions.IsReadOnly(finished));
        Assert.Equal([Id(url) + ".json"], Directory.EnumerateFiles(h.Folders.Uploads(CaseId)).Select(Path.GetFileName));

        // A client that lost the last answer asks again and learns the upload is complete.
        Assert.Equal(bytes.Length, await OffsetAsync(h.Alice, url));
        Assert.Equal(HttpStatusCode.Conflict, (await TerminateAsync(h.Alice, url)).StatusCode);
        Assert.True(File.Exists(finished));
    }

    [Fact]
    public async Task OptionsNamesTheExtensionsTheLimitAndTheChecksumAlgorithms()
    {
        await using var h = await StartAsync();
        var response = await h.Alice.SendAsync(new HttpRequestMessage(HttpMethod.Options, Base));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("1.0.0", response.Headers.GetValues("Tus-Version").Single());
        Assert.Equal(["checksum", "creation", "expiration", "termination"], response.Headers.GetValues("Tus-Extension").Single().Split(',').Order());
        Assert.Equal("21474836480", response.Headers.GetValues("Tus-Max-Size").Single());
        Assert.Equal(["sha1", "sha256"], response.Headers.GetValues("Tus-Checksum-Algorithm").Single().Split(',').Order());
    }

    [Fact]
    public async Task OneByteOverTheLimitIsRefusedAtCreationAndTheLimitItselfIsTaken()
    {
        await using var h = await StartAsync();
        const long limit = 20L << 30;
        var over = await CreateAsync(h.Alice, limit + 1, Metadata(("filename", "huge.log")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.False(Directory.Exists(h.Folders.Uploads(CaseId)));

        var exact = await CreateAsync(h.Alice, limit, Metadata(("filename", "huge.log")));
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);

        // A length that is declared cannot be exceeded later either.
        var url = await h.CreatedAsync(10);
        var tooMany = await PatchAsync(h.Alice, url, 0, new byte[11]);
        Assert.False(tooMany.IsSuccessStatusCode);
        await using var db = h.Factory.Open();
        Assert.Equal(0, await db.Files.CountAsync(Ct));
    }

    [Fact]
    public async Task AConfiguredLimitIsTheLargestFileThatIsTakenWholeAndOneByteMoreIsRefused()
    {
        const int limit = 4096;
        await using var h = await StartAsync(new Dictionary<string, string?> { ["Urlog:Upload:MaxFileBytes"] = "4096" });
        var options = await h.Alice.SendAsync(new HttpRequestMessage(HttpMethod.Options, Base));
        Assert.Equal("4096", options.Headers.GetValues("Tus-Max-Size").Single());

        var over = await CreateAsync(h.Alice, limit + 1, Metadata(("filename", "over.log")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.False(Directory.Exists(h.Folders.Uploads(CaseId)));

        // A file of exactly the limit goes through to its record.
        var bytes = Log(100)[..limit];
        var url = await h.UploadAsync(bytes, "exact.log", chunk: 1500);
        var status = await StatusAsync(h.Alice, url);
        Assert.Equal(("done", (long)limit, Sha256(bytes)), (status.State, status.Length, status.Sha256));
        await using var db = h.Factory.Open();
        Assert.Equal(limit, (await db.Files.AsNoTracking().SingleAsync(Ct)).Size);
    }

    [Fact]
    public async Task AChunkWithAWrongChecksumIsRefusedAndTheUploadGoesOnFromTheLastGoodOffset()
    {
        await using var h = await StartAsync();
        var bytes = Log(300);
        var half = bytes.Length / 2;
        var url = await h.CreatedAsync(bytes.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..half], Checksum("sha256", bytes[..half]))).StatusCode);

        var damaged = bytes[half..];
        damaged[7] ^= 0x20;
        var refused = await PatchAsync(h.Alice, url, half, damaged, Checksum("sha256", bytes[half..]));
        Assert.Equal(460, (int)refused.StatusCode);
        Assert.Equal(half, await OffsetAsync(h.Alice, url));
        Assert.Equal(half, new FileInfo(h.Folders.Data(CaseId, Id(url))).Length);
        await using var db = h.Factory.Open();
        Assert.Equal(0, await db.Files.CountAsync(Ct));

        // An algorithm the server does not offer, and a header that is not one, are refused before the bytes.
        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(h.Alice, url, half, bytes[half..], "md5 AAAA")).StatusCode);
        Assert.Equal(half, await OffsetAsync(h.Alice, url));

        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, half, bytes[half..], Checksum("sha1", bytes[half..]))).StatusCode);
        Assert.Equal(Sha256(bytes), (await db.Files.AsNoTracking().SingleAsync(Ct)).Sha256);
    }

    [Fact]
    public async Task AReadOnlyMemberIs403ANonMember404AndAClosedCase409()
    {
        await using var h = await StartAsync();
        var metadata = Metadata(("filename", "access.log"));
        using var carol = await h.LoginAsync("carol");
        var asReader = await CreateAsync(carol, 10, metadata);
        Assert.Equal(HttpStatusCode.Forbidden, asReader.StatusCode);
        Assert.Contains("needs rw", await asReader.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var bob = await h.LoginAsync("bob");
        Assert.Equal(HttpStatusCode.NotFound, (await CreateAsync(bob, 10, metadata)).StatusCode);

        using var dave = await h.LoginAsync("dave");
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(dave, 10, metadata)).StatusCode);

        // An upload that was under way when the case was closed takes no more bytes either.
        var bytes = Log(20);
        var url = await h.CreatedAsync(bytes.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..100])).StatusCode);
        await using (var db = h.Factory.Open())
        {
            await LifecycleOperations.CloseAsync(db, h.Factory.Store.Audit, new Core.Audit.AuditActor("alice", 1), CaseId, h.Factory.Clock, Ct);
        }

        var closed = await CreateAsync(h.Alice, 10, metadata);
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.Contains("case 1 is closed; only an active case takes files", await closed.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await PatchAsync(h.Alice, url, 100, bytes[100..])).StatusCode);
        Assert.Equal(100, await OffsetAsync(h.Alice, url));
        Assert.Equal(HttpStatusCode.NoContent, (await TerminateAsync(h.Alice, url)).StatusCode);
    }

    [Fact]
    public async Task ThePausedQueueAndAVolumeUnderItsMarkRefuseAtCreation()
    {
        await using var h = await StartAsync();
        var metadata = Metadata(("filename", "access.log"));

        // 1,000 GB volume, mark 100 GB. 130 GB free: 20 GB fit, 40 GB do not.
        h.Factory.Disk.FreeBytes = 130L << 30;
        var tooBig = await CreateAsync(h.Alice, 20L << 30, metadata);
        Assert.Equal(HttpStatusCode.Created, tooBig.StatusCode);

        // The first upload has sent nothing yet, so its 20 GB are owed: 10 more fit, 11 do not.
        var second = await CreateAsync(h.Alice, 11L << 30, metadata);
        Assert.Equal(HttpStatusCode.InsufficientStorage, second.StatusCode);
        Assert.Contains("under the mark", await second.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(h.Alice, 10L << 30, metadata)).StatusCode);

        h.Factory.Disk.FreeBytes = 500L << 30;
        await using (var db = h.Factory.Open())
        {
            await JobOperations.PauseAsync(db, h.Factory.Store.Audit, "test", new Dictionary<string, object?> { ["volume"] = "x" }, Ct);
        }

        var paused = await CreateAsync(h.Alice, 10, metadata);
        Assert.Equal(HttpStatusCode.InsufficientStorage, paused.StatusCode);
        Assert.Contains("ingest is paused by the disk watermark", await paused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(2, h.Folders.Ids(CaseId).Count());
    }

    [Fact]
    public async Task TerminatingAnUnfinishedUploadLeavesNoFileAndNoRecord()
    {
        await using var h = await StartAsync();
        var bytes = Log(50);
        var url = await h.CreatedAsync(bytes.Length);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..200])).StatusCode);
        Assert.Equal(2, Directory.EnumerateFiles(h.Folders.Uploads(CaseId)).Count());

        Assert.Equal(HttpStatusCode.NoContent, (await TerminateAsync(h.Alice, url)).StatusCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.Folders.Uploads(CaseId)));
        Assert.False(Directory.Exists(Path.Combine(h.Folders.CaseDirectory(CaseId), UploadFolders.FinishedFolder)));
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(h.Alice, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(h.Alice, url, 200, bytes[200..])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Alice.GetAsync(url)).StatusCode);
        await using var db = h.Factory.Open();
        Assert.Equal(0, await db.Files.CountAsync(Ct));
        Assert.Equal(0, await db.Jobs.CountAsync(Ct));
        Assert.Equal(0, await db.Audit.CountAsync(a => a.Action.StartsWith("file_"), Ct));
    }

    [Fact]
    public async Task AnUploadBelongsToItsCreatorAndIs404ToAnotherMember()
    {
        await using var h = await StartAsync();
        var bytes = Log(20);
        var url = await h.CreatedAsync(bytes.Length);
        using var dave = await h.LoginAsync("dave");
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(dave, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(dave, url, 0, bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TerminateAsync(dave, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dave.GetAsync(url)).StatusCode);

        // An id that is no id, and a path dressed as one, name nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(h.Alice, Base + "/..%2F..%2Fmetadata.db")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(h.Alice, Base + "/" + new string('A', 32))).StatusCode);
        Assert.Equal(0, await OffsetAsync(h.Alice, url));
    }

    [Fact]
    public async Task AMethodOverrideHeaderIsRefusedSoASafeMethodCannotCarryADelete()
    {
        await using var h = await StartAsync();
        var url = await h.CreatedAsync(10);
        foreach (var method in new[] { HttpMethod.Head, HttpMethod.Options })
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("Tus-Resumable", "1.0.0");
            request.Headers.Add("X-HTTP-Method-Override", "DELETE");
            // No CSRF token: HEAD and OPTIONS need none, which is why the header must not turn them into anything else.
            var response = await h.Alice.SendAsync(request, csrf: false);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, await OffsetAsync(h.Alice, url));
    }

    [Fact]
    public async Task AWritingRequestNeedsTheCsrfTokenAndNothingAnswersBeforeTheStoreIdentityCheck()
    {
        await using var h = await StartAsync();
        var url = await h.CreatedAsync(10);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(h.Alice, url, 0, new byte[10], csrf: false)).StatusCode);
        Assert.Equal(0, await OffsetAsync(h.Alice, url));

        await using var unpaired = new ApiFactory(paired: false);
        using var client = unpaired.Client();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await CreateAsync(client, 10, Metadata(("filename", "a.log")))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await HeadAsync(client, url)).StatusCode);
        Assert.False(Directory.Exists(unpaired.EvidenceRoot));
    }

    public static TheoryData<string, string, string> BadMetadata() => new()
    {
        { "", "", "Upload-Metadata filename: required" },
        { "host", "web-01", "Upload-Metadata filename: required" },
        { "filename", "   ", "Upload-Metadata filename: required" },
        { "filename", "a\u0007.log", "Upload-Metadata filename: control characters" },
        { "filename", new string('n', 256), "Upload-Metadata filename: longer than 255" },
        { "host", "web 01", "Upload-Metadata host: not a host name" },
        { "host", "-web", "Upload-Metadata host: not a host name" },
        { "host", new string('h', 254), "Upload-Metadata host: longer than 253" },
        { "timezone", "Mars/Olympus_Mons", "Upload-Metadata timezone: unknown zone" },
        { "year", "20x6", "Upload-Metadata year: four digits" },
        { "year", "1969", "Upload-Metadata year: four digits" },
        { "year", "2999", "Upload-Metadata year: four digits" },
        { "nginx_log_format", "$remote_addr$status", "Upload-Metadata nginx_log_format: log_format:" },
        { "nginx_log_format", "no variables here", "Upload-Metadata nginx_log_format: log_format: no variables" },
        { "nginx_log_format", new string('x', 4097), "Upload-Metadata nginx_log_format: longer than 4096" },
        { "name", "a\u0007.log", "Upload-Metadata name: control characters" },
        { "name", new string('n', 256), "Upload-Metadata name: longer than 255" },
        { "name", "   ", "Upload-Metadata filename: required" },
        { "filetype", "text/plain", "Upload-Metadata filename: required" },
        { "contentType", "text/plain", "Upload-Metadata contentType: unknown key" },
        { "Filename", "access.log", "Upload-Metadata Filename: unknown key" },
        { "relativepath", "logs/access.log", "Upload-Metadata relativepath: unknown key" },
        { "hostname", "web-01", "Upload-Metadata hostname: unknown key" },
    };

    [Theory]
    [MemberData(nameof(BadMetadata))]
    public async Task ABadMetadataValueIs400WithTheFieldNamedAndNothingIsCreated(string key, string value, string expected)
    {
        await using var h = await StartAsync();
        var pairs = new List<(string, string)>();
        if (key is not ("filename" or "name") && !expected.Contains("filename: required", StringComparison.Ordinal))
        {
            pairs.Add(("filename", "access.log"));
        }

        if (key.Length > 0)
        {
            pairs.Add((key, value));
        }

        var response = await CreateAsync(h.Alice, 100, pairs.Count == 0 ? null : Metadata([.. pairs]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.False(Directory.Exists(h.Folders.Uploads(CaseId)));
    }

    public static TheoryData<string, string, string> BrowserMetadata() => new()
    {
        // (the keys a client sends, the name it should be recorded under, a note for the reader)
        { "name=from-name.log", "from-name.log", "name alone is the original name" },
        { "name=from-name.log;filename=from-filename.log", "from-filename.log", "our own key wins" },
        { "filename=from-filename.log;name=bad\u0007name", "from-filename.log", "the other name is not looked at then" },
        { "filename=a.log;filetype=text/plain", "a.log", "filetype is accepted and not used" },
        { "filename=a.log;type=text/plain", "a.log", "type is accepted and not used" },
        { "filename=a.log;relativePath=../../etc/passwd", "a.log", "relativePath is accepted and not used" },
        { "name=a.log;type=application/x-gzip;filetype=application/x-gzip;relativePath=null", "a.log", "what one common browser client sends" },
        { "filename=a.log;filetype=", "a.log", "an ignored key without a value" },
    };

    [Theory]
    [MemberData(nameof(BrowserMetadata))]
    public async Task TheKeysBrowserClientsSendAreToleratedAndOnlyTheNameIsUsed(string sent, string expectedName, string note)
    {
        await using var h = await StartAsync();
        var pairs = sent.Split(';').Select(p => (p[..p.IndexOf('=')], p[(p.IndexOf('=') + 1)..])).ToArray();
        var bytes = Log(30);
        var url = await h.UploadAsync(bytes, metadata: Metadata(pairs));
        Assert.True((await StatusAsync(h.Alice, url)).Name == expectedName, note);

        // Nothing but the name reaches the record: no type, and no path from the client anywhere.
        await using var db = h.Factory.Open();
        var file = await db.Files.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(expectedName, file.OriginalName);
        Assert.Equal(UploadFolders.StoredPath(Id(url)), file.StoredPath);
        Assert.Null(file.OverrideOptions);
        Assert.Equal(["web"], Directory.EnumerateDirectories(h.Folders.CaseDirectory(CaseId)).Select(Path.GetFileName).Where(d => d != UploadFolders.UploadsFolder));
    }

    [Fact]
    public async Task MetadataThatIsNotUtf8IsRefusedAndGoodValuesAtTheirLimitsAreTaken()
    {
        await using var h = await StartAsync();
        var notUtf8 = await CreateAsync(h.Alice, 10, "filename " + Convert.ToBase64String([0xC3, 0x28]));
        Assert.Equal(HttpStatusCode.BadRequest, notUtf8.StatusCode);
        Assert.Contains("Upload-Metadata filename: not valid UTF-8", await notUtf8.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var year = (h.Factory.Clock.GetUtcNow().Year + 1).ToString(CultureInfo.InvariantCulture);
        var zone = OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin";
        var good = await CreateAsync(h.Alice, 10, Metadata(
            ("filename", new string('n', 251) + ".log"), ("host", "2001:db8::1"), ("timezone", zone), ("year", year),
            ("nginx_log_format", "$remote_addr - $remote_user [$time_local] \"$request\" $status")));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        var withName = await CreateAsync(h.Alice, 10, Metadata(("filename", "журнал доступа.log"), ("year", "1970")));
        Assert.Equal(HttpStatusCode.Created, withName.StatusCode);
    }

    [Fact]
    public async Task AFileThatIsNotALogIsKeptAndAnEmptyFileIsRecordedAtCreation()
    {
        await using var h = await StartAsync();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[200]];
        var url = await h.UploadAsync(png, "screenshot.png");
        var status = await StatusAsync(h.Alice, url);
        Assert.Equal(("done", null), (status.State, status.JobId));

        var empty = await CreateAsync(h.Alice, 0, Metadata(("filename", "empty.log")));
        Assert.Equal(HttpStatusCode.Created, empty.StatusCode);
        var emptyStatus = await StatusAsync(h.Alice, empty.Headers.Location!.OriginalString);
        Assert.Equal("done", emptyStatus.State);

        await using var db = h.Factory.Open();
        var files = await db.Files.AsNoTracking().OrderBy(f => f.Id).ToListAsync(Ct);
        Assert.Equal([FileState.Artifact, FileState.Artifact], files.Select(f => f.State));
        Assert.Equal([208L, 0L], files.Select(f => f.Size));
        Assert.Equal(0, await db.Jobs.CountAsync(Ct));
    }

    [Fact]
    public async Task BytesTheCaseAlreadyHoldsAreADuplicateAndTheSecondCopyIsRemoved()
    {
        await using var h = await StartAsync();
        var bytes = Log(100);
        var first = await h.UploadAsync(bytes, "access.log");
        var second = await h.UploadAsync(bytes, "access-copy.log");

        var status = await StatusAsync(h.Alice, second);
        Assert.Equal("duplicate", status.State);
        Assert.Equal((await StatusAsync(h.Alice, first)).FileId, status.FileId);
        Assert.Contains("access.log", status.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(h.Folders.Finished(CaseId, Id(first))));
        Assert.False(File.Exists(h.Folders.Finished(CaseId, Id(second))));
        Assert.False(File.Exists(h.Folders.Data(CaseId, Id(second))));
        await using var db = h.Factory.Open();
        Assert.Equal(1, await db.Files.CountAsync(Ct));
        Assert.Equal(1, await db.Jobs.CountAsync(Ct));
        Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == "file_duplicate", Ct));

        // The marker of a duplicate may be removed by its owner; the first file is not touched by it.
        Assert.Equal(HttpStatusCode.NoContent, (await TerminateAsync(h.Alice, second)).StatusCode);
        Assert.True(File.Exists(h.Folders.Finished(CaseId, Id(first))));
    }

    [Fact]
    public async Task APacketCaptureIsRefusedByIntakeAndNothingOfItIsKept()
    {
        await using var h = await StartAsync();
        byte[] pcap = [0xD4, 0xC3, 0xB2, 0xA1, 2, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 1, 0, 0, 0, .. new byte[64]];
        var url = await h.UploadAsync(pcap, "capture.pcap");
        var status = await StatusAsync(h.Alice, url);
        Assert.Equal("rejected", status.State);
        Assert.Equal(Sha256(pcap), status.Sha256);
        Assert.False(string.IsNullOrEmpty(status.Message));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Folders.CaseDirectory(CaseId), UploadFolders.FinishedFolder)));
        await using var db = h.Factory.Open();
        Assert.Equal(0, await db.Files.CountAsync(Ct));
        Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == "file_rejected", Ct));
    }

    [Fact]
    public async Task AnUploadExpires24HoursAfterItsLastByte()
    {
        await using var h = await StartAsync();
        var bytes = Log(50);
        var url = await h.CreatedAsync(bytes.Length);
        await h.AdvanceAsync(TimeSpan.FromHours(23));
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(h.Alice, url, 0, bytes[..100])).StatusCode);

        // 46 hours after creation, 23 after the last byte: still here.
        await h.AdvanceAsync(TimeSpan.FromHours(23));
        Assert.Equal(100, await OffsetAsync(h.Alice, url));

        await h.AdvanceAsync(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(h.Alice, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(h.Alice, url, 100, bytes[100..])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Alice.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task TheEvidenceRootIsNotServedAsAFileOfTheSiteOrThroughTheApi()
    {
        var site = Directory.CreateTempSubdirectory("urlog-site-");
        try
        {
            // A site with static files on, and the evidence root beside its web root.
            var web = Directory.CreateDirectory(Path.Combine(site.FullName, "wwwroot"));
            await File.WriteAllTextAsync(Path.Combine(web.FullName, "hello.txt"), "served", Ct);
            var evidence = Path.Combine(site.FullName, "data", "intake");
            await using var h = await StartAsync(new Dictionary<string, string?> { ["webroot"] = web.FullName, ["Urlog:Ingest:EvidenceRoot"] = evidence });
            var bytes = Log(40);
            var url = await h.UploadAsync(bytes);
            var id = Id(url);
            Assert.True(File.Exists(Path.Combine(evidence, "1", "web", id)));
            using var anonymous = h.Factory.Client();
            Assert.Equal("served", await (await anonymous.GetAsync("/hello.txt")).Content.ReadAsStringAsync(Ct));

            string[] paths =
            [
                $"/data/intake/1/web/{id}", $"/intake/1/web/{id}", $"/1/web/{id}", $"/web/{id}", $"/{id}",
                $"/../data/intake/1/web/{id}", $"/%2e%2e/data/intake/1/web/{id}", $"/..%2Fdata%2Fintake%2F1%2Fweb%2F{id}",
                $"/..%5Cdata%5Cintake%5C1%5Cweb%5C{id}", $"/hello.txt/../../data/intake/1/web/{id}",
                $"/api/v1/cases/1/web/{id}", $"/api/v1/cases/1/files/{id}", $"/api/v1/cases/1/uploads/{id}/content",
                $"/api/v1/cases/1/uploads/..%2Fweb%2F{id}", $"/api/v1/cases/1/.uploads/{id}.json",
            ];
            var marker = System.Text.Encoding.ASCII.GetString(bytes, 0, 60);
            foreach (var client in new[] { anonymous, h.Alice })
            {
                foreach (var path in paths)
                {
                    var response = await client.Http.SendAsync(Raw(client, path), Ct);
                    var body = await response.Content.ReadAsStringAsync(Ct);
                    Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest, $"{path}: {(int)response.StatusCode}");
                    Assert.True(client != h.Alice || response.StatusCode != HttpStatusCode.Unauthorized, path);
                    Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
                }
            }

            // The one answer about the upload is its status, which holds no byte of the file.
            var status = await h.Alice.GetAsync(url);
            Assert.DoesNotContain(marker, await status.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var file in site.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            site.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AHostWhoseEvidenceRootLiesInsideTheWebRootDoesNotStart()
    {
        var site = Directory.CreateTempSubdirectory("urlog-site-");
        try
        {
            await using var factory = new ApiFactory(new Dictionary<string, string?>
            {
                ["webroot"] = site.FullName,
                ["Urlog:Ingest:EvidenceRoot"] = Path.Combine(site.FullName, "data", "intake"),
            });
            var e = Assert.Throws<InvalidOperationException>(() => factory.Services);
            Assert.Contains("lies inside the web root", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            site.Delete(recursive: true);
        }
    }

    /// <summary>A GET with the session's cookie when there is one. The client folds plain dot segments before it sends; the encoded forms reach the server.</summary>
    private static HttpRequestMessage Raw(ApiClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(ApiFactory.ClientAddressHeader, client.Address);
        if (client.Cookie is not null)
        {
            request.Headers.Add("Cookie", client.Cookie);
        }

        return request;
    }
}
