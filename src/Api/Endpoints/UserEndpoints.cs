using Urlog.Api.Auth;
using Urlog.Api.Contracts;
using Urlog.Core.Auth;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Endpoints;

/// <summary><c>/api/v1/users</c>, admin only: list, create, get, role, unlock, password reset.</summary>
public static class UserEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var group = api.MapGroup("/users").RequireAuthorization(ApiHost.AdminPolicy);
        group.MapGet("", ListAsync).WithName("ListUsers");
        group.MapPost("", CreateAsync).WithName("CreateUser");
        group.MapGet("/{id:long}", GetAsync).WithName("GetUser");
        group.MapPut("/{id:long}/role", ChangeRoleAsync).WithName("ChangeUserRole");
        group.MapPost("/{id:long}/unlock", UnlockAsync).WithName("UnlockUser");
        group.MapPost("/{id:long}/password", SetPasswordAsync).WithName("SetUserPassword");
    }

    private static async Task<Ok<List<UserResponse>>> ListAsync(MetadataDb db, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(users.Select(u => UserResponse.From(u, options.Value.Auth.Lockout, now)).ToList());
    }

    private static async Task<Results<Created<UserResponse>, ProblemHttpResult>> CreateAsync(
        CreateUserRequest request, CurrentUser me, MetadataDb db, MetadataStore store, PasswordService passwords,
        IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (PasswordPolicy.Check(request.Password) is { } reason)
        {
            return TypedResults.Problem(Problems.BadRequest(reason));
        }

        var hash = await passwords.HashAsync(request.Password, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await UserOperations.CreateAsync(db, store.Audit, me.Actor, request.Name, request.Role, hash, now, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"/api/v1/users/{user.Id}", UserResponse.From(user, options.Value.Auth.Lockout, now));
    }

    private static async Task<Results<Ok<UserResponse>, ProblemHttpResult>> GetAsync(long id, MetadataDb db, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
        return user is null
            ? TypedResults.Problem(Problems.NotFound("no such user"))
            : TypedResults.Ok(UserResponse.From(user, options.Value.Auth.Lockout, clock.GetUtcNow().UtcDateTime));
    }

    private static async Task<Ok<UserResponse>> ChangeRoleAsync(long id, ChangeRoleRequest request, CurrentUser me, MetadataDb db, MetadataStore store, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await UserOperations.ChangeRoleAsync(db, store.Audit, me.Actor, id, request.Role, now, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(UserResponse.From(user, options.Value.Auth.Lockout, now));
    }

    private static async Task<Ok<UserResponse>> UnlockAsync(long id, CurrentUser me, MetadataDb db, MetadataStore store, IOptions<UrlogOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await UserOperations.UnlockAsync(db, store.Audit, me.Actor, id, now, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(UserResponse.From(user, options.Value.Auth.Lockout, now));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SetPasswordAsync(long id, SetPasswordRequest request, CurrentUser me, MetadataDb db, MetadataStore store, PasswordService passwords, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (PasswordPolicy.Check(request.Password) is { } reason)
        {
            return TypedResults.Problem(Problems.BadRequest(reason));
        }

        var hash = await passwords.HashAsync(request.Password, cancellationToken).ConfigureAwait(false);
        await UserOperations.ResetPasswordAsync(db, store.Audit, me.Actor, id, hash, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }
}
