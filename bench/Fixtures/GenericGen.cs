namespace Urlog.Bench.Fixtures;

/// <summary>
/// Mixed application-log lines for the generic source, fixed seed: the four timestamp shapes it reads (ISO 8601
/// with and without a zone, bracketed BSD, CLF inside a line, epoch at the start or behind a key), stack-trace
/// continuation lines and other lines with no timestamp (about 30 % together), and a few blank lines.
/// <see cref="Counts"/> says how many lines of each kind were written, so a test can check what the source found.
/// </summary>
public sealed class GenericGen : LineGenerator
{
    public const string TimestampLines = "timestamp";
    public const string NoTimestampLines = "no_timestamp";
    public const string BlankLines = "blank";

    private static readonly byte[][] Levels = Pools.Render(["INFO ", "INFO ", "INFO ", "WARN ", "DEBUG", "ERROR"]);
    private static readonly byte[][] Loggers = Pools.Render(["c.e.shop.OrderService", "c.e.web.Handler", "c.e.auth.TokenFilter", "c.e.db.Pool", "c.e.cache.Region"]);
    private static readonly byte[][] Messages = Pools.Render(
    [
        "request handled", "cache miss for key", "connection returned to the pool", "retrying upstream call", "session expired for user",
        "scheduled job finished", "payload rejected by the validator", "token refreshed for client",
    ]);

    private static readonly byte[][] Frames = Pools.Render(
    [
        "\tat com.example.shop.OrderService.load(OrderService.java:", "\tat com.example.web.Handler.handle(Handler.java:",
        "\tat com.example.db.Pool.borrow(Pool.java:", "\tat java.base/java.lang.Thread.run(Thread.java:",
    ]);

    private static readonly byte[][] Users = Pools.Render(Pools.Users);
    private static readonly byte[][] Paths = Pools.Render(["/", "/index.html", "/api/v1/users", "/login", "/static/app.js", "/api/orders"]);

