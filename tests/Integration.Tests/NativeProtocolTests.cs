using System.Globalization;
using System.Text;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Ingest.Native;
using Urlog.Core.Sources;

namespace Urlog.Integration.Tests;

/// <summary>
/// The hand-written native-protocol client is load-bearing and nothing else can catch its mistakes, so it has its
/// own tests against the pinned server image (ADR 0004): every column type round-trips with its edge values,
/// server-side failures surface as typed errors (never a hang), and a block that straddles compressed-frame
/// boundaries arrives intact. A ClickHouse LTS bump re-runs this class before the pin moves.
/// </summary>
public sealed class NativeProtocolTests(ClickHouseFixture ch, ITestOutputHelper output) : IClassFixture<ClickHouseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HelloNegotiatesThePinnedRevisionAgainstThePinnedLts()
    {
        using var connection = NativeConnection.Connect(BatchBuilder.Options(ch, "unused"));
        output.WriteLine($"{connection.ServerVersion}, server protocol revision {connection.ServerRevision}, timezone {connection.ServerTimezone}");
        Assert.Contains($" {Urlog.Core.CoreInfo.ClickHouseLts}.", connection.ServerVersion, StringComparison.Ordinal);
        Assert.True(connection.ServerRevision >= NativeConnection.ClientRevision, "the server must speak at least the client's revision");
        connection.Ping();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EveryColumnTypeRoundTripsWithEdgeValues()
    {
        const string table = "native_roundtrip";
        await using var client = Admin();
        await client.ExecuteAsync($"DROP TABLE IF EXISTS {table}", cancellationToken: Ct);
        await client.ExecuteAsync(BatchBuilder.Ddl(table), cancellationToken: Ct);

        using var builder = new BatchBuilder(batchRows: 16);
        var w = builder.Writer;

        // Row 0: everything at its default or empty: empty strings, empty map, :: addresses, ts at the epoch.
        w.AppendUInt64(EventColumn.FileOffset, 0);
        w.AppendTimestamp(0);
        w.CommitRow();

        // Row 1: a full row: IPv4-mapped source, full IPv6 destination, UTF-8 and lowercase handling, two map pairs.
        w.AppendUInt64(EventColumn.FileOffset, 1);
        w.AppendTimestamp(1_700_000_000_123); // 2023-11-14 22:13:20.123
        w.AppendString(EventColumn.Host, "WEB-01.Example.ORG"u8);
        w.AppendString(EventColumn.Program, "nginx"u8);
        w.AppendString(EventColumn.Severity, "Info"u8);
        w.AppendUInt32(EventColumn.EventCode, uint.MaxValue);
        w.AppendString(EventColumn.EventAction, "access"u8);
        w.AppendString(EventColumn.EventOutcome, "failure"u8);
        w.AppendString(EventColumn.UserName, "Ærøskøbing ユーザー"u8);
        w.AppendIp(EventColumn.SourceIp, [10, 1, 2, 3]);
        w.AppendIp(EventColumn.DestinationIp, [0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
        w.AppendString(EventColumn.ProcessName, "Nginx"u8);
        w.AppendString(EventColumn.UrlPath, "/a/b?c=d&e=é"u8);
        w.AppendUInt16(EventColumn.HttpStatus, ushort.MaxValue);
        w.AppendMessage(42, 25); // the quoted request, status and bytes: a slice of raw, read back through the alias
        w.AppendString(EventColumn.Raw, "10.1.2.3 - - [14/Nov/2023:22:13:20 +0000] \"GET /a/b HTTP/1.1\" 404 0"u8);
        w.AppendExtra("http.request.method"u8, "GET"u8);
        w.AppendExtra("empty"u8, ""u8);
        w.AppendString(EventColumn.HttpMethod, "GET"u8); // LowCardinality: lowercased
        w.AppendString(EventColumn.HttpVersion, "1.1"u8);
        w.AppendUInt64(EventColumn.HttpResponseBodyBytes, 1234);
        w.AppendString(EventColumn.HttpReferrer, "https://ex.example/"u8);
        w.AppendString(EventColumn.UserAgent, "curl/8"u8);
        w.AppendString(EventColumn.UrlQuery, "c=d&e=é"u8);
        w.AppendString(EventColumn.SyslogFacility, "Auth"u8);
        w.AppendUInt32(EventColumn.ProcessPid, 4242);
        w.CommitRow();

        // Row 2: timestamps past 2038 and the largest offsets; a message with every byte value.
        w.AppendUInt64(EventColumn.FileOffset, ulong.MaxValue);
        w.AppendTimestamp(4_102_444_800_999); // 2100-01-01 00:00:00.999
        var allBytes = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            allBytes[i] = (byte)i;
        }

        w.AppendString(EventColumn.Raw, allBytes);
        w.AppendMessage(120, 1); // byte 120 of raw is 'x'
        w.CommitRow();

        // Row 3: 2038-01-19 03:14:08 (the 32-bit boundary) and a 100 KB message.
        w.AppendUInt64(EventColumn.FileOffset, 3);
        w.AppendTimestamp(2_147_483_648_000);
        var big = Encoding.ASCII.GetBytes(new string('m', 100_000));
        w.AppendMessage(0, big.Length);
        w.AppendString(EventColumn.Raw, big);
        w.CommitRow();

        var batch = Assert.Single(builder.Flush());
        using (var connection = NativeConnection.Connect(BatchBuilder.Options(ch, table)))
        {
            var serializer = new NativeBlockSerializer();
            connection.BeginInsert(Encoding.UTF8.GetBytes($"INSERT INTO {table} VALUES"));
            connection.WriteBlock(serializer, batch);
            connection.EndInsert();
            Assert.Equal(EventColumns.Count, connection.LastHeader.Count); // the insertable columns: the alias is not one
            for (var c = 0; c < EventColumns.Count; c++)
            {
                Assert.Equal(EventColumns.Name((EventColumn)c), connection.LastHeader[c].Name);
                Assert.Equal(EventColumns.ClickHouseType((EventColumn)c), connection.LastHeader[c].Type);
            }
        }

        var rows = await BatchBuilder.ReadBackAsText(client, table, Ct);
        Assert.Equal(4, rows.Count);

        string[] expected0 = ["1", "1", "1", "0", "1", "1970-01-01 00:00:00.000", "web-01", "", "", "0", "", "", "", "::", "::", "", "", "0", "", "", "{}", "", "", "0", "", "", "", "", "0"];
        Assert.Equal(expected0, rows[0]);

        string[] expected1 =
        [
            "1", "1", "1", "1", "1", "2023-11-14 22:13:20.123", "web-01.example.org", "nginx", "info", uint.MaxValue.ToString(CultureInfo.InvariantCulture),
            "access", "failure", "Ærøskøbing ユーザー", "::ffff:10.1.2.3", "2001:db8::1", "nginx", "/a/b?c=d&e=é", "65535",
            "\"GET /a/b HTTP/1.1\" 404 0", "10.1.2.3 - - [14/Nov/2023:22:13:20 +0000] \"GET /a/b HTTP/1.1\" 404 0", "{'http.request.method':'GET','empty':''}",
            "get", "1.1", "1234", "https://ex.example/", "curl/8", "c=d&e=é", "auth", "4242",
        ];
        Assert.Equal(expected1, rows[1]);

        // Ordered by file_offset: the 2038 row (offset 3) comes before the row at offset UInt64 max.
        Assert.Equal("2038-01-19 03:14:08.000", rows[2][5]);
        Assert.Equal(100_000, rows[2][18].Length);
        Assert.Equal(100_000, rows[2][19].Length);

        Assert.Equal(ulong.MaxValue.ToString(CultureInfo.InvariantCulture), rows[3][3]);
        Assert.Equal("2100-01-01 00:00:00.999", rows[3][5]);
        Assert.Equal("x", rows[3][18]);
        var rawLength = Convert.ToInt32(await client.ExecuteScalarAsync($"SELECT length(raw) FROM {table} WHERE file_offset = {ulong.MaxValue}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        Assert.Equal(256, rawLength);
        var rawHash = (string?)await client.ExecuteScalarAsync($"SELECT hex(SHA256(raw)) FROM {table} WHERE file_offset = {ulong.MaxValue}", cancellationToken: Ct);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(allBytes)), rawHash);

        await client.ExecuteAsync($"DROP TABLE {table}", cancellationToken: Ct);
    }

    [Theory]
    [InlineData(200, "UInt8")]
    [InlineData(1_000, "UInt16")]
    [InlineData(70_000, "UInt32")]
    public async Task LowCardinalityDictionaryWidthFollowsTheDistinctCount(int distinct, string expectedKeyWidth)
    {
        const string table = "native_lowcard";
        await using var client = Admin();
        await client.ExecuteAsync($"DROP TABLE IF EXISTS {table}", cancellationToken: Ct);
        await client.ExecuteAsync(BatchBuilder.Ddl(table), cancellationToken: Ct);

        using var builder = new BatchBuilder(batchRows: distinct);
        var w = builder.Writer;
        Span<byte> host = stackalloc byte[32];
        for (var i = 0; i < distinct; i++)
        {
            w.AppendUInt64(EventColumn.FileOffset, (ulong)i);
            var n = Encoding.ASCII.GetBytes($"host-{i:D6}", host);
            w.AppendString(EventColumn.Host, host[..n]);
            w.AppendString(EventColumn.Program, i % 2 == 0 ? "sshd"u8 : "cron"u8);
            w.AppendString(EventColumn.Raw, "r"u8);
            w.CommitRow();
        }

        var batch = Assert.Single(builder.Flush());
        Assert.Equal(distinct, batch.RowCount);
        using (var connection = NativeConnection.Connect(BatchBuilder.Options(ch, table)))
        {
            connection.BeginInsert(Encoding.UTF8.GetBytes($"INSERT INTO {table} VALUES"));
            connection.WriteBlock(new NativeBlockSerializer(), batch);
            connection.EndInsert();
        }

        var hosts = Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT uniqExact(host) FROM {table}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        var programs = Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT uniqExact(program) FROM {table}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        var last = (string?)await client.ExecuteScalarAsync($"SELECT host FROM {table} WHERE file_offset = {distinct - 1}", cancellationToken: Ct);
        var mismatched = Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT count() FROM {table} WHERE host != concat('host-', leftPad(toString(file_offset), 6, '0'))", cancellationToken: Ct), CultureInfo.InvariantCulture);
        output.WriteLine($"{distinct} distinct hosts → {expectedKeyWidth} indexes: uniqExact {hosts}, last {last}, mismatched {mismatched}");
        Assert.Equal(distinct, hosts);
        Assert.Equal(2, programs);
        Assert.Equal($"host-{distinct - 1:D6}", last);
        Assert.Equal(0, mismatched);

        await client.ExecuteAsync($"DROP TABLE {table}", cancellationToken: Ct);
    }

    [Fact]
    public async Task MissingTableIsATypedServerError()
    {
        var error = await Assert.ThrowsAsync<ClickHouseInsertException>(() => WithTimeout(() =>
        {
            using var connection = NativeConnection.Connect(BatchBuilder.Options(ch, "native_does_not_exist"));
            connection.BeginInsert("INSERT INTO native_does_not_exist VALUES"u8);
        }));
        output.WriteLine($"{error.Code} {error.Name}: {error.ServerMessage.Split('\n')[0]}");
        Assert.True(error.IsServerError);
        Assert.Equal(60, error.Code); // UNKNOWN_TABLE
        Assert.Equal("DB::Exception", error.Name);
    }

    [Fact]
    public async Task ReadOnlyUserIsATypedServerError()
    {
        const string table = "native_readonly";
        await using var client = Admin();
        await client.ExecuteAsync($"DROP TABLE IF EXISTS {table}", cancellationToken: Ct);
        await client.ExecuteAsync(BatchBuilder.Ddl(table), cancellationToken: Ct);

        var error = await Assert.ThrowsAsync<ClickHouseInsertException>(() => WithTimeout(() =>
        {
            using var connection = NativeConnection.Connect(BatchBuilder.Options(ch, table, ClickHouseFixture.QueryUser, ClickHouseFixture.QueryPassword));
            connection.BeginInsert(Encoding.UTF8.GetBytes($"INSERT INTO {table} VALUES"));
        }));
        output.WriteLine($"{error.Code} {error.Name}: {error.ServerMessage.Split('\n')[0]}");
        Assert.True(error.Code is 164 or 497, $"code {error.Code}"); // READONLY (profile-only stub) or ACCESS_DENIED (explicit grants)
        await client.ExecuteAsync($"DROP TABLE {table}", cancellationToken: Ct);
    }

    [Fact]
    public async Task WrongColumnTypeIsATypedServerError()
    {
        const string table = "native_wrongtype";
        await using var client = Admin();
        await client.ExecuteAsync($"DROP TABLE IF EXISTS {table}", cancellationToken: Ct);
        await client.ExecuteAsync(BatchBuilder.Ddl(table).Replace("host LowCardinality(String)", "host UInt8", StringComparison.Ordinal), cancellationToken: Ct);

        using var builder = new BatchBuilder(batchRows: 8);
        builder.Writer.AppendString(EventColumn.Raw, "r"u8);
        builder.Writer.CommitRow();
        var batch = Assert.Single(builder.Flush());

        var error = await Assert.ThrowsAsync<ClickHouseInsertException>(() => WithTimeout(() =>
        {
            using var connection = NativeConnection.Connect(BatchBuilder.Options(ch, table));
            connection.BeginInsert(Encoding.UTF8.GetBytes($"INSERT INTO {table} VALUES"));
            connection.WriteBlock(new NativeBlockSerializer(), batch);
            connection.EndInsert();
            Assert.True(connection.Faulted);
        }));
        output.WriteLine($"{error.Code} {error.Name}: {error.ServerMessage.Split('\n')[0]}");
        Assert.True(error.IsServerError, error.Message);
        await client.ExecuteAsync($"DROP TABLE {table}", cancellationToken: Ct);
    }

    [Fact]
    public async Task ConstraintViolationInTheSecondBlockOfAnInsertIsATypedServerError()
    {
        const string table = "native_midstream";
        await using var client = Admin();
        await client.ExecuteAsync($"DROP TABLE IF EXISTS {table}", cancellationToken: Ct);
        await client.ExecuteAsync(BatchBuilder.Ddl(table, ",\n  CONSTRAINT status_range CHECK http_status < 600"), cancellationToken: Ct);

        using var builder = new BatchBuilder(batchRows: 1000, batches: 2);
        for (var i = 0; i < 2000; i++)
        {
            builder.Writer.AppendUInt64(EventColumn.FileOffset, (ulong)i);
            builder.Writer.AppendUInt16(EventColumn.HttpStatus, (ushort)(i < 1500 ? 200 : 999)); // the violation is in the second block
            builder.Writer.AppendString(EventColumn.Raw, "r"u8);
            builder.Writer.CommitRow();
        }

        var batches = builder.Flush();
        Assert.Equal(2, batches.Count);

        var error = await Assert.ThrowsAsync<ClickHouseInsertException>(() => WithTimeout(() =>
        {
            using var connection = NativeConnection.Connect(BatchBuilder.Options(ch, table));
            var serializer = new NativeBlockSerializer();
            connection.BeginInsert(Encoding.UTF8.GetBytes($"INSERT INTO {table} VALUES"));
            connection.WriteBlock(serializer, batches[0]);
            connection.WriteBlock(serializer, batches[1]);
            connection.EndInsert();
        }));
        output.WriteLine($"{error.Code} {error.Name}: {error.ServerMessage.Split('\n')[0]}");
        Assert.Equal(469, error.Code); // VIOLATED_CONSTRAINT
        var count = Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT count() FROM {table}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        output.WriteLine($"rows left in the table after the failed INSERT: {count} (the job re-parses after DELETE WHERE file_id either way)");
        await client.ExecuteAsync($"DROP TABLE {table}", cancellationToken: Ct);
    }

    [Fact]
    public async Task BlockStraddlingCompressedFrameBoundariesArrivesIntact()
    {
        const string table = "native_frames";
        await using var client = Admin();
        await client.ExecuteAsync($"DROP TABLE IF EXISTS {table}", cancellationToken: Ct);
        await client.ExecuteAsync(BatchBuilder.Ddl(table), cancellationToken: Ct);

        // 3,000 rows × 1,000-byte distinct raw lines (the message is the whole line): about 3 MB of payload, so the
        // raw column spans several 1 MiB frames and frame boundaries fall inside string values.
        const int rows = 3000;
        using var builder = new BatchBuilder(batchRows: rows);
        var w = builder.Writer;
        var message = new byte[1000];
        var rng = new Random(42);
        var hashes = new List<string>(rows);
        for (var i = 0; i < rows; i++)
        {
            rng.NextBytes(message);
            for (var b = 0; b < message.Length; b++)
            {
                message[b] = (byte)('a' + (message[b] % 26));
            }

            hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(message)));
            w.AppendUInt64(EventColumn.FileOffset, (ulong)i);
            w.AppendMessage(0, message.Length);
            w.AppendString(EventColumn.Raw, message);
            w.CommitRow();
        }

        var batch = Assert.Single(builder.Flush());
        long frames;
        long payload;
        long[] starts;
        using (var connection = NativeConnection.Connect(BatchBuilder.Options(ch, table)))
        {
            var serializer = new NativeBlockSerializer();
            connection.BeginInsert(Encoding.UTF8.GetBytes($"INSERT INTO {table} VALUES"));
            connection.WriteBlock(serializer, batch);
            connection.EndInsert();
            frames = connection.FramesWritten;
            payload = connection.PayloadBytes;
            starts = serializer.LastColumnPayloadStarts.ToArray();
        }

        var rawStart = starts[(int)EventColumn.Raw];
        var extrasStart = starts[(int)EventColumn.Extras];
        output.WriteLine($"payload {payload:N0} B in {frames} frames; raw column at {rawStart:N0}, extras at {extrasStart:N0}");
        Assert.True(frames >= 3, $"expected several frames, got {frames}");
        // Every boundary between the start of raw and the start of extras lies inside a string value.
        for (long boundary = ProtocolWriter.FrameBytes; boundary < extrasStart; boundary += ProtocolWriter.FrameBytes)
        {
            Assert.True(boundary > rawStart, $"boundary {boundary} before the raw column");
            var offset = boundary - rawStart;
            var row = offset / 1002; // varint(1000) is 2 bytes
            var within = offset % 1002;
            output.WriteLine($"frame boundary at {boundary:N0}: raw row {row}, byte {within} of 1002");
            Assert.True(within >= 2, "a boundary exactly on a length prefix would not prove the straddle");
        }

        var count = Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT count() FROM {table}", cancellationToken: Ct), CultureInfo.InvariantCulture);
        Assert.Equal(rows, count);
        var mismatched = Convert.ToInt64(await client.ExecuteScalarAsync($"SELECT count() FROM {table} WHERE message != raw OR length(message) != 1000", cancellationToken: Ct), CultureInfo.InvariantCulture);
        Assert.Equal(0, mismatched);
        var readHashes = new List<string>();
        await foreach (var row in client.QueryAsync($"SELECT hex(SHA256(message)) FROM {table} ORDER BY file_offset", cancellationToken: Ct))
        {
            readHashes.Add((string)row[0]!);
        }

        Assert.Equal(hashes, readHashes);
        await client.ExecuteAsync($"DROP TABLE {table}", cancellationToken: Ct);
    }

    private ClickHouseTcpClient Admin() => new(ch.NativeConnectionString(ClickHouseFixture.AdminUser, ClickHouseFixture.AdminPassword));

    /// <summary>Runs the protocol call on a worker and fails the test if it hangs: an error must surface, not block.</summary>
    private static async Task WithTimeout(Action action)
    {
        var task = Task.Run(action);
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(45)));
        if (finished != task)
        {
            throw new TimeoutException("the protocol call hung instead of surfacing an error");
        }

        await task;
    }
}
