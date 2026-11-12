using System.Net.Sockets;
using System.Text;
using Urlog.Core.Pipeline;

namespace Urlog.Core.Ingest.Native;

/// <summary>
/// One native-protocol connection that does exactly what the insert path needs: Hello, Ping, and
/// <c>INSERT ... VALUES</c> with blocks streamed as Data packets, synchronously on the calling thread
/// (one writer thread owns one connection). Decision and protocol notes: docs/adr/0004-native-protocol-client.md.
/// <para>
/// The client claims protocol revision <see cref="ClientRevision"/> (54449): new enough for LowCardinality on the
/// wire (54405) and string-encoded settings (54429), old enough that the server expects no handshake addendum
/// (54458), no per-column custom-serialisation flag (54454), no chunked packets (54470) and sends no
/// ProfileEvents during inserts (54456). The server negotiates down to it. A ClickHouse LTS bump re-runs
/// <c>tests/Integration.Tests/NativeProtocolTests.cs</c> against the new image before the pin moves.
/// </para>
/// After any failure the connection is <see cref="Faulted"/> and must be disposed; the owner reconnects.
/// </summary>
public sealed class NativeConnection : IDisposable
{
    public const int ClientRevision = 54449;

    private const int RevisionWithServerTimezone = 54058;
    private const int RevisionWithServerDisplayName = 54372;
    private const int RevisionWithVersionPatch = 54401;

    /// <summary>Oldest server line the protocol client is validated against (design record: "ClickHouse >= 26.3 LTS"; the text index is GA from 26.2).</summary>
    private const int MinimumServerMajor = 26;
    private const int MinimumServerMinor = 3;

    private static readonly byte[] ClientName = Encoding.UTF8.GetBytes("urlog");
    private static readonly byte[] OsUser = Encoding.UTF8.GetBytes(Environment.UserName);
    private static readonly byte[] ClientHost = Encoding.UTF8.GetBytes(Environment.MachineName);

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly PacketReader _reader;
    private readonly FrameSource _frames;
    private readonly ProtocolWriter _writer;
    private readonly bool _compress;
    private int _negotiatedRevision;
    private bool _insertOpen;

