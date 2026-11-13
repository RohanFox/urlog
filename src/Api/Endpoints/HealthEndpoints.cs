using Microsoft.AspNetCore.Http.HttpResults;

namespace Urlog.Api.Endpoints;

/// <summary>
/// <c>GET /api/v1/health</c>, anonymous: 200 once the metadata file and the event store are known to belong
/// together, 503 with the reason until then (the event store is not answering, or the two store identities
/// differ, with both values). The one route that answers before that comparison has passed.
/// </summary>
public static class HealthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/health", Health).AllowAnonymous().WithName("Health");
    }

    private static JsonHttpResult<StoreState> Health(StoreReadiness readiness)
    {
        var state = readiness.State;
        return TypedResults.Json(state, statusCode: state.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
