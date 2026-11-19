using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Audit;
using Urlog.Core.Auth;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Endpoints;

/// <summary><c>/api/v1/auth</c>: login (the one anonymous route, throttled), logout, me.</summary>
public static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var group = api.MapGroup("/auth");
        group.MapPost("/login", LoginAsync).AllowAnonymous().AddEndpointFilter<LoginThrottleFilter>().WithName("Login");
        group.MapPost("/logout", LogoutAsync).WithName("Logout");
        group.MapGet("/me", Me).WithName("Me");
        // The login throttle counts these too: a session in the wrong hands gets no faster a guess at the password than the login page gives.
        group.MapPost("/password", ChangePasswordAsync).AddEndpointFilter<LoginThrottleFilter>().WithName("ChangeOwnPassword");
    }

    /// <summary>
    /// A user changes their own password. The current one is verified outside any transaction, like a login; the
    /// new one is held to <see cref="PasswordPolicy"/>. Every other session of the user ends, this one stays. The
    /// row (<c>user_password_change</c>) holds neither password. A wrong current password is 403 and changes nothing.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest request, CurrentUser me, HttpContext http, MetadataDb db, MetadataStore store, PasswordService passwords,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger("Urlog.Api.Auth");
        var current = me.User.PasswordHash;
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!await passwords.VerifyAsync(current, request.CurrentPassword, cancellationToken).ConfigureAwait(false))
        {
            Log.PasswordChangeRefused(logger, me.Name, address);
            return TypedResults.Problem(Problems.Make(StatusCodes.Status403Forbidden, "the current password is not right", "https://urlog/problems/wrong-password"));
        }

        if (PasswordPolicy.Check(request.NewPassword) is { } reason)
        {
            return TypedResults.Problem(Problems.BadRequest(reason));
        }

        if (string.Equals(request.NewPassword, request.CurrentPassword, StringComparison.Ordinal))
        {
            return TypedResults.Problem(Problems.BadRequest("the new password is the current one"));
        }

        var hash = await passwords.HashAsync(request.NewPassword, cancellationToken).ConfigureAwait(false);
        var ended = await UserOperations.ChangeOwnPasswordAsync(db, store.Audit, me.Actor, me.Id, current, hash, me.Session.Id, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        Log.PasswordChanged(logger, me.Name, ended);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The verify runs outside any transaction (150 ms of hashing must not hold the write lock), against the user's hash
    /// or, for a name that does not exist, against <see cref="PasswordHasher.DummyHash"/>, so the response time is the same;
    /// then <see cref="LoginOperations.RecordAsync"/> applies the outcome with its audit rows. One 401 body for unknown,
    /// wrong and locked alike.
    /// </summary>
    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request, HttpContext http, MetadataDb db, MetadataStore store, PasswordService passwords,
        IOptions<UrlogOptions> options, TimeProvider clock, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var auth = options.Value.Auth;
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var user = UserOperations.TryNormalizeName(request.Name, out var normalized)
            ? await UserOperations.FindByNameAsync(db, normalized, cancellationToken).ConfigureAwait(false)
            : null;
        var verified = await passwords.VerifyAsync(user?.PasswordHash ?? PasswordHasher.DummyHash, request.Password, cancellationToken).ConfigureAwait(false) && user is not null;
        var rehash = verified && PasswordHasher.NeedsRehash(user!.PasswordHash)
            ? await passwords.HashAsync(request.Password, cancellationToken).ConfigureAwait(false)
            : null;

        var now = clock.GetUtcNow().UtcDateTime;
        var result = await LoginOperations.RecordAsync(db, store.Audit, new LoginAttempt(request.Name, address, verified, rehash), auth.Lockout, auth.Sessions, now, cancellationToken).ConfigureAwait(false);
        var logger = loggers.CreateLogger("Urlog.Api.Auth");
        Log.Login(logger, result.Outcome, normalized, address);
        if (result.Outcome != LoginOutcome.Success)
        {
            return TypedResults.Problem(Problems.InvalidCredentials());
        }

        http.Response.Cookies.Append(SessionAuthenticationHandler.CookieName, result.Session!.Token, SessionAuthenticationHandler.CookieOptions(http));
        return TypedResults.Ok(new SessionResponse(UserResponse.From(result.User!, auth.Lockout, now), result.Session.Row.CsrfToken));
    }

    /// <summary>Deletes the session row and writes <c>logout</c> in the same transaction; the cookie is cleared either way.</summary>
    private static async Task<NoContent> LogoutAsync(CurrentUser me, HttpContext http, MetadataDb db, MetadataStore store, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await AuditChain.BeginTransactionAsync(db, cancellationToken).ConfigureAwait(false);
            await SessionOperations.DeleteAsync(db, me.Session.Id, cancellationToken).ConfigureAwait(false);
            var details = new Dictionary<string, object?> { ["session"] = me.Session.Id[..12] };
            await store.Audit.AppendAsync(db, AuditEntry.ForUser(me.Name, me.Id, AuditActions.Logout, me.User, details), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }

        http.Response.Cookies.Delete(SessionAuthenticationHandler.CookieName, SessionAuthenticationHandler.CookieOptions(http));
        var logger = loggers.CreateLogger("Urlog.Api.Auth");
        Log.Logout(logger, me.Name);
        return TypedResults.NoContent();
    }

    private static Ok<SessionResponse> Me(CurrentUser me, IOptions<UrlogOptions> options, TimeProvider clock) =>
        TypedResults.Ok(new SessionResponse(UserResponse.From(me.User, options.Value.Auth.Lockout, clock.GetUtcNow().UtcDateTime), me.Session.CsrfToken));
}
