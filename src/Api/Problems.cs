using Microsoft.AspNetCore.Mvc;

namespace Urlog.Api;

/// <summary>
/// RFC 7807 bodies for the responses the middlewares and the auth handler write themselves. The 404 for a case the user
/// cannot see is byte-identical to the 404 for an id that does not exist, and the 401 for a bad login is one body for an
/// unknown name, a wrong password and a locked account.
/// </summary>
internal static class Problems
{
    public const string ContentType = "application/problem+json";

    public static Task WriteAsync(HttpContext context, int status, string title, string? type = null, string? detail = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        return context.Response.WriteAsJsonAsync(Make(status, title, type, detail), options: null, ContentType, context.RequestAborted);
    }

    /// <summary>Writes the problem as it is, its extension members included.</summary>
    public static Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = ContentType;
        return context.Response.WriteAsJsonAsync(problem, options: null, ContentType, context.RequestAborted);
    }

    public static ProblemDetails Make(int status, string title, string? type = null, string? detail = null) => new()
    {
        Status = status,
        Title = title,
        Type = type,
        Detail = detail,
    };

    public static ProblemDetails Unauthorized() => Make(StatusCodes.Status401Unauthorized, "not logged in", "https://urlog/problems/unauthorized");

    public static ProblemDetails InvalidCredentials() => Make(StatusCodes.Status401Unauthorized, "invalid credentials", "https://urlog/problems/invalid-credentials");

    public static ProblemDetails Forbidden() => Make(StatusCodes.Status403Forbidden, "forbidden", "https://urlog/problems/forbidden");

    public static ProblemDetails Csrf() => Make(StatusCodes.Status403Forbidden, "missing or wrong X-CSRF-Token header", "https://urlog/problems/csrf");

    public static ProblemDetails CaseNotFound() => Make(StatusCodes.Status404NotFound, "no such case", "https://urlog/problems/not-found");

    public static ProblemDetails NotFound(string title) => Make(StatusCodes.Status404NotFound, title, "https://urlog/problems/not-found");

    public static ProblemDetails Conflict(string title) => Make(StatusCodes.Status409Conflict, title, "https://urlog/problems/conflict");

    /// <summary>412: the request names a version that is no longer current; <c>current</c> is the one to repeat it with.</summary>
    public static ProblemDetails Stale(string title, string? current)
    {
        var problem = Make(StatusCodes.Status412PreconditionFailed, title, "https://urlog/problems/stale");
        problem.Extensions["current"] = current;
        return problem;
    }

    /// <summary>428: a change to a versioned record without <c>If-Match</c>.</summary>
    public static ProblemDetails PreconditionRequired(string what) => Make(StatusCodes.Status428PreconditionRequired, $"If-Match is required: {what}", "https://urlog/problems/precondition-required");

    public static ProblemDetails BadRequest(string title) => Make(StatusCodes.Status400BadRequest, title, "https://urlog/problems/bad-request");

    public static ProblemDetails UploadPending(int retryAfter) => Make(StatusCodes.Status503ServiceUnavailable, "the upload is complete and its record is pending", "https://urlog/problems/upload-pending",
        $"the metadata store is busy; the file is recorded without another request. Ask HEAD or the status route after {retryAfter} s and send nothing again");

    public static ProblemDetails Throttled(int retryAfter) => Make(StatusCodes.Status429TooManyRequests, "too many login attempts", "https://urlog/problems/throttled", $"retry after {retryAfter} s");
}
