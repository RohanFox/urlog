using System.Security.Cryptography;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>Backup: VACUUM INTO produces a file whose verify-audit is OK and whose SHA-256 is the next audit row.</summary>
public sealed class BackupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BackupDir(TempDb temp) => Path.Combine(Path.GetDirectoryName(temp.Path)!, "backups");

    [Fact]
    public async Task TheBackupVerifiesAndItsHashIsTheNextAuditRow()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc));
        await using var temp = await TempDb.CreateAsync(clock);
        await AuditRows.AppendAsync(temp.Path, 50);

        var backup = await SqliteBackup.TakeAsync(temp.Store, BackupDir(temp), SqliteBackup.ReasonManual, "alice", cancellationToken: Ct);
        Assert.Equal(Path.Combine(BackupDir(temp), "metadata-20261106T030000Z.sqlite"), backup.Path);
        Assert.Equal(SqliteBackup.DefaultDirectory(temp.Path), BackupDir(temp));

        // The file's own hash, recomputed, is what the row and the sidecar say.
        var bytes = await File.ReadAllBytesAsync(backup.Path, Ct);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(sha256, backup.Sha256);
        Assert.Equal(bytes.LongLength, backup.Size);
        Assert.Equal(sha256 + "  metadata-20261106T030000Z.sqlite\n", await File.ReadAllTextAsync(backup.SidecarPath, Ct));

        // The backup's chain is whole and ends just before the row that records it.
        var inBackup = await AuditVerifier.VerifyAsync(backup.Path, Ct);
        Assert.True(inBackup.Ok, inBackup.Detail);
        Assert.Equal(51, inBackup.HeadSeq);

        await using var db = temp.Open();
        var row = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(Ct);
        Assert.Equal((52L, AuditActions.Backup, AuditTargets.Backup, "alice"), (row.Seq, row.Action, row.TargetType, row.Actor));
        Assert.Equal(inBackup.HeadHash, row.PrevHash);
        Assert.Equal(CanonicalJson.SerializeToString(new Dictionary<string, object?> { ["path"] = backup.Path, ["reason"] = "manual", ["sha256"] = sha256, ["size"] = backup.Size }), row.DetailsJson);
        Assert.Equal(backup.AuditRow.Hash, row.Hash);

        var live = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(live.Ok);
        Assert.Equal(52, live.HeadSeq);
    }

    [Fact]
    public async Task TheBackupCarriesTheTriggers()
    {
        await using var temp = await TempDb.CreateAsync();
        var backup = await SqliteBackup.TakeAsync(temp.Store, BackupDir(temp), SqliteBackup.ReasonNightly, AuditActions.SystemActor, cancellationToken: Ct);
        await using var connection = AuditVerifier.OpenReadOnly(backup.Path);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'audit'";
        Assert.Equal(3L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task ABackupIsNeverOverwritten()
    {
        var clock = new FakeClock(new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc));
        await using var temp = await TempDb.CreateAsync(clock);
        await SqliteBackup.TakeAsync(temp.Store, BackupDir(temp), SqliteBackup.ReasonManual, "alice", cancellationToken: Ct);
        var first = await File.ReadAllBytesAsync(Path.Combine(BackupDir(temp), "metadata-20261106T030000Z.sqlite"), Ct);
        await Assert.ThrowsAsync<IOException>(() => SqliteBackup.TakeAsync(temp.Store, BackupDir(temp), SqliteBackup.ReasonManual, "alice", cancellationToken: Ct));
        Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(BackupDir(temp), "metadata-20261106T030000Z.sqlite"), Ct));
        await using var db = temp.Open();
        Assert.Equal(1, await db.Audit.CountAsync(a => a.Action == AuditActions.Backup, Ct));

        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        var second = await SqliteBackup.TakeAsync(temp.Store, BackupDir(temp), SqliteBackup.ReasonPrePurge, "alice", cancellationToken: Ct);
        Assert.EndsWith("metadata-20261106T030001Z.sqlite", second.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABackupWhoseAuditRowFailsIsRemoved()
    {
        // No genesis row (EF migrations only): the append fails, and no unrecorded backup file is left behind.
        await using var temp = TempDb.CreateUnmigrated();
        await using (var db = temp.Open())
        {
            await db.Database.MigrateAsync(Ct);
        }

        await Assert.ThrowsAsync<AuditChainException>(() => SqliteBackup.TakeAsync(temp.Store, BackupDir(temp), SqliteBackup.ReasonManual, "alice", cancellationToken: Ct));
        Assert.Empty(Directory.GetFiles(BackupDir(temp)));
    }

    [Fact]
    public async Task TheLiveChainExtendsTheBackupUntilItIsTruncatedOrRewritten()
    {
        await using var temp = await TempDb.CreateAsync();
        await AuditRows.AppendAsync(temp.Path, 20);
        var backup = await SqliteBackup.TakeAsync(temp.Store, BackupDir(temp), SqliteBackup.ReasonManual, "alice", cancellationToken: Ct);
        await AuditRows.AppendAsync(temp.Path, 5);

        var extends = await AuditVerifier.VerifyExtendsAsync(temp.Path, backup.Path, Ct);
        Assert.True(extends.Ok, extends.Detail);
        Assert.Equal(21, extends.HeadSeq);

        // Truncated below the backup's head: the chain alone still verifies, the backup says otherwise.
        await AuditRows.TamperAsync(temp.Path, "DELETE FROM audit WHERE seq > 15");
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
        var truncated = await AuditVerifier.VerifyExtendsAsync(temp.Path, backup.Path, Ct);
        Assert.Equal((false, 21L, AuditVerifier.Gap), (truncated.Ok, truncated.FirstBrokenSeq, truncated.Reason));
        Assert.Contains("truncated", truncated.Detail, StringComparison.Ordinal);

        // Regrown with different rows: a consistent chain that is not the one the backup saw.
        await AuditRows.AppendAsync(temp.Path, 10, action: "case_reopen");
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
        var rewritten = await AuditVerifier.VerifyExtendsAsync(temp.Path, backup.Path, Ct);
        Assert.Equal((false, 21L, AuditVerifier.Hash), (rewritten.Ok, rewritten.FirstBrokenSeq, rewritten.Reason));
        Assert.Contains("rewritten", rewritten.Detail, StringComparison.Ordinal);
    }
}
