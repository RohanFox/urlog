using System.Diagnostics;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The store waits five seconds for a lock. A test that stages a locked file passes a shorter wait to the
/// constructor; no setting does.
/// </summary>
public sealed class BusyTimeoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheDefaultIsFiveSeconds()
    {
        var path = Path.Combine(Path.GetTempPath(), "urlog-busy-timeout-default.db");
        Assert.Equal(5, MetadataStore.BusyTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(5), new MetadataStore(path).BusyTimeout);
        Assert.Equal(5, new SqliteConnectionStringBuilder(MetadataStore.ConnectionString(path)).DefaultTimeout);
        var options = new MetadataStore(path).Options;
        using var db = new MetadataDb(options);
        Assert.Equal(5, new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DefaultTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LessThanOneSecondIsRefused(int seconds)
    {
        // The provider reads zero as "wait without limit".
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetadataStore(Path.Combine(Path.GetTempPath(), "urlog-busy-timeout-refused.db"), busyTimeoutSeconds: seconds));
    }

    [Fact]
    public async Task AShorterWaitSetInCodeEndsALockedWriteSooner()
    {
        await using var temp = await TempDb.CreateAsync();
        var quick = new MetadataStore(temp.Path, busyTimeoutSeconds: 1);
        Assert.Equal(TimeSpan.FromSeconds(1), quick.BusyTimeout);

        await using var blocker = new SqliteConnection($"Data Source={temp.Path}");
        await blocker.OpenAsync(Ct);
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync(Ct);
        }

        var waited = Stopwatch.StartNew();
        await using var db = quick.CreateDbContext();
        db.Users.Add(new User { Name = "locked-out", PasswordHash = "x", CreatedAt = DateTime.UtcNow });
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
        waited.Stop();
        Assert.True(BusyRetry.IsBusy(failure), failure.ToString());
        Assert.InRange(waited.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(3));
    }
}
