namespace Urlog.Bench.Fixtures;

/// <summary>
/// syslog, about 120 B per line, fixed seed: 88 % RFC 3164 (no year, no offset), 12 % RFC 5424.
/// 5 % of lines carry an oddity: no PRI, non-ASCII bytes, a 1 to 2 KB message, structured data with
/// escaped quotes, an IPv6 address in the message, or a CRLF line ending.
/// </summary>
public sealed class SyslogGen : LineGenerator
{
    private const double OddityRate = 0.05;
    private const double Rfc5424Rate = 0.12;

    private static readonly byte[][] Hosts = Pools.Render(Pools.Hosts);
    private static readonly byte[][] Users = Pools.Render(Pools.Users);
    private static readonly byte[][] Commands = Pools.Render(Pools.Commands);
    private static readonly byte[][] Paths = Pools.Render(["/", "/index.html", "/wp-login.php", "/api/v1/users", "/.env", "/admin", "/static/app.js"]);

    // program name, pid present, PRI, weight
    private static readonly byte[][] Programs = Pools.Render(["sshd", "sudo", "kernel", "systemd", "CRON", "postfix/smtpd", "nginx", "dovecot", "systemd-logind", "audit"]);
    private static readonly bool[] HasPid = [true, true, false, false, true, true, true, true, true, false];
    private static readonly int[] Pris = [38, 85, 6, 30, 78, 22, 134, 22, 30, 13];
    private static readonly int[] ProgramWeights = [30, 6, 10, 12, 8, 12, 10, 6, 4, 2];

    private static readonly byte[] LongToken = "deadbeefcafebabe0123456789abcdef "u8.ToArray();
    private static readonly byte[] NonAscii = "Ünïcödé üsér näme Юрий 山田太郎 🔐"u8.ToArray();

    private readonly Xoshiro256 _rng;
    private readonly byte[][] _ipv4;
    private readonly byte[][] _ipv6;
    private DateTime _ts = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private int _millis;
    private int _session = 1000;

    public SyslogGen(ulong seed)
    {
        Seed = seed;
        _rng = new Xoshiro256(seed);
        _ipv4 = Pools.Ipv4Pool(_rng, 20_000);
        _ipv6 = Pools.Ipv6Pool(_rng, 2_000);
    }

    public override string Name => "syslog-3164-5424";

    public override int Version => 2; // v2: long-record oddity 1-2 KB so the mean stays near 120 B/line

    public override ulong Seed { get; }