    private NativeConnection(Socket socket, bool compress)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _reader = new PacketReader(_stream);
        _frames = new FrameSource(_reader);
        _writer = new ProtocolWriter(_stream, compress);
        _compress = compress;
    }

    public string ServerVersion { get; private set; } = "";

    public int ServerRevision { get; private set; }

    public string ServerTimezone { get; private set; } = "";

    /// <summary>Column names and types of the last insert's header block, as the server sent them.</summary>
    public IReadOnlyList<(string Name, string Type)> LastHeader { get; private set; } = [];

    /// <summary>True after an error; the connection is unusable and must be disposed.</summary>
    public bool Faulted { get; private set; }

    /// <summary>True between <see cref="BeginInsert"/> and <see cref="EndInsert"/>.</summary>
    public bool InsertOpen => _insertOpen;

    public long BytesSent => _writer.BytesSent;

    public long PayloadBytes => _writer.PayloadBytes;

    public long FramesWritten => _writer.FramesWritten;

    public static NativeConnection Connect(NativeInserterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            var connect = socket.ConnectAsync(options.Host, options.Port);
            if (!connect.Wait(options.ConnectTimeout))
            {
                throw new ClickHouseInsertException($"connect to {options.Host}:{options.Port} timed out after {options.ConnectTimeout.TotalSeconds:F0} s");
            }

            connect.GetAwaiter().GetResult();
            socket.SendBufferSize = 4 << 20;
            socket.ReceiveBufferSize = 1 << 20;
            socket.ReceiveTimeout = (int)options.ReadTimeout.TotalMilliseconds;
            socket.SendTimeout = (int)options.ReadTimeout.TotalMilliseconds;
            var connection = new NativeConnection(socket, options.Compress);
            connection.Hello(options.Database, options.User, options.Password);
            return connection;
        }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            socket.Dispose();
            throw new ClickHouseInsertException($"connect to {options.Host}:{options.Port} failed: {e.InnerExceptions[0].Message}", e.InnerExceptions[0]);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new ClickHouseInsertException($"connect to {options.Host}:{options.Port} failed: {e.Message}", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Round trip to the server; throws when the connection is dead.</summary>
    public void Ping()
    {
        Guard();
        try
        {
            _writer.RawVarUInt((ulong)ClientPacket.Ping);
            _writer.Flush();
            while (ReadPacket() != ServerPacket.Pong)
            {
            }
        }
        catch (Exception e) when (e is not ClickHouseInsertException)
        {
            throw Fault(e);
        }
    }

    /// <summary>
    /// Starts <paramref name="sql"/> (an <c>INSERT INTO ... VALUES</c>) and reads up to the server's header block.
    /// Follow with <see cref="WriteBlock"/> for each batch and <see cref="EndInsert"/> for the acknowledgement.
    /// </summary>
    public void BeginInsert(ReadOnlySpan<byte> sql)
    {
        Guard();
        if (_insertOpen)
        {
            throw new InvalidOperationException("an insert is already open on this connection");
        }

        try
        {
            WriteQuery(sql);
            WriteEmptyBlock();
            _writer.Flush();

            // Until the header block arrives: TableColumns, maybe Progress, then Data with zero rows.
            while (true)
            {
                var packet = ReadPacket();
                if (packet == ServerPacket.Data)
                {
                    break;
                }

                if (packet == ServerPacket.EndOfStream)
                {
                    throw new ClickHouseInsertException("the server ended the query before asking for data (not an INSERT?)");
                }
            }

            _insertOpen = true;
        }
        catch (Exception e)
        {
            throw Fault(e);
        }
    }

    /// <summary>Streams one batch as a Data packet into the open insert, in <paramref name="order"/> when given.</summary>
    public void WriteBlock(NativeBlockSerializer serializer, EventBatch batch, ReadOnlySpan<int> order = default)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        Guard();
        if (!_insertOpen)
        {
            throw new InvalidOperationException("BeginInsert first");
        }

        try
        {
            serializer.WriteBlock(_writer, batch, order);
            DrainPending();
        }
        catch (Exception e)
        {
            throw Fault(e);
        }
    }

    /// <summary>Ends the open insert and waits for the server's acknowledgement (EndOfStream); throws the server's exception otherwise.</summary>
    public void EndInsert()
    {
        Guard();
        if (!_insertOpen)
        {
            throw new InvalidOperationException("no insert is open");
        }

        try
        {
            WriteEmptyBlock();
            _writer.Flush();
            while (ReadPacket() != ServerPacket.EndOfStream)
            {
            }

            _insertOpen = false;
        }
        catch (Exception e)
        {
            throw Fault(e);
        }
    }

    /// <summary>One whole insert: <see cref="BeginInsert"/>, the callback's blocks, <see cref="EndInsert"/>.</summary>
    public void Insert(ReadOnlySpan<byte> sql, Action<ProtocolWriter> writeBlocks)
    {
        ArgumentNullException.ThrowIfNull(writeBlocks);
        BeginInsert(sql);
        try
        {
            writeBlocks(_writer);
        }
        catch (Exception e)
        {
            throw Fault(e);
        }

        EndInsert();
    }

    public void Dispose()
    {
        _stream.Dispose();
        _socket.Dispose();
    }

    private void Guard()
    {
        if (Faulted)
        {
            throw new ClickHouseInsertException("the connection faulted earlier and must be reopened");
        }
    }

    /// <summary>Marks the connection faulted and turns an I/O failure into the server's exception when one is waiting on the socket.</summary>
    private ClickHouseInsertException Fault(Exception e)
    {
        Faulted = true;
        _insertOpen = false;
        if (e is ClickHouseInsertException ready)
        {
            return ready;
        }

        if (e is IOException or SocketException or EndOfStreamException)
        {
            // A server that rejected the data stops reading and sends its exception; our write failed first. Prefer its message.
            try
            {
                _socket.ReceiveTimeout = 2000;
                if (_reader.HasBuffered || _socket.Available > 0)
                {
                    var type = (ServerPacket)_reader.ReadVarUInt();
                    if (type == ServerPacket.Exception)
                    {
                        return ReadException(e);
                    }
                }
            }
            catch (Exception ignored) when (ignored is IOException or SocketException or EndOfStreamException or ObjectDisposedException)
            {
                // the connection is gone; report the original failure below
            }

            return new ClickHouseInsertException($"connection to ClickHouse lost: {e.Message}", e);
        }

        return new ClickHouseInsertException($"insert failed: {e.Message}", e);
    }

    private void Hello(string database, string user, string password)
    {
        _writer.RawVarUInt((ulong)ClientPacket.Hello);
        _writer.RawString(ClientName);
        _writer.RawVarUInt(1);
        _writer.RawVarUInt(0);
        _writer.RawVarUInt(ClientRevision);
        _writer.RawString(Encoding.UTF8.GetBytes(database));
        _writer.RawString(Encoding.UTF8.GetBytes(user));
        _writer.RawString(Encoding.UTF8.GetBytes(password));
        _writer.Flush();

        var type = (ServerPacket)_reader.ReadVarUInt();
        if (type == ServerPacket.Exception)
        {
            throw ReadException(null);
        }

        if (type != ServerPacket.Hello)
        {
            throw new ClickHouseInsertException($"expected the server's Hello, got packet {type}");
        }

        var name = _reader.ReadString();
        var major = (int)_reader.ReadVarUInt();
        var minor = (int)_reader.ReadVarUInt();
        ServerRevision = (int)_reader.ReadVarUInt();
        _negotiatedRevision = Math.Min(ServerRevision, ClientRevision);
        if (_negotiatedRevision >= RevisionWithServerTimezone)
        {
            ServerTimezone = _reader.ReadString();
        }

        if (_negotiatedRevision >= RevisionWithServerDisplayName)
        {
            _reader.ReadString();
        }

        var patch = _negotiatedRevision >= RevisionWithVersionPatch ? _reader.ReadVarUInt() : 0;
        ServerVersion = $"{name} {major}.{minor}.{patch}";
        if (major < MinimumServerMajor || (major == MinimumServerMajor && minor < MinimumServerMinor))
        {
            throw new ClickHouseInsertException($"ClickHouse {major}.{minor} is older than the {MinimumServerMajor}.{MinimumServerMinor} LTS this build supports");
        }

        if (_negotiatedRevision < ClientRevision)
        {
            throw new ClickHouseInsertException($"the server speaks protocol revision {ServerRevision}, below the {ClientRevision} this client needs");
        }
    }

    private void WriteQuery(ReadOnlySpan<byte> sql)
    {
        var w = _writer;
        w.RawVarUInt((ulong)ClientPacket.Query);
        w.RawString(default); // query id: server-generated

        // ClientInfo for revision 54449.
        w.RawByte(1); // INITIAL_QUERY
        w.RawString(default); // initial user
        w.RawString(default); // initial query id
        w.RawString("0.0.0.0:0"u8); // initial address
        w.RawInt64((DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10); // initial query start time, microseconds
        w.RawByte(1); // interface TCP
        w.RawString(OsUser);
        w.RawString(ClientHost);
        w.RawString(ClientName);
        w.RawVarUInt(1); // client version major
        w.RawVarUInt(0); // minor
        w.RawVarUInt(ClientRevision);
        w.RawString(default); // quota key
        w.RawVarUInt(0); // distributed depth
        w.RawVarUInt(0); // version patch
        w.RawByte(0); // no OpenTelemetry context

        w.RawString(default); // settings: none (terminator). Never SQL_* settings on this path (ADR 0003).
        w.RawString(default); // interserver secret
        w.RawVarUInt(2); // stage: Complete
        w.RawVarUInt(_compress ? 1UL : 0UL);
        w.RawString(sql);
    }

    private void WriteEmptyBlock()
    {
        var w = _writer;
        w.RawVarUInt((ulong)ClientPacket.Data);
        w.RawString(default);
        NativeBlockSerializer.WriteBlockInfo(w);
        w.WriteVarUInt(0);
        w.WriteVarUInt(0);
        w.EndPacket();
    }

    /// <summary>Consumes packets the server sent while we were writing (Progress), so neither side's socket buffer fills up.</summary>
    private void DrainPending()
    {
        while (_reader.HasBuffered || _socket.Available > 0)
        {
            var packet = ReadPacket();
            if (packet == ServerPacket.EndOfStream)
            {
                throw new ClickHouseInsertException("EndOfStream in the middle of an insert");
            }
        }
    }

    private ServerPacket ReadPacket()
    {
        var type = (ServerPacket)_reader.ReadVarUInt();
        switch (type)
        {
            case ServerPacket.Data:
                ReadBlock();
                return type;
            case ServerPacket.Exception:
                throw ReadException(null);
            case ServerPacket.Progress:
                // read_rows, read_bytes, total_rows_to_read, written_rows, written_bytes (revision 54449)
                for (var i = 0; i < 5; i++)
                {
                    _reader.ReadVarUInt();
                }

                return type;
            case ServerPacket.EndOfStream:
            case ServerPacket.Pong:
                return type;
            case ServerPacket.TableColumns:
                _reader.ReadString();
                _reader.ReadString();
                return type;
            case ServerPacket.ProfileInfo:
                _reader.ReadVarUInt();
                _reader.ReadVarUInt();
                _reader.ReadVarUInt();
                _reader.ReadByte();
                _reader.ReadVarUInt();
                _reader.ReadByte();
                return type;
            default:
                throw new ClickHouseInsertException($"server packet {type} is not expected during an insert");
        }
    }

    private void ReadBlock()
    {
        _reader.ReadString(); // table name
        IPayloadSource payload;
        if (_compress)
        {
            _frames.Reset();
            payload = _frames;
        }
        else
        {
            payload = _reader;
        }

        // BlockInfo: (field number, value)* then 0.
        while (true)
        {
            var field = payload.ReadVarUInt();
            if (field == 0)
            {
                break;
            }

            if (field == 1)
            {
                payload.ReadByte();
            }
            else if (field == 2)
            {
                payload.ReadInt32();
            }
            else
            {
                throw new ClickHouseInsertException($"unknown BlockInfo field {field}");
            }
        }

        var columns = checked((int)payload.ReadVarUInt());
        var rows = checked((int)payload.ReadVarUInt());
        var header = new (string, string)[columns];
        for (var c = 0; c < columns; c++)
        {
            var name = payload.ReadString();
            var type = payload.ReadString();
            header[c] = (name, type);
        }

        if (rows != 0)
        {
            throw new ClickHouseInsertException($"the server sent a {rows}-row block to an insert client (send_logs_level set?)");
        }

        if (_compress && _frames.Remaining != 0)
        {
            throw new ClickHouseInsertException($"{_frames.Remaining} unread bytes after the block: layout mismatch");
        }

        if (columns > 0)
        {
            LastHeader = header;
        }
    }

    private ClickHouseInsertException ReadException(Exception? cause)
    {
        var code = _reader.ReadInt32();
        var name = _reader.ReadString();
        var message = _reader.ReadString();
        _reader.ReadString(); // stack trace
        var nested = _reader.ReadByte() != 0;
        Exception? inner = nested ? ReadException(null) : cause;
        return new ClickHouseInsertException(code, name, message, inner);
    }

    private enum ClientPacket : byte
    {
        Hello = 0,
        Query = 1,
        Data = 2,
        Ping = 4,
    }

    private enum ServerPacket : byte
    {
        Hello = 0,
        Data = 1,
        Exception = 2,
        Progress = 3,
        Pong = 4,
        EndOfStream = 5,
        ProfileInfo = 6,
        Totals = 7,
        Extremes = 8,
        TablesStatusResponse = 9,
        Log = 10,
        TableColumns = 11,
        PartUuids = 12,
        ReadTaskRequest = 13,
        ProfileEvents = 14,
    }
}
