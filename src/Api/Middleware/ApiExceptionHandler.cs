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
            UserNotFoundException or NotAMemberException or RecordNotFoundException => Problems.NotFound(exception.Message),
            StaleWriteException stale => Problems.Stale(stale.Message, stale.Current),
            Auth.PreconditionRequiredException => Problems.PreconditionRequired(exception.Message),
            SelectionTooLargeException => Problems.Make(StatusCodes.Status422UnprocessableEntity, exception.Message, "https://urlog/problems/selection-too-large"),
            LifecycleRoleException => Problems.Make(StatusCodes.Status403Forbidden, exception.Message, "https://urlog/problems/forbidden"),
            UserNameTakenException or AlreadyMemberException or LastAdminException or LastOwnerException
                or InvalidCaseTransitionException or CaseNotActiveException or CaseFrozenException or SettingsRefusedException
                or LifecycleRefusedException or JobRefusedException or JobStateException => Problems.Conflict(exception.Message),
            InvalidUserNameException or ArgumentException => Problems.BadRequest(exception.Message),
            _ => null,
        };
        if (problem is null)
        {
            return ValueTask.FromResult(false);
        }

        return Write(httpContext, problem);
    }

    private static async ValueTask<bool> Write(HttpContext httpContext, Microsoft.AspNetCore.Mvc.ProblemDetails problem)
    {
        await Problems.WriteAsync(httpContext, problem).ConfigureAwait(false);
        return true;
    }
}
