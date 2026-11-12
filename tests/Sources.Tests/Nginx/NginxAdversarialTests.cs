using System.Buffers;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Nginx;

namespace Urlog.Sources.Tests.Nginx;

/// <summary>Every file under <c>fixtures/nginx/adversarial/</c> produces rows or rejects, never an exception (rule 6).</summary>
public sealed class NginxAdversarialTests
{
    public static TheoryData<string> AdversarialFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures.Root, "fixtures", "nginx", "adversarial")).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AdversarialFiles))]
    public void RawBytesNeverThrow(string file)
    {
        // Fed to Parse as they are, with no decoding: the parser must cope with any byte sequence.
        var data = Fixtures.Bytes("nginx", Path.Combine("adversarial", file));
        var writer = new NullBatchWriter();
        var ctx = new ParseContext { CaseId = 1, DefaultHost = "web-01"u8.ToArray() };
        ctx.Bind(writer);
        new NginxSource().Parse(new ReadOnlySequence<byte>(data), ctx, writer);
        var records = data.Count(b => b == (byte)'\n') + (data.Length > 0 && data[^1] != (byte)'\n' ? 1 : 0);
        Assert.Equal(records, writer.Rows);
    }

    [Fact]
    public async Task TruncatedLastLineIsARejectAndTheRestParse()
    {
        var rows = await Fixtures.ParseFileAsync(new NginxSource(), Fixtures.Path("nginx", "adversarial/truncated-last-line.log"));
        Assert.Equal(7, rows.Count);
        Assert.Equal(6, rows.Count(r => !r.IsReject));
        Assert.Equal("bad timestamp", rows[^1].Extras["reject_reason"]);
        Assert.Equal("203.0.113.7 - - [01/Mar/20", rows[^1].Raw);
    }

    [Fact]
    public async Task Utf16IsTranscodedByCoreAndParsesCleanly()
    {
        var rows = await Fixtures.ParseFileAsync(new NginxSource(), Fixtures.Path("nginx", "adversarial/utf16.log"));
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsReject, r.ToString()));
        Assert.Equal("::ffff:203.0.113.7", rows[0].SourceIp);
    }

    [Fact]
    public void Utf16FedRawIsAllRejects()
    {
        var rows = NginxSourceTests.ParseBytes(Fixtures.Bytes("nginx", "adversarial/utf16.log"), int.MaxValue);
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.IsReject));
    }

    [Fact]
    public async Task Latin1BytesPassThroughRawUnchanged()
    {
        var path = Fixtures.Path("nginx", "adversarial/latin1.log");
        var rows = await Fixtures.ParseFileAsync(new NginxSource(), path);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsReject, r.ToString()));
        var file = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains((byte)0xE9, rows[0].RawBytes.ToArray());
        Assert.Equal(file.AsSpan(0, rows[0].RawBytes.Length).ToArray(), rows[0].RawBytes.ToArray());
    }

    [Fact]
    public async Task MixedSyslogLinesAreRejectedAndNginxLinesParsed()
    {
        var rows = await Fixtures.ParseFileAsync(new NginxSource(), Fixtures.Path("nginx", "adversarial/mixed-syslog.log"));
        Assert.Equal(7, rows.Count);
        Assert.Equal(4, rows.Count(r => !r.IsReject));
        Assert.Equal(3, rows.Count(r => r.IsReject));
    }

    [Fact]
    public async Task GarbageIsAllRejects()
    {
        var rows = await Fixtures.ParseFileAsync(new NginxSource(), Fixtures.Path("nginx", "adversarial/garbage.bin"));
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.IsReject));
    }
}
