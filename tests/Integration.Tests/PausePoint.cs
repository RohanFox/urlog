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
    /// whole second, which no write of the product does. Fails if the command ends instead.
    /// </summary>
    public async Task HeldAsync(Process process, string what, CancellationToken cancellationToken, int seconds = 300)
    {
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
                return; // SQLITE_BUSY for a full second: the writer is the held command
            }

            await using var rollback = probe.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync(cancellationToken);
            await Task.Delay(10, cancellationToken);
        }
    }

    /// <summary>Removes the trigger and its rows. The killed command's lock went with its process.</summary>
    public async Task DisarmAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP TRIGGER IF EXISTS {Trigger}; DROP TABLE IF EXISTS {Rows};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
