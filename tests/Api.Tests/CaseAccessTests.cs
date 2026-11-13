using System.Net;
using Urlog.Api.Auth;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Urlog.Api.Tests;

/// <summary>
/// The route theory: every registered route under a case answers 404 to a non-member and to an admin without membership,
/// 403 to a read-only member wherever the route asks for more than reading, and the 404 body equals the one for an id that
/// does not exist. Plus the startup assertion that refuses to start a host with an unguarded route.
/// </summary>
public sealed class CaseAccessTests
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every (method, route) with a case id in the live route table, from the factory's EndpointDataSource.</summary>
    public static IEnumerable<TheoryDataRow<string, string, string>> CaseRoutes()
    {
        using var factory = new ApiFactory();
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            // A group's "" route carries a trailing slash in its raw text; the request path does not need it.
            var text = (endpoint.RoutePattern.RawText ?? string.Empty).TrimEnd('/');
            if (!CaseAccess.IsCaseRoute(text))
            {
                continue;
            }

            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                // The role the route itself asks for; the startup assertion guarantees there is one.
                var needs = Names.Of(endpoint.Metadata.GetMetadata<CaseAccessRequirement>()!.Minimum);
                yield return new TheoryDataRow<string, string, string>(method, text, needs);
            }
        }
    }

    [Fact]
    public void TheRouteTableHasTheCaseRoutes()
    {
        var routes = CaseRoutes().Select(r => r.Data.Item1 + " " + r.Data.Item2).Order().ToList();
        Assert.True(routes.Count == 11, string.Join(" | ", routes));
        Assert.Equal(
        [
            "DELETE /api/v1/cases/{caseId:long}/members/{userId:long}",
            "DELETE /api/v1/cases/{caseId:long}/uploads/{TusFileId?}",
            "GET /api/v1/cases/{caseId:long}",
            "GET /api/v1/cases/{caseId:long}/members",
            "GET /api/v1/cases/{caseId:long}/uploads/{uploadId}",
            "HEAD /api/v1/cases/{caseId:long}/uploads/{TusFileId?}",
            "OPTIONS /api/v1/cases/{caseId:long}/uploads/{TusFileId?}",
            "PATCH /api/v1/cases/{caseId:long}/uploads/{TusFileId?}",
            "POST /api/v1/cases/{caseId:long}/members",
            "POST /api/v1/cases/{caseId:long}/uploads/{TusFileId?}",
            "PUT /api/v1/cases/{caseId:long}/members/{userId:long}",
        ], routes);
    }

    [Theory]
    [MemberData(nameof(CaseRoutes))]
    public async Task EveryCaseRouteRefusesANonMember(string method, string route, string needs)
    {
        await using var factory = new ApiFactory();
        var alice = await factory.AddUserAsync("alice", Password);
        var bob = await factory.AddUserAsync("bob", Password);
        var carol = await factory.AddUserAsync("carol", Password);
        await factory.AddUserAsync("root", Password, UserRole.Admin);
        using var a = factory.Client();
        await a.LoginAsync("alice", Password);
        var created = await a.PostAsync("/api/v1/cases", new { name = "alice's case" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var share = await a.PostAsync("/api/v1/cases/1/members", new { user = "carol", role = "ro" });
        Assert.Equal(HttpStatusCode.Created, share.StatusCode);

        var http = new HttpMethod(method);
        const string upload = "0123456789abcdef0123456789abcdef";
        var path = route.Replace("{caseId:long}", "1", StringComparison.Ordinal).Replace("{userId:long}", alice.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{TusFileId?}", upload, StringComparison.Ordinal).Replace("{uploadId}", upload, StringComparison.Ordinal);
        var body = http == HttpMethod.Post || http == HttpMethod.Put ? new { } : null;

        // B is not a member: 404, with the same body as an id that does not exist.
        using var b = factory.Client();
        await b.LoginAsync("bob", Password);
        var asNonMember = await b.SendAsync(http, path, body);
        Assert.Equal(HttpStatusCode.NotFound, asNonMember.StatusCode);
        var unknownPath = route.Replace("{caseId:long}", "4242", StringComparison.Ordinal).Replace("{userId:long}", "1", StringComparison.Ordinal)
            .Replace("{TusFileId?}", upload, StringComparison.Ordinal).Replace("{uploadId}", upload, StringComparison.Ordinal);
        var asUnknown = await a.SendAsync(http, unknownPath, body);
        Assert.Equal(HttpStatusCode.NotFound, asUnknown.StatusCode);
        Assert.Equal(await asUnknown.Content.ReadAsStringAsync(Ct), await asNonMember.Content.ReadAsStringAsync(Ct));

        // Admin without membership: 404 too.
        using var root = factory.Client();
        await root.LoginAsync("root", Password);
        Assert.Equal(HttpStatusCode.NotFound, (await root.SendAsync(http, path, body)).StatusCode);

        // A read-only member: a route that asks for reading answers, every other one is 403 (the case is visible) and
        // says which role it needs, whatever its method.
        using var c = factory.Client();
        await c.LoginAsync("carol", Password);
        var asReader = await c.SendAsync(http, path, body);
        if (needs == "ro")
        {
            Assert.Equal(HttpMethod.Get, http);
            Assert.Equal(HttpStatusCode.OK, asReader.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, asReader.StatusCode);
            if (http != HttpMethod.Head)
            {
                Assert.Contains("needs " + needs, await asReader.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            }
        }

        Assert.NotEqual(bob.Id, carol.Id);
    }

    [Fact]
    public async Task AHiddenCaseIs404ForItsOwnMembers()
    {
        await using var factory = new ApiFactory();
        var alice = await factory.AddUserAsync("alice", Password);
        using var a = factory.Client();
        await a.LoginAsync("alice", Password);
        await a.PostAsync("/api/v1/cases", new { name = "incident 1" });
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/v1/cases/1")).StatusCode);
        await using (var db = factory.Open())
        {
            await CaseOperations.TransitionAsync(db, factory.Store.Audit, new Core.Audit.AuditActor("alice", alice.Id), 1, CaseState.SoftDeleted, factory.Clock.GetUtcNow().UtcDateTime, Ct);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync("/api/v1/cases/1")).StatusCode);
        Assert.Equal("[]", await (await a.GetAsync("/api/v1/cases")).Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AnalystsGet403OnUsersAnd404OnEveryAdminRoute()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var a = factory.Client();
        await a.LoginAsync("alice", Password);
        await a.PostAsync("/api/v1/cases", new { name = "incident 1" });
        // /users is a visible admin function: a role refusal.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsync("/api/v1/users", new { name = "x", password = Password })).StatusCode);

        // /admin/... does not exist for a non-admin: 404 on every registered admin route, the case's own owner included.
        var adminRoutes = AdminRoutes().ToList();
        Assert.Equal(4, adminRoutes.Count);
        foreach (var (method, route) in adminRoutes)
        {
            var http = new HttpMethod(method);
            var path = route.Replace("{id:long}", "1", StringComparison.Ordinal).Replace("{userId:long}", "1", StringComparison.Ordinal);
            var response = await a.SendAsync(http, path, http == HttpMethod.Get || http == HttpMethod.Delete ? null : new { });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>Every (method, route) under /api/v1/admin/ in the live route table.</summary>
    private static IEnumerable<(string Method, string Route)> AdminRoutes()
    {
        using var factory = new ApiFactory();
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var text = (endpoint.RoutePattern.RawText ?? string.Empty).TrimEnd('/');
            if (!CaseAccess.IsAdminRoute(text))
            {
                continue;
            }

            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                yield return (method, text);
            }
        }
    }

    [Fact]
    public void TheListAndCreateExemptionIsExactNotAPrefix()
    {
        // Exempt: the collection itself, with or without the trailing slash a group's "" route carries.
        Assert.False(CaseAccess.IsCaseRoute("/api/v1/cases"));
        Assert.False(CaseAccess.IsCaseRoute("/api/v1/cases/"));
        // Anything deeper is a case route whatever the parameter is called, a literal segment included.
        Assert.True(CaseAccess.IsCaseRoute("/api/v1/cases/{caseId:long}"));
        Assert.True(CaseAccess.IsCaseRoute("/api/v1/cases/{caseId:long}/anything"));
        Assert.True(CaseAccess.IsCaseRoute("/api/v1/cases/{id:long}/x"));
        Assert.True(CaseAccess.IsCaseRoute("/api/v1/cases/all"));
        // Not under the prefix at all.
        Assert.False(CaseAccess.IsCaseRoute("/api/v1/casesx/{caseId:long}"));
        Assert.False(CaseAccess.IsCaseRoute("/api/v1/admin/cases/{id:long}/members"));

        // Through the assertion: each unguarded shape stops the host, the exempt shapes do not.
        foreach (var route in new[] { "/api/v1/cases/{caseId:long}/anything", "/api/v1/cases/{id:long}/x", "/api/v1/cases/all" })
        {
            var e = Assert.Throws<InvalidOperationException>(() => CaseAccess.AssertGuarded(Build(app => app.MapGet(route, () => "x"))));
            Assert.Contains($"GET {route}: a case route without RequireCaseRole", e.Message, StringComparison.Ordinal);
        }

        // The list and create routes exist in the production table without a requirement and the table passes.
        CaseAccess.AssertGuarded(Build(_ => { }));
    }

    [Fact]
    public void AdminRoutesCarryACaseIdWithTheAdminPolicyInPlaceOfMembership()
    {
        // The production admin routes: a case id, no RequireCaseRole, the admin policy; the table passes.
        var app = Build(_ => { });
        var admin = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => CaseAccess.IsAdminRoute(e.RoutePattern.RawText)).ToList();
        Assert.Equal(4, admin.Count);
        Assert.All(admin, e => Assert.Null(e.Metadata.GetMetadata<CaseAccessRequirement>()));
        Assert.All(admin, e => Assert.Contains(e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>(), a => a.Policy == ApiHost.AdminPolicy));
        Assert.Equal(3, admin.Count(e => e.RoutePattern.Parameters.Any(p => p.Name == "id")));
        CaseAccess.AssertGuarded(app);

        // An admin route without the admin policy is refused; an admin route with a membership requirement is refused too.
        var unpoliced = Build(a => a.MapGet("/api/v1/admin/cases/{id:long}/extra", () => "x"));
        Assert.Contains("an admin route without the admin policy", Assert.Throws<InvalidOperationException>(() => CaseAccess.AssertGuarded(unpoliced)).Message, StringComparison.Ordinal);
        var contradictory = Build(a => a.MapGet("/api/v1/admin/cases/{id:long}/extra", () => "x").RequireAuthorization(ApiHost.AdminPolicy).RequireCaseRole(Urlog.Core.Metadata.MemberRole.Ro));
        Assert.Contains("an admin route with RequireCaseRole", Assert.Throws<InvalidOperationException>(() => CaseAccess.AssertGuarded(contradictory)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostWithAnUnguardedRouteDoesNotStart()
    {
        // The production composition plus one route that forgot RequireCaseRole: the assertion names it and refuses.
        var unguarded = Build(app => app.MapGet("/api/v1/cases/{caseId:long}/extra", () => "x"));
        var e1 = Assert.Throws<InvalidOperationException>(() => CaseAccess.AssertGuarded(unguarded));
        Assert.Contains("GET /api/v1/cases/{caseId:long}/extra: a case route without RequireCaseRole", e1.Message, StringComparison.Ordinal);

        // A case route under another parameter name is caught by the prefix rule.
        var renamed = Build(app => app.MapGet("/api/v1/cases/{id:long}/extra", () => "x"));
        Assert.Contains("a case route without RequireCaseRole", Assert.Throws<InvalidOperationException>(() => CaseAccess.AssertGuarded(renamed)).Message, StringComparison.Ordinal);

        // An anonymous route under /api/v1 other than login.
        var anonymous = Build(app => app.MapGet("/api/v1/open", () => "x").AllowAnonymous());
        Assert.Contains("GET /api/v1/open: allows anonymous callers", Assert.Throws<InvalidOperationException>(() => CaseAccess.AssertGuarded(anonymous)).Message, StringComparison.Ordinal);

        // An admin route without the admin policy.
        var admin = Build(app => app.MapGet("/api/v1/admin/extra", () => "x"));
        Assert.Contains("GET /api/v1/admin/extra: an admin route without the admin policy", Assert.Throws<InvalidOperationException>(() => CaseAccess.AssertGuarded(admin)).Message, StringComparison.Ordinal);

        // The production table itself passes.
        CaseAccess.AssertGuarded(Build(_ => { }));
    }

    private static WebApplication Build(Action<WebApplication> extra)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        ApiHost.ConfigureServices(builder);
        var app = builder.Build();
        ApiHost.MapEndpoints(app);
        extra(app);
        return app;
    }
}
