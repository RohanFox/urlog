using System.Net;
using System.Text;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Api.Tests;

/// <summary>The users, cases, members and admin routes: status codes, bodies, the rows they write, the audit trail at the end.</summary>
public sealed class UsersAndCasesTests
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UsersCreateGetRoleUnlockPassword()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("root", Password, UserRole.Admin);
        using var admin = factory.Client();
        await admin.LoginAsync("root", Password);

        var created = await admin.PostAsync("/api/v1/users", new { name = "Bob", password = "another long password" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/v1/users/2", created.Headers.Location?.ToString());
        using (var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal("bob", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal("analyst", doc.RootElement.GetProperty("role").GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("locked_until").ValueKind);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/v1/users", new { name = "bob", password = "another long password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/v1/users", new { name = "bad name!", password = "another long password" })).StatusCode);
        var shortPassword = await admin.PostAsync("/api/v1/users", new { name = "carol", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Contains("at least 12", await shortPassword.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/v1/users", new { name = "carol" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/users/2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/users/4242")).StatusCode);
        var list = await ApiClient.ReadAsync<JsonElement>(await admin.GetAsync("/api/v1/users"));
        Assert.Equal(2, list.GetArrayLength());

        // The only admin cannot step down; bob can be promoted, then root can.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync("/api/v1/users/1/role", new { role = "analyst" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/v1/users/2/role", new { role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/v1/users/1/role", new { role = "analyst" })).StatusCode);
        // ... which ended root's own session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/users")).StatusCode);

        using var bob = factory.Client();
        await bob.LoginAsync("bob", "another long password");
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync("/api/v1/users/1/unlock")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync("/api/v1/users/4242/unlock")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.PostAsync("/api/v1/users/1/password", new { password = "a third long password" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsync("/api/v1/users/1/password", new { password = "short" })).StatusCode);
        using var root = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await root.LoginAsync("root", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await root.LoginAsync("root", "a third long password")).StatusCode);

        await using var db = factory.Open();
        var actions = await db.Audit.OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync(Ct);
        Assert.Equal(["genesis", "user_create", "login", "user_create", "user_role_change", "user_role_change", "login", "user_unlock", "user_password_reset", "login_failed", "login"], actions);
        Assert.All(await db.Audit.Where(a => a.Action == AuditActions.UserCreate && a.TargetId == 2).ToListAsync(Ct), r => Assert.Equal(("root", (long?)1), (r.Actor, r.ActorId)));
    }

    [Fact]
    public async Task CasesCreateListGetMembers()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        await factory.AddUserAsync("bob", Password);
        using var a = factory.Client();
        using var b = factory.Client();
        await a.LoginAsync("alice", Password);
        await b.LoginAsync("bob", Password);

        var one = await a.PostAsync("/api/v1/cases", new { name = "incident 1" });
        Assert.Equal(HttpStatusCode.Created, one.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync("/api/v1/cases", new { name = "" })).StatusCode);
        using (var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases") { Content = new StringContent("{\"name\":", Encoding.UTF8, "application/json") })
        {
            malformed.Headers.Add(ApiFactory.ClientAddressHeader, a.Address);
            malformed.Headers.Add("Cookie", a.Cookie!);
            malformed.Headers.Add("X-CSRF-Token", a.Csrf!);
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Http.SendAsync(malformed, Ct)).StatusCode);
        }

        await b.PostAsync("/api/v1/cases", new { name = "incident 2" });
        await a.PostAsync("/api/v1/cases", new { name = "incident 3" });

        // Interleaved lists: each user sees only their own memberships.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(new long[] { 1, 3 }, Ids(await ApiClient.ReadAsync<JsonElement>(await a.GetAsync("/api/v1/cases"))));
            Assert.Equal(new long[] { 2 }, Ids(await ApiClient.ReadAsync<JsonElement>(await b.GetAsync("/api/v1/cases"))));
        }

        var share = await a.PostAsync("/api/v1/cases/3/members", new { user = "Bob", role = "ro" });
        Assert.Equal(HttpStatusCode.Created, share.StatusCode);
        Assert.Equal("/api/v1/cases/3/members/2", share.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsync("/api/v1/cases/3/members", new { user = "bob", role = "rw" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync("/api/v1/cases/3/members", new { user = "nobody", role = "rw" })).StatusCode);
        Assert.Equal(new long[] { 2, 3 }, Ids(await ApiClient.ReadAsync<JsonElement>(await b.GetAsync("/api/v1/cases"))));

        var three = await ApiClient.ReadAsync<JsonElement>(await b.GetAsync("/api/v1/cases/3"));
        Assert.Equal(("incident 3", "active", "alice", 2, "ro"), (three.GetProperty("name").GetString(), three.GetProperty("state").GetString(), three.GetProperty("owner").GetString(), three.GetProperty("members").GetInt32(), three.GetProperty("my_role").GetString()));
        var members = await ApiClient.ReadAsync<JsonElement>(await b.GetAsync("/api/v1/cases/3/members"));
        Assert.Equal(["alice:owner", "bob:ro"], members.EnumerateArray().Select(m => m.GetProperty("name").GetString() + ":" + m.GetProperty("role").GetString()).ToArray());

        // Bob (ro) cannot change roles; alice can; the last owner cannot step down.
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PutAsync("/api/v1/cases/3/members/2", new { role = "rw" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsync("/api/v1/cases/3/members/2", new { role = "rw" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PutAsync("/api/v1/cases/3/members/1", new { role = "rw" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PutAsync("/api/v1/cases/3/members/4242", new { role = "rw" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync("/api/v1/cases/3/members/2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.DeleteAsync("/api/v1/cases/3/members/2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/api/v1/cases/3")).StatusCode);

        await using var db = factory.Open();
        var rows = await db.Audit.Where(a => a.TargetType == AuditTargets.Case && a.TargetId == 3).OrderBy(a => a.Seq).ToListAsync(Ct);
        Assert.Equal(["case_create", "case_share", "case_role_change", "case_unshare"], rows.Select(r => r.Action).ToArray());
        Assert.All(rows, r => Assert.Equal(("alice", (long?)1), (r.Actor, r.ActorId)));
        Assert.Contains("\"by_admin\":false", rows[1].DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminRoutesListMetadataAndGrantAuditedMembership()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        await factory.AddUserAsync("root", Password, UserRole.Admin);
        using var a = factory.Client();
        using var root = factory.Client();
        await a.LoginAsync("alice", Password);
        await root.LoginAsync("root", Password);
        await a.PostAsync("/api/v1/cases", new { name = "incident 1" });

        // No membership: 404 on the case routes, even for admin.
        Assert.Equal(HttpStatusCode.NotFound, (await root.GetAsync("/api/v1/cases/1")).StatusCode);
        var list = await ApiClient.ReadAsync<JsonElement>(await root.GetAsync("/api/v1/admin/cases"));
        var listed = Assert.Single(list.EnumerateArray());
        Assert.Equal(("incident 1", "alice"), (listed.GetProperty("name").GetString(), listed.GetProperty("owner").GetString()));
        Assert.Equal(["alice:owner"], listed.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("name").GetString() + ":" + m.GetProperty("role").GetString()).ToArray());
        // Metadata only: nothing that could be an event or a field.
        var text = await (await root.GetAsync("/api/v1/admin/cases")).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("events", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fields", text, StringComparison.Ordinal);

        // The admin grants themselves ro: explicit, audited in the case's own trail, and then the case is visible.
        var grant = await root.PostAsync("/api/v1/admin/cases/1/members", new { user = "root", role = "ro" });
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        Assert.Equal("/api/v1/admin/cases/1/members/2", grant.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await root.GetAsync("/api/v1/cases/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await root.PostAsync("/api/v1/admin/cases/4242/members", new { user = "root", role = "ro" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await root.PutAsync("/api/v1/admin/cases/1/members/2", new { role = "rw" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await root.DeleteAsync("/api/v1/admin/cases/1/members/2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await root.GetAsync("/api/v1/cases/1")).StatusCode);

        await using var db = factory.Open();
        using var stream = new MemoryStream();
        await AuditExport.WriteCaseAsync(db, 1, stream, Ct);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var actions = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString()).ToArray();
        Assert.Equal(["case_create", "case_share", "case_role_change", "case_unshare"], actions);
        foreach (var line in lines.Skip(1))
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("root", doc.RootElement.GetProperty("actor").GetString());
            Assert.Contains("\"by_admin\":true", doc.RootElement.GetProperty("details_json").GetString(), StringComparison.Ordinal);
            Assert.Contains("\"user_name\":\"root\"", doc.RootElement.GetProperty("details_json").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheWholeTrailVerifiesAndNamesEveryAction()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Auth:LoginThrottle:Attempts"] = "3" });
        await factory.AddUserAsync("root", Password, UserRole.Admin);
        using var root = factory.Client();
        await root.LoginAsync("root", Password);
        await root.PostAsync("/api/v1/users", new { name = "alice", password = Password });
        await root.PostAsync("/api/v1/users", new { name = "bob", password = Password });
        using var a = factory.Client("203.0.113.20");
        await a.LoginAsync("alice", Password);
        await a.PostAsync("/api/v1/cases", new { name = "incident 1" });
        await a.PostAsync("/api/v1/cases/1/members", new { user = "bob", role = "ro" });
        await a.PutAsync("/api/v1/cases/1/members/3", new { role = "rw" });
        await a.DeleteAsync("/api/v1/cases/1/members/3");
        await a.PostAsync("/api/v1/auth/logout");
        using var attacker = factory.Client("198.51.100.77");
        for (var i = 0; i < 4; i++)
        {
            await attacker.LoginAsync("bob", "not the password");
        }

        for (var window = 0; window < 3; window++)
        {
            factory.Clock.Advance(TimeSpan.FromSeconds(61));
            for (var i = 0; i < 3; i++)
            {
                await attacker.LoginAsync("bob", "not the password");
            }
        }

        await root.PostAsync("/api/v1/users/3/unlock");
        await root.PutAsync("/api/v1/users/2/role", new { role = "admin" });
        await root.PostAsync("/api/v1/users/3/password", new { password = "a brand new long password" });

        await using var db = factory.Open();
        var actions = (await db.Audit.Select(r => r.Action).ToListAsync(Ct)).ToHashSet();
        string[] expected =
        [
            AuditActions.Login, AuditActions.Logout, AuditActions.LoginFailed, AuditActions.LoginThrottled,
            AuditActions.UserCreate, AuditActions.UserRoleChange, AuditActions.UserLockout, AuditActions.UserUnlock, AuditActions.UserPasswordReset,
            AuditActions.CaseCreate, AuditActions.CaseShare, AuditActions.CaseRoleChange, AuditActions.CaseUnshare,
        ];
        Assert.All(expected, e => Assert.Contains(e, actions));
        // Every row a session wrote carries the actor id; the anonymous ones (throttle, unknown name) do not.
        Assert.All(await db.Audit.Where(a => a.Action != AuditActions.Genesis && a.Action != AuditActions.LoginThrottled && a.Actor != "cli").ToListAsync(Ct), r => Assert.NotNull(r.ActorId));
        var verified = await AuditVerifier.VerifyAsync(factory.MetadataPath, Ct);
        Assert.True(verified.Ok, verified.Detail);
    }

    private static long[] Ids(JsonElement array) => array.EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).ToArray();
}
