using Urlog.Api.Search;
using Urlog.Query;

namespace Urlog.Api.Tests;

/// <summary>
/// How a page of a newest-first listing is read (one window when it holds the page, longer ones on a miss, the
/// keys alone after the last), and the slots that bound what the API runs on the event store at once.
/// </summary>
public sealed class SearchRunnerTests
{
    private static readonly DateTime First = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SearchPlan Plan(string spl = "") =>
        SearchPlan.Create(spl, new TranspileOptions(1, new DateTimeOffset(First), new DateTimeOffset(First.AddDays(100)), TimeZoneInfo.Utc, new DateTimeOffset(First.AddDays(100))));

    /// <summary>One event a second for the last <paramref name="recent"/> seconds of the range, and <paramref name="old"/> more on its first day.</summary>
    private static FakeSearchBackend Store(int recent, int old = 0)
    {
        var backend = new FakeSearchBackend();
        for (var i = 1; i <= recent; i++)
        {
            backend.Events.Add(new FakeEvent(First.AddDays(100).AddSeconds(-i), 1, (ulong)i, "recent"));
        }

        for (var i = 0; i < old; i++)
        {
            backend.Events.Add(new FakeEvent(First.AddSeconds(i), 2, (ulong)i, "old"));
        }

        return backend;
    }

