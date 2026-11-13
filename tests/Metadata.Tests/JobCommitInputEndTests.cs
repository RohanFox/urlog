using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Metadata.Tests;

/// <summary>
/// The commit of a parse and the end of its input: no file is marked parsed without a statement of how its input
/// ended, and a truncated input is never committed without its flag and its audit row.
/// </summary>
public sealed class JobCommitInputEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime Now => DateTime.UtcNow;

    [Fact]
    public async Task ACommitWithoutAKnownEndIsRefusedAndChangesNothing()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var job = await ClaimedAsync(temp, db);
        var auditBefore = await db.Audit.CountAsync(Ct);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            JobOperations.CompleteAsync(db, temp.Store.Audit, job.Id, "w:1", new JobCounts(1, 1, 10, 0, 100), new JobOutcome(default), Now, Ct));

        Assert.Contains("unknown", error.Message, StringComparison.Ordinal);
        Assert.Equal(JobState.Parsing, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)).State);
        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == job.FileId, Ct);
        Assert.Equal(FileState.Received, file.State);
        Assert.Equal(auditBefore, await db.Audit.CountAsync(Ct));
    }

    [Fact]
    public async Task ATruncatedInputIsCommittedWithItsFlagBothOffsetsAndOneAuditRow()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var job = await ClaimedAsync(temp, db);
        var min = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

        await JobOperations.CompleteAsync(db, temp.Store.Audit, job.Id, "w:1", new JobCounts(2, 2, 40, 1, 5000),
            new JobOutcome(InputEnd.Truncated(5000, 777), min, min.AddHours(3), SpansDst: true), Now, Ct);

        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == job.FileId, Ct);
        Assert.Equal((FileState.Parsed, true, 5000L, 777L), (file.State, file.InputTruncated, file.TruncatedDecodedOffset, file.TruncatedStoredOffset));
        Assert.Equal((min, min.AddHours(3), true), (file.TsMin, file.TsMax, file.SpansDst));
        var row = await db.Audit.AsNoTracking().SingleAsync(a => a.Action == AuditActions.InputTruncated, Ct);
        Assert.Equal((AuditTargets.File, file.Id, AuditActions.SystemActor), (row.TargetType, row.TargetId, row.Actor));
        Assert.Contains("\"decoded_offset\":5000", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"stored_offset\":777", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"sha256\":\"{file.Sha256}\"", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"job_id\":{job.Id}", row.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedAuditWriteLeavesTheTruncatedFileUncommitted()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var job = await ClaimedAsync(temp, db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            JobOperations.CompleteAsync(db, new FailingAuditWriter(), job.Id, "w:1", new JobCounts(2, 2, 40, 1, 5000), new JobOutcome(InputEnd.Truncated(5000, 777)), Now, Ct));

        await using var fresh = temp.Open();
        var file = await fresh.Files.AsNoTracking().SingleAsync(f => f.Id == job.FileId, Ct);
        Assert.Equal((FileState.Received, false), (file.State, file.InputTruncated));
        Assert.Equal(JobState.Parsing, (await fresh.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)).State);
    }

    [Fact]
    public async Task ACompleteInputWritesNoTruncationRowAndKeepsItsTrailingBytes()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var job = await ClaimedAsync(temp, db);

        await JobOperations.CompleteAsync(db, temp.Store.Audit, job.Id, "w:1", new JobCounts(1, 1, 10, 0, 100),
            new JobOutcome(InputEnd.Complete(100, 60, trailingBytes: 512, trailingBytesExact: false)), Now, Ct);

        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == job.FileId, Ct);
        Assert.Equal((FileState.Parsed, false, 512L, true), (file.State, file.InputTruncated, file.TrailingBytes, file.TrailingBytesInexact));
        Assert.Null(file.TruncatedDecodedOffset);
        Assert.False(await db.Audit.AnyAsync(a => a.Action == AuditActions.InputTruncated, Ct));
    }

    [Fact]
    public void OnlyTheCommitMarksAFileParsed()
    {
        // The commit refuses an unknown end; that is worth something only while nothing else can set the state.
        var source = Path.Combine(RepositoryRoot(), "src");
        var setters = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadLines(path).Where(line => line.Contains("State = FileState.Parsed", StringComparison.Ordinal)).Select(_ => Path.GetFileName(path)))
            .ToList();

        Assert.Equal(["JobOperations.cs"], setters);
    }

    private static async Task<Job> ClaimedAsync(TempDb temp, MetadataDb db)
    {
        var @case = await TempDb.AddCaseAsync(db, await TempDb.AddUserAsync(db));
        var file = new EvidenceFile
        {
            CaseId = @case.Id,
            Sha256 = new string('a', 64),
            Size = 777,
            OriginalName = "a.log.gz",
            StoredPath = "a.log.gz",
            Source = FileSource.Cli,
            Uploader = "alice",
            ReceivedAt = Now,
            Encoding = "utf8",
            Compression = "gzip",
        };
        var added = (await FileOperations.AddAsync(db, temp.Store.Audit, new AuditActor("alice"), file, Ct)).File;
        await JobOperations.EnqueueAsync(db, added.Id, Now, Ct);
        return (await JobOperations.ClaimAsync(db, "w:1", Now, Ct))!.Job;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "urlog.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("urlog.sln was not found above the test binaries");
    }

    private sealed class FailingAuditWriter : IAuditWriter
    {
        public Task<Urlog.Core.Metadata.AuditRow> AppendAsync(MetadataDb db, AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the audit log refused the row");
    }
}
