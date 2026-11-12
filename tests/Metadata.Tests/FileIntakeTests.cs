using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>Every files insert needs a case in active; same bytes in one case are one row (evidence rule 3).</summary>
public sealed class FileIntakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<CaseState> NotActive => [CaseState.Closed, CaseState.Archived, CaseState.SoftDeleted, CaseState.Purging, CaseState.Purged];

    [Fact]
    public async Task AnActiveCaseAcceptsAFile()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));

        var result = await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(@case.Id, "line 1\n"), Ct);

        Assert.False(result.Duplicate);
        Assert.True(result.File.Id > 0);
        Assert.Equal(FileState.Received, result.File.State);
        Assert.Equal(1, result.File.ParseGeneration);
        Assert.Null(result.File.HostId);
    }

    [Theory]
    [MemberData(nameof(NotActive))]
    public async Task ACaseThatIsNotActiveRefusesAFile(CaseState state)
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        @case.State = state; // straight to the state: the lifecycle path is CaseLifecycleTests' business
        await db.SaveChangesAsync(Ct);

        var error = await Assert.ThrowsAsync<CaseNotActiveException>(() => FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(@case.Id, "x"), Ct));

        Assert.Equal(state, error.State);
        Assert.Contains(Names.Of(state), error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await db.Files.CountAsync(Ct));
    }

    [Fact]
    public async Task TheSameBytesInOneCaseAreOneRow()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));

        var first = await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(@case.Id, "same bytes", "a.log"), Ct);
        var second = await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(@case.Id, "same bytes", "b.log"), Ct);

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(first.File.Id, second.File.Id);
        Assert.Equal("a.log", second.File.OriginalName);
        Assert.Equal(1, await db.Files.CountAsync(Ct));
    }

    [Fact]
    public async Task TheSameBytesInTwoCasesAreTwoRows()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var owner = await TempDb.AddUserAsync(db);
        var one = await TempDb.AddCaseAsync(db, owner, "one");
        var two = await TempDb.AddCaseAsync(db, owner, "two");

        var a = await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(one.Id, "same bytes"), Ct);
        var b = await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(two.Id, "same bytes"), Ct);

        Assert.False(a.Duplicate);
        Assert.False(b.Duplicate);
        Assert.NotEqual(a.File.Id, b.File.Id);
        Assert.Equal(a.File.Sha256, b.File.Sha256);
    }

    [Fact]
    public async Task TheUniqueIndexHoldsWithoutTheDomainRule()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        db.Files.Add(TempDb.NewFile(@case.Id, "bytes"));
        await db.SaveChangesAsync(Ct);

        db.Files.Add(TempDb.NewFile(@case.Id, "bytes"));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.IsType<SqliteException>(error.InnerException);
    }

    [Fact]
    public async Task UnknownCaseAndMalformedHashAreRefused()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        await Assert.ThrowsAsync<CaseNotFoundException>(() => FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(4242, "x"), Ct));

        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        var file = TempDb.NewFile(@case.Id, "x");
        var upper = new EvidenceFile
        {
            CaseId = file.CaseId,
            Sha256 = file.Sha256.ToUpperInvariant(),
            Size = file.Size,
            OriginalName = file.OriginalName,
            StoredPath = file.StoredPath,
            Source = file.Source,
            Uploader = file.Uploader,
            ReceivedAt = file.ReceivedAt,
            Encoding = file.Encoding,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, upper, Ct));
        Assert.True(FileOperations.IsSha256(file.Sha256));
        Assert.False(FileOperations.IsSha256(file.Sha256[..63]));
        Assert.False(FileOperations.IsSha256(null));
    }

    [Fact]
    public async Task CheckConstraintsRefuseAValueOutsideTheEnum()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        var sha = new string('0', 64);

        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO files (case_id, sha256, size, original_name, stored_path, source, uploader, received_at, encoding, parse_generation, state, row_count, reject_count) VALUES ({@case.Id}, {sha}, 1, 'a', 'a', 'web', 'u', '2026-11-06 00:00:00', 'utf8', 1, 'bogus', 0, 0)", Ct));
        Assert.Contains("ck_files_state", error.Message, StringComparison.Ordinal);

        var sha63 = new string('0', 63);
        error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO files (case_id, sha256, size, original_name, stored_path, source, uploader, received_at, encoding, parse_generation, state, row_count, reject_count) VALUES ({@case.Id}, {sha63}, 1, 'a', 'a', 'web', 'u', '2026-11-06 00:00:00', 'utf8', 1, 'received', 0, 0)", Ct));
        Assert.Contains("ck_files_sha256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DateTimesComeBackAsUtc()
    {
        await using var temp = await TempDb.CreateAsync();
        var received = new DateTime(2026, 11, 6, 10, 30, 0, 123, DateTimeKind.Utc);
        long id;
        await using (var db = temp.Open())
        {
            var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
            var file = new EvidenceFile
            {
                CaseId = @case.Id,
                Sha256 = new string('a', 64),
                Size = 1,
                OriginalName = "a",
                StoredPath = "a",
                Source = FileSource.Sftp,
                Uploader = "svc",
                ReceivedAt = received.ToLocalTime(), // a Local value is converted on the way in
                Encoding = "utf8",
            };
            id = (await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, file, Ct)).File.Id;
        }

        await using var check = temp.Open();
        var stored = await check.Files.SingleAsync(f => f.Id == id, Ct);
        Assert.Equal(DateTimeKind.Utc, stored.ReceivedAt.Kind);
        Assert.Equal(received, stored.ReceivedAt);
        Assert.Equal(FileSource.Sftp, stored.Source);
    }
}
