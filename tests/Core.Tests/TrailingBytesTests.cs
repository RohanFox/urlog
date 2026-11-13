using System.IO.Compression;
using Urlog.Core.Pipeline;
using Urlog.Sources.Tests.Detection;

namespace Urlog.Core.Tests;

/// <summary>
/// Stored bytes behind the last member, frame or stream that start no further one: padding with NULs, or anything
/// else. The input is complete, the content is whole, and the end says how many such bytes there are. A file cut
/// anywhere inside them is complete too, with fewer of them.
/// </summary>
public sealed class TrailingBytesTests
{
    private static readonly byte[] Content = TestData.NumberedLines(6 << 10, seed: 41).Bytes;

    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var format in new[] { "gzip", "gzip-stored", "gzip-two-members", "zstd", "xz", "bzip2" })
            {
                foreach (var trailing in new[] { "nul-1", "nul-512", "nul-65536", "garbage-9", "garbage-300", "garbage-0x1f-first", "garbage-0x1f-twice", "garbage-0x1f-last" })
                {
                    data.Add(format, trailing);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TrailingBytesAreCountedAndTheInputIsComplete(string format, string trailing)
    {
        var (stored, kind) = Encode(format, Content);
        var extra = Trailing(trailing);
        byte[] file = [.. stored, .. extra];

        var (decoded, end) = Decode(file, kind);

        Assert.Equal(Content, decoded);
        Assert.Equal(Expected(format, stored.Length, extra), end);
    }

    /// <summary>
    /// Exact everywhere but one place: several gzip members followed by bytes that begin with 0x1F. The reader
    /// cannot tell that 0x1F from the last byte of the last member's length, so it reports one byte fewer and
    /// says the count is a lower bound.
    /// </summary>
    private static InputEnd Expected(string format, int storedLength, ReadOnlySpan<byte> trailing)
    {
        var provable = format != "gzip-two-members" || trailing.Length < 2 || trailing[0] != 0x1F;
        return provable
            ? InputEnd.Complete(Content.Length, storedLength, trailing.Length)
            : InputEnd.Complete(Content.Length, storedLength + 1, trailing.Length - 1, trailingBytesExact: false);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ACutInsideTheTrailingBytesLeavesFewerOfThem(string format, string trailing)
    {
        var (stored, kind) = Encode(format, Content);
        var extra = Trailing(trailing);
        byte[] file = [.. stored, .. extra];

        // Every cut for the short ones; the first and last 300 and every 997th in between for 64 KB.
        for (var kept = 0; kept < extra.Length; kept += extra.Length <= 1024 || kept < 300 || kept >= extra.Length - 300 ? 1 : 997)
        {
            if (LooksLikeAMemberStart(kind, extra.AsSpan(0, kept)))
            {
                continue; // covered by the facts below: a cut right behind a possible first byte of a member
            }

            var (decoded, end) = Decode(file.AsSpan(0, stored.Length + kept).ToArray(), kind);

            Assert.True(end == Expected(format, stored.Length, extra.AsSpan(0, kept)), $"{format} + {trailing}, {kept} of {extra.Length} kept: {end}");
            Assert.Equal(Content.Length, decoded.Length);
        }
    }

    [Theory]
    [InlineData("gzip", new byte[] { 0x1F })]
    [InlineData("zstd", new byte[] { 0x28 })]
    [InlineData("zstd", new byte[] { 0x28, 0xB5, 0x2F })]
    [InlineData("zstd", new byte[] { 0x53, 0x2A })]
    [InlineData("xz", new byte[] { 0xFD, 0x37, 0x7A })]
    [InlineData("xz", new byte[] { 0, 0, 0, 0, 0xFD })]
    [InlineData("bzip2", new byte[] { (byte)'B' })]
    [InlineData("bzip2", new byte[] { (byte)'B', (byte)'Z', (byte)'h' })]
    public void BytesThatCouldBeTheStartOfAnotherMemberAndThenStopAreATruncation(string format, byte[] extra)
    {
        var (stored, kind) = Encode(format, Content);

        var (decoded, end) = Decode([.. stored, .. extra], kind);

        Assert.Equal(InputEndKind.Truncated, end.Kind);
        Assert.Equal(Content, decoded);
    }

    [Theory]
    [InlineData("zstd", new byte[] { 0x29, 0xB5, 0x2F, 0xFD })]
    [InlineData("zstd", new byte[] { 0x28, 0xB5, 0x2F })]
    [InlineData("xz", new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x01 })]
    [InlineData("bzip2", new byte[] { (byte)'B', (byte)'Z', (byte)'h', (byte)'0' })]
    [InlineData("bzip2", new byte[] { (byte)'B', (byte)'Z', (byte)'x' })]
    public void AlmostAMagicWithMoreBehindItIsTrailing(string format, byte[] almost)
    {
        var (stored, kind) = Encode(format, Content);
        byte[] extra = [.. almost, 1, 2, 3, 4, 5];

        var (decoded, end) = Decode([.. stored, .. extra], kind);

        Assert.Equal(Content, decoded);
        Assert.Equal(InputEnd.Complete(Content.Length, stored.Length, extra.Length), end);
    }

    [Fact]
    public void ZeroPaddingBetweenXzStreamsIsNotTrailingAndPaddingAtTheEndIs()
    {
        var (first, second) = (Content[..3000], Content[3000..]);
        byte[] streams = [.. FixtureContainers.Xz(first), 0, 0, 0, 0, 0, 0, 0, 0, .. FixtureContainers.Xz(second)];

        Assert.Equal(InputEnd.Complete(Content.Length, streams.Length), Decode(streams, CompressionKind.Xz).End);
        Assert.Equal(InputEnd.Complete(Content.Length, streams.Length, 12), Decode([.. streams, .. new byte[12]], CompressionKind.Xz).End);
    }

    [Fact]
    public void ConcatenatedBzip2StreamsAreAllReadAndBytesBehindThemAreTrailing()
    {
        var (first, second, third) = (Content[..2000], Content[2000..4000], Content[4000..]);
        byte[] streams = [.. FixtureContainers.Bzip2(first), .. FixtureContainers.Bzip2(second), .. FixtureContainers.Bzip2(third)];

        var (decoded, end) = Decode([.. streams, .. new byte[700]], CompressionKind.Bzip2);

        Assert.Equal(Content, decoded);
        Assert.Equal(InputEnd.Complete(Content.Length, streams.Length, 700), end);
    }

    [Fact]
    public void ALargeGzipWithTrailingBytesInsideTheWindowIsCountedExactly()
    {
        var big = new byte[3 << 20];
        new Random(42).NextBytes(big);
        var stored = RealGzip(big);
        var extra = Trailing("garbage-300");

        using var stream = InputDecoding.OpenChecked(new MemoryStream([.. stored, .. extra]), CompressionKind.Gzip);
        var decoded = ReadAll(stream);

        Assert.True(decoded.AsSpan().SequenceEqual(big));
        Assert.Equal(InputEnd.Complete(big.Length, stored.Length, extra.Length), stream.End);
    }

    [Fact]
    public void MoreGzipTrailingBytesThanTheWindowGiveALowerBound()
    {
        var stored = RealGzip(Content);
        var extra = new byte[200 << 10];
        new Random(43).NextBytes(extra);
        extra[0] = 0x42;

        var (decoded, end) = Decode([.. stored, .. extra], CompressionKind.Gzip);

        Assert.Equal(Content, decoded);
        Assert.Equal(InputEndKind.Complete, end.Kind);
        Assert.False(end.TrailingBytesExact);
        Assert.InRange(end.TrailingBytes, extra.Length - (64 << 10), extra.Length);
        Assert.Equal(stored.Length + extra.Length, end.StoredOffset + end.TrailingBytes);
    }

    [Fact]
    public void ACountMarkedExactIsExactAndTheOneUnprovableCaseIsMarked()
    {
        // The same trailing bytes, beginning with 0x1F, behind one member and behind two.
        byte[] extra = [0x1F, 0x00, 0x05, 0x1F];
        var one = RealGzip(Content);
        byte[] two = [.. RealGzip(Content[..1000]), .. RealGzip(Content[1000..])];

        var single = Decode([.. one, .. extra], CompressionKind.Gzip).End;
        var several = Decode([.. two, .. extra], CompressionKind.Gzip).End;

        Assert.True(single.TrailingBytesExact);
        Assert.Equal(extra.Length, single.TrailingBytes);
        Assert.Equal(one.Length, single.StoredOffset);

        Assert.False(several.TrailingBytesExact);
        Assert.InRange(several.TrailingBytes, extra.Length - 1, extra.Length);
        Assert.Equal(two.Length + extra.Length, several.StoredOffset + several.TrailingBytes);

        // Bytes that do not begin with 0x1F are proved behind any number of members.
        var plain = Decode([.. two, 0x07, 0x1F, 0x00], CompressionKind.Gzip).End;
        Assert.Equal(InputEnd.Complete(Content.Length, two.Length, 3), plain);
    }

    [Fact]
    public void SeveralMembersWhoseLastLengthEndsIn0x1FAreMarkedInexactToo()
    {
        // The reverse of the case above: the 0x1F really is the trailer's last byte and one NUL follows. With a
        // member before it the reader cannot prove that either, and says so; the lower bound is the true count here.
        var last = ZerosGzip(0x1F000010);
        byte[] stored = [.. RealGzip(Content), .. last];
        Assert.Equal(0x1F, stored[^1]);

        using var stream = InputDecoding.OpenChecked(new MemoryStream([.. stored, 0]), CompressionKind.Gzip);
        var decoded = Drain(stream);

        Assert.Equal(Content.Length + 0x1F000010L, decoded);
        Assert.Equal(InputEnd.Complete(decoded, stored.Length, 1, trailingBytesExact: false), stream.End);
    }

    [Fact]
    public void A0x1FThatIsTheLastByteOfTheLengthFieldIsNotCountedAsTrailing()
    {
        // 0x1F000010 decoded bytes: the trailer's length field ends in 0x1F, and one NUL follows it. The reader
        // hands 0x1F and the NUL to the decoder together and has to tell which of the two the decoder stopped at.
        const int Length = 0x1F000010;
        var stored = ZerosGzip(Length);
        Assert.Equal(0x1F, stored[^1]);

        using var stream = InputDecoding.OpenChecked(new MemoryStream([.. stored, 0]), CompressionKind.Gzip);
        var decoded = Drain(stream);

        Assert.Equal(InputEnd.Complete(Length, stored.Length, 1), stream.End);
        Assert.Equal(Length, decoded);
    }

    /// <summary>One gzip member of <paramref name="length"/> zero bytes, written without holding them in memory.</summary>
    private static byte[] ZerosGzip(int length)
    {
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var zeros = new byte[1 << 20];
            for (var written = 0; written < length; written += zeros.Length)
            {
                gzip.Write(zeros, 0, Math.Min(zeros.Length, length - written));
            }
        }

        return compressed.ToArray();
    }

    private static long Drain(Stream stream)
    {
        var buffer = new byte[1 << 20];
        long decoded = 0;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            decoded += n;
        }

        return decoded;
    }

    private static bool LooksLikeAMemberStart(CompressionKind kind, ReadOnlySpan<byte> kept)
    {
        if (kept.IsEmpty)
        {
            return false;
        }

        return kind switch
        {
            CompressionKind.Gzip => kept[^1] == 0x1F && kept.Length == 1,
            _ => false,
        };
    }

    private static byte[] Trailing(string name)
    {
        switch (name)
        {
            case "nul-1":
                return new byte[1];
            case "nul-512":
                return new byte[512];
            case "nul-65536":
                return new byte[65536];
            case "garbage-9":
                return [1, 2, 3, 4, 5, 6, 7, 8, 9];
            case "garbage-300":
                var bytes = new byte[300];
                new Random(44).NextBytes(bytes);
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = bytes[i] is 0 or 0x1F or 0x28 or 0xFD or (byte)'B' ? (byte)0x77 : bytes[i];
                }

                return bytes;
            case "garbage-0x1f-first":
                return [0x1F, 0x00, 0x05, 0x1F];
            case "garbage-0x1f-last":
                return [0x07, 0x07, 0x1F];
            case "garbage-0x1f-twice":
                return [0x1F, 0x1F, 0x1F, 0x09, 0x1F, 0x08];
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "unknown trailing bytes");
        }
    }

    private static (byte[] Stored, CompressionKind Kind) Encode(string format, byte[] content)
    {
        switch (format)
        {
            case "gzip":
                return (RealGzip(content), CompressionKind.Gzip);
            case "gzip-stored":
                return (FixtureContainers.Gzip(content), CompressionKind.Gzip);
            case "gzip-two-members":
                return ([.. RealGzip(content[..1000]), .. FixtureContainers.Gzip(content[1000..])], CompressionKind.Gzip);
            case "zstd":
                var output = new MemoryStream();
                using (var z = new ZstdSharp.CompressionStream(output, level: 3, leaveOpen: true))
                {
                    z.Write(content);
                }

                return (output.ToArray(), CompressionKind.Zstd);
            case "xz":
                return (FixtureContainers.Xz(content), CompressionKind.Xz);
            case "bzip2":
                return (FixtureContainers.Bzip2(content), CompressionKind.Bzip2);
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "unknown format");
        }
    }

    private static byte[] RealGzip(byte[] content)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static (byte[] Decoded, InputEnd End) Decode(byte[] stored, CompressionKind kind)
    {
        using var stream = InputDecoding.OpenChecked(new MemoryStream(stored), kind);
        return (ReadAll(stream), stream.End);
    }

    private static byte[] ReadAll(Stream stream)
    {
        var output = new MemoryStream();
        var buffer = new byte[4099];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        return output.ToArray();
    }
}
