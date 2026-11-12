using System.Security.Claims;
using System.Text.Encodings.Web;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Auth;

/// <summary>The live session and its user, on <c>HttpContext.Features</c> for the CSRF middleware and the handlers.</summary>
public sealed record SessionContext(Session Session, User User)
{
    public AuditActor Actor => new(User.Name, User.Id);
}

/// <summary>
/// The one authentication scheme: the <c>urlog_session</c> cookie carries a token whose SHA-256 is a row in
/// <c>sessions</c>. Every request runs one query (<c>sessions</c> joined to <c>users</c>), checks the idle and absolute
/// timeouts and the lockout, touches <c>last_seen_at</c> at most once per interval, and puts a <see cref="SessionContext"/>
/// on the request. The role is read from <c>users</c> on every request, so a role change or a lockout applies at once.
/// No session -> the challenge is 401 with a problem body and the cookie cleared; a role refusal is 403.
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<UrlogOptions> urlog,
    TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Session";

    public const string CookieName = "urlog_session";

    /// <summary>HttpOnly, Secure (the deployed origin is HTTPS at the proxy; browsers accept Secure on localhost), SameSite=Strict, no Max-Age: the server's timeouts rule.</summary>
    public static CookieOptions CookieOptions(HttpContext context) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
    };

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var auth = urlog.Value.Auth;
        var now = clock.GetUtcNow().UtcDateTime;
        var db = Context.RequestServices.GetRequiredService<MetadataDb>();
        var found = await SessionOperations.FindAsync(db, token, now, auth.IdleTimeout, Context.RequestAborted).ConfigureAwait(false);
        if (found is null || LoginOperations.IsLocked(found.User, auth.Lockout, now))
        {
            return AuthenticateResult.NoResult();
        }

        if (now - found.Session.LastSeenAt >= auth.SessionTouchInterval)
        {
            await SessionOperations.TouchAsync(db, found.Session.Id, now, Context.RequestAborted).ConfigureAwait(false);
        }

        Context.Features.Set(new SessionContext(found.Session, found.User));
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, found.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, found.User.Name),
                new Claim(ClaimTypes.Role, Names.Of(found.User.Role)),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Cookies.Delete(CookieName, CookieOptions(Context));
        return Problems.WriteAsync(Context, StatusCodes.Status401Unauthorized, Problems.Unauthorized().Title!, Problems.Unauthorized().Type);
    }

    /// <summary>
    /// A role refusal is 403, except under <c>/api/v1/admin/</c>, where a non-admin gets 404 as if the route did not exist:
    /// the admin grant path is invisible to analysts, like a case they are not a member of.
    /// </summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (CaseAccess.IsAdminRoute(Context.GetEndpoint()))
        {
            var hidden = Problems.NotFound("not found");
            return Problems.WriteAsync(Context, hidden.Status!.Value, hidden.Title!, hidden.Type);
        }

        return Problems.WriteAsync(Context, StatusCodes.Status403Forbidden, Problems.Forbidden().Title!, Problems.Forbidden().Type);
    }
}

/// <summary>The caller, bound from the request's <see cref="SessionContext"/> (minimal-API <c>BindAsync</c>).</summary>
public sealed class CurrentUser
{
    private CurrentUser(SessionContext context)
    {
        Session = context.Session;
        User = context.User;
    }

    public Session Session { get; }

    public User User { get; }

    public long Id => User.Id;

    public string Name => User.Name;

    public UserRole Role => User.Role;

    public bool IsAdmin => User.Role == UserRole.Admin;

    public AuditActor Actor => new(User.Name, User.Id);

    public static ValueTask<CurrentUser?> BindAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(context.Features.Get<SessionContext>() is { } session ? new CurrentUser(session) : null);
    }
}
