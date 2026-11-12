using System.Net;
using Urlog.Api.Auth;
using Urlog.Core.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Urlog.Api.Tests;

/// <summary>The per-address window on /login: the 6th attempt, the lockout counter it protects, the forwarded-address rule, one audit row per window.</summary>
public sealed class ThrottleTests
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSixthAttemptInAMinuteIs429WhateverTheCredentials()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();
        var throttle = factory.Services.GetRequiredService<LoginThrottle>();
        Assert.Equal((5, TimeSpan.FromMinutes(1)), (throttle.Attempts, throttle.Window));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("alice", "not the password")).StatusCode);
        }

        var sixth = await client.LoginAsync("alice", Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.NotNull(sixth.Headers.RetryAfter?.Delta);
        Assert.InRange(sixth.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.Equal("application/problem+json", sixth.Content.Headers.ContentType?.MediaType);
        // The window ends: the same address may try again.
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("alice", Password)).StatusCode);
    }

    [Fact]
    public async Task FiftyAttemptsFromOneAddressLeaveTheCounterAtFiveAndOneAuditRow()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var attacker = factory.Client("198.51.100.77");
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 50; i++)
        {
            codes.Add((await attacker.LoginAsync("alice", "not the password")).StatusCode);
        }

        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(45, codes.Count(c => c == HttpStatusCode.TooManyRequests));

        await using (var db = factory.Open())
        {
            var alice = await db.Users.SingleAsync(Ct);
            Assert.Equal((5, (DateTime?)null), (alice.FailedLogins, alice.LockedAt));
            Assert.Equal(5, await db.Audit.CountAsync(a => a.Action == AuditActions.LoginFailed, Ct));
            var throttled = await db.Audit.Where(a => a.Action == AuditActions.LoginThrottled).ToListAsync(Ct);
            var row = Assert.Single(throttled);
            Assert.Equal("""{"address":"198.51.100.77","attempts":5,"name":"alice","window_seconds":60}""", row.DetailsJson);
            Assert.Equal(("alice", (long?)null), (row.Actor, row.ActorId));
        }

        // Alice logs in from her own address meanwhile.
        using var own = factory.Client("203.0.113.10");
        Assert.Equal(HttpStatusCode.OK, (await own.LoginAsync("alice", Password)).StatusCode);

        // A second window from the attacker: one more row, not forty-five.
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        for (var i = 0; i < 10; i++)
        {
            await attacker.LoginAsync("alice", "not the password");
        }

        await using var check = factory.Open();
        Assert.Equal(2, await check.Audit.CountAsync(a => a.Action == AuditActions.LoginThrottled, Ct));
    }

    [Fact]
    public async Task TheThrottleTripsBeforeTheLockoutDoes()
    {
        // 29 attempts from one address over seven minutes: five per window are counted, the rest refused; the lockout lands
        // on the 10th counted failure, in the second window, so a single burst cannot lock the account.
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var attacker = factory.Client("198.51.100.77");
        var lockedAtAttempt = -1;
        for (var attempt = 1; attempt <= 29; attempt++)
        {
            if (attempt is 8 or 15 or 22)
            {
                factory.Clock.Advance(TimeSpan.FromSeconds(61));
            }

            await attacker.LoginAsync("alice", "not the password");
            await using var db = factory.Open();
            if (lockedAtAttempt < 0 && await db.Users.AnyAsync(u => u.LockedAt != null, Ct))
            {
                lockedAtAttempt = attempt;
            }
        }

        Assert.Equal(12, lockedAtAttempt);
        await using var final = factory.Open();
        Assert.Equal(10, await final.Audit.CountAsync(a => a.Action == AuditActions.LoginFailed, Ct) - await final.Audit.CountAsync(a => a.Action == AuditActions.LoginFailed && a.DetailsJson.Contains("\"reason\":\"locked\""), Ct));
    }

    [Fact]
    public async Task ASpoofedForwardedHeaderFromAnUntrustedPeerDoesNotChangeTheKey()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var peer = factory.Client("198.51.100.77");
        for (var i = 0; i < 5; i++)
        {
            await SendWithForwardedAsync(peer, $"203.0.113.{i + 1}");
        }

        // Five different spoofed addresses: still one window, keyed on the peer.
        var sixth = await SendWithForwardedAsync(peer, "203.0.113.99");
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        await using var db = factory.Open();
        var row = await db.Audit.SingleAsync(a => a.Action == AuditActions.LoginThrottled, Ct);
        Assert.Contains("\"address\":\"198.51.100.77\"", row.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListedProxyForwardedAddressIsTheKey()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?> { ["Urlog:Http:TrustedProxies:0"] = "10.0.0.1" });
        await factory.AddUserAsync("alice", Password);
        using var proxy = factory.Client("10.0.0.1");
        for (var i = 0; i < 5; i++)
        {
            await SendWithForwardedAsync(proxy, "203.0.113.5");
        }

        // The same client behind the proxy: throttled; a different client behind the proxy: not.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendWithForwardedAsync(proxy, "203.0.113.5")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendWithForwardedAsync(proxy, "203.0.113.6")).StatusCode);
        // An unlisted peer sending the same header is keyed on itself.
        using var other = factory.Client("198.51.100.77");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendWithForwardedAsync(other, "203.0.113.5")).StatusCode);

        await using var db = factory.Open();
        var row = await db.Audit.SingleAsync(a => a.Action == AuditActions.LoginThrottled, Ct);
        Assert.Contains("\"address\":\"203.0.113.5\"", row.DetailsJson, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendWithForwardedAsync(ApiClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login");
        request.Headers.Add(ApiFactory.ClientAddressHeader, client.Address);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Content = System.Net.Http.Json.JsonContent.Create(new { name = "alice", password = "not the password" }, options: ApiFactory.Json);
        return await client.Http.SendAsync(request);
    }
}
