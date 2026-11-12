using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;

namespace Urlog.Api.Auth;

/// <summary>
/// The second lock on mutating requests (the first is <c>SameSite=Strict</c>): every request whose method is not GET,
/// HEAD or OPTIONS must carry the session's CSRF token in <see cref="HeaderName"/>, compared in constant time. A
/// middleware, not a per-endpoint filter, so no endpoint can opt out by omission; the only exemption is an endpoint that
/// allows anonymous callers (login), which has no session to protect.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-CSRF-Token";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return next(context);
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return next(context);
        }

        // Authorization ran before this: an endpoint without a session has already been refused with 401.
        if (context.Features.Get<SessionContext>() is not { } session)
        {
            return next(context);
        }

        var header = context.Request.Headers[HeaderName].ToString();
        if (header.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(session.Session.CsrfToken)))
        {
            var problem = Problems.Csrf();
            return Problems.WriteAsync(context, StatusCodes.Status403Forbidden, problem.Title!, problem.Type);
        }

        return next(context);
    }
}
