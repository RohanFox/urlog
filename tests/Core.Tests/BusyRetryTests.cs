using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Tests;

/// <summary>
/// The one policy for a write that waits for a locked metadata file: the write is started again from its
/// beginning while SQLite reports busy, up to a timeout, and nothing else is retried.
/// </summary>
public sealed class BusyRetryTests
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private static readonly BusyRetry Quick = new() { Pause = TimeSpan.FromMilliseconds(1) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheDefaultIsSixtySecondsAndTheRunnerCommitUsesIt()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), BusyRetry.DefaultTimeout);
        Assert.Equal(BusyRetry.DefaultTimeout, BusyRetry.Default.Timeout);
        Assert.Equal(BusyRetry.DefaultTimeout, new Urlog.Core.Jobs.JobRunnerOptions().CommitBusyTimeout);
    }

    [Fact]
    public async Task AWriteThatEndsBusyIsStartedAgainUntilItSucceeds()
    {
        var attempts = 0;
        var announced = 0;
        var result = await Quick.RunAsync(
            () =>
            {
                attempts++;
                return attempts switch
                {
                    1 => throw new SqliteException("database is locked", SqliteBusy),
                    // As EF reports it from SaveChanges, and the table-level form of the same condition.
                    2 => throw new DbUpdateException("save failed", new SqliteException("database table is locked", SqliteLocked)),
                    _ => Task.FromResult(attempts),
                };
            },
            () => announced++,
            Ct);

        Assert.Equal((3, 3, 1), (result, attempts, announced));
    }

    [Fact]
    public async Task PastTheTimeoutTheBusyFailureEndsTheWrite()
    {
        var attempts = 0;
        var announced = 0;
        var policy = Quick with { Timeout = TimeSpan.Zero };
        var busy = new SqliteException("database is locked", SqliteBusy);
        var failure = await Assert.ThrowsAsync<MetadataBusyException>(() => policy.RunAsync<int>(
            () =>
            {
                attempts++;
                throw busy;
            },
            () => announced++,
            Ct));

        Assert.Same(busy, failure.InnerException);
        Assert.Equal((1, 0), (attempts, announced));
        Assert.True(BusyRetry.IsBusy(failure));
    }

    [Fact]
    public async Task AnyOtherFailureIsNotRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Quick.RunAsync<int>(
            () =>
            {
                attempts++;
                throw new InvalidOperationException("not a lock");
            },
            cancellationToken: Ct));
        // A constraint failure is SQLite's answer, not a wait.
        await Assert.ThrowsAsync<SqliteException>(() => Quick.RunAsync<int>(
            () =>
            {
                attempts++;
                throw new SqliteException("UNIQUE constraint failed", 19);
            },
            cancellationToken: Ct));

        Assert.Equal(2, attempts);
    }
}
