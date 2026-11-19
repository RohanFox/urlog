using System.Net;
using System.Text.Json;
using Urlog.Api.Auth;
using Urlog.Core.Audit;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Urlog.Api.Tests;

/// <summary>
/// The cases, hosts, files and jobs routes: what each answers in every state of its case, that every change is on
/// the audit trail, that a change names the version it was made against, and that a change which would have files
/// parsed again says what it costs before it is made.
/// </summary>
public sealed class CasesApiTests(ITestOutputHelper output)
{
    private const string Password = "correct horse battery staple";

    // With invariant globalization Windows resolves its own zone ids only.
    private static readonly string Berlin = OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin";

    private static readonly long[] SecondFile = [2];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Every route that changes a case, with what it answers when the case is closed, archived and soft-deleted,
    /// and the audit action it writes when it succeeds on a case that is ready for it (null: none at the request).
    /// </summary>
    private static readonly (string Method, string Route, int Closed, int Archived, int Deleted, string? Audit)[] Mutations =
    [
        ("POST", "/api/v1/cases/{caseId:long}/close", 409, 409, 404, AuditActions.CaseClose),
        ("POST", "/api/v1/cases/{caseId:long}/reopen", 200, 409, 404, AuditActions.CaseReopen),
        // No row at the request: the commit of the archive names who asked.
        ("POST", "/api/v1/cases/{caseId:long}/archive", 202, 409, 404, null),
        // In this scene no archive is on record, so a re-import is refused in every state; the re-import itself is tested with the executor.
        ("POST", "/api/v1/cases/{caseId:long}/reimport", 409, 409, 404, null),
        ("POST", "/api/v1/cases/{caseId:long}/delete", 204, 204, 404, AuditActions.CaseSoftDelete),
        ("POST", "/api/v1/cases/{caseId:long}/restore", 409, 409, 200, AuditActions.CaseRestore),
        ("POST", "/api/v1/cases/{caseId:long}/hosts", 409, 409, 404, AuditActions.HostCreate),
        ("PATCH", "/api/v1/cases/{caseId:long}/hosts/{hostId:long}", 409, 409, 404, AuditActions.HostUpdate),
        ("POST", "/api/v1/cases/{caseId:long}/hosts/{hostId:long}/merge", 409, 409, 404, AuditActions.HostMerge),
        ("PATCH", "/api/v1/cases/{caseId:long}/files/{fileId:long}", 409, 409, 404, AuditActions.FileUpdate),
        ("PUT", "/api/v1/cases/{caseId:long}/files/{fileId:long}/override", 409, 409, 404, AuditActions.FileOverride),
        ("DELETE", "/api/v1/cases/{caseId:long}/files/{fileId:long}/override", 409, 409, 404, AuditActions.FileOverride),
        ("POST", "/api/v1/cases/{caseId:long}/files/assign-host", 409, 409, 404, AuditActions.FileBulkHost),
        ("POST", "/api/v1/cases/{caseId:long}/files/override", 409, 409, 404, AuditActions.FileBulkOverride),
        ("POST", "/api/v1/cases/{caseId:long}/files/redetect", 409, 409, 404, AuditActions.FileBulkRedetect),
        // A closed case holds its queued jobs, and one of them can be cancelled there.
        ("POST", "/api/v1/cases/{caseId:long}/jobs/{jobId:long}/cancel", 200, 409, 404, AuditActions.JobCancelled),
        ("POST", "/api/v1/cases/{caseId:long}/jobs/{jobId:long}/retry", 409, 409, 404, AuditActions.JobRetry),
    ];

    public static IEnumerable<TheoryDataRow<string, string, string, int>> RefusalsByState() =>
        from m in Mutations
        from state in new[] { ("closed", m.Closed), ("archived", m.Archived), ("soft_deleted", m.Deleted) }
        select new TheoryDataRow<string, string, string, int>(m.Method, m.Route, state.Item1, state.Item2);

    public static IEnumerable<TheoryDataRow<string, string, string?>> Audited() =>
        from m in Mutations
        where !m.Route.EndsWith("/reimport", StringComparison.Ordinal)
        select new TheoryDataRow<string, string, string?>(m.Method, m.Route, m.Audit);

    [Fact]
    public void TheStateTableListsEveryRouteThatChangesACase()
    {
        // The routes that were there before and have their own tests: members, search, uploads.
        string[] elsewhere = ["/members", "/search", "/sql", "/uploads"];
        var live = CaseAccessTests.CaseRoutes()
            .Where(r => r.Data.Item1 is not ("GET" or "HEAD" or "OPTIONS") && !elsewhere.Any(e => r.Data.Item2.Contains(e, StringComparison.Ordinal)))
            .Select(r => r.Data.Item1 + " " + r.Data.Item2).Order().ToList();
        Assert.Equal(Mutations.Select(m => m.Method + " " + m.Route).Order().ToList(), live);
    }

