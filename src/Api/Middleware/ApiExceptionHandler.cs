using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Diagnostics;

namespace Urlog.Api.Middleware;

/// <summary>
/// Domain exceptions to status codes, as RFC 7807 bodies: not found (404), a rule refused the change (409), a bad name or
/// value (400), a complete upload whose record waits for a locked metadata file (503 with <c>Retry-After</c>), a body the framework could not read (400 in every environment: the Development host throws
/// <see cref="BadHttpRequestException"/> instead of answering by itself). The messages of the metadata exceptions are
/// written for users and carry no secret. Anything else falls through to the default handler: 500 with no detail.
/// </summary>
internal sealed class ApiExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is Uploads.UploadPendingException pending)
        {
            httpContext.Response.Headers.RetryAfter = pending.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var problem = exception switch
        {
            Uploads.UploadPendingException p => Problems.UploadPending(p.RetryAfterSeconds),
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

        return Write(httpContext, problem.Status!.Value, problem.Title!, problem.Type, problem.Detail);
    }

    private static async ValueTask<bool> Write(HttpContext httpContext, int status, string title, string? type, string? detail)
    {
        await Problems.WriteAsync(httpContext, status, title, type, detail).ConfigureAwait(false);
        return true;
    }
}
