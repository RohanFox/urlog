using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Urlog.Api.Auth;

/// <summary>Endpoint metadata: the caller must be a member of the route's case with at least this role.</summary>
public sealed class CaseAccessRequirement(MemberRole minimum)
{
    public MemberRole Minimum { get; } = minimum;
}

/// <summary>The case the request is about and the caller's role in it, on <c>HttpContext.Features</c> for the handler.</summary>
public sealed record CaseContext(CaseRecord Case, MemberRole Role);

public static class CaseAccessExtensions
{
    /// <summary>Marks a case endpoint with the role it needs; <see cref="CaseAccessMiddleware"/> enforces it before the handler runs.</summary>
    public static TBuilder RequireCaseRole<TBuilder>(this TBuilder builder, MemberRole minimum) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new CaseAccessRequirement(minimum));
    }
}

/// <summary>
/// The case ACL (design record, "Security"), enforced before parameter binding and validation on every endpoint that carries a
/// <see cref="CaseAccessRequirement"/>: no membership, or a case in a hidden state, is 404 with the same body as an unknown
/// id; a membership below the required role is 403. An endpoint whose route has a case id but no requirement is refused
/// with 500 (fail closed); <see cref="CaseAccess.AssertGuarded"/> makes that unreachable by refusing to start the host.
/// Admin has no implicit access: membership is what counts here, for every role.
/// </summary>
public sealed class CaseAccessMiddleware(RequestDelegate next, ILogger<CaseAccessMiddleware> logger)
{
    public const string RouteParameter = "caseId";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        var requirement = endpoint?.Metadata.GetMetadata<CaseAccessRequirement>();
        var hasCaseId = context.Request.RouteValues.TryGetValue(RouteParameter, out var raw);
        if (requirement is null)
        {
            if (hasCaseId || CaseAccess.IsCaseRoute(endpoint))
            {
                Log.UnguardedRoute(logger, endpoint?.DisplayName ?? context.Request.Path);
                await Problems.WriteAsync(context, StatusCodes.Status500InternalServerError, "case route without an access requirement").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        var problem = Problems.CaseNotFound();
        if (!hasCaseId || !long.TryParse(raw?.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var caseId))
        {
            await Problems.WriteAsync(context, problem.Status!.Value, problem.Title!, problem.Type).ConfigureAwait(false);
            return;
        }

        if (context.Features.Get<SessionContext>() is not { } session)
        {
            // Authorization runs first; this is the fail-closed branch for an endpoint that somehow allowed anonymous callers.
            var unauthorized = Problems.Unauthorized();
            await Problems.WriteAsync(context, unauthorized.Status!.Value, unauthorized.Title!, unauthorized.Type).ConfigureAwait(false);
            return;
        }

        var db = context.RequestServices.GetRequiredService<MetadataDb>();
        var membership = await CaseAcl.MembershipAsync(db, session.User.Id, caseId, context.RequestAborted).ConfigureAwait(false);
        if (membership is null)
        {
            await Problems.WriteAsync(context, problem.Status!.Value, problem.Title!, problem.Type).ConfigureAwait(false);
            return;
        }

        if (!CaseAcl.Satisfies(membership.Role, requirement.Minimum))
        {
            var forbidden = Problems.Forbidden();
            await Problems.WriteAsync(context, forbidden.Status!.Value, $"your role in this case is {Names.Of(membership.Role)}; this needs {Names.Of(requirement.Minimum)}", forbidden.Type).ConfigureAwait(false);
            return;
        }

        context.Features.Set(new CaseContext(membership.Case, membership.Role));
        await next(context).ConfigureAwait(false);
    }
}

/// <summary>The startup assertion: the route table is checked once, and the host does not start if any route could skip the ACL.</summary>
public static class CaseAccess
{
    public const string CasesPrefix = "/api/v1/cases/";

    /// <summary>The one route text exempt from the membership rule: the list (GET) and create (POST) routes, which name no case.</summary>
    public const string CasesCollection = "/api/v1/cases";

    public const string AdminPrefix = "/api/v1/admin/";

    public const string ApiPrefix = "/api/v1/";

    public const string LoginRoute = "/api/v1/auth/login";

