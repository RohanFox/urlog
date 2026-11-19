using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Urlog.Core.Metadata;

namespace Urlog.Integration.Tests;

/// <summary>
/// A place where a command stops and waits to be killed, set up in the metadata file before the command starts.
/// A test that first watches for a state and then acts on it races the command: on a busy machine the command is
/// past the place by the time the test moves. Here the command stops itself: a trigger on one table holds the
/// write that matches a condition (the write of a step, the commit of a state) inside a statement that does not
/// end, with everything before that write done and the write itself not committed. The command cannot pass it,
/// however late the test looks. <see cref="HeldAsync"/> says when the command is there; the kill then lands on a
/// process that is waiting, as it would on one waiting for the write lock. <see cref="DisarmAsync"/> takes the
/// trigger out again before the next runner starts.
/// <para>
/// The command is known to be at the point by two things together: the write lock stays taken for a whole
/// second, and what the step before the held write leaves behind is there (every row in the table, the files
/// removed, the backup recorded). The lock alone does not say it. On a loaded machine another transaction of the
/// command, its start or a claim, can hold the lock for a second too, long before the point is reached.
/// </para>
/// </summary>
internal sealed class PausePoint
{
    private const string Rows = "test_pause_rows";
    private const string Trigger = "test_pause";

    private readonly string _connectionString;

    private PausePoint(string connectionString) => _connectionString = connectionString;

    /// <summary>
    /// Arms the point: the first <c>UPDATE</c> of <paramref name="table"/> for which <paramref name="when"/> is
    /// true (SQL over <c>OLD</c> and <c>NEW</c>) does not return.
    /// </summary>
    public static async Task<PausePoint> ArmAsync(string metadataPath, string table, string when, CancellationToken cancellationToken)
    {
        var point = new PausePoint(MetadataStore.ConnectionString(metadataPath));
        await using var connection = new SqliteConnection(point._connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // A thousand rows joined four times over is 10^12 steps: hours, where the kill comes within seconds.
        command.CommandText = $"""
            CREATE TABLE {Rows} (n INTEGER NOT NULL);
            INSERT INTO {Rows} (n) WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < 1000) SELECT x FROM c;
            CREATE TRIGGER {Trigger} BEFORE UPDATE ON "{table}" WHEN {when}
            BEGIN
                SELECT count(*) FROM {Rows} a, {Rows} b, {Rows} c, {Rows} d WHERE a.n + b.n + c.n + d.n < 0;
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return point;
    }

    /// <summary>
    /// Returns once the command sits in the held write: the write lock of the metadata file stays taken for a
    /// whole second and <paramref name="reached"/> says that what the step before the held write leaves is there.
    /// A lock held that long while the state is not there yet is another write of the command, and the wait goes
    /// on. <paramref name="reached"/> reads committed state only (the metadata file, the event store, the disk)
    /// and takes no write lock. Fails if the command ends instead.
    /// </summary>
    public async Task HeldAsync(Process process, string what, Func<Task<bool>> reached, CancellationToken cancellationToken, int seconds = 300)
    {
        ArgumentNullException.ThrowIfNull(reached);
        var deadline = Stopwatch.StartNew();
        await using var probe = new SqliteConnection(_connectionString);
        await probe.OpenAsync(cancellationToken);
        while (true)
        {
            Assert.False(process.HasExited, $"the command ended before it reached {what}");
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(seconds), $"timed out waiting for the command to reach {what}");
            await using var begin = probe.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.CommandTimeout = 1; // the provider retries a busy lock for this long, then gives up
            try
            {
                await begin.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 5)
            {
                // SQLITE_BUSY for a full second: a writer holds the lock. It is the held command when the state says so.
                if (await reached())
                {
                    return;
                }

                LongLocksBeforeThePoint++;
                await Task.Delay(10, cancellationToken);
                continue;
            }

            await using var rollback = probe.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync(cancellationToken);
            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>Times the lock was held for a second before the state of the point was there: another write of the command.</summary>
    public int LongLocksBeforeThePoint { get; private set; }

    /// <summary>Attempts of the last <see cref="DisarmAsync"/> that failed before one went through, with what each saw.</summary>
    public IReadOnlyList<string> DisarmRetries { get; private set; } = [];

    /// <summary>
    /// Removes the trigger and its rows. It runs straight after the kill, and the first open of a file that a
    /// killed process had open may fail for a moment: its lock and its handles go with the process, and the report
    /// that the process has ended can come before they are gone. A busy file, a locked one, an I/O error and a file
    /// that cannot be opened are tried again on a new connection for <paramref name="seconds"/>; after that the
    /// call fails with every error it saw.
    /// </summary>
    public async Task DisarmAsync(CancellationToken cancellationToken, int seconds = 10)
    {
        var clock = Stopwatch.StartNew();
        var seen = new List<string>();
        while (true)
        {
            try
            {
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP TRIGGER IF EXISTS {Trigger}; DROP TABLE IF EXISTS {Rows};";
                await command.ExecuteNonQueryAsync(cancellationToken);
                DisarmRetries = seen;
                // The output of a test that passes is not printed, so a run that is asked to keeps a line for every
                // point at which the lock was long before the state was there or the disarm had to be tried again.
                if ((seen.Count > 0 || LongLocksBeforeThePoint > 0) && Environment.GetEnvironmentVariable("URLOG_PAUSE_LOG") is { Length: > 0 } log)
                {
                    await File.AppendAllTextAsync(log, $"{DateTime.UtcNow:O} {LongLocksBeforeThePoint} long lock(s) before the point; disarm retried {seen.Count} time(s): {string.Join(" | ", seen)}{Environment.NewLine}", cancellationToken);
                }

                return;
            }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6 or 10 or 14)
            {
                seen.Add($"{clock.ElapsedMilliseconds} ms: SQLite error {e.SqliteErrorCode} (extended {e.SqliteExtendedErrorCode}): {e.Message}");
                // A connection that failed must not come back from the pool for the next attempt.
                SqliteConnection.ClearAllPools();
                if (clock.Elapsed >= TimeSpan.FromSeconds(seconds))
                {
                    throw new InvalidOperationException($"the pause point could not be removed in {seconds} s; {seen.Count} attempt(s) failed:{Environment.NewLine}{string.Join(Environment.NewLine, seen)}", e);
                }

                await Task.Delay(50, cancellationToken);
            }
        }
    }
}