    private readonly Xoshiro256 _rng;
    private readonly byte[][] _ipv4;
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal) { [TimestampLines] = 0, [NoTimestampLines] = 0, [BlankLines] = 0 };
    private DateTime _ts = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private int _millis;
    private int _traceLeft;

    public GenericGen(ulong seed)
    {
        Seed = seed;
        _rng = new Xoshiro256(seed);
        _ipv4 = Pools.Ipv4Pool(_rng, 5_000);
    }

    public override string Name => "generic-app-mix";

    public override int Version => 1;

    public override ulong Seed { get; }

    public override IReadOnlyDictionary<string, long> Counts => _counts;

    public override void WriteLine(ByteSink sink)
    {
        Lines++;
        if (_traceLeft > 0)
        {
            // A stack-trace line: no timestamp of its own.
            _traceLeft--;
            sink.Write(Frames[_rng.Next(Frames.Length)]);
            sink.WriteUInt((ulong)_rng.Next(10, 900));
            sink.Write(")\n"u8);
            _counts[NoTimestampLines]++;
            return;
        }

        if (!_rng.Chance(0.4))
        {
            _ts = _ts.AddSeconds(_rng.Next(3));
            _millis = _rng.Next(1000);
        }

        var shape = _rng.Next(100);
        if (shape < 40)
        {
            Iso(sink, (byte)' ', (byte)',');
            sink.WriteByte((byte)' ');
            sink.Write(Levels[_rng.Next(Levels.Length)]);
            sink.Write(" [http-nio-8080-exec-"u8);
            sink.WriteUInt((ulong)_rng.Next(1, 33));
            sink.Write("] "u8);
            sink.Write(Loggers[_rng.Next(Loggers.Length)]);
            sink.Write(" - "u8);
            Message(sink);
        }
        else if (shape < 55)
        {
            Iso(sink, (byte)'T', (byte)'.');
            sink.Write(_rng.Chance(0.6) ? "Z level=info msg=\""u8 : "+02:00 level=info msg=\""u8);
            Message(sink);
            sink.Write("\" user="u8);
            sink.Write(Users[_rng.NextSkewed(Users.Length)]);
            sink.Write(" ip="u8);
            sink.Write(_ipv4[_rng.NextSkewed(_ipv4.Length)]);
        }
        else if (shape < 63)
        {
            sink.WriteByte((byte)'[');
            sink.Write(Pools.Months[_ts.Month - 1]);
            sink.WriteByte((byte)' ');
            if (_ts.Day < 10)
            {
                sink.WriteByte((byte)' ');
            }

            sink.WriteUInt((ulong)_ts.Day);
            sink.WriteByte((byte)' ');
            Time(sink);
            sink.Write("] worker "u8);
            sink.WriteUInt((ulong)_rng.Next(1, 17));
            sink.Write(": "u8);
            Message(sink);
        }
        else if (shape < 70)
        {
            sink.Write("proxy: "u8);
            sink.Write(_ipv4[_rng.NextSkewed(_ipv4.Length)]);
            sink.Write(" - - ["u8);
            sink.WritePadded(_ts.Day, 2);
            sink.WriteByte((byte)'/');
            sink.Write(Pools.Months[_ts.Month - 1]);
            sink.WriteByte((byte)'/');
            sink.WritePadded(_ts.Year, 4);
            sink.WriteByte((byte)':');
            Time(sink);
            sink.Write(" +0000] \"GET "u8);
            sink.Write(Paths[_rng.Next(Paths.Length)]);
            sink.Write(" HTTP/1.1\" 200 "u8);
            sink.WriteUInt((ulong)_rng.Next(0, 50_000));
        }
        else if (shape < 77)
        {
            sink.WriteUInt((ulong)new DateTimeOffset(_ts).ToUnixTimeSeconds());
            sink.WriteByte((byte)'.');
            sink.WritePadded(_millis, 3);
            sink.WriteByte((byte)' ');
            Message(sink);
        }
        else if (shape < 82)
        {
            sink.Write("ts="u8);
            sink.WriteUInt((ulong)new DateTimeOffset(_ts).ToUnixTimeSeconds());
            sink.Write(" level=warn msg=\""u8);
            Message(sink);
            sink.WriteByte((byte)'"');
        }
        else if (shape < 91)
        {
            sink.Write("  -> attempt "u8);
            sink.WriteUInt((ulong)_rng.Next(1, 6));
            sink.Write(" of 5 for upstream app-"u8);
            sink.WriteUInt((ulong)_rng.Next(1, 9));
            sink.Write(".internal.example.com"u8);
            sink.WriteByte((byte)'\n');
            _counts[NoTimestampLines]++;
            return;
        }
        else if (shape < 92)
        {
            sink.WriteByte((byte)'\n');
            _counts[BlankLines]++;
            return;
        }
        else
        {
            Iso(sink, (byte)' ', (byte)',');
            sink.Write(" ERROR [http-nio-8080-exec-"u8);
            sink.WriteUInt((ulong)_rng.Next(1, 33));
            sink.Write("] c.e.web.Handler - request failed: java.lang.IllegalStateException: order "u8);
            sink.WriteUInt((ulong)_rng.Next(1, 100_000));
            sink.Write(" has no customer"u8);
            _traceLeft = _rng.Next(2, 7);
        }

        sink.WriteByte((byte)'\n');
        _counts[TimestampLines]++;
    }

    private void Message(ByteSink sink)
    {
        sink.Write(Messages[_rng.Next(Messages.Length)]);
        sink.Write(" id="u8);
        sink.WriteUInt((ulong)_rng.Next(1, 1_000_000));
        sink.Write(" took="u8);
        sink.WriteUInt((ulong)_rng.Next(1, 5_000));
        sink.Write("ms"u8);
    }

    private void Iso(ByteSink sink, byte separator, byte fraction)
    {
        sink.WritePadded(_ts.Year, 4);
        sink.WriteByte((byte)'-');
        sink.WritePadded(_ts.Month, 2);
        sink.WriteByte((byte)'-');
        sink.WritePadded(_ts.Day, 2);
        sink.WriteByte(separator);
        Time(sink);
        sink.WriteByte(fraction);
        sink.WritePadded(_millis, 3);
    }

    private void Time(ByteSink sink)
    {
        sink.WritePadded(_ts.Hour, 2);
        sink.WriteByte((byte)':');
        sink.WritePadded(_ts.Minute, 2);
        sink.WriteByte((byte)':');
        sink.WritePadded(_ts.Second, 2);
    }
}