    [Fact]
    public async Task ADenseCaseIsOneWindowStatement()
    {
        var backend = Store(recent: 5000);

        // A case as dense as its newest stretch: one event a second.
        var page = await new SearchRunner(backend, 1).PageAsync(Plan(), 200, null, new CaseDensity(8_640_000, TimeSpan.FromDays(100)), Ct);

        Assert.Equal(200, page.Rows.Count);
        Assert.Equal(1, page.Statements);
        Assert.Equal(TimeSpan.FromSeconds(ListingWindow.SeedPages * 200), page.Window);
        var call = Assert.Single(backend.Calls);
        Assert.Contains("ts >= {s_wfrom:DateTime64(3)}", call.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThinTailGrowsTheWindowByWhatItFound()
    {
        // The density says 6,400 s hold 32 pages; the tail holds an event every 100 s, so the first window finds 64.
        var backend = new FakeSearchBackend();
        for (var i = 1; i <= 3000; i++)
        {
            backend.Events.Add(new FakeEvent(First.AddDays(100).AddSeconds(-100 * i), 1, (ulong)i, "sparse"));
        }

        var page = await new SearchRunner(backend, 1).PageAsync(Plan(), 200, null, new CaseDensity(8_640_000, TimeSpan.FromDays(100)), Ct);

        Assert.Equal(200, page.Rows.Count);
        Assert.Equal(2, page.Statements);
        Assert.Equal(TimeSpan.FromSeconds(6400 * 16), page.Window);

        // The page is the newest 200 whatever the windows were.
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (ulong)i), page.Rows.Select(r => Convert.ToUInt64(r[Plan().FileOffsetColumn], System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task AfterThreeWindowsThePageIsFoundFromTheKeys()
    {
        // Nothing recent at all: 150 events on the first day of a hundred.
        var backend = Store(recent: 0, old: 150);

        var page = await new SearchRunner(backend, 1).PageAsync(Plan(), 200, null, new CaseDensity(864_000_000, TimeSpan.FromDays(100)), Ct);

        Assert.Equal(150, page.Rows.Count);
        Assert.Null(page.Window);
        Assert.Equal(["page", "page", "page", "boundary", "page"], backend.Calls.Select(c => c.Kind));
        Assert.Equal(5, page.Statements);

        // And with no event in the range at all, the keys say so and no rows are read.
        var empty = new FakeSearchBackend();
        var none = await new SearchRunner(empty, 1).PageAsync(Plan(), 200, null, new CaseDensity(864_000_000, TimeSpan.FromDays(100)), Ct);
        Assert.Empty(none.Rows);
        Assert.Equal("boundary", empty.Calls.Last().Kind);
    }

    [Fact]
    public async Task WithoutADensityOrWithASortThePageIsOneStatementOverTheRange()
    {
        var backend = Store(recent: 300);
        var plain = await new SearchRunner(backend, 1).PageAsync(Plan(), 200, null, null, Ct);
        Assert.Equal((200, 1), (plain.Rows.Count, plain.Statements));
        Assert.DoesNotContain("s_wfrom", backend.Calls.Single().Sql, StringComparison.Ordinal);

        var sorted = new FakeSearchBackend();
        await new SearchRunner(sorted, 1).PageAsync(Plan("| sort host"), 200, null, new CaseDensity(8_640_000, TimeSpan.FromDays(100)), Ct);
        Assert.DoesNotContain("s_wfrom", sorted.Calls.Single().Sql, StringComparison.Ordinal);

        Assert.Empty((await new SearchRunner(backend, 1).PageAsync(Plan(), 0, null, null, Ct)).Rows);
    }

    [Fact]
    public async Task TheNextPageStartsFromTheWindowThatFoundTheLast()
    {
        var backend = Store(recent: 5000);
        var plan = Plan();
        var runner = new SearchRunner(backend, 1);
        var density = new CaseDensity(8_640_000, TimeSpan.FromDays(100));
        var first = await runner.PageAsync(plan, 200, null, density, Ct);
        var cursor = SearchRunner.NextCursor(plan, first, "UTC", new DateTimeOffset(First.AddDays(100)), "b", 0);
        Assert.Equal((200, 6400L), (cursor.Sent, cursor.WindowSeconds));

        var second = await runner.PageAsync(plan, 200, cursor, density, Ct);
        Assert.Equal(TimeSpan.FromSeconds(6400), second.Window);
        Assert.Equal(Enumerable.Range(201, 200).Select(i => (ulong)i), second.Rows.Select(r => Convert.ToUInt64(r[plan.FileOffsetColumn], System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void TheLimitsAreASearchAndOneMoreForAUserAndThreeUsersInAll()
    {
        var defaults = new QueryOptions();
        defaults.Validate();

        // One search is three statements at once; a user has that and one more; all users have three users' worth;
        // and the event store's own limit stays above the total, as the backstop.
        Assert.Equal((4, 12, 16), (defaults.UserSlots, defaults.TotalSlots, QueryOptions.ServerBackstop));
        Assert.Equal((200, 10_000, 1_000_000L), (defaults.PageSize, defaults.MaxPagedRows, defaults.MaxExportRows));
        Assert.Throws<InvalidOperationException>(() => new QueryOptions { TotalSlots = 16 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new QueryOptions { UserSlots = 2 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new QueryOptions { UserSlots = 8, TotalSlots = 6 }.Validate());

        // What the deployed profile says is what the constant says.
        var profile = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "clickhouse", "users.d", "urlog.xml"));
        Assert.Contains($"<max_concurrent_queries_for_user>{QueryOptions.ServerBackstop}</max_concurrent_queries_for_user>", profile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneUserAtTheLimitDoesNotStopASecondUser()
    {
        var slots = new QuerySlots(perUser: 4, total: 12, wait: TimeSpan.FromMilliseconds(100));
        using var search = await slots.AcquireAsync(1, 3, cancellationToken: Ct);
        using var extra = await slots.AcquireAsync(1, 1, cancellationToken: Ct);
        Assert.NotNull(search);
        Assert.NotNull(extra);

        // User 1 is full: the next request waits and is refused. It is still waiting when user 2 asks.
        var refused = slots.AcquireAsync(1, 1, cancellationToken: Ct);
        using var other = await slots.AcquireAsync(2, 3, cancellationToken: Ct);
        Assert.NotNull(other);
        Assert.Null(await refused);
        Assert.Equal(7, slots.InUse);

        // A slot given back goes to the one who waits for it.
        var waiting = new QuerySlots(4, 12, TimeSpan.FromSeconds(10)).AcquireAsync(1, 4, cancellationToken: Ct);
        Assert.NotNull(await waiting);
        var long1 = new QuerySlots(4, 12, TimeSpan.FromSeconds(10));
        var held = await long1.AcquireAsync(1, 4, cancellationToken: Ct);
        var next = long1.AcquireAsync(1, 1, cancellationToken: Ct);
        Assert.False(next.IsCompleted);
        held!.Dispose();
        held.Dispose();
        using var granted = await next;
        Assert.NotNull(granted);
        Assert.Equal(1, long1.InUse);
    }

    [Fact]
    public async Task TheTotalBoundsAllUsersAndAnExportIsOneAtATimeForAUser()
    {
        var slots = new QuerySlots(perUser: 4, total: 6, wait: TimeSpan.FromMilliseconds(50));
        using var a = await slots.AcquireAsync(1, 3, cancellationToken: Ct);
        using var b = await slots.AcquireAsync(2, 3, cancellationToken: Ct);
        Assert.Null(await slots.AcquireAsync(3, 1, cancellationToken: Ct));
        Assert.Equal(6, slots.InUse);
        a!.Dispose();

        using var export = await slots.AcquireAsync(3, 1, export: true, Ct);
        Assert.NotNull(export);
        Assert.Null(await slots.AcquireAsync(3, 1, export: true, Ct));
        using var search = await slots.AcquireAsync(3, 1, cancellationToken: Ct);
        Assert.NotNull(search);
        export!.Dispose();
        using var again = await slots.AcquireAsync(3, 1, export: true, Ct);
        Assert.NotNull(again);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => slots.AcquireAsync(1, 5, cancellationToken: Ct));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("urlog.sln not found above the test binary");
    }
}
