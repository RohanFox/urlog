using System.Buffers;
using System.Text;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Generic;

namespace Urlog.Sources.Tests.Generic;

public sealed class GenericSourceTests
{
    /// <summary>The round-trip context of <c>fixtures/generic/expected.jsonl</c>: a +02:00 clock, the year 2026, a file last written on 2 March.</summary>
    private static readonly TimeSpan SampleOffset = TimeSpan.FromHours(2);
    private const int SampleYear = 2026;
    private static readonly DateTime SampleFileTime = new(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RegisteredAsTheStatefulFallbackThatNeverScores()
    {
        var registration = Assert.Single(LogSourceRegistry.Registrations, r => r.Id == "generic");
        Assert.Equal(["raw"], registration.Aliases);
        var source = registration.Factory();
        Assert.IsType<GenericSource>(source);
        Assert.Equal(Framing.Line, source.Framing);
        Assert.True(source.Info.Stateful, "a line without a timestamp takes the one before it, so chunks must arrive in order");
        var sample = Fixtures.Bytes("generic", "sample.log");
        Assert.Equal(0f, source.Sniff(sample, default, new SniffContext("sample.log", sample.Length)));
    }

    [Fact]
    public async Task SampleRoundTripsExpectedJsonl()
    {
        var expected = Fixtures.ReadExpected("generic");
        var actual = await Fixtures.ParseFileAsync(new GenericSource(), Fixtures.Path("generic", "sample.log"), timeZoneHint: SampleOffset, yearHint: SampleYear, fileTime: SampleFileTime);

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i] == actual[i], $"row {i} differs.\nexpected: {expected[i]}\nactual:   {actual[i]}");
        }

        var file = Fixtures.Bytes("generic", "sample.log");
        foreach (var row in actual)
        {
            Assert.Equal(file.AsSpan((int)row.FileOffset, row.RawBytes.Length).ToArray(), row.RawBytes.ToArray());
            var next = (int)row.FileOffset + row.RawBytes.Length;
            Assert.True(next == file.Length || file[next] == (byte)'\n' || file[next] == (byte)'\r', $"raw at {row.FileOffset} does not end at a line terminator");
        }

        Assert.DoesNotContain(actual, r => r.IsReject);
        Assert.Equal(28, actual.Count); // 29 lines, one of them blank
        Assert.Contains(actual, r => r.RawBytes.Length > 10_000);
        Assert.Equal(1, actual.Count(r => r.Extras.GetValueOrDefault("timestamp.source") == "file"));
        Assert.Equal(5, actual.Count(r => r.Extras.GetValueOrDefault("timestamp.source") == "previous"));
    }

    [Fact]
    public void TheRowsDoNotDependOnWhereTheChunksAreCut()
    {
        var sample = Fixtures.Bytes("generic", "sample.log").AsSpan(3).ToArray();
        var whole = Fixtures.ParseBytes(new GenericSource(), sample, timeZoneHint: SampleOffset, yearHint: SampleYear, fileTime: SampleFileTime);
        foreach (var maxChunk in new[] { 64, 200, 1500 })
        {
            var cut = Fixtures.ParseBytes(new GenericSource(), sample, maxChunk, timeZoneHint: SampleOffset, yearHint: SampleYear, fileTime: SampleFileTime);
            Assert.Equal(whole, cut);
        }
    }

    [Fact]
    public void WithoutAFileTimeTheLeadingLinesKeepTheDefaultAndSaySo()
    {
        var rows = Fixtures.ParseBytes(new GenericSource(), "banner\nstill nothing\n2026-03-01T00:00:00Z first\nafter\n"u8.ToArray());
        Assert.Equal(4, rows.Count);
        Assert.Equal(ParsedRow.Epoch, rows[0].Ts);
        Assert.Equal("none", rows[0].Extras["timestamp.source"]);
        Assert.Equal("none", rows[1].Extras["timestamp.source"]);
        Assert.Empty(rows[2].Extras);
        Assert.Equal("2026-03-01T00:00:00.000Z", rows[3].Ts);
        Assert.Equal("previous", rows[3].Extras["timestamp.source"]);
    }

    [Theory]
    [InlineData("truncated-last-line.log")]
    [InlineData("latin1.log")]
    [InlineData("mixed-syslog.log")]
    [InlineData("nul-run.log")]
    [InlineData("garbage.bin")]
    public async Task AdversarialInputBecomesRowsNeverRejectsOrExceptions(string name)
    {
        var path = Fixtures.Path("generic", "adversarial/" + name);
        var rows = await Fixtures.ParseFileAsync(new GenericSource(), path);
        var file = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(rows, r => r.IsReject);
        Assert.Equal(NonBlankLines(file), rows.Count);
        Assert.Equal(file.Length - file.AsSpan().Count((byte)'\n') - file.AsSpan().Count("\r\n"u8), rows.Sum(r => r.RawBytes.Length));
    }

    [Fact]
    public void ALineLongerThanOneMebibyteIsCutIntoPieces()
    {
        // "before", then 3 MiB with no newline, then "after": pieces of 64 KiB while more than 1 MiB remains, then the rest.
        var run = new byte[3 << 20];
        for (var i = 0; i < run.Length; i++)
        {
            run[i] = (byte)('a' + (i % 23));
        }

        byte[] data = [.. "2026-03-01T00:00:00Z before\n"u8, .. run, (byte)'\n', .. "2026-03-01T00:00:05Z after\n"u8];
        var rows = Fixtures.ParseBytes(new GenericSource(), data);

        Assert.Equal(1 + 32 + 1 + 1, rows.Count);
        Assert.Equal("2026-03-01T00:00:00Z before", rows[0].Raw);
        var position = (long)rows[0].RawBytes.Length + 1;
        for (var i = 1; i <= 33; i++)
        {
            Assert.Equal((ulong)position, rows[i].FileOffset);
            Assert.Equal(i <= 32 ? 64 << 10 : 1 << 20, rows[i].RawBytes.Length);
            Assert.True(run.AsSpan((int)(position - 28), rows[i].RawBytes.Length).SequenceEqual(rows[i].RawBytes.Span), $"piece {i} is not the bytes at its offset");
            Assert.Equal("previous", rows[i].Extras["timestamp.source"]);
            // Every piece says which line it is a piece of and where it stands in it.
            Assert.Equal(("28", $"{i}/33"), (rows[i].Extras["line.first_offset"], rows[i].Extras["line.part"]));
            position += rows[i].RawBytes.Length;
        }

        Assert.Equal("2026-03-01T00:00:05Z after", rows[^1].Raw);
        Assert.DoesNotContain(rows, r => r.IsReject);
        // The lines around it are whole lines and say nothing of the kind.
        Assert.DoesNotContain("line.part", rows[0].Extras.Keys);
        Assert.DoesNotContain("line.first_offset", rows[^1].Extras.Keys);
    }

    [Fact]
    public void ALineOfExactlyOneMebibyteIsOneEventAndOneByteMoreIsTwoMarkedPieces()
    {
        var whole = new byte[1 << 20];
        Array.Fill(whole, (byte)'x');
        "2026-03-01T00:00:01Z "u8.CopyTo(whole);
        var one = Fixtures.ParseBytes(new GenericSource(), [.. whole, (byte)'\n']);
        Assert.Single(one);
        Assert.Empty(one[0].Extras);

        // One byte more: 64 KiB, then the rest. Inside the second piece stands what looks like a time; it is the
        // middle of a line and not searched, so both pieces carry the time of the line's start.
        byte[] longer = [.. whole, (byte)'y'];
        "2031-01-01T00:00:00Z"u8.CopyTo(longer.AsSpan(70_000));
        var two = Fixtures.ParseBytes(new GenericSource(), [.. longer, (byte)'\n', .. "2026-03-01T00:00:09Z next\n"u8]);
        Assert.Equal(3, two.Count);
        Assert.Equal((64 << 10, (1 << 20) + 1 - (64 << 10)), (two[0].RawBytes.Length, two[1].RawBytes.Length));
        Assert.Equal(("0", "1/2"), (two[0].Extras["line.first_offset"], two[0].Extras["line.part"]));
        Assert.Equal(("0", "2/2"), (two[1].Extras["line.first_offset"], two[1].Extras["line.part"]));
        Assert.DoesNotContain("timestamp.source", two[0].Extras.Keys); // the first piece has the line's own time
        Assert.Equal("previous", two[1].Extras["timestamp.source"]);
        Assert.Equal(two[0].Ts, two[1].Ts);
        Assert.Empty(two[2].Extras);

        // The pieces in the order of their offsets are the line, byte for byte.
        byte[] joined = [.. two[0].RawBytes.Span, .. two[1].RawBytes.Span];
        Assert.True(joined.AsSpan().SequenceEqual(longer));
        Assert.Equal((ulong)(64 << 10), two[1].FileOffset);
    }

    [Fact]
    public void ALongLastLineWithoutALineBreakIsMarkedLikeAnyOther()
    {
        var line = new byte[(1 << 20) + (100 << 10)];
        Array.Fill(line, (byte)'z');
        byte[] data = [.. "2026-03-01T00:00:00Z first\n"u8, .. line];
        var rows = Fixtures.ParseBytes(new GenericSource(), data);
        Assert.Equal(1 + 3, rows.Count);
        Assert.Equal(["1/3", "2/3", "3/3"], rows.Skip(1).Select(r => r.Extras["line.part"]));
        Assert.All(rows.Skip(1), r => Assert.Equal("27", r.Extras["line.first_offset"]));
        Assert.Equal(line.Length, rows.Skip(1).Sum(r => r.RawBytes.Length));
    }

    [Fact]
    public async Task ALineTheReaderHadToCutAcrossChunksIsOneLineInItsMarks()
    {
        // The reader of this run cuts a record at 1 MiB. A line of 2.5 MiB reaches the source as three records in
        // three chunks; the total is not known while the first two are written.
        var line = new byte[(2 << 20) + (512 << 10)];
        for (var i = 0; i < line.Length; i++)
        {
            line[i] = (byte)('a' + (i % 19));
        }

        var path = Path.Combine(Path.GetTempPath(), $"urlog-long-line-{Guid.NewGuid():n}.log");
        try
        {
            await File.WriteAllBytesAsync(path, [.. "2026-03-01T00:00:00Z before\n"u8, .. line, (byte)'\n', .. "2026-03-01T00:00:05Z after\n"u8], TestContext.Current.CancellationToken);
            var rows = await Fixtures.ParseFileAsync(new GenericSource(), path);

            Assert.Equal("2026-03-01T00:00:00Z before", rows[0].Raw);
            Assert.Equal("2026-03-01T00:00:05Z after", rows[^1].Raw);
            var pieces = rows.Skip(1).SkipLast(1).ToList();
            Assert.Equal(["1", "2", "3/3"], pieces.Select(r => r.Extras["line.part"]));
            Assert.All(pieces, r => Assert.Equal("28", r.Extras["line.first_offset"]));
            Assert.Equal([28UL, 28UL + (1 << 20), 28UL + (2 << 20)], pieces.Select(r => r.FileOffset));
            byte[] joined = [.. pieces.SelectMany(r => r.RawBytes.ToArray())];
            Assert.True(joined.AsSpan().SequenceEqual(line));
            Assert.Empty(rows[^1].Extras);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParsingTheSampleAllocatesNothingPerRow()
    {
        byte[] sample = [.. Fixtures.Bytes("generic", "sample.log").AsSpan(3), (byte)'\n'];
        var data = new byte[sample.Length * 50];
        for (var i = 0; i < 50; i++)
        {
            sample.CopyTo(data, i * sample.Length);
        }

        var source = new GenericSource();
        var writer = new NullBatchWriter();
        var ctx = new ParseContext { CaseId = 1, HostId = 1, FileId = 1, DefaultHost = "web-01"u8.ToArray(), YearHint = SampleYear, TimeZoneHint = SampleOffset };
        ctx.Bind(writer);

        source.Parse(new ReadOnlySequence<byte>(data), ctx, writer); // warm-up: JIT, tiering, the regex runner
        source.Parse(new ReadOnlySequence<byte>(data), ctx, writer);
        var rowsBefore = writer.Rows;
        var allocated = AllocationProbe.Least(_ => source.Parse(new ReadOnlySequence<byte>(data), ctx, writer));

        Assert.Equal(28 * 50 * AllocationProbe.Passes, writer.Rows - rowsBefore);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData("2026-03-01 12:00:00 x", 0, "2026-03-01T12:00:00.000Z")]
    [InlineData("2026-03-01 12:00:00 x", 120, "2026-03-01T10:00:00.000Z")]
    [InlineData("2026-03-01T12:00:00Z x", 120, "2026-03-01T12:00:00.000Z")]
    [InlineData("2026-03-01T12:00:00+02 x", 0, "2026-03-01T10:00:00.000Z")]
    [InlineData("2026-03-01T12:00:00.1-0330 x", 0, "2026-03-01T15:30:00.100Z")]
    [InlineData("x=1 2024-02-29 23:59:59,999 leap day", 0, "2024-02-29T23:59:59.999Z")]
    [InlineData("[01/Mar/2026:12:00:00 -0500] x", 0, "2026-03-01T17:00:00.000Z")]
    [InlineData("Mar  1 12:00:00 x", -60, "2026-03-01T13:00:00.000Z")]
    [InlineData("1772366400 x", 120, "2026-03-01T12:00:00.000Z")]
    [InlineData("1772366400.5 x", 0, "2026-03-01T12:00:00.500Z")]
    [InlineData("time=1772366400999 x", 0, "2026-03-01T12:00:00.999Z")]
    [InlineData("\"ts\":\"1772366400\"", 0, "2026-03-01T12:00:00.000Z")]
    public void ATimestampIsFound(string line, int zoneMinutes, string expectedUtc)
    {
        Assert.True(GenericTimestamps.TryFind(Encoding.UTF8.GetBytes(line), zoneMinutes * 60_000L, new YearTracker(2026), out var ms));
        Assert.Equal(expectedUtc, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no digits here")]
    [InlineData("2026-02-30 12:00:00 no such day")]
    [InlineData("2023-02-29 12:00:00 not a leap year")]
    [InlineData("2026-03-01 24:00:00 no such hour")]
    [InlineData("12026-03-01 12:00:00 five digit year")]
    [InlineData("order 1772366400 shipped")]
    [InlineData("0000000001 ten digits before 2000")]
    [InlineData("9999999999 ten digits after 2100")]
    [InlineData("17723664001 eleven digits")]
    [InlineData("Marc  1 12:00:00 not a month")]
    [InlineData("32/Mar/2026:12:00:00 +0000 no such day")]
    [InlineData("12:00:00 a time alone")]
    [InlineData("2026-03-01 a date alone")]
    public void WhatIsNotATimestampIsNotFound(string line)
    {
        Assert.False(GenericTimestamps.TryFind(Encoding.UTF8.GetBytes(line), 0, new YearTracker(2026), out _));
    }

    [Fact]
    public void TheYearOfABsdTimestampFollowsTheFileAcrossNewYear()
    {
        var rows = Fixtures.ParseBytes(new GenericSource(), "[Dec 31 23:59:59] a\n[Jan  1 00:00:01] b\n[Dec 31 23:59:58] late\n"u8.ToArray(), yearHint: 2025);
        Assert.Equal("2025-12-31T23:59:59.000Z", rows[0].Ts);
        Assert.Equal("2026-01-01T00:00:01.000Z", rows[1].Ts);
        Assert.Equal("2025-12-31T23:59:58.000Z", rows[2].Ts);
    }

    private static int NonBlankLines(byte[] file)
    {
        var count = 0;
        foreach (var record in new RecordEnumerator(file, Framing.Line, 0))
        {
            if (!record.Bytes.IsEmpty)
            {
                count++;
            }
        }

        return count;
    }
}