    public override void WriteLine(ByteSink sink)
    {
        var oddity = _rng.Chance(OddityRate) ? _rng.Next(6) : -1;
        if (oddity >= 0)
        {
            OddityLines++;
        }

        if (!_rng.Chance(0.5))
        {
            _ts = _ts.AddSeconds(1 + _rng.Next(4));
            _millis = _rng.Next(1000);
        }

        var prog = _rng.NextWeighted(ProgramWeights);
        var host = Hosts[_rng.NextSkewed(Hosts.Length)];
        var rfc5424 = _rng.Chance(Rfc5424Rate) || oddity == 3;

        if (oddity != 0)
        {
            sink.WriteByte((byte)'<');
            sink.WriteUInt((ulong)Pris[prog]);
            sink.WriteByte((byte)'>');
        }

        if (rfc5424)
        {
            // <PRI>1 2026-03-01T00:00:00.123+00:00 host app pid msgid [sd] msg
            sink.Write("1 "u8);
            sink.WritePadded(_ts.Year, 4);
            sink.WriteByte((byte)'-');
            sink.WritePadded(_ts.Month, 2);
            sink.WriteByte((byte)'-');
            sink.WritePadded(_ts.Day, 2);
            sink.WriteByte((byte)'T');
            sink.WritePadded(_ts.Hour, 2);
            sink.WriteByte((byte)':');
            sink.WritePadded(_ts.Minute, 2);
            sink.WriteByte((byte)':');
            sink.WritePadded(_ts.Second, 2);
            sink.WriteByte((byte)'.');
            sink.WritePadded(_millis, 3);
            sink.Write(_rng.Chance(0.7) ? "+00:00 "u8 : "Z "u8);
            sink.Write(host);
            sink.WriteByte((byte)' ');
            sink.Write(Programs[prog]);
            sink.WriteByte((byte)' ');
            if (HasPid[prog])
            {
                sink.WriteUInt((ulong)_rng.Next(100, 65536));
            }
            else
            {
                sink.WriteByte((byte)'-');
            }

            sink.Write(_rng.Chance(0.5) ? " ID47 "u8 : " - "u8);
            if (oddity == 3)
            {
                sink.Write("[exampleSDID@32473 iut=\"3\" eventSource=\"App\\\"lication\" eventID=\"10\\]11\"][origin ip=\""u8);
                sink.Write(_ipv4[_rng.Next(_ipv4.Length)]);
                sink.Write("\"] "u8);
            }
            else if (_rng.Chance(0.4))
            {
                sink.Write("[meta sequenceId=\""u8);
                sink.WriteUInt((ulong)Lines);
                sink.Write("\"] "u8);
            }
            else
            {
                sink.Write("- "u8);
            }
        }
        else
        {
            // <PRI>Mar  1 00:00:00 host prog[pid]: msg
            sink.Write(Pools.Months[_ts.Month - 1]);
            sink.WriteByte((byte)' ');
            if (_ts.Day < 10)
            {
                sink.WriteByte((byte)' ');
            }

            sink.WriteUInt((ulong)_ts.Day);
            sink.WriteByte((byte)' ');
            sink.WritePadded(_ts.Hour, 2);
            sink.WriteByte((byte)':');
            sink.WritePadded(_ts.Minute, 2);
            sink.WriteByte((byte)':');
            sink.WritePadded(_ts.Second, 2);
            sink.WriteByte((byte)' ');
            sink.Write(host);
            sink.WriteByte((byte)' ');
            sink.Write(Programs[prog]);
            if (HasPid[prog])
            {
                sink.WriteByte((byte)'[');
                sink.WriteUInt((ulong)_rng.Next(100, 65536));
                sink.WriteByte((byte)']');
            }

            sink.Write(": "u8);
        }

        WriteMessage(sink, prog, oddity);

        sink.Write(oddity == 5 ? "\r\n"u8 : "\n"u8);
        Lines++;
    }

