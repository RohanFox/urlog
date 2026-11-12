using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>The session store: token to row id, the two expiries, touch, delete, sweep.</summary>
public sealed class SessionOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime T0 = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Idle = TimeSpan.FromHours(8);
    private static readonly TimeSpan Absolute = TimeSpan.FromHours(24);

    [Fact]
    public async Task TheRowHoldsTheHashOfTheTokenAndIsFoundByTheToken()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var created = SessionOperations.Create(alice, "198.51.100.7", T0, Absolute);
        db.Sessions.Add(created.Row);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(64, created.Row.Id.Length);
        Assert.NotEqual(created.Token, created.Row.CsrfToken);
        Assert.DoesNotContain(created.Token, created.Row.Id, StringComparison.Ordinal);
        var found = await SessionOperations.FindAsync(db, created.Token, T0.AddMinutes(1), Idle, Ct);
        Assert.NotNull(found);
        Assert.Equal((created.Row.Id, alice, "alice"), (found.Session.Id, found.User.Id, found.User.Name));
        Assert.Null(await SessionOperations.FindAsync(db, created.Token + "x", T0, Idle, Ct));
        Assert.Null(await SessionOperations.FindAsync(db, created.Row.Id, T0, Idle, Ct));
        Assert.Null(await SessionOperations.FindAsync(db, "", T0, Idle, Ct));
    }

    [Fact]
    public async Task IdleAndAbsoluteTimeoutsEndASession()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var created = SessionOperations.Create(alice, "198.51.100.7", T0, Absolute);
        db.Sessions.Add(created.Row);
        await db.SaveChangesAsync(Ct);

        Assert.NotNull(await SessionOperations.FindAsync(db, created.Token, T0 + Idle - TimeSpan.FromSeconds(1), Idle, Ct));
        Assert.Null(await SessionOperations.FindAsync(db, created.Token, T0 + Idle, Idle, Ct));
        // A touch at seven hours keeps it alive past eight.
        Assert.Equal(1, await SessionOperations.TouchAsync(db, created.Row.Id, T0.AddHours(7), Ct));
        Assert.NotNull(await SessionOperations.FindAsync(db, created.Token, T0.AddHours(14), Idle, Ct));
        // Touches cannot extend past the absolute timeout.
        await SessionOperations.TouchAsync(db, created.Row.Id, T0.AddHours(23), Ct);
        Assert.NotNull(await SessionOperations.FindAsync(db, created.Token, T0 + Absolute - TimeSpan.FromSeconds(1), Idle, Ct));
        Assert.Null(await SessionOperations.FindAsync(db, created.Token, T0 + Absolute, Idle, Ct));
    }

    [Fact]
    public async Task DeleteAndSweep()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var bob = await TempDb.AddUserAsync(db, "bob");
        var a1 = SessionOperations.Create(alice, "198.51.100.7", T0, Absolute);
        var a2 = SessionOperations.Create(alice, "198.51.100.7", T0.AddHours(20), Absolute);
        var b1 = SessionOperations.Create(bob, "198.51.100.8", T0.AddHours(20), Absolute);
        db.Sessions.AddRange(a1.Row, a2.Row, b1.Row);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(1, await SessionOperations.DeleteAsync(db, b1.Row.Id, Ct));
        Assert.Equal(0, await SessionOperations.DeleteAsync(db, b1.Row.Id, Ct));
        Assert.Equal(1, await SessionOperations.SweepAsync(db, T0.AddHours(21), Idle, Ct));
        Assert.NotNull(await SessionOperations.FindAsync(db, a2.Token, T0.AddHours(21), Idle, Ct));
        Assert.Equal(1, await SessionOperations.DeleteForUserAsync(db, alice, Ct));
        Assert.Equal(0, await db.Sessions.CountAsync(Ct));
    }
}
