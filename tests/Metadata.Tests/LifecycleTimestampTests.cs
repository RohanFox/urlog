using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The times the lifecycle writes are taken after the operation holds the write lock, like the times of a job.
/// <c>deleted_at</c> matters most: the purge window is counted from it. Each case holds the lock from a connection
/// of its own, starts the operation behind it, moves the clock and lets go.
/// </summary>
public sealed class LifecycleTimestampTests
{
    private static readonly DateTime Called = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Released = Called.AddMinutes(10);
    private static readonly PurgeBackup Backup = new(new string('b', 64), "metadata-20261114T090000Z.sqlite", 4096);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("close")]
    [InlineData("soft-delete")]
    [InlineData("request-purge")]
    [InlineData("mark-purging")]
    [InlineData("set-step")]
    [InlineData("note")]
    public async Task EveryTimeTheLifecycleWritesIsTakenUnderTheWriteLock(string operation)
    {
        var clock = new FakeClock(Called.AddHours(-1));
        await using var temp = await TempDb.CreateAsync(clock);
        var audit = temp.Store.Audit;
        var week = TimeSpan.FromDays(7);
        long caseId, taskId = 0;
        await using (var db = temp.Open())
        {
            caseId = (await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db))).Id;
            if (operation is "request-purge" or "mark-purging" or "set-step" or "note")
            {
                await LifecycleOperations.SoftDeleteAsync(db, audit, TempDb.Alice, caseId, "incident 1", week, clock, Ct);
            }

            if (operation is "mark-purging" or "set-step" or "note")
            {
                taskId = (await LifecycleOperations.RequestPurgeAsync(db, audit, TempDb.Alice, caseId, twoPerson: false, clock, Ct)).Id;
            }
        }

        await using var holder = new SqliteConnection(MetadataStore.ConnectionString(temp.Path));
        await holder.OpenAsync(Ct);
        await ExecuteAsync(holder, "BEGIN IMMEDIATE");
        clock.UtcNow = Called;
        await using var writer = temp.Open();
        var write = Task.Run(
            async () =>
            {
                switch (operation)
                {
                    case "close":
                        await LifecycleOperations.CloseAsync(writer, audit, TempDb.Alice, caseId, clock, Ct);
                        break;
                    case "soft-delete":
                        await LifecycleOperations.SoftDeleteAsync(writer, audit, TempDb.Alice, caseId, "incident 1", week, clock, Ct);
                        break;
                    case "request-purge":
                        await LifecycleOperations.RequestPurgeAsync(writer, audit, TempDb.Alice, caseId, twoPerson: false, clock, Ct);
                        break;
                    case "mark-purging":
                        Assert.True(await LifecycleOperations.MarkPurgingAsync(writer, audit, taskId, Backup, clock, Ct));
                        break;
                    case "set-step":
                        await LifecycleOperations.SetStepAsync(writer, taskId, "backup", newAttempt: true, clock, Ct);
                        break;
                    default:
                        await LifecycleOperations.NoteAsync(writer, taskId, "stopped for the test", clock, Ct);
                        break;
                }
            },
            Ct);
        await Task.Delay(400, Ct);
        Assert.False(write.IsCompleted, "the write got past a held write lock");
        clock.UtcNow = Released;
        await ExecuteAsync(holder, "COMMIT");
        await write;

        await using var check = temp.Open();
        var @case = await check.Cases.AsNoTracking().SingleAsync(c => c.Id == caseId, Ct);
        var task = await check.CaseTasks.AsNoTracking().OrderByDescending(t => t.Id).FirstOrDefaultAsync(Ct);
        var row = await check.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        switch (operation)
        {
            case "close":
                Assert.Equal(Released, @case.ClosedAt);
                break;
            case "soft-delete":
                Assert.Equal(Released, @case.DeletedAt);
                // The window the row announces is counted from the same instant.
                Assert.Equal("2026-11-21T09:10:00Z", JsonDocument.Parse(row.DetailsJson).RootElement.GetProperty("purge_due").GetString());
                Assert.Equal([caseId], await LifecycleOperations.DueForPurgeAsync(check, week, Released.AddDays(7), Ct));
                Assert.Empty(await LifecycleOperations.DueForPurgeAsync(check, week, Called.AddDays(7), Ct));
                break;
            case "request-purge":
                Assert.Equal((Released, Released), (task!.CreatedAt, task.UpdatedAt));
                break;
            case "mark-purging":
                Assert.Equal((Released, Released), (@case.PurgingAt!.Value, task!.UpdatedAt));
                break;
            default:
                Assert.Equal(Released, task!.UpdatedAt);
                break;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