    private void WriteMessage(ByteSink sink, int prog, int oddity)
    {
        if (oddity == 2)
        {
            sink.Write("audit: long record "u8);
            sink.WriteRepeated(LongToken, _rng.Next(1024, 2049));
            return;
        }

        if (oddity == 1)
        {
            sink.Write("Accepted password for "u8);
            sink.Write(NonAscii);
            sink.Write(" from "u8);
            sink.Write(_ipv4[_rng.NextSkewed(_ipv4.Length)]);
            sink.Write(" port 51234 ssh2"u8);
            return;
        }

        var ip = oddity == 4 ? _ipv6[_rng.Next(_ipv6.Length)] : _ipv4[_rng.NextSkewed(_ipv4.Length)];
        var user = Users[_rng.NextSkewed(Users.Length)];
        switch (prog)
        {
            case 0: // sshd
                switch (_rng.Next(5))
                {
                    case 0:
                        sink.Write("Accepted publickey for "u8);
                        sink.Write(user);
                        sink.Write(" from "u8);
                        sink.Write(ip);
                        sink.Write(" port "u8);
                        sink.WriteUInt((ulong)_rng.Next(1024, 65536));
                        sink.Write(" ssh2: ED25519 SHA256:Zq7T3mN1kL9pXvB2cR4dF6gH8jK0lM2nP4qS6tU8vW0"u8);
                        break;
                    case 1:
                        sink.Write("Failed password for invalid user "u8);
                        sink.Write(user);
                        sink.Write(" from "u8);
                        sink.Write(ip);
                        sink.Write(" port "u8);
                        sink.WriteUInt((ulong)_rng.Next(1024, 65536));
                        sink.Write(" ssh2"u8);
                        break;
                    case 2:
                        sink.Write("Failed password for "u8);
                        sink.Write(user);
                        sink.Write(" from "u8);
                        sink.Write(ip);
                        sink.Write(" port "u8);
                        sink.WriteUInt((ulong)_rng.Next(1024, 65536));
                        sink.Write(" ssh2"u8);
                        break;
                    case 3:
                        sink.Write("Connection closed by "u8);
                        sink.Write(ip);
                        sink.Write(" port "u8);
                        sink.WriteUInt((ulong)_rng.Next(1024, 65536));
                        sink.Write(" [preauth]"u8);
                        break;
                    default:
                        sink.Write("pam_unix(sshd:session): session opened for user "u8);
                        sink.Write(user);
                        sink.Write(" by (uid=0)"u8);
                        break;
                }

                break;
            case 1: // sudo
                sink.Write(user);
                sink.Write(" : TTY=pts/"u8);
                sink.WriteUInt((ulong)_rng.Next(0, 9));
                sink.Write(" ; PWD=/home/"u8);
                sink.Write(user);
                sink.Write(" ; USER=root ; COMMAND="u8);
                sink.Write(Commands[_rng.Next(Commands.Length)]);
                break;
            case 2: // kernel
                sink.WriteByte((byte)'[');
                sink.WriteUInt((ulong)_rng.Next(0, 999999));
                sink.WriteByte((byte)'.');
                sink.WritePadded(_rng.Next(1000000), 6);
                sink.Write(_rng.Chance(0.5) ? "] TCP: request_sock_TCP: Possible SYN flooding on port 443. Sending cookies."u8 : "] audit: type=1400 audit(0.0:1): apparmor=\"DENIED\" operation=\"open\" profile=\"/usr/sbin/cupsd\" name=\"/etc/shadow\" pid=1 comm=\"cupsd\""u8);
                break;
            case 3: // systemd
                sink.Write("Started Session "u8);
                sink.WriteUInt((ulong)_session++);
                sink.Write(" of user "u8);
                sink.Write(user);
                sink.WriteByte((byte)'.');
                break;
            case 4: // CRON
                sink.WriteByte((byte)'(');
                sink.Write(user);
                sink.Write(") CMD ("u8);
                sink.Write(Commands[_rng.Next(Commands.Length)]);
                sink.WriteByte((byte)')');
                break;
            case 5: // postfix/smtpd
                if (_rng.Chance(0.5))
                {
                    sink.Write("connect from unknown["u8);
                    sink.Write(ip);
                    sink.WriteByte((byte)']');
                }
                else
                {
                    sink.Write("NOQUEUE: reject: RCPT from unknown["u8);
                    sink.Write(ip);
                    sink.Write("]: 554 5.7.1 Service unavailable; Client host blocked using zen.spamhaus.org; from=<spam@example.net> to=<"u8);
                    sink.Write(user);
                    sink.Write("@example.com> proto=ESMTP helo=<mail.example.net>"u8);
                }

                break;
            case 6: // nginx (as syslog)
                sink.Write(ip);
                sink.Write(" - - \"GET "u8);
                sink.Write(Paths[_rng.Next(Paths.Length)]);
                sink.Write(" HTTP/1.1\" "u8);
                sink.Write(_rng.Chance(0.8) ? "200 "u8 : "404 "u8);
                sink.WriteUInt((ulong)_rng.Next(0, 50000));
                break;
            case 7: // dovecot
                sink.Write("imap-login: Login: user=<"u8);
                sink.Write(user);
                sink.Write(">, method=PLAIN, rip="u8);
                sink.Write(ip);
                sink.Write(", lip=10.0.0.5, TLS, session=<Zq7T3mN1kL9p>"u8);
                break;
            case 8: // systemd-logind
                sink.Write("New session "u8);
                sink.WriteUInt((ulong)_session);
                sink.Write(" of user "u8);
                sink.Write(user);
                sink.WriteByte((byte)'.');
                break;
            default: // audit
                sink.Write("type=USER_AUTH msg=audit("u8);
                sink.WriteUInt((ulong)new DateTimeOffset(_ts).ToUnixTimeSeconds());
                sink.Write(".123:4567): pid=1234 uid=0 auid=1000 ses=3 msg='op=PAM:authentication acct=\""u8);
                sink.Write(user);
                sink.Write("\" exe=\"/usr/sbin/sshd\" hostname="u8);
                sink.Write(ip);
                sink.Write(" addr="u8);
                sink.Write(ip);
                sink.Write(" terminal=ssh res=failed'"u8);
                break;
        }
    }
}
