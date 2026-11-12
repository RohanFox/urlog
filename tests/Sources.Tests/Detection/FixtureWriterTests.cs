namespace Urlog.Sources.Tests.Detection;

/// <summary>
/// The committed fixtures under <c>fixtures/detection</c> and <c>fixtures/generic</c> are exactly what
/// <see cref="FixtureWriter"/> writes. To change one, change the writer and run the explicit test below
/// (<c>dotnet test tests/Sources.Tests -- --filter-method '*WriteTheFixturesIntoTheRepository' --explicit only</c>).
/// </summary>
public sealed class FixtureWriterTests
{
    private static readonly string FixturesRoot = Path.Combine(Fixtures.Root, "fixtures");

    [Fact]
    public void WritingAgainReproducesTheCommittedFilesByteForByte()
    {
        var written = FixtureWriter.Build();

        var differing = new List<string>();
        foreach (var (name, bytes) in written)
        {
            var path = Path.Combine(FixturesRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                differing.Add(name + " (missing on disk)");
            }
            else if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                differing.Add(name);
            }
        }

        foreach (var root in new[] { "detection", "generic" })
        {
            foreach (var path in Directory.GetFiles(Path.Combine(FixturesRoot, root), "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(FixturesRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                if (!written.ContainsKey(name))
                {
                    differing.Add(name + " (on disk, not written)");
                }
            }
        }

        Assert.True(differing.Count == 0, "fixtures differ from what the writer produces:\n" + string.Join('\n', differing));
    }

    [Fact]
    public void TwoRunsWriteTheSameBytes()
    {
        var first = FixtureWriter.Build();
        var second = FixtureWriter.Build();
        Assert.Equal(first.Keys, second.Keys);
        foreach (var (name, bytes) in first)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(second[name]), name);
        }
    }

    [Fact]
    public void TheHandWrittenContainersDecodeToWhatWentIn()
    {
        var data = new byte[150_000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 31) + (i >> 8));
        }

        foreach (var input in new[] { data, [], [42] })
        {
            Assert.Equal(input, Decode(FixtureContainers.Gzip(input), Urlog.Core.Pipeline.CompressionKind.Gzip));
            Assert.Equal(input, Decode(FixtureContainers.Xz(input), Urlog.Core.Pipeline.CompressionKind.Xz));
            Assert.Equal(input, Decode(FixtureContainers.Bzip2(input), Urlog.Core.Pipeline.CompressionKind.Bzip2));
        }

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(FixtureContainers.Zip(("a/b.log", data), ("c.txt", [1, 2, 3]))));
        Assert.Equal(["a/b.log", "c.txt"], zip.Entries.Select(e => e.FullName));
        using var entry = zip.Entries[0].Open();
        using var copy = new MemoryStream();
        entry.CopyTo(copy);
        Assert.Equal(data, copy.ToArray());

        using var tar = new System.Formats.Tar.TarReader(new MemoryStream(FixtureContainers.Tar(("a/b.log", data), ("c.txt", [1, 2, 3]))));
        var first = tar.GetNextEntry()!;
        Assert.Equal("a/b.log", first.Name);
        Assert.Equal(data.Length, first.Length);
        Assert.Equal("c.txt", tar.GetNextEntry()!.Name);
        Assert.Null(tar.GetNextEntry());
    }

    [Fact(Explicit = true)]
    public void WriteTheFixturesIntoTheRepository()
    {
        foreach (var root in new[] { "detection", "generic" })
        {
            var directory = Path.Combine(FixturesRoot, root);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        foreach (var (name, bytes) in FixtureWriter.Build())
        {
            var path = Path.Combine(FixturesRoot, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }

    private static byte[] Decode(byte[] compressed, Urlog.Core.Pipeline.CompressionKind kind)
    {
        using var stream = Urlog.Core.Pipeline.InputDecoding.Decompress(new MemoryStream(compressed), kind);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