    /// <summary>
    /// True for every route under <c>/api/v1/cases/</c> except the collection itself: the exemption is an exact match on
    /// <c>/api/v1/cases</c> (with or without the trailing slash a group's "" route carries), not a prefix, so
    /// <c>/api/v1/cases/{caseId}/anything</c>, <c>/api/v1/cases/{id}/x</c> and <c>/api/v1/cases/all</c> are case routes
    /// whatever the parameter is called.
    /// </summary>
    public static bool IsCaseRoute(Endpoint? endpoint) => endpoint is RouteEndpoint route && IsCaseRoute(route.RoutePattern.RawText);

    public static bool IsCaseRoute(string? routeText)
    {
        if (routeText is null)
        {
            return false;
        }

        var trimmed = routeText.TrimEnd('/');
        return trimmed.StartsWith(CasesPrefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(trimmed, CasesCollection, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True under <c>/api/v1/admin/</c>: the admin's explicit grant path. These routes carry a case id but no membership
    /// requirement by design; the assertion accepts the admin policy in its place (and demands it), the case-access
    /// middleware ignores them (their parameter is <c>id</c>, not <c>caseId</c>), and a non-admin is answered 404 there as
    /// if the route did not exist (<see cref="SessionAuthenticationHandler.HandleForbiddenAsync"/>).
    /// </summary>
    public static bool IsAdminRoute(Endpoint? endpoint) => endpoint is RouteEndpoint route && IsAdminRoute(route.RoutePattern.RawText);

    public static bool IsAdminRoute(string? routeText) => routeText is { } text && text.StartsWith(AdminPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every route with a <c>caseId</c> parameter or under <c>/api/v1/cases/</c> carries a <see cref="CaseAccessRequirement"/>;
    /// every route under <c>/api/v1/</c> other than login requires an authenticated caller (none allows anonymous);
    /// every route under <c>/api/v1/users</c> and <c>/api/v1/admin/</c> carries the admin policy, and an admin route carries
    /// no membership requirement (the admin policy stands in its place).
    /// </summary>
    /// <exception cref="InvalidOperationException">A route breaks one of the rules; the message lists every offender.</exception>
    public static void AssertGuarded(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var offenders = new List<string>();
        foreach (var endpoint in app.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var text = endpoint.RoutePattern.RawText ?? string.Empty;
            var hasCaseParameter = endpoint.RoutePattern.Parameters.Any(p => string.Equals(p.Name, CaseAccessMiddleware.RouteParameter, StringComparison.OrdinalIgnoreCase));
            var display = Describe(endpoint);
            var hasRequirement = endpoint.Metadata.GetMetadata<CaseAccessRequirement>() is not null;
            var hasAdminPolicy = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => string.Equals(a.Policy, ApiHost.AdminPolicy, StringComparison.Ordinal));
            if (IsAdminRoute(text))
            {
                // The admin grant path: a case id with the admin policy in place of membership; a membership requirement
                // here would be a contradiction (the middleware never runs on these routes), so it is refused too.
                if (hasRequirement)
                {
                    offenders.Add($"{display}: an admin route with RequireCaseRole (membership is not what admin routes check)");
                }
            }
            else if ((hasCaseParameter || IsCaseRoute(text)) && !hasRequirement)
            {
                offenders.Add($"{display}: a case route without RequireCaseRole");
            }

            if (!text.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isLogin = string.Equals(text, LoginRoute, StringComparison.OrdinalIgnoreCase);
            if (!isLogin && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                offenders.Add($"{display}: allows anonymous callers");
            }

            var adminOnly = text.StartsWith(ApiPrefix + "users", StringComparison.OrdinalIgnoreCase) || IsAdminRoute(text);
            if (adminOnly && !hasAdminPolicy)
            {
                offenders.Add($"{display}: an admin route without the admin policy");
            }
        }

        if (offenders.Count > 0)
        {
            throw new InvalidOperationException("the route table is not guarded; the host refuses to start:\n  " + string.Join("\n  ", offenders));
        }
    }

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return (methods is { Count: > 0 } ? string.Join(",", methods) : "ANY") + " " + (endpoint.RoutePattern.RawText ?? RoutePatternFactory.Pattern(endpoint.RoutePattern.PathSegments).RawText);
    }
}
