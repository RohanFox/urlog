using System.IO.Compression;
using Urlog.Core.Pipeline;
using Urlog.Sources.Tests.Detection;

namespace Urlog.Core.Tests;

/// <summary>
/// A compressed input that stops early reads as far as it goes and says so; a whole one says it is whole; a
/// damaged one is still an error. Each format is cut at every byte, because the decoders behave differently in
/// the header, in the data and in the end structure.
/// </summary>
public sealed class CheckedDecodeStreamTests
{
    private static readonly byte[] Content = TestData.NumberedLines(6 << 10, seed: 16).Bytes;

    public static TheoryData<string> Formats => ["gzip", "gzip-stored", "zstd", "zstd-checksum", "xz", "bzip2"];

    [Theory]
    [MemberData(nameof(Formats))]
    public void AWholeFileIsComplete(string format)
    {
        var (stored, kind) = Encode(format, Content);

        var (decoded, end) = Decode(stored, kind);

        Assert.Equal(Content, decoded);
        Assert.Equal(InputEnd.Complete(Content.Length, stored.Length), end);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void AFileCutAtAnyByteIsTruncatedAndReadsAsAPrefix(string format)
    {
        var (stored, kind) = Encode(format, Content);

        for (var cut = 1; cut < stored.Length; cut++)
        {
            var (decoded, end) = Decode(stored.AsSpan(0, cut).ToArray(), kind);

            Assert.True(end.Kind == InputEndKind.Truncated, $"{format} cut at {cut} of {stored.Length}: {end}");
            Assert.Equal(cut, end.StoredOffset);
            Assert.Equal(decoded.Length, end.DecodedOffset);
            Assert.True(Content.AsSpan().StartsWith(decoded), $"{format} cut at {cut}: the decoded bytes are not a prefix of the content");
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void ACutInTheSecondHalfKeepsMostOfTheContent(string format)
    {
        var big = TestData.NumberedLines(3 << 20, seed: 17).Bytes;
        var (stored, kind) = Encode(format, big);

        var (decoded, end) = Decode(stored.AsSpan(0, stored.Length / 2).ToArray(), kind);

        Assert.Equal(InputEndKind.Truncated, end.Kind);
        Assert.True(big.AsSpan().StartsWith(decoded));
        Assert.InRange(decoded.Length, big.Length / 4, big.Length - 1);
    }

    [Fact]
    public void GzipMembersAreReadOneAfterAnotherAndACutBetweenTwoIsAWholeShorterFile()
    {
        var (first, second, third) = (Content[..2000], Content[2000..2000], Content[2000..]);
        byte[][] members = [RealGzip(first), FixtureContainers.Gzip(second), RealGzip(third), FixtureContainers.Gzip(first)];
        var stored = members.SelectMany(m => m).ToArray();
        byte[] whole = [.. first, .. second, .. third, .. first];
        var boundaries = new HashSet<int>();
        var at = 0;
        foreach (var member in members)
        {
            boundaries.Add(at += member.Length);
        }

        var (decoded, end) = Decode(stored, CompressionKind.Gzip);
        Assert.Equal(whole, decoded);
        Assert.Equal(InputEnd.Complete(whole.Length, stored.Length), end);

        for (var cut = 1; cut < stored.Length; cut++)
        {
            (decoded, end) = Decode(stored.AsSpan(0, cut).ToArray(), CompressionKind.Gzip);

            var expected = boundaries.Contains(cut) ? InputEndKind.Complete : InputEndKind.Truncated;
            Assert.True(end.Kind == expected, $"cut at {cut} of {stored.Length}: {end}, expected {expected}");
            Assert.True(whole.AsSpan().StartsWith(decoded));
        }
    }

    [Theory]
    [InlineData("sample.log.gz", CompressionKind.Gzip)]
    [InlineData("sample.log.xz", CompressionKind.Xz)]
    [InlineData("sample.log.bz2", CompressionKind.Bzip2)]
    public void TheCommittedCompressedSamplesCutAtAnyByteReadAsAPrefix(string name, CompressionKind kind)
    {
        // Written by the real tools, so real LZMA2 and Huffman blocks, which the in-process encoders do not produce.
        var directory = Path.Combine(FindRepositoryRoot(), "fixtures", "core");
        var whole = File.ReadAllBytes(Path.Combine(directory, "sample.log"));
        var stored = File.ReadAllBytes(Path.Combine(directory, name));
        Assert.Equal(InputEnd.Complete(whole.Length, stored.Length), Decode(stored, kind).End);

        for (var cut = 1; cut < stored.Length; cut++)
        {
            var (decoded, end) = Decode(stored.AsSpan(0, cut).ToArray(), kind);

            Assert.True(end.Kind == InputEndKind.Truncated, $"{name} cut at {cut} of {stored.Length}: {end}");
            Assert.True(whole.AsSpan().StartsWith(decoded), $"{name} cut at {cut}: the decoded bytes are not a prefix of the content");
        }
    }

    [Fact]
    public void AGzipHeaderWithEveryOptionalFieldIsSkipped()
    {
        var plain = RealGzip(Content);
        byte[] header = [0x1F, 0x8B, 8, 0x1E, 0, 0, 0, 0, 0, 3, 3, 0, 9, 9, 9, .. "name.log\0"u8, .. "a comment\0"u8];
        var headerCrc = FixtureContainers.Crc32(header);
        byte[] stored = [.. header, (byte)headerCrc, (byte)(headerCrc >> 8), .. plain.AsSpan(10)];

        var (decoded, end) = Decode(stored, CompressionKind.Gzip);

        Assert.Equal(Content, decoded);
        Assert.Equal(InputEndKind.Complete, end.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(4096)]
    public void BytesAfterTheLastGzipMemberAreIgnored(int padding)
    {
        byte[] stored = [.. RealGzip(Content), .. new byte[padding]];

        var (decoded, end) = Decode(stored, CompressionKind.Gzip);

        Assert.Equal(Content, decoded);
        Assert.Equal(InputEndKind.Complete, end.Kind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-4)]
    [InlineData(-5)]
    [InlineData(-8)]
    public void AWholeGzipWithADamagedTrailerIsAnErrorNotATruncation(int fromEnd)
    {
        var stored = RealGzip(Content);
        stored[^-fromEnd] ^= 0x55;

        Assert.Throws<InvalidDataException>(() => Decode(stored, CompressionKind.Gzip));
    }

    [Fact]
    public void AGzipWhoseContentWasChangedIsAnError()
    {
        // Stored blocks: a changed byte still inflates, so only the CRC can tell.
        var stored = FixtureContainers.Gzip(Content);
        stored[stored.Length / 2] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => Decode(stored, CompressionKind.Gzip));

        // The same damage in a member that is not the last one.
        byte[] two = [.. stored, .. RealGzip(Content)];
        Assert.Throws<InvalidDataException>(() => Decode(two, CompressionKind.Gzip));
    }

    [Fact]
    public void ALargeGzipDecodesAcrossManyInflaterBlocks()
    {
        var big = new byte[5 << 20];
        new Random(18).NextBytes(big);
        byte[] stored = [.. RealGzip(big), .. RealGzip(Content), .. FixtureContainers.Gzip(big)];

        var (decoded, end) = Decode(stored, CompressionKind.Gzip);

        Assert.Equal(big.Length + Content.Length + big.Length, decoded.Length);
        Assert.True(decoded.AsSpan(0, big.Length).SequenceEqual(big));
        Assert.True(decoded.AsSpan(big.Length + Content.Length).SequenceEqual(big));
        Assert.Equal(InputEnd.Complete(decoded.Length, stored.Length), end);
    }

    [Fact]
    public void ConcatenatedXzStreamsAreAllRead()
    {
        var (first, second) = (Content[..3000], Content[3000..]);
        byte[] stored = [.. FixtureContainers.Xz(first), 0, 0, 0, 0, .. FixtureContainers.Xz(second)];

        var (decoded, end) = Decode(stored, CompressionKind.Xz);

        Assert.Equal(Content, decoded);
        Assert.Equal(InputEnd.Complete(Content.Length, stored.Length), end);

        var cut = stored.Length - 20;
        (decoded, end) = Decode(stored.AsSpan(0, cut).ToArray(), CompressionKind.Xz);
        Assert.Equal(InputEndKind.Truncated, end.Kind);
        Assert.True(Content.AsSpan().StartsWith(decoded));
        Assert.True(decoded.Length >= first.Length);
    }

    [Fact]
    public void ConcatenatedZstdFramesAndBzip2StreamsAreAllRead()
    {
        var (first, second) = (Content[..3000], Content[3000..]);
        foreach (var format in new[] { "zstd", "bzip2" })
        {
            var (a, kind) = Encode(format, first);
            byte[] stored = [.. a, .. Encode(format, second).Stored];

            var (decoded, end) = Decode(stored, kind);

            Assert.Equal(Content, decoded);
            Assert.Equal(InputEnd.Complete(Content.Length, stored.Length), end);

            (decoded, end) = Decode(stored.AsSpan(0, stored.Length - 5).ToArray(), kind);
            Assert.Equal(InputEndKind.Truncated, end.Kind);
            Assert.True(Content.AsSpan().StartsWith(decoded));
        }
    }

    [Theory]
    [InlineData("zstd")]
    [InlineData("xz")]
    [InlineData("bzip2")]
    public void DamageInTheMiddleOfAWholeFileIsStillAnError(string format)
    {
        var big = TestData.NumberedLines(1 << 20, seed: 19).Bytes;
        var (stored, kind) = Encode(format, big, checksum: true);
        if (format == "xz")
        {
            // Stored LZMA2 chunks: break a chunk header, the content itself is only covered by the check at the end.
            stored[24 + 65536 + 3] = 0x7F;
        }
        else
        {
            for (var i = 0; i < 16; i++)
            {
                stored[(stored.Length / 2) + i] ^= 0xA5;
            }
        }

        Assert.ThrowsAny<Exception>(() => Decode(stored, kind));
    }

    [Fact]
    public void AZstdSkippableFrameBetweenTwoFramesIsPassedOverAndCutsInsideItAreTruncations()
    {
        var (first, second) = (Content[..3000], Content[3000..]);
        var a = Encode("zstd", first).Stored;
        byte[] skippable = [0x5A, 0x2A, 0x4D, 0x18, 7, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7];
        byte[] stored = [.. a, .. skippable, .. Encode("zstd-checksum", second).Stored];

        var (decoded, end) = Decode(stored, CompressionKind.Zstd);
        Assert.Equal(Content, decoded);
        Assert.Equal(InputEnd.Complete(Content.Length, stored.Length), end);

        for (var cut = a.Length; cut < stored.Length; cut++)
        {
            (decoded, end) = Decode(stored.AsSpan(0, cut).ToArray(), CompressionKind.Zstd);

            var expected = cut == a.Length || cut == a.Length + skippable.Length ? InputEndKind.Complete : InputEndKind.Truncated;
            Assert.True(end.Kind == expected, $"cut at {cut} of {stored.Length}: {end}, expected {expected}");
            Assert.True(Content.AsSpan().StartsWith(decoded));
        }
    }

    [Fact]
    public void AnUncompressedFileIsCompleteAtItsEnd()
    {
        using var stream = InputDecoding.OpenChecked(new MemoryStream(Content), CompressionKind.None);
        Assert.Equal(InputEndKind.Unknown, stream.End.Kind);

        var decoded = ReadAll(stream);

        Assert.Equal(Content, decoded);
        Assert.Equal(InputEnd.Complete(Content.Length, Content.Length), stream.End);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void TheStoredStreamStaysOpenUntilTheDecoderIsDisposed(string format)
    {
        // The job runner reads the rest of the file through the hash after the last decoded byte.
        var (stored, kind) = Encode(format, Content);
        var raw = new MemoryStream([.. stored, 1, 2, 3]);
        var decoder = InputDecoding.OpenChecked(raw, kind);

        try
        {
            ReadAll(decoder);
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            // Three stray bytes are an error for some formats; the stream under the decoder must survive that too.
        }

        Assert.True(raw.CanRead);
        raw.Position = raw.Length;
        Assert.Equal(-1, raw.ReadByte());
        decoder.Dispose();
        Assert.False(raw.CanRead);
    }

    [Fact]
    public void TheDefaultEndIsUnknown()
    {
        Assert.Equal(InputEndKind.Unknown, default(InputEnd).Kind);
        Assert.False(default(InputEnd).IsTruncated);
        Assert.True(InputEnd.Truncated(1, 2).IsTruncated);
    }

    private static (byte[] Stored, CompressionKind Kind) Encode(string format, byte[] content, bool checksum = false)
    {
        switch (format)
        {
            case "gzip":
                return (RealGzip(content), CompressionKind.Gzip);
            case "gzip-stored":
                return (FixtureContainers.Gzip(content), CompressionKind.Gzip);
            case "zstd-checksum":
                return Encode("zstd", content, checksum: true);
            case "zstd":
                var output = new MemoryStream();
                using (var z = new ZstdSharp.CompressionStream(output, level: 3, leaveOpen: true))
                {
                    // Without the frame checksum zstd has nothing to notice changed literals with.
                    z.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, checksum ? 1 : 0);
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

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "fixtures", "core")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("fixtures/core was not found above the test binaries");
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
        var decoded = ReadAll(stream);
        Assert.NotEqual(InputEndKind.Unknown, stream.End.Kind);
        return (decoded, stream.End);
    }

    private static byte[] ReadAll(Stream stream)
    {
        var output = new MemoryStream();
        var buffer = new byte[1777];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        return output.ToArray();
    }
}
