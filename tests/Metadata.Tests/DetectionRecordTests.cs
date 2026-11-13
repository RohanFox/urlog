using Urlog.Core.Audit;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Urlog.Metadata.Tests;

/// <summary>The detection columns on the intake record, and the migration that adds them to a store that already holds files.</summary>
public sealed class DetectionRecordTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDetectionRecordIsStoredWithTheFile()
    {
        await using var temp = await TempDb.CreateAsync();
        long id;
        var fileTime = new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc);
        await using (var db = temp.Open())
        {
            var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
            var file = new EvidenceFile
            {
                CaseId = @case.Id,
                Sha256 = new string('b', 64),
                Size = 10,
                OriginalName = "report.pdf",
                StoredPath = "report.pdf",
                Source = FileSource.Web,
                Uploader = "alice",
                ReceivedAt = DateTime.UtcNow,
                Encoding = "utf8",
                DetectedKind = FileKind.Artifact,
                Detection = "{\"detector_version\":1,\"kind\":\"artifact\",\"format\":\"pdf\"}",
                FileTime = fileTime,
                State = FileState.Artifact,
            };
            id = (await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, file, Ct)).File.Id;
        }

        await using var fresh = temp.Open();
        var stored = await fresh.Files.SingleAsync(f => f.Id == id, Ct);
        Assert.Equal(FileKind.Artifact, stored.DetectedKind);
        Assert.Equal("{\"detector_version\":1,\"kind\":\"artifact\",\"format\":\"pdf\"}", stored.Detection);
        Assert.Equal(fileTime, stored.FileTime);
        Assert.Equal(DateTimeKind.Utc, stored.FileTime!.Value.Kind);
        Assert.Equal("artifact", await ScalarAsync(fresh, $"SELECT detected_kind FROM files WHERE id = {id}"));
    }

    [Fact]
    public async Task TheKindColumnRefusesAValueOutsideTheList()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        var added = (await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(@case.Id, "x"), Ct)).File;

        Assert.Null(added.DetectedKind); // a row with no detection record is allowed
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlAsync($"UPDATE files SET detected_kind = 'nonsense' WHERE id = {added.Id}", Ct));
        Assert.Contains("ck_files_detected_kind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoreThatAlreadyHoldsFilesKeepsThemAcrossTheMigration()
    {
        await using var temp = TempDb.CreateUnmigrated();
        var sha = new string('c', 64);
        await using (var old = temp.Open())
        {
            await old.GetService<IMigrator>().MigrateAsync("20261111172457_Sessions", Ct);
            await old.Database.ExecuteSqlRawAsync("INSERT INTO users (name, role, password_hash, created_at, failed_logins) VALUES ('alice', 'analyst', 'x', '2026-11-01 00:00:00', 0)", Ct);
            await old.Database.ExecuteSqlRawAsync("INSERT INTO cases (name, state, owner_id, created_at) VALUES ('incident 1', 'active', 1, '2026-11-01 00:00:00')", Ct);
            await old.Database.ExecuteSqlAsync(
                $"INSERT INTO files (case_id, sha256, size, original_name, stored_path, source, uploader, received_at, encoding, detected_source, detected_confidence, parse_generation, state, row_count, reject_count) VALUES (1, {sha}, 42, 'access.log', 'access.log', 'cli', 'alice', '2026-11-01 00:00:00', 'utf8', 'nginx', 0.97, 3, 'parsed', 7, 1)", Ct);
        }

        await temp.Store.MigrateAsync(Ct);

        await using var db = temp.Open();
        var file = await db.Files.SingleAsync(Ct);
        Assert.Equal((sha, 42L, "access.log", "nginx", 3, FileState.Parsed, 7L, 1L), (file.Sha256, file.Size, file.OriginalName, file.DetectedSource, file.ParseGeneration, file.State, file.RowCount, file.RejectCount));
        Assert.Equal(0.97f, file.DetectedConfidence);
        Assert.Null(file.DetectedKind);
        Assert.Null(file.Detection);
        Assert.Null(file.FileTime);

        // The table was rebuilt for the new CHECK: its other constraints and its references must have come along.
        Assert.Null(await ScalarAsync(db, "SELECT count(*) FROM pragma_foreign_key_check HAVING count(*) > 0"));
        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO files (case_id, sha256, size, original_name, stored_path, source, uploader, received_at, encoding, parse_generation, state, row_count, reject_count, archive_depth, expanded_members, host_inferred, input_truncated, spans_dst, year_assumed, trailing_bytes, trailing_bytes_inexact, container_cut, year_given, hash_of_stored_bytes) VALUES (1, {sha}, 42, 'again.log', 'again.log', 'cli', 'alice', '2026-11-01 00:00:00', 'utf8', 1, 'received', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)", Ct));
        Assert.Contains("UNIQUE", duplicate.Message, StringComparison.Ordinal);
        var badState = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE files SET state = 'nonsense'", Ct));
        Assert.Contains("ck_files_state", badState.Message, StringComparison.Ordinal);
    }

    private static async Task<object?> ScalarAsync(MetadataDb db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull ? null : value;
    }
}
