using System.IO.Compression;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Sources.Tests.Detection;

namespace Urlog.Core.Tests;

/// <summary>
/// What a job writes about the end of its input and about its times: a compressed file cut short is parsed as far
/// as it reads and committed with the flag, both offsets and an audit row; trailing bytes are a note on a whole
/// file; the least and greatest event time go on the record; one zone offset over a change of that zone is flagged.
/// </summary>
public sealed class JobRunnerInputEndTests
{
    private static readonly string Berlin = OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("gzip")]
    [InlineData("zstd")]
    [InlineData("xz")]
    [InlineData("bzip2")]
    public async Task AFileCutAtHalfIsParsedUpToTheCutAndCommittedWithTheFlag(string compression)
    {
        // Large enough for several bzip2 blocks: a cut bzip2 file ends at its last whole block.
        var (content, lines) = TestData.NumberedLines(3 << 20, seed: 51);
        var whole = Compress(compression, content);
        var cut = whole[..(whole.Length / 2)];
        var prefix = Decode(cut, compression);
        Assert.True(content.AsSpan().StartsWith(prefix.Bytes));
        Assert.Equal(InputEndKind.Truncated, prefix.End.Kind);

        await using var h = await RunnerHarness.CreateAsync();
        var a = await h.AddCaseAsync("cut");
        var b = await h.AddCaseAsync("whole");
        var c = await h.AddCaseAsync("the same bytes, never compressed");
        var (cutFile, cutJob, _) = await h.AddFileAsync(a.Id, cut, "a.log." + compression, compression: compression);
        var (wholeFile, wholeJob, _) = await h.AddFileAsync(b.Id, whole, "a.log." + compression, compression: compression);
        var (_, plainJob, _) = await h.AddFileAsync(c.Id, prefix.Bytes, "a.log");

        await h.Runner().RunAsync(Ct);

        var done = await h.JobAsync(cutJob.Id);
        var expected = await h.JobAsync(plainJob.Id);
        Assert.True(done.State == JobState.Done, $"{compression}: {done.State}, {done.Message}");
        Assert.Equal(expected.Rows + expected.Rejects, done.Rows + done.Rejects);
        Assert.InRange(done.Rows, lines / 4, lines - 1);
        Assert.Equal(done.Rows, h.Events.Visible((ulong)cutFile.Id));

        var record = await h.FileAsync(cutFile.Id);
        Assert.Equal(FileState.Parsed, record.State);
        Assert.True(record.InputTruncated);
        Assert.Equal(prefix.Bytes.Length, record.TruncatedDecodedOffset);
        Assert.Equal(cut.Length, record.TruncatedStoredOffset);

        var audit = await h.AuditAsync();
        var row = Assert.Single(audit, r => r.Action == AuditActions.InputTruncated);
        Assert.Equal(cutFile.Id, row.TargetId);
        Assert.Contains($"\"decoded_offset\":{prefix.Bytes.Length}", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"stored_offset\":{cut.Length}", row.DetailsJson, StringComparison.Ordinal);
        Assert.Contains($"\"compression\":\"{compression}\"", row.DetailsJson, StringComparison.Ordinal);

        // The same file uncut: every row, no flag, no audit row of its own.
        var wholeDone = await h.JobAsync(wholeJob.Id);
        var wholeRecord = await h.FileAsync(wholeFile.Id);
        Assert.Equal((JobState.Done, (long)lines), (wholeDone.State, wholeDone.Rows));
        Assert.False(wholeRecord.InputTruncated);
        Assert.Null(wholeRecord.TruncatedDecodedOffset);
        Assert.Null(wholeRecord.TruncatedStoredOffset);
        Assert.Equal(0, wholeRecord.TrailingBytes);
    }

    [Fact]
    public async Task TrailingBytesAreANoteOnAWholeFile()
    {
        var (content, lines) = TestData.NumberedLines(20_000, seed: 52);
        byte[] stored = [.. Compress("gzip", content), .. new byte[512]];
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (file, job, _) = await h.AddFileAsync(@case.Id, stored, "a.log.gz", compression: "gzip");

        await h.Runner().RunAsync(Ct);

        var record = await h.FileAsync(file.Id);
        Assert.Equal((JobState.Done, (long)lines), ((await h.JobAsync(job.Id)).State, record.RowCount));
        Assert.False(record.InputTruncated);
        Assert.Equal(512, record.TrailingBytes);
        Assert.False(record.TrailingBytesInexact);
        Assert.DoesNotContain(await h.AuditAsync(), r => r.Action == AuditActions.InputTruncated);
    }

    [Fact]
    public async Task AnUncompressedFileIsCompleteAndCarriesItsTimeRange()
    {
        // The numbered-line source reads the leading number as the event time in milliseconds.
        var content = "1700000300000 c\n1700000100000 a\nBAD no number\n1700000200000 b\n"u8.ToArray();
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (file, job, _) = await h.AddFileAsync(@case.Id, content, "a.log");

        await h.Runner().RunAsync(Ct);

        var record = await h.FileAsync(file.Id);
        Assert.Equal(JobState.Done, (await h.JobAsync(job.Id)).State);
        Assert.False(record.InputTruncated);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000100000).UtcDateTime, record.TsMin);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000300000).UtcDateTime, record.TsMax);
        Assert.False(record.SpansDst);
    }

    [Fact]
    public async Task AFileWithoutAnyDatedRowHasNoTimeRange()
    {
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var (file, _, _) = await h.AddFileAsync(@case.Id, "BAD one\nBAD two\n"u8.ToArray(), "a.log");

        await h.Runner().RunAsync(Ct);

        var record = await h.FileAsync(file.Id);
        Assert.Equal((FileState.Parsed, 2L), (record.State, record.RejectCount));
        Assert.Null(record.TsMin);
        Assert.Null(record.TsMax);
    }

    [Fact]
    public async Task OneZoneOffsetOverAChangeOfTheZoneIsFlaggedOnlyForASourceThatReadsNaiveTimes()
    {
        // Europe/Berlin left summer time on 2026-10-25 at 01:00 UTC. Events a day before and a day after.
        var before = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var after = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var across = System.Text.Encoding.ASCII.GetBytes($"{before} a\n{after} b\n");
        var oneSide = System.Text.Encoding.ASCII.GetBytes($"{after} a\n{after + 1000} b\n");
        var fileTime = new DateTime(2026, 10, 26, 13, 0, 0, DateTimeKind.Utc);

        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var naiveAcross = await h.AddFileAsync(@case.Id, across, "across.log", NaiveLineSource.Id, timezone: Berlin, fileTime: fileTime);
        var naiveOneSide = await h.AddFileAsync(@case.Id, oneSide, "one-side.log", NaiveLineSource.Id, timezone: Berlin, fileTime: fileTime);
        var zonedAcross = await h.AddFileAsync(@case.Id, [.. across, (byte)'\n'], "zoned.log", timezone: Berlin, fileTime: fileTime);
        var utcAcross = await h.AddFileAsync(@case.Id, [.. across, (byte)'\n', (byte)'\n'], "utc.log", NaiveLineSource.Id, fileTime: fileTime);

        await h.Runner().RunAsync(Ct);

        Assert.True((await h.FileAsync(naiveAcross.File.Id)).SpansDst);
        Assert.False((await h.FileAsync(naiveOneSide.File.Id)).SpansDst);
        Assert.False((await h.FileAsync(zonedAcross.File.Id)).SpansDst); // its source never reads the zone hint
        Assert.False((await h.FileAsync(utcAcross.File.Id)).SpansDst);   // UTC has no change to span
    }

    [Fact]
    public async Task TheZoneOffsetIsTheOneAtTheFilesTimeNotAtTheTimeOfTheParse()
    {
        TimeSpan? summer = null;
        TimeSpan? winter = null;
        await using var h = await RunnerHarness.CreateAsync();
        var @case = await h.AddCaseAsync("one");
        var july = await h.AddFileAsync(@case.Id, "1 a\n"u8.ToArray(), "july.log", HookedSource.Id, timezone: Berlin, fileTime: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var january = await h.AddFileAsync(@case.Id, "2 a\n"u8.ToArray(), "january.log", HookedSource.Id, timezone: Berlin, fileTime: new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        h.BeforeChunk = ctx =>
        {
            if (ctx.FileId == (ulong)july.File.Id)
            {
                summer = ctx.TimeZoneHint;
            }
            else if (ctx.FileId == (ulong)january.File.Id)
            {
                winter = ctx.TimeZoneHint;
            }
        };

        await h.Runner().RunAsync(Ct);

        Assert.Equal(TimeSpan.FromHours(2), summer);
        Assert.Equal(TimeSpan.FromHours(1), winter);
    }

    private static byte[] Compress(string compression, byte[] content)
    {
        var output = new MemoryStream();
        switch (compression)
        {
            case "gzip":
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    gzip.Write(content);
                }

                return output.ToArray();
            case "zstd":
                using (var zstd = new ZstdSharp.CompressionStream(output, level: 3, leaveOpen: true))
                {
                    zstd.Write(content);
                }

                return output.ToArray();
            case "xz":
                return FixtureContainers.Xz(content);
            case "bzip2":
                return FixtureContainers.Bzip2(content);
            default:
                throw new ArgumentOutOfRangeException(nameof(compression), compression, "unknown compression");
        }
    }

    private static (byte[] Bytes, InputEnd End) Decode(byte[] stored, string compression)
    {
        using var stream = InputDecoding.OpenChecked(new MemoryStream(stored), Names.Parse<CompressionKind>(compression));
        var output = new MemoryStream();
        stream.CopyTo(output);
        return (output.ToArray(), stream.End);
    }
}
