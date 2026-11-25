using System.Net;

namespace Urlog.Api.Tests;

/// <summary>
/// Every answer of the host says what a browser may do with it. Log lines are written by whoever attacked or
/// was attacked, so these headers are the second line behind the interface writing them as text: what a page
/// of the origin may load and run, that a type is never guessed, that no address leaves with a link, that no
/// other origin can hold a handle on the page or read its files, which browser features it may use, and that
/// nothing the API answers is kept by a cache. These tests hold each header to every kind of answer.
/// </summary>
public sealed class ResponseHeaderTests
{
    private const string Password = "correct horse battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Header(HttpResponseMessage answer, string name) =>
        Assert.Single(answer.Headers.TryGetValues(name, out var values) ? values : answer.Content.Headers.TryGetValues(name, out var own) ? own : []);

    /// <summary>One answer of each kind: a success, a refusal without a session, a wrong sign-in, a route that does not exist.</summary>
    private static async Task<List<HttpResponseMessage>> AnswersAsync(ApiFactory factory)
    {
        await factory.AddUserAsync("alice", Password);
        var anonymous = factory.Client();
        var alice = factory.Client();
        await alice.LoginAsync("alice", Password);
        var answers = new List<HttpResponseMessage>
        {
            await anonymous.GetAsync("/api/v1/health"),
            await anonymous.GetAsync("/api/v1/cases"),
            await anonymous.PostAsync("/api/v1/auth/login", new { name = "nobody", password = "wrong wrong wrong" }),
            await alice.GetAsync("/api/v1/cases"),
            await alice.GetAsync("/api/v1/cases/999"),
            await alice.PostAsync("/api/v1/cases", new { name = "incident" }),
        };
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.Created],
            answers.Select(a => a.StatusCode));
        return answers;
    }

    [Fact]
    public async Task EveryKindOfAnswerCarriesTheContentPolicy()
    {
        await using var factory = new ApiFactory();
        Assert.All(await AnswersAsync(factory), a => Assert.Equal(ApiHost.ContentSecurityPolicy, Header(a, "Content-Security-Policy")));
    }

    [Fact]
    public async Task NoAnswerLetsABrowserGuessItsType()
    {
        await using var factory = new ApiFactory();
        Assert.All(await AnswersAsync(factory), a => Assert.Equal("nosniff", Header(a, "X-Content-Type-Options")));
    }

    [Fact]
    public async Task NoAddressOfTheInstanceLeavesWithALink()
    {
        // The address of a search holds the query; a referrer would hand it to wherever a link leads.
        await using var factory = new ApiFactory();
        Assert.All(await AnswersAsync(factory), a => Assert.Equal("no-referrer", Header(a, "Referrer-Policy")));
    }

    [Fact]
    public async Task NoOtherOriginKeepsAHandleOnThePage()
    {
        await using var factory = new ApiFactory();
        Assert.All(await AnswersAsync(factory), a => Assert.Equal("same-origin", Header(a, "Cross-Origin-Opener-Policy")));
    }

    [Fact]
    public async Task NoOtherOriginLoadsWhatTheInstanceServes()
    {
        await using var factory = new ApiFactory();
        Assert.All(await AnswersAsync(factory), a => Assert.Equal("same-origin", Header(a, "Cross-Origin-Resource-Policy")));
    }

    [Fact]
    public async Task EveryAnswerNamesTheBrowserFeaturesThePageMayUse()
    {
        await using var factory = new ApiFactory();
        Assert.All(await AnswersAsync(factory), a => Assert.Equal(ApiHost.PermissionsPolicy, Header(a, "Permissions-Policy")));
    }

    [Fact]
    public void ThePermissionsPolicyGrantsNothingButTheClipboardWriteOfThePageItself()
    {
        var features = ApiHost.PermissionsPolicy
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(f => f.Split('=')[0], f => f.Split('=')[1], StringComparer.Ordinal);

        // Copy buttons write to the clipboard; nothing else is granted to anyone, the page included.
        Assert.Equal("(self)", features["clipboard-write"]);
        Assert.All(features.Where(f => f.Key != "clipboard-write"), f => Assert.Equal("()", f.Value));
        foreach (var feature in new[] { "camera", "microphone", "geolocation", "clipboard-read", "display-capture", "usb", "serial", "hid", "bluetooth", "payment", "fullscreen" })
        {
            Assert.Contains(feature, features.Keys);
        }

        Assert.DoesNotContain("*", ApiHost.PermissionsPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingTheApiAnswersIsKeptByACache()
    {
        await using var factory = new ApiFactory();
        Assert.All(await AnswersAsync(factory), a => Assert.Equal("no-store", Header(a, "Cache-Control")));
    }

    [Fact]
    public async Task TheSearchStreamAndAnExportAreNotKeptEither()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var alice = factory.Client();
        await alice.LoginAsync("alice", Password);
        var created = await ApiClient.ReadAsync<System.Text.Json.JsonElement>(await alice.PostAsync("/api/v1/cases", new { name = "incident" }));
        var caseId = created.GetProperty("id").GetInt64();

        var stream = await alice.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" });
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Contains("no-store", Header(stream, "Cache-Control"), StringComparison.Ordinal);
        Assert.Equal("nosniff", Header(stream, "X-Content-Type-Options"));

        var export = await alice.GetAsync($"/api/v1/cases/{caseId}/search/export?format=csv&query=");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("no-store", Header(export, "Cache-Control"), StringComparison.Ordinal);
        Assert.Equal("nosniff", Header(export, "X-Content-Type-Options"));
        _ = Ct;
    }

    [Fact]
    public async Task AHostThatIsNotReadyStillSendsEveryHeader()
    {
        await using var factory = new ApiFactory();
        factory.EventStore.Failure = new IOException("connection refused");
        using var client = factory.Client();

        var health = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal(ApiHost.ContentSecurityPolicy, Header(health, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(health, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(health, "Referrer-Policy"));
        Assert.Equal("same-origin", Header(health, "Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", Header(health, "Cross-Origin-Resource-Policy"));
        Assert.Equal(ApiHost.PermissionsPolicy, Header(health, "Permissions-Policy"));
        Assert.Equal("no-store", Header(health, "Cache-Control"));
    }

    [Fact]
    public async Task TheHostSetsNoTransportSecurityHeader()
    {
        // The host listens on plain HTTP behind a proxy; that header is the proxy's to set.
        await using var factory = new ApiFactory();
        Assert.All(await AnswersAsync(factory), a => Assert.False(a.Headers.Contains("Strict-Transport-Security")));
    }

    [Fact]
    public void TheContentPolicyAllowsTheInstanceAndNothingElse()
    {
        var directives = ApiHost.ContentSecurityPolicy
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(d => d.Split(' ')[0], d => d.Split(' ').Skip(1).ToArray(), StringComparer.Ordinal);

        Assert.Equal(["'none'"], directives["default-src"]);
        foreach (var name in new[] { "script-src", "style-src", "img-src", "font-src", "connect-src", "form-action" })
        {
            Assert.Equal(["'self'"], directives[name]);
        }

        Assert.Equal(["'none'"], directives["base-uri"]);
        Assert.Equal(["'none'"], directives["frame-ancestors"]);

        // No inline code, no eval, no other origin, no scheme that carries its own content.
        foreach (var word in new[] { "unsafe-inline", "unsafe-eval", "unsafe-hashes", "*", "http:", "https:", "data:", "blob:" })
        {
            Assert.DoesNotContain(word, ApiHost.ContentSecurityPolicy, StringComparison.Ordinal);
        }
    }
}
