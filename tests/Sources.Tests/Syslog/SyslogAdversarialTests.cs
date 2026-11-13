using System.Buffers;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Urlog.Sources.Syslog;

namespace Urlog.Sources.Tests.Syslog;

/// <summary>Every file under <c>fixtures/syslog/adversarial/</c> produces rows or rejects, never an exception (rule 6).</summary>
public sealed class SyslogAdversarialTests
{
    public static TheoryData<string> AdversarialFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures.Root, "fixtures", "syslog", "adversarial")).Order(StringComparer.Ordinal))
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
        var data = Fixtures.Bytes("syslog", Path.Combine("adversarial", file));
        var writer = new NullBatchWriter();
        var ctx = new ParseContext { CaseId = 1, DefaultHost = "web-01"u8.ToArray() };
        ctx.Bind(writer);
        new SyslogSource().Parse(new ReadOnlySequence<byte>(data), ctx, writer);
        var records = data.Count(b => b == (byte)'\n') + (data.Length > 0 && data[^1] != (byte)'\n' ? 1 : 0);
        Assert.Equal(records, writer.Rows);
    }

    [Theory]
    [MemberData(nameof(AdversarialFiles))]
    public void RawBytesNeverAllocatePerRow(string file)
    {
        var data = Fixtures.Bytes("syslog", Path.Combine("adversarial", file));
        var writer = new NullBatchWriter();
        var ctx = new ParseContext { CaseId = 1, DefaultHost = "web-01"u8.ToArray() };
        ctx.Bind(writer);
        var source = new SyslogSource();
        source.Parse(new ReadOnlySequence<byte>(data), ctx, writer);
        Assert.Equal(0, AllocationProbe.Least(_ => source.Parse(new ReadOnlySequence<byte>(data), ctx, writer)));
    }

    [Fact]
    public async Task TruncatedLastLineIsARejectAndTheRestParse()
    {
        var rows = await Fixtures.ParseFileAsync(new SyslogSource(), Fixtures.Path("syslog", "adversarial/truncated-last-line.log"), yearHint: 2026);
        Assert.Equal(5, rows.Count);
        Assert.Equal(4, rows.Count(r => !r.IsReject));
        Assert.Equal("bad timestamp", rows[^1].Extras["reject_reason"]);
        Assert.Equal("<38>Jan  1 00:0", rows[^1].Raw);
    }

    [Fact]
    public async Task Utf16IsTranscodedByCoreAndParsesCleanly()
    {
        var rows = await Fixtures.ParseFileAsync(new SyslogSource(), Fixtures.Path("syslog", "adversarial/utf16.log"), yearHint: 2026);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsReject, r.ToString()));
        Assert.Equal("sshd", rows[0].Program);
        Assert.Equal("1", rows[2].Extras["log.syslog.structured_data.meta.sequenceId"]);
    }

    [Fact]
    public void Utf16FedRawIsAllRejects()
    {
        var rows = Fixtures.ParseBytes(new SyslogSource(), Fixtures.Bytes("syslog", "adversarial/utf16.log"));
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.IsReject));
    }

    [Fact]
    public async Task Latin1BytesPassThroughRawUnchanged()
    {
        var path = Fixtures.Path("syslog", "adversarial/latin1.log");
        var rows = await Fixtures.ParseFileAsync(new SyslogSource(), path, yearHint: 2026);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsReject, r.ToString()));
        var file = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains((byte)0xE9, rows[0].RawBytes.ToArray());
        Assert.Equal(file.AsSpan(0, rows[0].RawBytes.Length).ToArray(), rows[0].RawBytes.ToArray());
    }

    [Fact]
    public async Task MixedNginxLinesAreRejectedAndSyslogLinesParsed()
    {
        var rows = await Fixtures.ParseFileAsync(new SyslogSource(), Fixtures.Path("syslog", "adversarial/mixed-nginx.log"), yearHint: 2026);
        Assert.Equal(7, rows.Count);
        Assert.Equal(4, rows.Count(r => !r.IsReject));
        Assert.Equal(3, rows.Count(r => r.IsReject));
        Assert.All(rows.Where(r => r.IsReject), r => Assert.Equal("bad timestamp", r.Extras["reject_reason"]));
    }

    [Fact]
    public async Task GarbageIsAllRejects()
    {
        var rows = await Fixtures.ParseFileAsync(new SyslogSource(), Fixtures.Path("syslog", "adversarial/garbage.bin"));
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r.IsReject));
    }

    [Fact]
    public async Task Hostile5424LinesAreRowsOrRejects()
    {
        var rows = await Fixtures.ParseFileAsync(new SyslogSource(), Fixtures.Path("syslog", "adversarial/hostile-5424.log"));
        Assert.Equal(13, rows.Count);

        var huge = rows[0];
        Assert.False(huge.IsReject, huge.ToString());
        Assert.Equal("huge element", huge.Message);
        Assert.Equal(1500, huge.Extras.Count); // 1500 parameters (the facility is a column)
        Assert.Equal(new string('x', 64), huge.Extras["log.syslog.structured_data.big.p1499"]);

        var brackets = rows[1];
        Assert.False(brackets.IsReject, brackets.ToString()); // "[" is a legal SD-NAME byte: one element with a 2000-byte id, keyed under the generic key
        Assert.Equal("[" + new string('[', 2000) + "]", brackets.Extras["log.syslog.structured_data"]);
        Assert.Equal("brackets", brackets.Message);

        Assert.All(rows.Skip(2).Take(9), r => Assert.True(r.IsReject, r.ToString())); // "<14>", "<14>1", ..., the backslash at the end
        var longHost = rows[11];
        Assert.False(longHost.IsReject);
        Assert.Equal(new string('h', 300), longHost.Host);
        Assert.Equal(new string('a', 100), longHost.Program);
        var longKey = rows[12];
        Assert.False(longKey.IsReject);
        Assert.Equal($"[{new string('k', 300)} v=\"1\"]", longKey.Extras["log.syslog.structured_data"]);
    }
}
