using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Diagnostics;

namespace Urlog.Api.Middleware;

/// <summary>
/// Domain exceptions to status codes, as RFC 7807 bodies: not found (404), a rule refused the change (409), a bad name or
/// value (400), a body the framework could not read (400 in every environment: the Development host throws
/// <see cref="BadHttpRequestException"/> instead of answering by itself). The messages of the metadata exceptions are
/// written for users and carry no secret. Anything else falls through to the default handler: 500 with no detail.
/// </summary>
internal sealed class ApiExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);
        var problem = exception switch
        {
            BadHttpRequestException bad => Problems.Make(bad.StatusCode, "malformed request", "https://urlog/problems/bad-request"),
            CaseNotFoundException => Problems.CaseNotFound(),
            UserNotFoundException or NotAMemberException => Problems.NotFound(exception.Message),
            UserNameTakenException or AlreadyMemberException or LastAdminException or LastOwnerException
                or InvalidCaseTransitionException or CaseNotActiveException => Problems.Conflict(exception.Message),
            InvalidUserNameException or ArgumentException => Problems.BadRequest(exception.Message),
            _ => null,
        };
        if (problem is null)
        {
            return ValueTask.FromResult(false);
        }

        return Write(httpContext, problem.Status!.Value, problem.Title!, problem.Type);
    }

    private static async ValueTask<bool> Write(HttpContext httpContext, int status, string title, string? type)
    {
        await Problems.WriteAsync(httpContext, status, title, type).ConfigureAwait(false);
        return true;
    }
}
