using System.Buffers.Binary;
using Urlog.Core.Ingest;
using Urlog.Core.Ingest.Native;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Core.Tests;

/// <summary>The framing layer on its own: frames, checksums and values that straddle frame boundaries, read back by our own frame reader.</summary>
public sealed class NativeFramingTests
{
    [Fact]
    public void PayloadStraddlingFramesRoundTripsThroughTheFrameReader()
    {
        var stream = new MemoryStream();
        var writer = new ProtocolWriter(stream, compress: true);
        var expected = new byte[3 * ProtocolWriter.FrameBytes + 12345];
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)('a' + ((i / 13 + i / 1_000_003) % 20)); // runs of 13: compressible, and no two frames alike
        }

        // Values of odd sizes so several of them cross the 1 MiB boundaries.
        writer.Raw([2]);
        writer.RawString(default);
        var pos = 0;
        while (pos < expected.Length)
        {
            var n = Math.Min(expected.Length - pos, 70_001);
            writer.WriteString(expected.AsSpan(pos, n));
            pos += n;
        }

        writer.EndPacket();
        writer.Flush();
        Assert.Equal(4, writer.FramesWritten);
        Assert.True(writer.BytesSent < expected.Length / 2, "LZ4 should have compressed the payload");

        stream.Position = 0;
        var reader = new PacketReader(stream);
        Assert.Equal(2UL, reader.ReadVarUInt());
        Assert.Equal("", reader.ReadString());
        var frames = new FrameSource(reader);
        var actual = new byte[expected.Length];
        pos = 0;
        while (pos < expected.Length)
        {
            var n = checked((int)frames.ReadVarUInt());
            frames.Read(actual.AsSpan(pos, n));
            pos += n;
        }

        Assert.Equal(expected, actual);
        Assert.Equal(0, frames.Remaining);
    }

    [Fact]
    public void CorruptedFrameIsRejectedByTheChecksum()
    {
        var stream = new MemoryStream();
        var writer = new ProtocolWriter(stream, compress: true);
        writer.WriteString("hello frames"u8);
        writer.EndPacket();
        writer.Flush();
        var bytes = stream.ToArray();
        bytes[^1] ^= 0x55;
        var frames = new FrameSource(new PacketReader(new MemoryStream(bytes)));
        var error = Assert.Throws<ClickHouseInsertException>(() => frames.ReadByte());
        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VarUIntEncodesLikeLeb128()
    {
        Span<byte> buffer = stackalloc byte[10];
        Assert.Equal(1, ProtocolWriter.EncodeVarUInt(0, buffer));
        Assert.Equal(0, buffer[0]);
        Assert.Equal(1, ProtocolWriter.EncodeVarUInt(127, buffer));
        Assert.Equal(2, ProtocolWriter.EncodeVarUInt(128, buffer));
        Assert.Equal(0x80, buffer[0]);
        Assert.Equal(0x01, buffer[1]);
        Assert.Equal(3, ProtocolWriter.EncodeVarUInt(1_048_576, buffer));
        Assert.Equal(10, ProtocolWriter.EncodeVarUInt(ulong.MaxValue, buffer));
    }

    [Fact]
    public void SerializerWritesTheBlockHeaderAndColumnOrderOfThePinnedSchema()
    {
        var buffers = new PipelineBuffers(new PipelineOptions { BatchRows = 4, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 2 });
        var sink = new CollectingBatchSink();
        var w = new EventBatchWriter(buffers.Batches, sink);
        w.Bind(new ParseContext { CaseId = 9, HostId = 8, FileId = 7, DefaultHost = "h"u8.ToArray() });
        w.AppendUInt64(EventColumn.FileOffset, 5);
        w.AppendTimestamp(1234);
        w.AppendString(EventColumn.Raw, "line"u8);
        w.AppendExtra("k"u8, "v"u8);
        w.CommitRow();
        w.Flush();
        var batch = Assert.Single(sink.Batches);

        var stream = new MemoryStream();
        var writer = new ProtocolWriter(stream, compress: false);
        var serializer = new NativeBlockSerializer();
        serializer.WriteBlock(writer, batch);
        writer.Flush();

        var reader = new PacketReader(new MemoryStream(stream.ToArray()));
        Assert.Equal(2UL, reader.ReadVarUInt()); // Data
        Assert.Equal("", reader.ReadString()); // table name
        Assert.Equal(1UL, reader.ReadVarUInt()); // block info: is_overflows
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(2UL, reader.ReadVarUInt()); // bucket_num
        Assert.Equal(-1, reader.ReadInt32());
        Assert.Equal(0UL, reader.ReadVarUInt());
        Assert.Equal((ulong)EventColumns.Count, reader.ReadVarUInt());
        Assert.Equal(1UL, reader.ReadVarUInt());

        Assert.Equal("case_id", reader.ReadString());
        Assert.Equal("UInt64", reader.ReadString());
        Span<byte> u64 = stackalloc byte[8];
        reader.Read(u64);
        Assert.Equal(9UL, BinaryPrimitives.ReadUInt64LittleEndian(u64));
        Assert.Equal("host_id", reader.ReadString());
        Assert.Equal("UInt64", reader.ReadString());
        reader.Read(u64);
        Assert.Equal(8UL, BinaryPrimitives.ReadUInt64LittleEndian(u64));
        Assert.Equal("file_id", reader.ReadString());
        reader.ReadString();
        reader.Read(u64);
        Assert.Equal(7UL, BinaryPrimitives.ReadUInt64LittleEndian(u64));
        Assert.Equal("file_offset", reader.ReadString());
        reader.ReadString();
        reader.Read(u64);
        Assert.Equal(5UL, BinaryPrimitives.ReadUInt64LittleEndian(u64));
        Assert.Equal("parse_generation", reader.ReadString());
        Assert.Equal("UInt32", reader.ReadString());
        reader.Read(u64[..4]);
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(u64));
        Assert.Equal("ts", reader.ReadString());
        Assert.Equal("DateTime64(3, 'UTC')", reader.ReadString());
        reader.Read(u64);
        Assert.Equal(1234L, BinaryPrimitives.ReadInt64LittleEndian(u64));

        // host: LowCardinality prefix (1), flags (UInt8 keys | additional keys | update), 1 key "h", 1 row, index 0
        Assert.Equal("host", reader.ReadString());
        Assert.Equal("LowCardinality(String)", reader.ReadString());
        reader.Read(u64);
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(u64));
        reader.Read(u64);
        Assert.Equal((1UL << 9) | (1UL << 10), BinaryPrimitives.ReadUInt64LittleEndian(u64));
        reader.Read(u64);
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(u64));
        Assert.Equal("h", reader.ReadString());
        reader.Read(u64);
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(u64));
        Assert.Equal(0, reader.ReadByte());

        var starts = serializer.LastColumnPayloadStarts;
        Assert.Equal(EventColumns.Count, starts.Length);
        for (var c = 1; c < starts.Length; c++)
        {
            Assert.True(starts[c] > starts[c - 1], $"column {c} must start after column {c - 1}");
        }

        sink.ReturnAll();
    }

    [Fact]
    public void BlockOrderSortsByCaseHostTimestampAndOffset()
    {
        var buffers = new PipelineBuffers(new PipelineOptions { BatchRows = 8, Parallelism = 1, InFlightChunks = 3, InFlightBatches = 2 });
        var sink = new CollectingBatchSink();
        var w = new EventBatchWriter(buffers.Batches, sink);
        w.Bind(new ParseContext { CaseId = 1 });
        Append(w, "b", 5, 0);
        Append(w, "a", 9, 1);
        Append(w, "a", 3, 2);
        Append(w, "a", 3, 3);
        Append(w, "c", 1, 4);
        w.Flush();
        var batch = Assert.Single(sink.Batches);
        Assert.False(BlockOrder.IsSorted(batch));

        var scratch = Array.Empty<int>();
        var n = BlockOrder.Compute(batch, ref scratch);
        Assert.Equal(5, n);
        Assert.Equal([2, 3, 1, 0, 4], scratch.AsSpan(0, n).ToArray());

        // Serialising through the order writes rows in that order: file_offset column becomes 2,3,1,0,4.
        var stream = new MemoryStream();
        var writer = new ProtocolWriter(stream, compress: false);
        new NativeBlockSerializer().WriteBlock(writer, batch, scratch.AsSpan(0, n));
        writer.Flush();
        var reader = new PacketReader(new MemoryStream(stream.ToArray()));
        reader.ReadVarUInt(); // Data
        reader.ReadString(); // table name
        reader.ReadVarUInt(); // block info: field 1
        reader.ReadByte();
        reader.ReadVarUInt(); // field 2
        reader.ReadInt32();
        reader.ReadVarUInt(); // terminator
        Assert.Equal((ulong)EventColumns.Count, reader.ReadVarUInt());
        Assert.Equal(5UL, reader.ReadVarUInt());
        Span<byte> u64 = stackalloc byte[8];
        string name = "";
        for (var c = 0; c < 4; c++)
        {
            name = reader.ReadString();
            reader.ReadString();
            if (c < 3)
            {
                for (var i = 0; i < 5; i++)
                {
                    reader.Read(u64);
                }
            }
        }

        Assert.Equal("file_offset", name);
        var offsets = new ulong[5];
        for (var i = 0; i < 5; i++)
        {
            reader.Read(u64);
            offsets[i] = BinaryPrimitives.ReadUInt64LittleEndian(u64);
        }

        Assert.Equal([2UL, 3UL, 1UL, 0UL, 4UL], offsets);
        sink.ReturnAll();

        static void Append(EventBatchWriter w, string host, long ts, ulong offset)
        {
            w.AppendString(EventColumn.Host, System.Text.Encoding.ASCII.GetBytes(host));
            w.AppendTimestamp(ts);
            w.AppendUInt64(EventColumn.FileOffset, offset);
            w.AppendString(EventColumn.Raw, "r"u8);
            w.CommitRow();
        }
    }

    [Fact]
    public void MigrationStatementsSplitOnLineEndSemicolonsAndKeepComments()
    {
        var migration = new Migration(7, "two_tables", "-- header comment\nCREATE TABLE a (x UInt8) ENGINE = Memory; -- trailing\n\nCREATE TABLE b\n(\n  y String -- a; inside a comment\n)\nENGINE = Memory;\n-- footer only\n");
        var statements = migration.Statements();
        Assert.Equal(2, statements.Count);
        Assert.StartsWith("-- header comment\nCREATE TABLE a", statements[0], StringComparison.Ordinal);
        Assert.Contains("y String -- a; inside a comment", statements[1], StringComparison.Ordinal);
        Assert.EndsWith("ENGINE = Memory", statements[1], StringComparison.Ordinal);
        Assert.Equal(64, migration.Sha256.Length);
        Assert.Equal(new Migration(7, "two_tables", migration.Sql.Replace("\n", "\r\n", StringComparison.Ordinal)).Sha256, migration.Sha256);
    }

    [Fact]
    public void EmbeddedMigrationsAreTheNineInOrder()
    {
        var set = MigrationSet.Embedded();
        Assert.Equal(["events", "file_generation", "events_v", "field_stats", "events_v_definer", "events_v_hide_uncommitted", "store_identity", "field_stats_v", "case_histogram"], set.Select(m => m.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], set.Select(m => m.Version));
        Assert.All(set, m => Assert.NotEmpty(m.Statements()));
        Assert.Contains("tokenizer = 'splitByNonAlpha'", set[0].Sql, StringComparison.Ordinal);
    }
}
