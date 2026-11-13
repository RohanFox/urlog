using System.Buffers.Binary;
using System.IO.Compression;
using Urlog.Core.Pipeline;
using Urlog.Sources.Tests.Detection;

namespace Urlog.Core.Tests;

/// <summary>
/// Files built to mislead anything that looks for a member's end by its bytes: several members, empty ones,
/// trailer bytes that also stand somewhere else, a member that holds its own trailer. Every file is cut at every byte.
/// A cut on a member boundary leaves a whole shorter file; any other cut is a truncation; what was decoded is
/// always the start of the whole content.
/// </summary>
public sealed class GzipMemberCutTests
{
    private static readonly byte[] Text = TestData.NumberedLines(5 << 10, seed: 21).Bytes;

    public static TheoryData<string> Cases =>
    [
        "two members",
        "five members",
        "an empty member in the middle",
        "two empty members in a row",
        "a member that ends with the trailer of the one before it",
        "a member that ends with the trailer of the one after it",
        "a member that ends with eight zero bytes before an empty member",
        "a member that holds its own trailer earlier",
        "a member that holds its own trailer earlier, then another member",
        "the same member three times",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryCutIsAWholeShorterFileOrATruncation(string name)
    {
        var members = Build(name);
        var stored = members.SelectMany(m => m.Stored).ToArray();
        var whole = members.SelectMany(m => m.Content).ToArray();
        var boundaries = new HashSet<int>();
        var at = 0;
        foreach (var member in members)
        {
            boundaries.Add(at += member.Stored.Length);
        }

        var (decoded, end) = Decode(stored);
        Assert.Equal(whole, decoded);
        Assert.Equal(InputEnd.Complete(whole.Length, stored.Length), end);

        for (var cut = 1; cut < stored.Length; cut++)
        {
            (decoded, end) = Decode(stored.AsSpan(0, cut).ToArray());

            var expected = boundaries.Contains(cut) ? InputEndKind.Complete : InputEndKind.Truncated;
            Assert.True(end.Kind == expected, $"{name}: cut at {cut} of {stored.Length} is {end.Kind}, expected {expected}");
            Assert.True(whole.AsSpan().StartsWith(decoded), $"{name}: cut at {cut}, the decoded bytes are not a prefix of the content");
            Assert.Equal(decoded.Length, end.DecodedOffset);
            if (expected == InputEndKind.Complete)
            {
                Assert.Equal(members.TakeWhile((_, i) => members.Take(i + 1).Sum(m => m.Stored.Length) <= cut).Sum(m => m.Content.Length), decoded.Length);
            }
        }
    }

    [Fact]
    public void AWholeFileWhoseTrailerBytesAlsoStandEarlierInTheLastBlockIsComplete()
    {
        // Under 8 KB, so the inflater's last block is the whole file and holds both places.
        var member = SelfTrailer(seed: 31);
        var trailer = member.Stored.AsSpan(member.Stored.Length - 8);
        Assert.True(member.Stored.AsSpan(0, member.Stored.Length - 8).IndexOf(trailer) > 0, "the trailer's bytes must also stand inside the data");
        Assert.InRange(member.Stored.Length, 1, 8000);

        var (decoded, end) = Decode(member.Stored);

        Assert.Equal(member.Content, decoded);
        Assert.Equal(InputEnd.Complete(member.Content.Length, member.Stored.Length), end);
    }

    [Fact]
    public void TheSameHoldsWhenTheEarlierPlaceIsInAnOlderBlock()
    {
        // 40 KB of stored data: the earlier copy is long gone from the inflater's last block.
        var member = SelfTrailer(seed: 32, tailBytes: 40 << 10);

        var (decoded, end) = Decode(member.Stored);

        Assert.Equal(member.Content, decoded);
        Assert.Equal(InputEndKind.Complete, end.Kind);
    }

    private static (byte[] Content, byte[] Stored)[] Build(string name)
    {
        var a = Real(Text[..1500]);
        var b = Real(Text[1500..3100]);
        var c = Stored(Text[3100..]);
        var empty = Stored([]);
        var emptyDeflated = Real([]);
        switch (name)
        {
            case "two members":
                return [a, c];
            case "five members":
                return [a, b, c, Real(Text[..700]), Stored(Text[700..900])];
            case "an empty member in the middle":
                return [a, empty, b];
            case "two empty members in a row":
                return [a, emptyDeflated, empty, c];
            case "a member that ends with the trailer of the one before it":
                return [a, Stored([.. Text[..300], .. Trailer(a)]), b];
            case "a member that ends with the trailer of the one after it":
                return [Stored([.. Text[..300], .. Trailer(b)]), b, a];
            case "a member that ends with eight zero bytes before an empty member":
                return [Stored([.. Text[..300], .. new byte[8]]), empty, a];
            case "a member that holds its own trailer earlier":
                return [SelfTrailer(seed: 33)];
            case "a member that holds its own trailer earlier, then another member":
                return [a, SelfTrailer(seed: 34), b];
            case "the same member three times":
                return [a, a, a];
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "unknown case");
        }
    }

    private static byte[] Trailer((byte[] Content, byte[] Stored) member) => member.Stored[^8..];

    private static (byte[] Content, byte[] Stored) Real(byte[] content)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return (content, output.ToArray());
    }

    /// <summary>Stored deflate blocks: the content's bytes stand in the file as they are.</summary>
    private static (byte[] Content, byte[] Stored) Stored(byte[] content) => (content, FixtureContainers.Gzip(content));

    /// <summary>
    /// A stored member whose content holds the eight bytes of its own trailer. The CRC is chosen first and the
    /// last four content bytes are solved so the content has it.
    /// </summary>
    private static (byte[] Content, byte[] Stored) SelfTrailer(int seed, int tailBytes = 60)
    {
        var random = new Random(seed);
        var head = new byte[120];
        var tail = new byte[tailBytes];
        random.NextBytes(head);
        random.NextBytes(tail);
        var crc = (uint)random.Next() | 0x8000_0001u;
        var length = head.Length + 8 + tail.Length + 4;

        var content = new byte[length];
        head.CopyTo(content, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(head.Length), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(head.Length + 4), (uint)length);
        tail.CopyTo(content, head.Length + 8);
        ForceCrc(content, crc);

        Assert.Equal(crc, FixtureContainers.Crc32(content));
        return Stored(content);
    }

    /// <summary>Sets the last four bytes of <paramref name="data"/> so that its CRC-32 is <paramref name="crc"/>.</summary>
    private static void ForceCrc(byte[] data, uint crc)
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        // Backwards from the wanted final state: each step's table entry is known by its top byte.
        var wanted = ~crc;
        Span<byte> entries = stackalloc byte[4];
        for (var i = 3; i >= 0; i--)
        {
            var top = wanted >> 24;
            var index = 0;
            while (table[index] >> 24 != top)
            {
                index++;
            }

            entries[i] = (byte)index;
            wanted = (wanted ^ table[index]) << 8;
        }

        // Forwards from the state the data before the four bytes leaves.
        var state = ~FixtureContainers.Crc32(data.AsSpan(0, data.Length - 4));
        for (var i = 0; i < 4; i++)
        {
            data[data.Length - 4 + i] = (byte)(state ^ entries[i]);
            state = table[entries[i]] ^ (state >> 8);
        }
    }

    private static (byte[] Decoded, InputEnd End) Decode(byte[] stored)
    {
        using var stream = InputDecoding.OpenChecked(new MemoryStream(stored), CompressionKind.Gzip);
        var output = new MemoryStream();
        var buffer = new byte[1531];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        return (output.ToArray(), stream.End);
    }
}
