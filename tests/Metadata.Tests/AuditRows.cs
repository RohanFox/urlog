using System.Globalization;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Urlog.Metadata.Tests;

/// <summary>
/// Test helpers that go around EF: a fast generator of well-formed rows (one prepared INSERT in one transaction, hashes
/// from <see cref="AuditHash"/>) for the large-chain tests, and the tamper tool, which drops the triggers first because
/// that is the only way to change a row.
/// </summary>
internal static class AuditRows
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Appends <paramref name="count"/> rows after the current tail through raw SQL. The triggers stay on unless
    /// <paramref name="withTriggers"/> is false (the million-row fill: the per-row tail lookups of the insert trigger
    /// are the cost, not the verifier under test).
    /// </summary>
    public static async Task AppendAsync(string path, int count, string action = "case_close", bool withTriggers = true)
    {
        if (!withTriggers)
        {
            await WithoutTriggersAsync(path, c => FillAsync(c, count, action));
            return;
        }

        await using var connection = new SqliteConnection(MetadataStore.ConnectionString(path));
        await connection.OpenAsync(Ct);
        await FillAsync(connection, count, action);
    }

    /// <summary>Runs <paramref name="sql"/> with the three triggers dropped and re-created around it: the only way to alter a row.</summary>
    public static Task TamperAsync(string path, string sql) => WithoutTriggersAsync(path, async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    });

    private static async Task FillAsync(SqliteConnection connection, int count, string action)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(Ct);
        long seq;
        string prev;
        await using (var tail = connection.CreateCommand())
        {
            tail.CommandText = "SELECT seq, hash FROM audit ORDER BY seq DESC LIMIT 1";
            await using var reader = await tail.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct), "no genesis row");
            seq = reader.GetInt64(0);
            prev = reader.GetString(1);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO audit (seq, ts_utc, actor, actor_id, action, target_type, target_id, details_json, prev_hash, hash, schema_version) VALUES ($seq, $ts, $actor, NULL, $action, 'case', 1, $details, $prev, $hash, NULL)";
        var pSeq = insert.Parameters.Add("$seq", SqliteType.Integer);
        var pTs = insert.Parameters.Add("$ts", SqliteType.Text);
        var pActor = insert.Parameters.Add("$actor", SqliteType.Text);
        var pAction = insert.Parameters.Add("$action", SqliteType.Text);
        var pDetails = insert.Parameters.Add("$details", SqliteType.Text);
        var pPrev = insert.Parameters.Add("$prev", SqliteType.Text);
        var pHash = insert.Parameters.Add("$hash", SqliteType.Text);
        pAction.Value = action;
        pDetails.Value = """{"case_name":"incident 1"}""";
        // Row seq N is stamped 12:00:00 + (N - 1) µs, so a test can restore a row's timestamp from its seq.
        var baseTs = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            seq++;
            var ts = baseTs.AddTicks((seq - 1) * 10);
            var actor = "writer-" + (i % 4).ToString(CultureInfo.InvariantCulture);
            var hash = AuditHash.Compute(seq, ts, actor, null, action, "case", 1, (string)pDetails.Value, prev, null);
            pSeq.Value = seq;
            pTs.Value = ts.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            pActor.Value = actor;
            pPrev.Value = prev;
            pHash.Value = hash;
            insert.ExecuteNonQuery();
            prev = hash;
        }

        await transaction.CommitAsync(Ct);
    }

    private static async Task WithoutTriggersAsync(string path, Func<SqliteConnection, Task> work)
    {
        await using var connection = new SqliteConnection(MetadataStore.ConnectionString(path));
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'audit'";
        var triggers = new List<(string Name, string Sql)>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                triggers.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.Equal(3, triggers.Count);
        foreach (var (name, _) in triggers)
        {
            command.CommandText = "DROP TRIGGER " + name;
            await command.ExecuteNonQueryAsync(Ct);
        }

        await work(connection);
        foreach (var (_, create) in triggers)
        {
            command.CommandText = create;
            await command.ExecuteNonQueryAsync(Ct);
        }
    }
}
