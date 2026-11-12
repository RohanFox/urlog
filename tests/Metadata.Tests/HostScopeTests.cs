using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>The same host name in two cases is two rows; in one case, one row (also under concurrent creates).</summary>
public sealed class HostScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameNameInTwoCasesIsTwoRows()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var one = await TempDb.AddCaseAsync(db, owner, "one");
        var two = await TempDb.AddCaseAsync(db, owner, "two");

        var a = await HostOperations.GetOrCreateAsync(db, one.Id, "web-01", DateTime.UtcNow, Ct);
        var b = await HostOperations.GetOrCreateAsync(db, two.Id, "web-01", DateTime.UtcNow, Ct);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, await db.Hosts.CountAsync(h => h.Name == "web-01", Ct));
    }

    [Fact]
    public async Task SameNameInOneCaseIsOneRowWhateverTheSpelling()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var @case = await TempDb.AddCaseAsync(db, owner);

        var a = await HostOperations.GetOrCreateAsync(db, @case.Id, "web-01", DateTime.UtcNow, Ct);
        var b = await HostOperations.GetOrCreateAsync(db, @case.Id, "WEB-01", DateTime.UtcNow, Ct);
        var c = await HostOperations.GetOrCreateAsync(db, @case.Id, "  Web-01 ", DateTime.UtcNow, Ct);

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Id, c.Id);
        Assert.Equal("web-01", a.Name);
        Assert.Equal(1, await db.Hosts.CountAsync(Ct));
        Assert.Equal("UTC", a.Timezone);
        Assert.True(a.TimezoneAssumed);
    }

    [Fact]
    public async Task ConcurrentCreatesOfOneNameYieldOneRow()
    {
        await using var temp = await TempDb.CreateAsync();
        long caseId;
        await using (var db = temp.Open())
        {
            caseId = (await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db))).Id;
        }

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = temp.Open();
            return (await HostOperations.GetOrCreateAsync(db, caseId, "db-01", DateTime.UtcNow, Ct)).Id;
        }, Ct)));

        Assert.Single(ids.Distinct());
        await using var check = temp.Open();
        Assert.Equal(1, await check.Hosts.CountAsync(Ct));
    }

    [Fact]
    public async Task EmptyNameAndUnknownCaseAreRefused()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        await Assert.ThrowsAsync<ArgumentException>(() => HostOperations.GetOrCreateAsync(db, 1, "   ", DateTime.UtcNow, Ct));
        await Assert.ThrowsAsync<DbUpdateException>(() => HostOperations.GetOrCreateAsync(db, 777, "web-01", DateTime.UtcNow, Ct));
    }
}
