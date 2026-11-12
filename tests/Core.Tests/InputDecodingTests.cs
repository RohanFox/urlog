using System.IO.Compression;
using System.Text;
using Urlog.Core.Pipeline;
using SharpCompress.Compressors.BZip2;

namespace Urlog.Core.Tests;

public sealed class InputDecodingTests
{
    private static readonly string FixtureDir = FindFixtureDir();

    [Theory]
    [InlineData("sample.log", CompressionKind.None)]
    [InlineData("sample.log.gz", CompressionKind.Gzip)]
    [InlineData("sample.log.xz", CompressionKind.Xz)]
    [InlineData("sample.log.bz2", CompressionKind.Bzip2)]
    public void CommittedFixturesDecodeToTheSameBytes(string name, CompressionKind expectedKind)
    {
        var expected = File.ReadAllBytes(Path.Combine(FixtureDir, "sample.log"));
        var raw = File.ReadAllBytes(Path.Combine(FixtureDir, name));
        Assert.Equal(expectedKind, InputDecoding.DetectCompression(raw));

        using var decoded = InputDecoding.OpenDecoded(new MemoryStream(raw), expectedKind, InputEncoding.Utf8);
        Assert.Equal(expected, ReadAll(decoded));
    }

    [Fact]
    public void ZstdRoundTripsInProcess()
    {
        var (data, _) = TestData.NumberedLines(300 << 10, seed: 5);
        var compressed = new MemoryStream();
        using (var z = new ZstdSharp.CompressionStream(compressed, level: 3, leaveOpen: true))
        {
            z.Write(data);
        }

        var raw = compressed.ToArray();
        Assert.Equal(CompressionKind.Zstd, InputDecoding.DetectCompression(raw));
        using var decoded = InputDecoding.Decompress(new MemoryStream(raw), CompressionKind.Zstd);
        Assert.Equal(data, ReadAll(decoded));
    }

    [Fact]
    public void GzipAndBzip2RoundTripInProcessIncludingConcatenatedMembers()
    {
        var (data, _) = TestData.NumberedLines(200 << 10, seed: 6);
        var half = data.Length / 2;

        var gz = new MemoryStream();
        using (var g = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            g.Write(data, 0, half);
        }

        using (var g = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            g.Write(data, half, data.Length - half);
        }

        using var gzDecoded = InputDecoding.Decompress(new MemoryStream(gz.ToArray()), CompressionKind.Gzip);
        Assert.Equal(data, ReadAll(gzDecoded));

        var bz = new MemoryStream();
        using (var b = BZip2Stream.Create(bz, SharpCompress.Compressors.CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true))
        {
            b.Write(data);
        }

        var bzBytes = bz.ToArray();
        Assert.Equal(CompressionKind.Bzip2, InputDecoding.DetectCompression(bzBytes));
        using var bzDecoded = InputDecoding.Decompress(new MemoryStream(bzBytes), CompressionKind.Bzip2);
        Assert.Equal(data, ReadAll(bzDecoded));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Utf16TranscodesToTheUtf8Original(bool bigEndian, bool bom)
    {
        var text = "1 plain ascii\n2 Ünïcödé üsér 山田太郎\n3 emoji 🔐🔑 end\n4 last line without newline";
        var utf8 = Encoding.UTF8.GetBytes(text);
        var encoding = bigEndian ? new UnicodeEncoding(bigEndian: true, byteOrderMark: bom) : new UnicodeEncoding(bigEndian: false, byteOrderMark: bom);
        var utf16 = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

        var declared = bigEndian ? InputEncoding.Utf16BigEndian : InputEncoding.Utf16LittleEndian;
        if (bom)
        {
            Assert.Equal(declared, InputDecoding.DetectEncoding(utf16, out var bomLength));
            Assert.Equal(2, bomLength);
        }

        // Feed it one byte at a time so surrogate pairs and code units split across reads.
        using var decoded = InputDecoding.Transcode(new TrickleStream(utf16), declared);
        Assert.Equal(utf8, ReadAll(decoded, readSize: 5));
    }

    [Fact]
    public void BomOverridesADeclaredEndianness()
    {
        var text = "1 hello\n";
        var le = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var bytes = le.GetPreamble().Concat(le.GetBytes(text)).ToArray();
        using var decoded = InputDecoding.Transcode(new MemoryStream(bytes), InputEncoding.Utf16BigEndian);
        Assert.Equal(Encoding.UTF8.GetBytes(text), ReadAll(decoded));
    }

    [Fact]
    public void DanglingOddByteBecomesAReplacementCharacter()
    {
        var le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        var bytes = le.GetBytes("ab").Concat(new byte[] { 0x41 }).ToArray();
        using var decoded = InputDecoding.Transcode(new MemoryStream(bytes), InputEncoding.Utf16LittleEndian);
        Assert.Equal("ab�", Encoding.UTF8.GetString(ReadAll(decoded)));
    }

    private static byte[] ReadAll(Stream s, int readSize = 7)
    {
        var output = new MemoryStream();
        var buffer = new byte[readSize];
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        return output.ToArray();
    }

    private static string FindFixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("urlog.sln not found"), "fixtures", "core");
    }

    /// <summary>Returns one byte per read, to exercise every split point of the transcoder.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _pos;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0)
            {
                return 0;
            }

            buffer[offset] = data[_pos++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
