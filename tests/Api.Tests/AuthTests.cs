using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Urlog.Api.Auth;
using Urlog.Core.Audit;
using Urlog.Core.Auth;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Urlog.Api.Tests;

/// <summary>Login, the cookie, CSRF, logout, the lockout and its expiry, the session timeouts, the equalised timing, the log capture.</summary>
public sealed class AuthTests
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoginSetsTheCookieAndReturnsTheCsrfToken()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();

        var response = await client.LoginAsync("Alice", Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("urlog_session=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(client.Csrf);
        Assert.Equal(43, client.Csrf.Length);

        using var me = JsonDocument.Parse(await (await client.GetAsync("/api/v1/auth/me")).Content.ReadAsStringAsync(Ct));
        Assert.Equal("alice", me.RootElement.GetProperty("user").GetProperty("name").GetString());
        Assert.Equal("analyst", me.RootElement.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(client.Csrf, me.RootElement.GetProperty("csrf_token").GetString());
        Assert.False(me.RootElement.GetProperty("user").TryGetProperty("password_hash", out _));
    }

    [Fact]
    public async Task NoSessionIs401AndAMutatingCallWithoutTheCsrfTokenIs403()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();
        var anonymous = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("application/problem+json", anonymous.Content.Headers.ContentType?.MediaType);

        await client.LoginAsync("alice", Password);
        var without = await client.SendAsync(HttpMethod.Post, "/api/v1/cases", new { name = "incident 1" }, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.Contains("csrf", await without.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var saved = client.Csrf;
        client.Csrf = "wrong-token";
        var wrong = await client.PostAsync("/api/v1/cases", new { name = "incident 1" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        client.Csrf = saved;
        var with = await client.PostAsync("/api/v1/cases", new { name = "incident 1" });
        Assert.Equal(HttpStatusCode.Created, with.StatusCode);
        Assert.Equal("/api/v1/cases/1", with.Headers.Location?.ToString());
    }

    [Fact]
    public async Task LogoutInvalidatesTheSession()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();
        await client.LoginAsync("alice", Password);
        var logout = await client.PostAsync("/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith("urlog_session=;", StringComparison.Ordinal) || c.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        await using var db = factory.Open();
        Assert.Equal(0, await db.Sessions.CountAsync(Ct));
        Assert.Equal(["genesis", "user_create", "login", "logout"], await db.Audit.OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync(Ct));
    }

    [Fact]
    public async Task UnknownWrongAndLockedShareOne401Body()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Auth:LoginThrottle:Attempts"] = "1000" });
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();
        var unknown = await client.LoginAsync("nobody", Password);
        var wrong = await client.LoginAsync("alice", "not the password");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(Strip(await unknown.Content.ReadAsStringAsync(Ct)), Strip(await wrong.Content.ReadAsStringAsync(Ct)));
        for (var i = 0; i < 9; i++)
        {
            await client.LoginAsync("alice", "not the password");
        }

        var locked = await client.LoginAsync("alice", Password);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(Strip(await unknown.Content.ReadAsStringAsync(Ct)), Strip(await locked.Content.ReadAsStringAsync(Ct)));
        Assert.DoesNotContain("lock", await locked.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheEleventhLoginIsRefusedWithTheRightPasswordAndTheAuditShowsTheLockout()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Auth:LoginThrottle:Attempts"] = "1000" });
        await factory.AddUserAsync("alice", Password);
        await factory.AddUserAsync("root", Password, UserRole.Admin);
        using var client = factory.Client();
        await client.LoginAsync("alice", Password);
        var live = client.Cookie;
        using var other = factory.Client();
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await other.LoginAsync("alice", "not the password")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await other.LoginAsync("alice", Password)).StatusCode);
        // The live session of the locked user is refused on its next request.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.NotNull(live);

        await using (var db = factory.Open())
        {
            var actions = await db.Audit.OrderBy(a => a.Seq).Select(a => a.Action).ToListAsync(Ct);
            Assert.Equal(10, actions.Count(a => a == AuditActions.LoginFailed) - 1);
            Assert.Contains(AuditActions.UserLockout, actions);
            Assert.Equal(AuditActions.LoginFailed, actions[^1]);
            var lockout = await db.Audit.SingleAsync(a => a.Action == AuditActions.UserLockout, Ct);
            Assert.Contains("\"count\":10", lockout.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("\"address\":\"203.0.113.10\"", lockout.DetailsJson, StringComparison.Ordinal);
        }

        // Admin unlock restores the login.
        using var admin = factory.Client();
        await admin.LoginAsync("root", Password);
        var unlock = await admin.PostAsync("/api/v1/users/1/unlock");
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.LoginAsync("alice", Password)).StatusCode);
    }

    [Fact]
    public async Task TheLockExpiresAfterFifteenMinutesAndTheCountStartsFromZero()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Auth:LoginThrottle:Attempts"] = "1000" });
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();
        for (var i = 0; i < 10; i++)
        {
            await client.LoginAsync("alice", "not the password");
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("alice", Password)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        // Expired: a failure counts from zero, then the right password gets in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("alice", "not the password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("alice", Password)).StatusCode);
        await using var db = factory.Open();
        var alice = await db.Users.SingleAsync(Ct);
        Assert.Equal((0, (DateTime?)null), (alice.FailedLogins, alice.LockedAt));
        Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == AuditActions.UserLockout, Ct));
        Assert.Equal(0, await db.Audit.CountAsync(a => a.Action == AuditActions.UserUnlock, Ct));
    }

    [Fact]
    public async Task IdleAndAbsoluteTimeoutsEndTheSession()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();
        await client.LoginAsync("alice", Password);
        factory.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        // The touch at seven hours restarts the idle clock.
        factory.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        factory.Clock.Advance(TimeSpan.FromHours(8));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        await client.LoginAsync("alice", Password);
        for (var i = 0; i < 23; i++)
        {
            factory.Clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        }

        factory.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ARoleChangeAppliesOnTheNextRequest()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("root", Password, UserRole.Admin);
        var bob = await factory.AddUserAsync("bob", Password);
        using var admin = factory.Client();
        await admin.LoginAsync("root", Password);
        using var client = factory.Client();
        await client.LoginAsync("bob", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/users")).StatusCode);

        var promote = await admin.PutAsync($"/api/v1/users/{bob.Id}/role", new { role = "admin" });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        // The role change ended bob's sessions; a new login carries the new role.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/users")).StatusCode);
        await client.LoginAsync("bob", Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);
    }

    [Fact]
    public async Task AnUnknownNameCostsOneVerificationAtThePinnedParametersLikeAWrongPassword()
    {
        // What keeps the response time from saying whether an account exists is the work done, so that is what is
        // asserted: each path runs exactly one Argon2 verification, at the pinned parameters. The clock comparison
        // is the explicit test below; it depends on what else the box is doing.
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Auth:LoginThrottle:Attempts"] = "1000" });
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();
        var verified = new List<Argon2Parameters?>();
        factory.Services.GetRequiredService<PasswordService>().Verified = parameters =>
        {
            lock (verified)
            {
                verified.Add(parameters);
            }
        };

        using (var unknown = await client.LoginAsync("nobody", "not the password"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        }

        Assert.Equal([PasswordHasher.Pinned], verified);
        verified.Clear();

        using (var wrong = await client.LoginAsync("alice", "not the password"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        Assert.Equal([PasswordHasher.Pinned], verified);
        verified.Clear();

        // The successful login is one verification too, and the dummy the unknown name ran against is a real hash
        // at the pinned parameters that needs no re-hash.
        using (var right = await client.LoginAsync("alice", Password))
        {
            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        }

        Assert.Equal([PasswordHasher.Pinned], verified);
        Assert.True(PasswordHasher.TryParse(PasswordHasher.DummyHash, out var dummy));
        Assert.Equal(PasswordHasher.Pinned, dummy.Parameters);
        Assert.False(PasswordHasher.NeedsRehash(PasswordHasher.DummyHash));
    }

    /// <summary>
    /// The wall-clock form of the test above: medians of seven logins, unknown name against wrong password, within
    /// half of the slower. Explicit, outside the default run and the gates, because two series timed one after the
    /// other measure the box as much as the code: run it alone on a quiet box with <c>tools/timing-tests.ps1</c>.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Timing")]
    public async Task TimingAnUnknownNameCostsAsMuchAsAWrongPassword()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Auth:LoginThrottle:Attempts"] = "1000" });
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();
        await client.LoginAsync("alice", "warm up the dummy hash and the JIT");
        await client.LoginAsync("nobody", "warm up the dummy hash and the JIT");

        var unknown = await MedianMsAsync(() => client.LoginAsync("nobody", "not the password"));
        var wrong = await MedianMsAsync(() => client.LoginAsync("alice", "not the password"));
        var slower = Math.Max(unknown, wrong);
        await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, "api.timing"), $"POST /auth/login median of 7: unknown name {unknown:F0} ms, wrong password {wrong:F0} ms" + Environment.NewLine, Ct);
        Assert.True(unknown >= 50 && wrong >= 50, $"unknown {unknown:F0} ms, wrong {wrong:F0} ms: the dummy verify did not run");
        Assert.True(Math.Abs(unknown - wrong) <= 0.5 * slower, $"unknown {unknown:F0} ms, wrong {wrong:F0} ms differ by more than half");
    }

    [Fact]
    public async Task NoSecretReachesALogLineOrAUserResponse()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("root", Password, UserRole.Admin);
        using var admin = factory.Client();
        await admin.LoginAsync("root", Password);
        const string bobPassword = "a different long password";
        var created = await admin.PostAsync("/api/v1/users", new { name = "bob", password = bobPassword, role = "analyst" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var bob = factory.Client();
        await bob.LoginAsync("bob", bobPassword);
        await bob.LoginAsync("bob", "wrong wrong wrong wrong");
        var users = await (await admin.GetAsync("/api/v1/users")).Content.ReadAsStringAsync(Ct);
        var me = await (await bob.GetAsync("/api/v1/auth/me")).Content.ReadAsStringAsync(Ct);
        var one = await (await admin.GetAsync("/api/v1/users/2")).Content.ReadAsStringAsync(Ct);

        string[] hashes;
        await using (var db = factory.Open())
        {
            hashes = await db.Users.Select(u => u.PasswordHash).ToArrayAsync(Ct);
        }

        var secrets = new List<string> { Password, bobPassword, "wrong wrong wrong wrong", admin.Cookie!.Split('=')[1], bob.Cookie!.Split('=')[1], admin.Csrf!, bob.Csrf! };
        secrets.AddRange(hashes);
        Assert.NotEmpty(factory.Logs.Lines);
        for (var i = 0; i < secrets.Count; i++)
        {
            var secret = secrets[i];
            var leaked = factory.Logs.Lines.FirstOrDefault(line => line.Contains(secret, StringComparison.Ordinal));
            Assert.True(leaked is null, $"secret #{i} ({secret.Length} chars) in a log line: {leaked}");
            Assert.False(users.Contains(secret, StringComparison.Ordinal), $"secret #{i} in GET /users: {users}");
            // /auth/me returns the caller's own CSRF token by design; everything else stays out of it.
            Assert.True(secret == bob.Csrf || !me.Contains(secret, StringComparison.Ordinal), $"secret #{i} in GET /auth/me");
            Assert.False(one.Contains(secret, StringComparison.Ordinal), $"secret #{i} in GET /users/2");
        }

        Assert.DoesNotContain("argon2", users + me + one, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(factory.Logs.Lines, line => line.Contains("login Success for 'bob'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MeCostsUnderFiveMillisecondsWarm()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();
        await client.LoginAsync("alice", Password);
        for (var i = 0; i < 20; i++)
        {
            await client.GetAsync("/api/v1/auth/me");
        }

        var samples = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var sw = Stopwatch.StartNew();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p50 = samples[samples.Count / 2];
        await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, "api.timing"), $"GET /auth/me p50 {p50:F2} ms (50 requests, warm)\n", Ct);
        Assert.True(p50 < 5, $"p50 {p50:F2} ms");
    }

    private static async Task<double> MedianMsAsync(Func<Task<HttpResponseMessage>> call)
    {
        var samples = new List<double>(7);
        for (var i = 0; i < 7; i++)
        {
            var sw = Stopwatch.StartNew();
            using var response = await call();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[3];
    }

    /// <summary>The problem body without the per-request trace id.</summary>
    internal static string Strip(string problemJson)
    {
        using var doc = JsonDocument.Parse(problemJson);
        return string.Join(";", doc.RootElement.EnumerateObject().Where(p => p.Name != "trace_id").Select(p => p.Name + "=" + p.Value.ToString()));
    }
}
