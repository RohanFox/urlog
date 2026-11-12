namespace Urlog.Bench.Fixtures;

/// <summary>
/// nginx/Apache combined log format, about 200 B per line, fixed seed. 5 % of lines carry an oddity:
/// an IPv6 client, a 1 to 4 KB user agent, quotes inside the request, or missing fields.
/// </summary>
public sealed class NginxGen : LineGenerator
{
    private const double OddityRate = 0.05;

    private static readonly byte[][] Methods = Pools.Render(["GET", "POST", "HEAD", "PUT", "DELETE", "OPTIONS", "PATCH"]);
    private static readonly int[] MethodWeights = [860, 90, 30, 10, 5, 3, 2];
    private static readonly byte[][] Protocols = Pools.Render(["HTTP/1.1", "HTTP/2.0", "HTTP/1.0"]);
    private static readonly int[] ProtocolWeights = [80, 19, 1];
    private static readonly byte[][] Statuses = Pools.Render(["200", "304", "301", "302", "404", "403", "401", "500", "502", "503", "429"]);
    private static readonly int[] StatusWeights = [780, 70, 20, 20, 60, 10, 10, 15, 5, 5, 5];
    private static readonly byte[][] Users = Pools.Render(Pools.Users);
    private static readonly byte[][] UserAgents = Pools.Render(Pools.UserAgents);
    private static readonly byte[][] Referers = Pools.Render(Pools.Referers);
    private static readonly byte[] LongUaToken = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) "u8.ToArray();

    private readonly Xoshiro256 _rng;
    private readonly byte[][] _ipv4;
    private readonly byte[][] _ipv6;
    private readonly byte[][] _paths;
    private DateTime _ts = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    public NginxGen(ulong seed)
    {
        Seed = seed;
        _rng = new Xoshiro256(seed);
        _ipv4 = Pools.Ipv4Pool(_rng, 20_000);
        _ipv6 = Pools.Ipv6Pool(_rng, 2_000);
        _paths = Pools.PathPool(_rng, 5_000);
    }

    public override string Name => "nginx-combined";

    public override int Version => 1;

    public override ulong Seed { get; }

    public override void WriteLine(ByteSink sink)
    {
        var oddity = _rng.Chance(OddityRate) ? _rng.Next(4) : -1;
        if (oddity >= 0)
        {
            OddityLines++;
        }

        // client ip
        sink.Write(oddity == 0 ? _ipv6[_rng.Next(_ipv6.Length)] : _ipv4[_rng.NextSkewed(_ipv4.Length)]);
        sink.Write(" - "u8);

        // remote user
        if (_rng.Chance(0.03))
        {
            sink.Write(Users[_rng.Next(Users.Length)]);
        }
        else
        {
            sink.WriteByte((byte)'-');
        }

        // [01/Mar/2026:00:00:00 +0000]
        if (!_rng.Chance(0.6))
        {
            _ts = _ts.AddSeconds(1 + _rng.Next(3));
        }

        sink.Write(" ["u8);
        sink.WritePadded(_ts.Day, 2);
        sink.WriteByte((byte)'/');
        sink.Write(Pools.Months[_ts.Month - 1]);
        sink.WriteByte((byte)'/');
        sink.WritePadded(_ts.Year, 4);
        sink.WriteByte((byte)':');
        sink.WritePadded(_ts.Hour, 2);
        sink.WriteByte((byte)':');
        sink.WritePadded(_ts.Minute, 2);
        sink.WriteByte((byte)':');
        sink.WritePadded(_ts.Second, 2);
        sink.Write(" +0000] \""u8);

        // request
        if (oddity == 3)
        {
            // missing fields: empty request, 400, no bytes, no referer, no UA
            sink.Write("-\" 400 0 \"-\" \"-\"\n"u8);
            Lines++;
            return;
        }

        sink.Write(Methods[_rng.NextWeighted(MethodWeights)]);
        sink.WriteByte((byte)' ');
        sink.Write(_paths[_rng.NextSkewed(_paths.Length)]);
        if (oddity == 2)
        {
            // quotes inside the request: nginx escapes as \x22, Apache as \" ; both appear in the wild
            sink.Write("?q=\\x22\\x3cscript\\x3e\\x22&name=\\\"admin\\\"' OR 1=1--"u8);
        }
        else if (_rng.Chance(0.2))
        {
            sink.Write("?id="u8);
            sink.WriteUInt((ulong)_rng.Next(1, 100000));
            if (_rng.Chance(0.5))
            {
                sink.Write("&page="u8);
                sink.WriteUInt((ulong)_rng.Next(1, 50));
            }
        }

        sink.WriteByte((byte)' ');
        sink.Write(Protocols[_rng.NextWeighted(ProtocolWeights)]);
        sink.Write("\" "u8);

        // status and bytes
        var status = _rng.NextWeighted(StatusWeights);
        sink.Write(Statuses[status]);
        sink.WriteByte((byte)' ');
        if (status == 1)
        {
            sink.WriteByte((byte)'0');
        }
        else
        {
            sink.WriteUInt((ulong)Math.Exp(_rng.NextDouble() * 12.0));
        }

        // referer
        sink.Write(" \""u8);
        sink.Write(Referers[_rng.Next(Referers.Length)]);
        sink.Write("\" \""u8);

        // user agent
        if (oddity == 1)
        {
            sink.WriteRepeated(LongUaToken, _rng.Next(1024, 4097));
        }
        else
        {
            sink.Write(UserAgents[_rng.NextSkewed(UserAgents.Length)]);
        }

        sink.Write("\"\n"u8);
        Lines++;
    }
}
