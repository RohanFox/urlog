using Urlog.Core.Metadata;

namespace Urlog.Core.Tests;

/// <summary>
/// A compressed file goes through the whole job like a plain one: decoded, parsed, and its stored bytes hashed to
/// the end after the last decoded byte, which needs the stored stream still open when the decoder is done.
/// </summary>
public sealed class JobRunnerCompressedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("sample.log.gz", "gzip")]
    [InlineData("sample.log.xz", "xz")]
    [InlineData("sample.log.bz2", "bzip2")]
    [InlineData("zstd", "zstd")]
    public async Task ACompressedFileIsParsedToTheSameRowsAsThePlainOne(string stored, string compression)
    {
        var directory = FixtureDirectory();
        var plain = await File.ReadAllBytesAsync(Path.Combine(directory, "sample.log"), Ct);
        var compressed = stored == "zstd" ? Zstd(plain) : await File.ReadAllBytesAsync(Path.Combine(directory, stored), Ct);

        await using var h = await RunnerHarness.CreateAsync();
        var first = await h.AddCaseAsync("plain");
        var second = await h.AddCaseAsync("compressed");
        var (plainFile, plainJob, _) = await h.AddFileAsync(first.Id, plain, "sample.log");
        var (file, job, _) = await h.AddFileAsync(second.Id, compressed, "sample.log." + compression, compression: compression);

        await h.Runner().RunAsync(Ct);

        var expected = await h.JobAsync(plainJob.Id);
        var done = await h.JobAsync(job.Id);
        Assert.Equal(JobState.Done, expected.State);
        Assert.True(expected.Rows > 0);
        Assert.True(done.State == JobState.Done, $"{compression}: {done.State}, {done.Message}");
        Assert.Equal(expected.Rows, done.Rows);
        Assert.Equal(FileState.Parsed, (await h.FileAsync(file.Id)).State);
        Assert.Equal(expected.Rows, h.Events.Visible((ulong)file.Id));
        Assert.Equal(h.Events.Visible((ulong)plainFile.Id), h.Events.Visible((ulong)file.Id));
    }

    private static byte[] Zstd(byte[] content)
    {
        var output = new MemoryStream();
        using (var z = new ZstdSharp.CompressionStream(output, level: 3, leaveOpen: true))
        {
            z.Write(content);
        }

        return output.ToArray();
    }

    private static string FixtureDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "fixtures", "core");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("fixtures/core was not found above the test binaries");
    }
}