    [Theory]
    [MemberData(nameof(RefusalsByState))]
    public async Task EveryMutationAnswersAsTheTableSaysInEveryStateOfTheCase(string method, string route, string state, int expected)
    {
        await using var scene = await Scene.CreateAsync();
        switch (state)
        {
            case "closed":
                Assert.Equal(HttpStatusCode.OK, (await scene.Alice.PostAsync("/api/v1/cases/1/close")).StatusCode);
                break;
            case "archived":
                Assert.Equal(HttpStatusCode.OK, (await scene.Alice.PostAsync("/api/v1/cases/1/close")).StatusCode);
                await scene.SetStateAsync("archived");
                break;
            default:
                Assert.Equal(HttpStatusCode.NoContent, (await scene.Alice.PostAsync("/api/v1/cases/1/delete", new { name = "incident 1" })).StatusCode);
                break;
        }

        var rows = await scene.Factory.AuditCountAsync();
        var response = await scene.SendAsync(method, route, prepare: false);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True((int)response.StatusCode == expected, $"{method} {route} in {state}: {(int)response.StatusCode} {text}");
        if (expected == 409)
        {
            // A refusal says why, and leaves no row.
            Assert.Contains("\"title\":\"", text, StringComparison.Ordinal);
            Assert.Equal(rows, await scene.Factory.AuditCountAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Audited))]
    public async Task EveryMutationThatSucceedsIsOnTheTrailAsTheSessionsUser(string method, string route, string? action)
    {
        await using var scene = await Scene.CreateAsync();
        await scene.PrepareAsync(route, method);
        var rows = await scene.Factory.AuditCountAsync();

        var response = await scene.SendAsync(method, route, prepare: true);

        Assert.True(response.IsSuccessStatusCode, $"{method} {route}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        var written = await scene.Factory.AuditSinceAsync(rows);
        if (action is null)
        {
            Assert.Empty(written);
            return;
        }

        var row = Assert.Single(written, r => r.Action == action);
        Assert.Equal(("alice", (long?)scene.AliceId), (row.Actor, row.ActorId));
        Assert.Contains("\"case_name\":\"incident 1\"", row.DetailsJson, StringComparison.Ordinal);
        await using var db = scene.Factory.Open();
        Assert.True((await AuditVerifier.VerifyAsync(scene.Factory.MetadataPath, Ct)).Ok);
    }

    [Fact]
    public async Task AnOwnerFindsAndRestoresASoftDeletedCaseAndNobodyElseSeesIt()
    {
        await using var scene = await Scene.CreateAsync();
        Assert.Equal("[]", await (await scene.Alice.GetAsync("/api/v1/cases?state=deleted")).Content.ReadAsStringAsync(Ct));
        var wrong = await scene.Alice.PostAsync("/api/v1/cases/1/delete", new { name = "Incident 1" });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await scene.Alice.PostAsync("/api/v1/cases/1/delete", new { name = "incident 1" })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await scene.Alice.GetAsync("/api/v1/cases/1")).StatusCode);
        using var deleted = await Json(await scene.Alice.GetAsync("/api/v1/cases?state=deleted"));
        var listed = deleted.RootElement.EnumerateArray().Single();
        Assert.Equal((1, "incident 1", "active"), (listed.GetProperty("id").GetInt32(), listed.GetProperty("name").GetString(), listed.GetProperty("prior_state").GetString()));
        Assert.Equal(scene.Factory.Clock.GetUtcNow().AddDays(7), listed.GetProperty("purge_due").GetDateTimeOffset());
        // Only its owner: a member with another role sees neither the list entry nor the route.
        Assert.Equal("[]", await (await scene.Bob.GetAsync("/api/v1/cases?state=deleted")).Content.ReadAsStringAsync(Ct));
        var asMember = await scene.Bob.PostAsync("/api/v1/cases/1/restore");
        var asStranger = await scene.Dave.PostAsync("/api/v1/cases/1/restore");
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (asMember.StatusCode, asStranger.StatusCode));
        Assert.Equal(await asStranger.Content.ReadAsStringAsync(Ct), await asMember.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.GetAsync("/api/v1/cases?state=archived")).StatusCode);

        var restored = await scene.Alice.PostAsync("/api/v1/cases/1/restore");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using var body = await Json(restored);
        Assert.Equal("active", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await scene.Bob.GetAsync("/api/v1/cases/1")).StatusCode);
        // A purge that has marked the case leaves nothing to restore, and the route says so to its owner.
        Assert.Equal(HttpStatusCode.NoContent, (await scene.Alice.PostAsync("/api/v1/cases/1/delete", new { name = "incident 1" })).StatusCode);
        await scene.SetStateAsync("purging");
        Assert.Equal(HttpStatusCode.NotFound, (await scene.Alice.PostAsync("/api/v1/cases/1/restore")).StatusCode);
    }

    [Fact]
    public async Task AnArchiveIsAskedForOnAClosedCaseOfADeploymentThatArchives()
    {
        await using (var none = await Scene.CreateAsync(archiveRoot: false))
        {
            Assert.Equal(HttpStatusCode.OK, (await none.Alice.PostAsync("/api/v1/cases/1/close")).StatusCode);
            var refused = await none.Alice.PostAsync("/api/v1/cases/1/archive");
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("no archive root is configured", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        await using var scene = await Scene.CreateAsync();
        var open = await scene.Alice.PostAsync("/api/v1/cases/1/archive");
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scene.Alice.PostAsync("/api/v1/cases/1/close")).StatusCode);
        var asked = await scene.Alice.PostAsync("/api/v1/cases/1/archive");
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        using var task = await Json(asked);
        Assert.Equal(("archive", "alice"), (task.RootElement.GetProperty("kind").GetString(), task.RootElement.GetProperty("requested_by").GetString()));
        // The case says what is under way, and nothing else moves it until that has ended.
        using var @case = await Json(await scene.Alice.GetAsync("/api/v1/cases/1"));
        Assert.Equal("archive", @case.RootElement.GetProperty("task").GetProperty("kind").GetString());
        Assert.Equal("none", @case.RootElement.GetProperty("statistics").GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await scene.Alice.PostAsync("/api/v1/cases/1/reopen")).StatusCode);
    }

    [Fact]
    public async Task AnAdministratorPurgesAloneUnlessTwoPersonApprovalIsOn()
    {
        await using var scene = await Scene.CreateAsync();
        var early = await scene.Root.PostAsync("/api/v1/admin/cases/1/purge");
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await scene.Root.PostAsync("/api/v1/admin/cases/1/delete", new { name = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await scene.Root.PostAsync("/api/v1/admin/cases/1/delete", new { name = "incident 1" })).StatusCode);
        Assert.Contains("\"by_admin\":true", (await scene.Factory.AuditSinceAsync(0)).Last(r => r.Action == AuditActions.CaseSoftDelete).DetailsJson, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await scene.Root.PostAsync("/api/v1/admin/cases/1/restore")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await scene.Root.PostAsync("/api/v1/admin/cases/1/delete", new { name = "incident 1" })).StatusCode);

        // Off, the default: the one who asks releases it, and there is nothing to confirm.
        var nothing = await scene.Root2.PostAsync("/api/v1/admin/cases/1/purge/confirm");
        Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode);
        Assert.Contains("two-person approval is off", await nothing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var released = await scene.Root.PostAsync("/api/v1/admin/cases/1/purge");
        Assert.Equal(HttpStatusCode.Accepted, released.StatusCode);
        using var task = await Json(released);
        Assert.Equal(("purge", "running", "root"), (task.RootElement.GetProperty("kind").GetString(), task.RootElement.GetProperty("state").GetString(), task.RootElement.GetProperty("requested_by").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await scene.Root2.PostAsync("/api/v1/admin/cases/1/purge")).StatusCode);
        // An owner without the admin role has no such route.
        Assert.Equal(HttpStatusCode.NotFound, (await scene.Alice.PostAsync("/api/v1/admin/cases/1/purge")).StatusCode);
    }

    [Fact]
    public async Task WithTwoPersonApprovalASecondAdministratorConfirmsAndAskingIsNeverConfirming()
    {
        await using var scene = await Scene.CreateAsync(twoPerson: true);
        Assert.Equal(HttpStatusCode.NoContent, (await scene.Alice.PostAsync("/api/v1/cases/1/delete", new { name = "incident 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await scene.Root2.PostAsync("/api/v1/admin/cases/1/purge/confirm")).StatusCode);

        var asked = await scene.Root.PostAsync("/api/v1/admin/cases/1/purge");
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        using (var task = await Json(asked))
        {
            Assert.Equal("requested", task.RootElement.GetProperty("state").GetString());
        }

        // The admin list shows who asked, so the second one knows what there is to confirm.
        using (var list = await Json(await scene.Root2.GetAsync("/api/v1/admin/cases")))
        {
            var entry = list.RootElement.EnumerateArray().Single();
            Assert.Equal(("soft_deleted", "requested", "root"), (entry.GetProperty("state").GetString(), entry.GetProperty("task").GetProperty("state").GetString(), entry.GetProperty("task").GetProperty("requested_by").GetString()));
        }

        var askedAgain = await scene.Root2.PostAsync("/api/v1/admin/cases/1/purge");
        Assert.Equal(HttpStatusCode.Conflict, askedAgain.StatusCode);
        Assert.Contains("already asked for by root", await askedAgain.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await scene.Root.PostAsync("/api/v1/admin/cases/1/purge/confirm")).StatusCode);
        await using (var db = scene.Factory.Open())
        {
            Assert.Equal(CaseTaskState.Requested, (await db.CaseTasks.AsNoTracking().SingleAsync(Ct)).State);
        }

        var confirmed = await scene.Root2.PostAsync("/api/v1/admin/cases/1/purge/confirm");
        Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        using var released = await Json(confirmed);
        Assert.Equal(("running", "root", "root2"), (released.RootElement.GetProperty("state").GetString(), released.RootElement.GetProperty("requested_by").GetString(), released.RootElement.GetProperty("confirmed_by").GetString()));
        var steps = (await scene.Factory.AuditSinceAsync(0)).Where(r => r.Action == AuditActions.CasePurgeRequest).Select(r => r.Actor).ToArray();
        Assert.Equal(["root", "root2"], steps);
        // A restore is still the owner's until the runner marks the case.
        Assert.Equal(HttpStatusCode.OK, (await scene.Alice.PostAsync("/api/v1/cases/1/restore")).StatusCode);
    }

    [Fact]
    public async Task AChangeNamesTheVersionItWasMadeAgainst()
    {
        await using var scene = await Scene.CreateAsync();
        var missing = await scene.Alice.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/files/1", new { host = "web-02" }, ifMatch: null);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Contains("precondition-required", await missing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode)428, (await scene.Alice.SendAsync(HttpMethod.Put, "/api/v1/cases/1/files/1/override", new { source = "syslog" }, ifMatch: null)).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await scene.Alice.SendAsync(HttpMethod.Delete, "/api/v1/cases/1/files/1/override", null, ifMatch: null)).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await scene.Alice.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/hosts/1", new { timezone = Berlin }, ifMatch: null)).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await scene.Alice.SendAsync(HttpMethod.Post, "/api/v1/cases/1/hosts/1/merge", new { into = 2 }, ifMatch: null)).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await scene.Alice.SendAsync(HttpMethod.Post, "/api/v1/cases/1/files/assign-host?confirm=true", new { pattern = "*.log", host = "web-02" }, ifMatch: null)).StatusCode);

        // The version is the ETag of the record and a member of its body.
        var read = await scene.Alice.GetAsync("/api/v1/cases/1/files/1");
        Assert.Equal("\"1\"", read.Headers.ETag!.Tag);
        var first = await scene.Alice.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/files/1", new { host = "web-02" }, ifMatch: "1");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal("\"2\"", first.Headers.ETag!.Tag);

        var stale = await scene.Bob.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/files/1", new { host = "web-01" }, ifMatch: "1");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var problem = await Json(stale);
        Assert.Equal(("2", "https://urlog/problems/stale"), (problem.RootElement.GetProperty("current").GetString(), problem.RootElement.GetProperty("type").GetString()));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await scene.Bob.SendAsync(HttpMethod.Put, "/api/v1/cases/1/files/1/override", new { source = "syslog" }, ifMatch: "banana")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await scene.Bob.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/hosts/1", new { timezone = Berlin }, ifMatch: "9")).StatusCode);
        using var after = await Json(await scene.Alice.GetAsync("/api/v1/cases/1/files/1"));
        Assert.Equal("web-02", after.RootElement.GetProperty("file").GetProperty("host").GetProperty("name").GetString());
    }

    [Fact]
    public async Task OfTwoPeopleWhoChangeTheSameFileFromTheSameViewOneIsTold()
    {
        await using var scene = await Scene.CreateAsync();
        for (var round = 1; round <= 5; round++)
        {
            var version = round.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var a = scene.Alice.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/files/1", new { year = 2000 + (2 * round) }, ifMatch: version);
            var b = scene.Bob.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/files/1", new { year = 2001 + (2 * round) }, ifMatch: version);
            var codes = (await Task.WhenAll(a, b)).Select(r => (int)r.StatusCode).Order().ToArray();
            Assert.True(codes[1] == 412 && codes[0] is 200 or 202, string.Join(",", codes));
        }

        await using var db = scene.Factory.Open();
        Assert.Equal(6, (await db.Files.AsNoTracking().SingleAsync(f => f.Id == 1, Ct)).SettingsVersion);
        Assert.Equal(5, await db.Audit.CountAsync(a => a.Action == AuditActions.FileUpdate, Ct));
    }

    [Fact]
    public async Task AnOverrideIsShownBesideTheDetectionResultAndSaysWhenItHasTakenEffect()
    {
        await using var scene = await Scene.CreateAsync();
        var format = "$remote_addr [$time_local] \"$request\" $status";
        var set = await scene.Alice.SendAsync(HttpMethod.Put, "/api/v1/cases/1/files/1/override", new { source = "nginx", options = new Dictionary<string, string> { ["nginx.log_format"] = format } }, ifMatch: "1");
        Assert.Equal(HttpStatusCode.Accepted, set.StatusCode);
        using (var body = await Json(set))
        {
            var file = body.RootElement.GetProperty("file");
            // The detection result is what it was; the override is beside it; the visible parse is still the old one.
            Assert.Equal(("log", "syslog", false), (file.GetProperty("detection").GetProperty("kind").GetString(), file.GetProperty("detection").GetProperty("source").GetString(), file.GetProperty("detection").GetProperty("stale").GetBoolean()));
            Assert.Equal(("nginx", format), (file.GetProperty("override").GetProperty("source").GetString(), file.GetProperty("override").GetProperty("options").GetProperty("nginx.log_format").GetString()));
            Assert.Equal(("nginx", "file"), (file.GetProperty("effective").GetProperty("source").GetString(), file.GetProperty("effective").GetProperty("options_from").GetProperty("nginx.log_format").GetString()));
            Assert.Equal((false, 1, 10L, "queued", "reparse"), (file.GetProperty("settings_applied").GetBoolean(), file.GetProperty("parse_generation").GetInt32(), file.GetProperty("rows").GetInt64(), file.GetProperty("reparse").GetProperty("state").GetString(), file.GetProperty("reparse").GetProperty("kind").GetString()));
            Assert.Contains("reparse_pending", file.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
            Assert.Equal((1, 5L), (body.RootElement.GetProperty("reparse").GetProperty("files").GetInt32(), body.RootElement.GetProperty("reparse").GetProperty("bytes").GetInt64()));
        }

        // The first file's re-parse is behind the second file's first parse in the queue.
        await scene.Factory.RunNextJobAsync();
        await scene.Factory.RunNextJobAsync(rows: 42, rejects: 0);
        using (var read = await Json(await scene.Alice.GetAsync("/api/v1/cases/1/files/1")))
        {
            var file = read.RootElement.GetProperty("file");
            Assert.Equal((true, 2, 42L, JsonValueKind.Null), (file.GetProperty("settings_applied").GetBoolean(), file.GetProperty("parse_generation").GetInt32(), file.GetProperty("rows").GetInt64(), file.GetProperty("reparse").ValueKind));
            Assert.Equal("syslog", file.GetProperty("detection").GetProperty("source").GetString());
            Assert.Equal(["reparse", "parse"], read.RootElement.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("kind").GetString()).ToArray());
        }

        var actions = (await scene.Factory.AuditSinceAsync(0)).Where(r => r.TargetType == AuditTargets.File && r.TargetId == 1).Select(r => r.Action).ToArray();
        Assert.Equal([AuditActions.FileIntake, AuditActions.FileOverride, AuditActions.FileReparse], actions);

        var bad = await scene.Alice.SendAsync(HttpMethod.Put, "/api/v1/cases/1/files/1/override", new { source = "nginx", options = new Dictionary<string, string> { ["nginx.log_format"] = "no variables here" } }, ifMatch: "2");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("nginx.log_format", await bad.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.SendAsync(HttpMethod.Put, "/api/v1/cases/1/files/1/override", new { source = "nginx", options = new Dictionary<string, string> { ["nginx.log_fromat"] = "combined" } }, ifMatch: "2")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.SendAsync(HttpMethod.Put, "/api/v1/cases/1/files/1/override", new { }, ifMatch: "2")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await scene.Alice.SendAsync(HttpMethod.Delete, "/api/v1/cases/1/files/1/override", null, ifMatch: "2")).StatusCode);
    }

    [Fact]
    public async Task AHostChangeThatReparsesAnswersWithItsCostUntilItIsConfirmed()
    {
        await using var scene = await Scene.CreateAsync();
        var preview = await scene.Alice.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/hosts/1", new { timezone = Berlin }, ifMatch: "1");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using (var body = await Json(preview))
        {
            Assert.Equal((false, 1, 5L, "UTC", 1), (body.RootElement.GetProperty("applied").GetBoolean(), body.RootElement.GetProperty("reparse").GetProperty("files").GetInt32(), body.RootElement.GetProperty("reparse").GetProperty("bytes").GetInt64(), body.RootElement.GetProperty("host").GetProperty("timezone").GetString(), body.RootElement.GetProperty("host").GetProperty("version").GetInt32()));
        }

        var made = await scene.Alice.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/hosts/1?confirm=true", new { timezone = Berlin }, ifMatch: "1");
        Assert.Equal(HttpStatusCode.Accepted, made.StatusCode);
        using (var body = await Json(made))
        {
            Assert.Equal((true, 1, Berlin, false, 2), (body.RootElement.GetProperty("applied").GetBoolean(), body.RootElement.GetProperty("jobs_queued").GetInt32(), body.RootElement.GetProperty("host").GetProperty("timezone").GetString(), body.RootElement.GetProperty("host").GetProperty("timezone_assumed").GetBoolean(), body.RootElement.GetProperty("host").GetProperty("version").GetInt32()));
        }

        var row = (await scene.Factory.AuditSinceAsync(0)).Last(r => r.Action == AuditActions.HostUpdate);
        Assert.Contains("\"reparse_bytes\":5,\"reparse_files\":1", row.DetailsJson, StringComparison.Ordinal);

        // Default options: a key keeps its dots through the API, and a format that does not compile is refused.
        var options = await scene.Alice.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/hosts/1", new { source_options = new Dictionary<string, string> { ["nginx.log_format"] = "combined" } }, ifMatch: "2");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        using (var hosts = await Json(await scene.Alice.GetAsync("/api/v1/cases/1/hosts")))
        {
            var host = hosts.RootElement.EnumerateArray().Single(h => h.GetProperty("id").GetInt64() == 1);
            Assert.Equal(("combined", 1, 10L), (host.GetProperty("source_options").GetProperty("nginx.log_format").GetString(), host.GetProperty("files").GetInt32(), host.GetProperty("rows").GetInt64()));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/hosts/1", new { source_options = new Dictionary<string, string> { ["nginx.log_format"] = "nothing" } }, ifMatch: "3")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.SendAsync(HttpMethod.Patch, "/api/v1/cases/1/hosts/1", new { timezone = "Mars/Olympus" }, ifMatch: "3")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await scene.Alice.PostAsync("/api/v1/cases/1/hosts", new { name = "WEB-02" })).StatusCode);

        var merged = await scene.Alice.SendAsync(HttpMethod.Post, "/api/v1/cases/1/hosts/2/merge", new { into = 1 }, ifMatch: "1");
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        using var all = await Json(await scene.Alice.GetAsync("/api/v1/cases/1/hosts"));
        Assert.Equal(1, all.RootElement.EnumerateArray().Single(h => h.GetProperty("name").GetString() == "web-02").GetProperty("merged_into").GetInt64());
    }

    [Fact]
    public async Task ABulkChangeIsPreviewedThenConfirmedWithTheDigestOfWhatWasShown()
    {
        await using var scene = await Scene.CreateAsync();
        for (var i = 0; i < 10; i++)
        {
            await scene.Factory.AddFileAsync(1, $"access.log.{i}", "web02-" + i, archivePath: $"bundle.zip!web-02/nginx/access.log.{i}");
        }

        var request = new { pattern = "bundle.zip!web-02/**", host = "web-02" };
        var rows = await scene.Factory.AuditCountAsync();
        var preview = await scene.Alice.PostAsync("/api/v1/cases/1/files/assign-host", request);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        string digest;
        using (var body = await Json(preview))
        {
            digest = body.RootElement.GetProperty("digest").GetString()!;
            Assert.Equal((false, 10, 10, 0), (body.RootElement.GetProperty("applied").GetBoolean(), body.RootElement.GetProperty("matched").GetInt32(), body.RootElement.GetProperty("changed").GetInt32(), body.RootElement.GetProperty("jobs_queued").GetInt32()));
            Assert.Equal("\"" + digest + "\"", preview.Headers.ETag!.Tag);
        }

        Assert.Equal(rows, await scene.Factory.AuditCountAsync());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await scene.Alice.SendAsync(HttpMethod.Post, "/api/v1/cases/1/files/assign-host?confirm=true", request, ifMatch: new string('a', 64))).StatusCode);
        // The selection moves between the preview and the confirmation: refused, nothing changed.
        await scene.Factory.AddFileAsync(1, "access.log.10", "web02-late", archivePath: "bundle.zip!web-02/nginx/access.log.10");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await scene.Alice.SendAsync(HttpMethod.Post, "/api/v1/cases/1/files/assign-host?confirm=true", request, ifMatch: digest)).StatusCode);
        // The arrival of that file is the one row since; the refused confirmations wrote none.
        Assert.Equal(rows + 1, await scene.Factory.AuditCountAsync());
        rows++;

        using (var again = await Json(await scene.Alice.PostAsync("/api/v1/cases/1/files/assign-host", request)))
        {
            digest = again.RootElement.GetProperty("digest").GetString()!;
        }

        var made = await scene.Alice.SendAsync(HttpMethod.Post, "/api/v1/cases/1/files/assign-host?confirm=true", request, ifMatch: digest);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        using (var body = await Json(made))
        {
            // The files wait for their first parse, which reads the host when it is claimed: nothing is queued twice.
            Assert.Equal((true, 11, 0), (body.RootElement.GetProperty("applied").GetBoolean(), body.RootElement.GetProperty("changed").GetInt32(), body.RootElement.GetProperty("jobs_queued").GetInt32()));
        }

        var written = await scene.Factory.AuditSinceAsync(rows);
        var row = Assert.Single(written);
        Assert.Equal((AuditActions.FileBulkHost, AuditTargets.Case, (long?)1), (row.Action, row.TargetType, row.TargetId));
        Assert.Contains("\"count\":11", row.DetailsJson, StringComparison.Ordinal);
        using var listed = await Json(await scene.Alice.GetAsync("/api/v1/cases/1/files?host=2&limit=500"));
        Assert.Equal(11, listed.RootElement.GetProperty("files").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.PostAsync("/api/v1/cases/1/files/assign-host", new { pattern = "{host}/**", host = "web-02" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.PostAsync("/api/v1/cases/1/files/override", new { source = "nginx" })).StatusCode);
    }

    [Fact]
    public async Task ASelectionAboveTheLimitIsRefusedWithItsSizeAndCanBeWalked()
    {
        await using var scene = await Scene.CreateAsync();
        await using (var db = scene.Factory.Open())
        {
            for (var i = 0; i < FileSettingsOperations.BulkLimit + 1; i++)
            {
                db.Files.Add(new EvidenceFile { CaseId = 1, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i))), Size = 10, OriginalName = i + ".log", StoredPath = "x", Source = FileSource.Web, Uploader = "alice", ReceivedAt = DateTime.UtcNow, Encoding = "utf8", ArchivePath = $"big.zip!web-07/{i}.log" });
            }

            await db.SaveChangesAsync(Ct);
        }

        var refused = await scene.Alice.PostAsync("/api/v1/cases/1/files/assign-host", new { pattern = "big.zip!web-07/*", host = "web-07" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("matches 1001 files", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        using var page = await Json(await scene.Alice.PostAsync("/api/v1/cases/1/files/assign-host", new { pattern = "big.zip!web-07/*", host = "web-07", limit = 400 }));
        Assert.Equal((400, JsonValueKind.Number), (page.RootElement.GetProperty("matched").GetInt32(), page.RootElement.GetProperty("next_after_id").ValueKind));
    }

    [Fact]
    public async Task TheFileListIsPagedAndFilteredAndShowsWhatAPersonHasToLookAt()
    {
        await using var scene = await Scene.CreateAsync();
        await scene.Factory.AddFileAsync(1, "notes.pdf", "pdf", f =>
        {
            f.DetectedKind = FileKind.Artifact;
            f.DetectedSource = null;
            f.State = FileState.Artifact;
        });
        await scene.Factory.AddFileAsync(1, "app.log.2.gz", "generic", f =>
        {
            f.DetectedSource = "generic";
            f.DetectionRegistry = "0:generic";
            f.HostInferred = true;
        }, archivePath: "bundle.zip!web-03/app/app.log.2.gz");
        await using (var db = scene.Factory.Open())
        {
            await db.Files.Where(f => f.Id == 2).ExecuteUpdateAsync(s => s.SetProperty(f => f.State, FileState.Quarantined).SetProperty(f => f.SeenPaths, """[{"path":"copy/b.log","host":"web-09","at":"2026-11-18T08:00:00.000000Z"}]"""), Ct);
            await db.Files.Where(f => f.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(f => f.UndatedRows, 3L).SetProperty(f => f.FutureRows, 2L), Ct);
        }

        using (var first = await Json(await scene.Alice.GetAsync("/api/v1/cases/1/files?limit=3")))
        {
            Assert.Equal("1,2,3 next 3", string.Join(",", first.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("id").GetInt64())) + " next " + first.RootElement.GetProperty("next_after").GetInt64());
            var one = first.RootElement.GetProperty("files")[0];
            Assert.Equal((3L, 2L, true), (one.GetProperty("span").GetProperty("undated_events").GetInt64(), one.GetProperty("span").GetProperty("future_events").GetInt64(), one.GetProperty("stored_histogram").GetBoolean()));
            Assert.Contains("undated", one.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
            var two = first.RootElement.GetProperty("files")[1];
            Assert.Equal((true, "quarantined"), (two.GetProperty("quarantined").GetBoolean(), two.GetProperty("state").GetString()));
            Assert.Contains("seen_elsewhere", two.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        }

        using (var rest = await Json(await scene.Alice.GetAsync("/api/v1/cases/1/files?limit=3&after=3")))
        {
            Assert.Equal(("4", JsonValueKind.Null), (string.Join(",", rest.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("id").GetInt64())), rest.RootElement.GetProperty("next_after").ValueKind));
            var stale = rest.RootElement.GetProperty("files")[0];
            Assert.True(stale.GetProperty("detection").GetProperty("stale").GetBoolean());
            Assert.Equal("bundle.zip!web-03/app/app.log.2.gz", stale.GetProperty("archive_path").GetString());
        }

        async Task<string> IdsAsync(string query)
        {
            using var page = await Json(await scene.Alice.GetAsync("/api/v1/cases/1/files?" + query));
            return string.Join(", ", page.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("id").GetInt64()));
        }

        Assert.Equal("2, 3, 4", await IdsAsync("unassigned=true"));
        Assert.Equal("1", await IdsAsync("host=1"));
        Assert.Equal("3", await IdsAsync("kind=artifact"));
        Assert.Equal("2", await IdsAsync("state=quarantined"));
        Assert.Equal("4", await IdsAsync("pattern=" + Uri.EscapeDataString("bundle.zip!**")));
        Assert.Equal("1, 2, 4", await IdsAsync("extension=log"));
        Assert.Equal("4", await IdsAsync("stale=true"));
        Assert.Equal("4", await IdsAsync("q=app.log"));
        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.GetAsync("/api/v1/cases/1/files?state=nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.GetAsync("/api/v1/cases/1/files?limit=501")).StatusCode);

        // The record in full: every place the bytes were seen, and the detection record as it was written.
        using var detail = await Json(await scene.Alice.GetAsync("/api/v1/cases/1/files/2"));
        Assert.Equal("web-09", detail.RootElement.GetProperty("seen")[0].GetProperty("host").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await scene.Alice.GetAsync("/api/v1/cases/1/files/4242")).StatusCode);
    }

    [Fact]
    public async Task AJobShowsItsReasonAndIsCancelledOrRunAgainByAMemberWhoMayWrite()
    {
        await using var scene = await Scene.CreateAsync();
        using (var jobs = await Json(await scene.Carol.GetAsync("/api/v1/cases/1/jobs")))
        {
            Assert.Equal([(2L, "queued"), (1L, "done")], jobs.RootElement.GetProperty("jobs").EnumerateArray().Select(j => (j.GetProperty("id").GetInt64(), j.GetProperty("state").GetString()!)).ToArray());
        }

        var claimed = await scene.Factory.ClaimNextJobAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await scene.Bob.PostAsync($"/api/v1/cases/1/jobs/{claimed.Id}/cancel")).StatusCode);
        await scene.Factory.FailJobAsync(claimed.Id, "chunk 3 at offset 196608: the store refused the batch");
        using (var failed = await Json(await scene.Carol.GetAsync($"/api/v1/cases/1/jobs/{claimed.Id}")))
        {
            Assert.Equal(("failed", "chunk 3 at offset 196608: the store refused the batch", 5L), (failed.RootElement.GetProperty("state").GetString(), failed.RootElement.GetProperty("message").GetString(), failed.RootElement.GetProperty("bytes_total").GetInt64()));
        }

        Assert.Equal(HttpStatusCode.Conflict, (await scene.Bob.PostAsync("/api/v1/cases/1/jobs/1/retry")).StatusCode);
        var retried = await scene.Bob.PostAsync($"/api/v1/cases/1/jobs/{claimed.Id}/retry");
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        long next;
        using (var body = await Json(retried))
        {
            next = body.RootElement.GetProperty("id").GetInt64();
            Assert.Equal("queued", body.RootElement.GetProperty("state").GetString());
        }

        var cancelled = await scene.Bob.PostAsync($"/api/v1/cases/1/jobs/{next}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await scene.Bob.PostAsync($"/api/v1/cases/1/jobs/{next}/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scene.Bob.PostAsync("/api/v1/cases/1/jobs/4242/cancel")).StatusCode);
        var trail = (await scene.Factory.AuditSinceAsync(0)).Where(r => r.Action is AuditActions.JobRetry or AuditActions.JobCancelled).Select(r => (r.Action, r.Actor, r.TargetType)).ToArray();
        Assert.Equal([(AuditActions.JobRetry, "bob", AuditTargets.File), (AuditActions.JobCancelled, "bob", AuditTargets.File)], trail);
        using var filtered = await Json(await scene.Carol.GetAsync("/api/v1/cases/1/jobs?state=failed&file=2"));
        Assert.Equal(claimed.Id, filtered.RootElement.GetProperty("jobs").EnumerateArray().Single().GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task TheDashboardCountsWhatTheFileRecordsHoldAndSaysWhenDiskUseIsMissing()
    {
        await using var scene = await Scene.CreateAsync();
        scene.Factory.Usage.Cases[1] = new Urlog.Core.Lifecycle.CaseUsage(1, "incident 1", CaseState.Active, 2, 10, 10, 0, 0, 0, 25, 4096, []);
        await scene.Factory.ClaimNextJobAsync();

        using (var body = await Json(await scene.Carol.GetAsync("/api/v1/cases/1/dashboard")))
        {
            var root = body.RootElement;
            Assert.Equal((1, "incident 1", "active", "ro", "alice"), (root.GetProperty("case").GetProperty("id").GetInt32(), root.GetProperty("case").GetProperty("name").GetString(), root.GetProperty("case").GetProperty("state").GetString(), root.GetProperty("case").GetProperty("my_role").GetString(), root.GetProperty("case").GetProperty("owner").GetString()));
            var files = root.GetProperty("files");
            // Rows and rejects are the sums of the file records: one file is parsed, the other is being parsed.
            Assert.Equal((2, 2, 10L, 10L, 1L), (files.GetProperty("total").GetInt32(), files.GetProperty("arrived").GetInt32(), files.GetProperty("bytes_arrived").GetInt64(), files.GetProperty("rows").GetInt64(), files.GetProperty("rejects").GetInt64()));
            Assert.Equal((1, 1), (files.GetProperty("by_state").GetProperty("parsed").GetInt32(), files.GetProperty("by_state").GetProperty("received").GetInt32()));
            Assert.Equal(2, files.GetProperty("by_kind").GetProperty("log").GetInt32());
            Assert.Equal((1, 0, 0), (files.GetProperty("flags").GetProperty("unassigned").GetInt32(), files.GetProperty("flags").GetProperty("quarantined").GetInt32(), files.GetProperty("flags").GetProperty("reparse_pending").GetInt32()));
            var ingest = root.GetProperty("ingest");
            Assert.Equal((0, 1, 1, 5L, 10L), (ingest.GetProperty("queued").GetInt32(), ingest.GetProperty("parsing").GetInt32(), ingest.GetProperty("done").GetInt32(), ingest.GetProperty("bytes_done").GetInt64(), ingest.GetProperty("bytes_total").GetInt64()));
            Assert.Equal((2L, 5L), (ingest.GetProperty("current")[0].GetProperty("file_id").GetInt64(), ingest.GetProperty("current")[0].GetProperty("bytes_total").GetInt64()));
            var host = root.GetProperty("hosts").EnumerateArray().Single(h => h.GetProperty("name").GetString() == "web-01");
            Assert.Equal((1, 10L, 1L), (host.GetProperty("files").GetInt32(), host.GetProperty("rows").GetInt64(), host.GetProperty("rejects").GetInt64()));
            Assert.Equal(1, root.GetProperty("unassigned").GetProperty("files").GetInt32());
            Assert.Equal((25L, 4096L, 10L), (root.GetProperty("usage").GetProperty("store_rows").GetInt64(), root.GetProperty("usage").GetProperty("store_bytes").GetInt64(), root.GetProperty("usage").GetProperty("recorded_bytes").GetInt64()));
            Assert.Equal(JsonValueKind.Null, root.GetProperty("usage_unavailable").ValueKind);
            Assert.False(root.TryGetProperty("file_list", out _));
            Assert.NotEqual(JsonValueKind.Null, root.GetProperty("span").GetProperty("from").ValueKind);
        }

        // The event store is away: the counts are there all the same, and the answer says what is missing.
        scene.Factory.Usage.Failure = new IOException("connection refused");
        using var without = await Json(await scene.Carol.GetAsync("/api/v1/cases/1/dashboard"));
        Assert.Equal((JsonValueKind.Null, 2), (without.RootElement.GetProperty("usage").ValueKind, without.RootElement.GetProperty("files").GetProperty("total").GetInt32()));
        Assert.Contains("did not answer", without.RootElement.GetProperty("usage_unavailable").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A case of 10,000 file records, 10,000 jobs and 50 hosts: the dashboard is asked for 50 times over HTTP and
    /// the times are written out. The counts are asserted here; the bound on the time is the explicit test below.
    /// </summary>
    [Fact]
    public async Task TheDashboardOfACaseWithTenThousandFilesIsCountedRight()
    {
        var (p50, p95, bytes) = await MeasureDashboardAsync();
        await RecordAsync($"dashboard, 10,000 file records, 10,000 jobs, 50 hosts, without disk use: p50 {p50:F1} ms, p95 {p95:F1} ms, body {bytes} bytes");
    }

    /// <summary>The wall-clock form: p95 at most 100 ms without the disk-use part. Explicit; run alone with <c>tools/timing-tests.ps1</c>.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Timing")]
    public async Task TimingTheDashboardOfTenThousandFilesAnswersInAHundredMilliseconds()
    {
        var (p50, p95, bytes) = await MeasureDashboardAsync();
        await RecordAsync($"dashboard, 10,000 file records: p50 {p50:F1} ms, p95 {p95:F1} ms, body {bytes} bytes");
        Assert.True(p95 <= 100, $"p95 {p95:F1} ms");
    }

    [Fact]
    public async Task AUserChangesTheirOwnPasswordAndEveryOtherSessionOfTheirsEnds()
    {
        const string NewPassword = "an entirely different passphrase";
        await using var scene = await Scene.CreateAsync();
        using var elsewhere = scene.Factory.Client("203.0.113.77");
        Assert.Equal(HttpStatusCode.OK, (await elsewhere.LoginAsync("alice", Password)).StatusCode);
        var rows = await scene.Factory.AuditCountAsync();

        var wrong = await scene.Alice.PostAsync("/api/v1/auth/password", new { current_password = "not the password at all", new_password = NewPassword });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Contains("wrong-password", await wrong.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.PostAsync("/api/v1/auth/password", new { current_password = Password, new_password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await scene.Alice.PostAsync("/api/v1/auth/password", new { current_password = Password, new_password = Password })).StatusCode);
        Assert.Equal(rows, await scene.Factory.AuditCountAsync());
        Assert.Equal(HttpStatusCode.OK, (await elsewhere.GetAsync("/api/v1/auth/me")).StatusCode);

        var changed = await scene.Alice.PostAsync("/api/v1/auth/password", new { current_password = Password, new_password = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // This session goes on, every other one of the user is over, and nobody else's is touched.
        Assert.Equal(HttpStatusCode.OK, (await scene.Alice.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scene.Bob.GetAsync("/api/v1/auth/me")).StatusCode);
        var row = Assert.Single(await scene.Factory.AuditSinceAsync(rows));
        Assert.Equal((AuditActions.UserPasswordChange, "alice", AuditTargets.User), (row.Action, row.Actor, row.TargetType));
        Assert.Contains("\"sessions_ended\":1", row.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPassword, row.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, row.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("argon2", row.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scene.Factory.Logs.Lines, l => l.Contains(NewPassword, StringComparison.Ordinal) || l.Contains(Password, StringComparison.Ordinal));

        using var fresh = scene.Factory.Client("203.0.113.78");
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.LoginAsync("alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fresh.LoginAsync("alice", NewPassword)).StatusCode);
        // No session, no route: the change is not a way to test a password from outside.
        using var anonymous = scene.Factory.Client("203.0.113.79");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/auth/password", new { current_password = Password, new_password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task WrongCurrentPasswordsRunIntoTheLoginThrottle()
    {
        await using var scene = await Scene.CreateAsync();
        // The scene's own login from this address was the first attempt of the window; the limit is five a minute.
        using var client = scene.Factory.Client("203.0.113.90");
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("bob", Password)).StatusCode);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/auth/password", new { current_password = "guess number " + i + " of many", new_password = "whatever comes after this" })).StatusCode);
        }

        var throttled = await client.PostAsync("/api/v1/auth/password", new { current_password = Password, new_password = "whatever comes after this" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.Contains("Retry-After"));
        var row = (await scene.Factory.AuditSinceAsync(0)).Last(r => r.Action == AuditActions.LoginThrottled);
        Assert.Equal("bob", row.Actor);
        // Refused before the password was looked at: it is still the old one.
        scene.Factory.Clock.Advance(TimeSpan.FromMinutes(2));
        using var later = scene.Factory.Client("203.0.113.91");
        Assert.Equal(HttpStatusCode.OK, (await later.LoginAsync("bob", Password)).StatusCode);
    }

    /// <summary>A measured figure: on the output helper, and appended to the file URLOG_API_TIMING names when set.</summary>
    private async Task RecordAsync(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("URLOG_API_TIMING") is { Length: > 0 } path)
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, Ct);
        }
    }

    private static async Task<JsonDocument> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode || (int)response.StatusCode == 412, $"{(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text);
    }

    private static async Task<(double P50, double P95, int Bytes)> MeasureDashboardAsync()
    {
        const int Files = 10_000;
        const int Hosts = 50;
        await using var scene = await Scene.CreateAsync(seed: false);
        await using (var db = scene.Factory.Open())
        {
            for (var h = 0; h < Hosts; h++)
            {
                db.Hosts.Add(new Urlog.Core.Metadata.Host { CaseId = 1, Name = $"host-{h:D2}", CreatedAt = DateTime.UtcNow });
            }

            await db.SaveChangesAsync(Ct);
            var now = DateTime.UtcNow;
            for (var i = 0; i < Files; i++)
            {
                var parsed = i % 20 != 0;
                db.Files.Add(new EvidenceFile
                {
                    CaseId = 1,
                    Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i))),
                    Size = 1_000_000 + i,
                    OriginalName = $"access.log.{i}",
                    StoredPath = "x",
                    Source = FileSource.Web,
                    Uploader = "alice",
                    ReceivedAt = now,
                    Encoding = "utf8",
                    ArchivePath = $"bundle-{i / 1000}.zip!host-{i % Hosts:D2}/nginx/access.log.{i}",
                    ParentFileId = null,
                    HostId = i % 97 == 0 ? null : 1 + (i % Hosts),
                    DetectedKind = FileKind.Log,
                    DetectedSource = i % 3 == 0 ? "generic" : "nginx",
                    DetectionRegistry = i % 500 == 0 ? "0:generic" : null,
                    State = parsed ? FileState.Parsed : FileState.Received,
                    RowCount = parsed ? 4_000 + i : 0,
                    RejectCount = parsed ? i % 7 : 0,
                    TsMin = parsed ? now.AddDays(-7) : null,
                    TsMax = parsed ? now.AddDays(-1) : null,
                    HistogramGeneration = 1,
                });
                if (i % 1000 == 999)
                {
                    await db.SaveChangesAsync(Ct);
                    db.ChangeTracker.Clear();
                }
            }

            await db.SaveChangesAsync(Ct);
            db.ChangeTracker.Clear();
            var ids = await db.Files.AsNoTracking().OrderBy(f => f.Id).Select(f => new { f.Id, f.State }).ToListAsync(Ct);
            foreach (var chunk in ids.Chunk(1000))
            {
                db.Jobs.AddRange(chunk.Select(f => new Job { CaseId = 1, FileId = f.Id, Kind = JobKind.Parse, State = f.State == FileState.Parsed ? JobState.Done : JobState.Queued, Generation = f.State == FileState.Parsed ? 1 : 0, CreatedAt = now }));
                await db.SaveChangesAsync(Ct);
                db.ChangeTracker.Clear();
            }
        }

        // Unmeasured first: the model, the plans and the serializer are built once per process.
        using (var warm = await Json(await scene.Carol.GetAsync("/api/v1/cases/1/dashboard")))
        {
            var files = warm.RootElement.GetProperty("files");
            var parsedCount = Files - (Files / 20);
            Assert.Equal((Files, parsedCount, Files / 20), (files.GetProperty("total").GetInt32(), files.GetProperty("by_state").GetProperty("parsed").GetInt32(), files.GetProperty("by_state").GetProperty("received").GetInt32()));
            long rows = 0;
            for (var i = 0; i < Files; i++)
            {
                rows += i % 20 != 0 ? 4_000 + i : 0;
            }

            Assert.Equal(rows, files.GetProperty("rows").GetInt64());
            Assert.Equal(Hosts, warm.RootElement.GetProperty("hosts").GetArrayLength());
            Assert.Equal(rows, warm.RootElement.GetProperty("hosts").EnumerateArray().Sum(h => h.GetProperty("rows").GetInt64()) + warm.RootElement.GetProperty("unassigned").GetProperty("rows").GetInt64());
            Assert.Equal(Files / 20, warm.RootElement.GetProperty("ingest").GetProperty("queued").GetInt32());
        }

        var times = new List<double>();
        var bytes = 0;
        for (var run = 0; run < 50; run++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var response = await scene.Carol.GetAsync("/api/v1/cases/1/dashboard");
            var text = await response.Content.ReadAsStringAsync(Ct);
            times.Add(clock.Elapsed.TotalMilliseconds);
            bytes = text.Length;
        }

        times.Sort();
        return (times[24], times[47], bytes);
    }

    /// <summary>
    /// One case of alice's with bob (rw) and carol (ro) as members, dave as a stranger and two administrators; two
    /// hosts; one file that is parsed and belongs to the first host, and one that waits for its parse.
    /// </summary>
    private sealed class Scene : IAsyncDisposable
    {
        private Scene(ApiFactory factory)
        {
            Factory = factory;
        }

        public ApiFactory Factory { get; }

        public long AliceId { get; private set; }

        public ApiClient Alice { get; private set; } = null!;

        public ApiClient Bob { get; private set; } = null!;

        public ApiClient Carol { get; private set; } = null!;

        public ApiClient Dave { get; private set; } = null!;

        public ApiClient Root { get; private set; } = null!;

        public ApiClient Root2 { get; private set; } = null!;

        public static async Task<Scene> CreateAsync(bool archiveRoot = true, bool twoPerson = false, bool seed = true)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Urlog:Auth:LoginThrottle:Attempts"] = "5",
                ["Urlog:Lifecycle:TwoPersonPurge"] = twoPerson ? "true" : "false",
                ["Urlog:Lifecycle:ArchiveRoot"] = archiveRoot ? Path.Combine(Path.GetTempPath(), "urlog-api-archive-" + Guid.NewGuid().ToString("n")) : string.Empty,
            };
            var scene = new Scene(new ApiFactory(settings));
            scene.AliceId = (await scene.Factory.AddUserAsync("alice", Password)).Id;
            foreach (var name in new[] { "bob", "carol", "dave" })
            {
                await scene.Factory.AddUserAsync(name, Password);
            }

            await scene.Factory.AddUserAsync("root", Password, UserRole.Admin);
            await scene.Factory.AddUserAsync("root2", Password, UserRole.Admin);
            // One address each: the login throttle counts per address.
            scene.Alice = await scene.LoginAsync("alice", "203.0.113.1");
            scene.Bob = await scene.LoginAsync("bob", "203.0.113.2");
            scene.Carol = await scene.LoginAsync("carol", "203.0.113.3");
            scene.Dave = await scene.LoginAsync("dave", "203.0.113.4");
            scene.Root = await scene.LoginAsync("root", "203.0.113.5");
            scene.Root2 = await scene.LoginAsync("root2", "203.0.113.6");
            Assert.Equal(HttpStatusCode.Created, (await scene.Alice.PostAsync("/api/v1/cases", new { name = "incident 1" })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await scene.Alice.PostAsync("/api/v1/cases/1/members", new { user = "bob", role = "rw" })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await scene.Alice.PostAsync("/api/v1/cases/1/members", new { user = "carol", role = "ro" })).StatusCode);
            if (seed)
            {
                Assert.Equal(HttpStatusCode.Created, (await scene.Alice.PostAsync("/api/v1/cases/1/hosts", new { name = "web-01" })).StatusCode);
                Assert.Equal(HttpStatusCode.Created, (await scene.Alice.PostAsync("/api/v1/cases/1/hosts", new { name = "web-02" })).StatusCode);
                // A source with naive timestamps, so its host's zone is something a parse reads.
                await scene.Factory.AddFileAsync(1, "a.log", "aaaaa", f =>
                {
                    f.DetectedSource = "syslog";
                    f.HostId = 1;
                });
                await scene.Factory.RunNextJobAsync();
                await scene.Factory.AddFileAsync(1, "b.log", "bbbbb", f => f.DetectedSource = "generic");
            }

            return scene;
        }

        /// <summary>Puts the case into a state no request of these tests can reach by itself (archived, purging).</summary>
        public async Task SetStateAsync(string state)
        {
            await using var db = Factory.Open();
            await db.Database.ExecuteSqlAsync($"UPDATE cases SET state = {state} WHERE id = 1", Ct);
        }

        /// <summary>What a route needs before it can succeed: a closed case to reopen, a failed job to run again, and so on.</summary>
        public async Task PrepareAsync(string route, string method)
        {
            if (route.EndsWith("/reopen", StringComparison.Ordinal) || route.EndsWith("/archive", StringComparison.Ordinal))
            {
                Assert.Equal(HttpStatusCode.OK, (await Alice.PostAsync("/api/v1/cases/1/close")).StatusCode);
            }
            else if (route.EndsWith("/restore", StringComparison.Ordinal))
            {
                Assert.Equal(HttpStatusCode.NoContent, (await Alice.PostAsync("/api/v1/cases/1/delete", new { name = "incident 1" })).StatusCode);
            }
            else if (route.EndsWith("/retry", StringComparison.Ordinal))
            {
                var job = await Factory.ClaimNextJobAsync();
                await Factory.FailJobAsync(job.Id, "the store refused the batch");
            }
            else if (route.EndsWith("/override", StringComparison.Ordinal) && method == "DELETE")
            {
                Assert.Equal(HttpStatusCode.Accepted, (await Alice.SendAsync(HttpMethod.Put, "/api/v1/cases/1/files/1/override", new { source = "generic" }, ifMatch: "1")).StatusCode);
            }
            else if (route.EndsWith("/redetect", StringComparison.Ordinal))
            {
                // The second file went to the fallback source; once it is parsed it is a candidate.
                await Factory.RunNextJobAsync();
            }
        }

        /// <summary>
        /// The request a client would send to the route for this scene. With <paramref name="prepare"/> a bulk
        /// route is asked first for its digest and then confirmed with it.
        /// </summary>
        public async Task<HttpResponseMessage> SendAsync(string method, string route, bool prepare)
        {
            var path = route.Replace("{caseId:long}", "1", StringComparison.Ordinal).Replace("{hostId:long}", "1", StringComparison.Ordinal).Replace("{fileId:long}", "1", StringComparison.Ordinal).Replace("{jobId:long}", "2", StringComparison.Ordinal);
            var tail = route[(route.IndexOf("{caseId:long}", StringComparison.Ordinal) + "{caseId:long}".Length)..];
            var http = new HttpMethod(method);
            async Task<string> VersionAsync(string of)
            {
                await using var db = Factory.Open();
                var version = of == "host"
                    ? await db.Hosts.AsNoTracking().Where(h => h.Id == 1).Select(h => h.Version).SingleAsync(Ct)
                    : await db.Files.AsNoTracking().Where(f => f.Id == 1).Select(f => f.SettingsVersion).SingleAsync(Ct);
                return version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            async Task<HttpResponseMessage> BulkAsync(object body)
            {
                if (!prepare)
                {
                    return await Alice.PostAsync(path, body);
                }

                var preview = await Alice.PostAsync(path, body);
                using var document = JsonDocument.Parse(await preview.Content.ReadAsStringAsync(Ct));
                return await Alice.SendAsync(http, path + "?confirm=true", body, ifMatch: document.RootElement.GetProperty("digest").GetString());
            }

            return tail switch
            {
                "/delete" => await Alice.PostAsync(path, new { name = "incident 1" }),
                "/hosts" => await Alice.PostAsync(path, new { name = "web-09" }),
                "/hosts/{hostId:long}" => await Alice.SendAsync(http, path + "?confirm=true", new { timezone = Berlin }, ifMatch: await VersionAsync("host")),
                "/hosts/{hostId:long}/merge" => await Alice.SendAsync(http, path, new { into = 2 }, ifMatch: await VersionAsync("host")),
                "/files/{fileId:long}" => await Alice.SendAsync(http, path, new { host = "web-02" }, ifMatch: await VersionAsync("file")),
                "/files/{fileId:long}/override" => await Alice.SendAsync(http, path, method == "PUT" ? new { source = "generic" } : null, ifMatch: await VersionAsync("file")),
                "/files/assign-host" => await BulkAsync(new { pattern = "*.log", host = "web-02" }),
                "/files/override" => await BulkAsync(new { match = new { extension = "log" }, source = "generic" }),
                "/files/redetect" => await BulkAsync(new { file_ids = SecondFile }),
                _ => await Alice.SendAsync(http, path, null),
            };
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in new[] { Alice, Bob, Carol, Dave, Root, Root2 })
            {
                client?.Dispose();
            }

            await Factory.DisposeAsync();
        }

        private async Task<ApiClient> LoginAsync(string name, string address)
        {
            var client = Factory.Client(address);
            Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(name, Password)).StatusCode);
            return client;
        }
    }
}
