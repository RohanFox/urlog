using System.Globalization;
using System.Text;

namespace Urlog.Sources.Tests.Detection;

/// <summary>
/// Writes <c>fixtures/detection/</c> and <c>fixtures/generic/</c>: every file, each group's <c>expected.tsv</c> and
/// <c>README.md</c>. Every line is invented (documentation address ranges, example.com names, made-up users) in
/// the shape real exports have. The output depends on this file and <see cref="FixtureContainers"/> only: its own
/// generator with a fixed seed, no clock, no culture. <c>FixtureWriterTests</c> requires the committed files to be
/// exactly what <see cref="Build"/> returns, so the way to change a fixture is to change this file and write again.
/// </summary>
internal static class FixtureWriter
{
    private const string Utf16Le = "utf16_little_endian";

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] Users = ["-", "-", "-", "alice", "bob", "carol", "deploy"];
    private static readonly string[] Paths = ["/", "/index.html", "/api/v1/users?id=7", "/login", "/static/app.js", "/wp-login.php", "/.env", "/favicon.ico", "/api/orders/42", "/search?q=caf%C3%A9"];
    private static readonly string[] Agents =
    [
        "Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0", "curl/8.11.0", "-",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36", "kube-probe/1.31",
    ];

    private static readonly string[] Referrers = ["-", "-", "https://example.com/", "https://shop.example.com/cart"];
    private static readonly string[] Hosts = ["web-01", "db-01", "mail-01", "fw-01", "app-02"];
    private static readonly string[] Levels = ["INFO", "WARN", "ERROR", "DEBUG"];
    private static readonly string[] Messages = ["request handled in {0} ms", "cache miss for key user:{0}", "connection pool size {0}", "retrying upstream call, attempt {0}", "worker {0} heartbeat"];
    private static readonly (string Name, bool Pid, int Pri)[] Programs =
    [
        ("sshd", true, 38), ("sudo", true, 85), ("kernel", false, 6), ("systemd", false, 30), ("CRON", true, 78), ("postfix/smtpd", true, 22), ("dovecot", true, 22),
    ];

    /// <summary>Every fixture file by its path under <c>fixtures/</c>, with forward slashes.</summary>
    public static SortedDictionary<string, byte[]> Build()
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var w = new Writer(new Rng(15));
        Group(files, "nginx", w.Nginx(),
            "Synthetic nginx and Apache access logs in the shapes real exports have: the built-in layouts, rotated and\ncompressed names, CRLF, a BOM, IPv6 clients, and a custom `log_format` no built-in fits (probable, layout unknown).");
        Group(files, "syslog", w.Syslog(),
            "Synthetic syslog files: RFC 3164 with and without a PRI, the rsyslog ISO form, RFC 5424 with structured data, a\nsequence-numbered network device, the usual file names and rotations, three compressions, CRLF, a BOM and UTF-16\nwith and without one.");
        Group(files, "generic", w.Generic(),
            "Synthetic application logs that no source claims: each timestamp shape the generic source reads, stack traces,\nlines with no timestamp at all, Latin-1, UTF-16, and the formats that get their own source later (JSON lines, CSV).\nAll of them must fall back to `generic`.");
        Group(files, "adversarial", w.Adversarial(),
            "Synthetic files built to mislead the detector: logs with artifact names, an artifact with a log name, mixed formats,\nbinary bytes inside a log, damaged and doubled compression, empty files, text that starts like a binary signature,\nand a UTF-16 Event Viewer XML export (generic until the Windows event sources exist). A gzip cut short decodes\nwithout an error up to the cut, so `truncated.log.gz` carries no flag: the detector cannot see the cut.");
        Group(files, "artifact", Writer.Artifacts(),
            "Stubs of files that are not logs: a signature plus padding (nothing executable or readable as the real format),\nsmall real zips for the zip-based documents, and text files that are artifacts by extension only.");
        Group(files, "journal", Writer.Unparsed(),
            "Header-only stubs of recognised log formats this build does not parse: the binary systemd journal and, until its\nsource exists, the Windows event log. Kind `unparsed`, each with a message.");
        Group(files, "rejected", Writer.Rejected(), "Header-only packet capture stubs. Intake refuses them with a message.");
        Group(files, "container", w.Containers(), "Small archives of synthetic logs. The detector reports the chain; intake expands them and detects each member.");
        foreach (var (name, bytes) in GenericSample.Build(w))
        {
            files["generic/" + name] = bytes;
        }

        return files;
    }

    private static void Group(SortedDictionary<string, byte[]> files, string name, List<Sample> samples, string about)
    {
        var table = new StringBuilder("file\tkind\tsource\tband\tlayout\tencoding\tcontainers\tformat\tflags\n");
        foreach (var s in samples)
        {
            files[$"detection/{name}/{s.File}"] = s.Bytes;
            table.Append(string.Join('\t', s.File, s.Kind, s.Source, s.Band, s.Layout, s.Encoding, s.Containers, s.Format, s.Flags)).Append('\n');
        }

        files[$"detection/{name}/expected.tsv"] = Encoding.UTF8.GetBytes(table.ToString());
        files[$"detection/{name}/README.md"] = Encoding.UTF8.GetBytes(
            $"# fixtures/detection/{name}\n\n{about}\n\n`expected.tsv` holds what the detector must report for each file. The files are written by\n`tests/Sources.Tests/Detection/FixtureWriter.cs`; change that file and write again, never a fixture by hand.\n");
    }

    internal sealed record Sample(string File, byte[] Bytes, string Kind, string Source, string Band, string Layout, string Encoding, string Containers, string Format, string Flags);

    /// <summary>SplitMix64: a few lines, no dependency on the runtime's own generator.</summary>
    internal sealed class Rng(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform in [min, max], both included.</summary>
        public int Int(int min, int max) => min + (int)(Next() % (ulong)(max - min + 1));

        public T Pick<T>(T[] values) => values[Int(0, values.Length - 1)];

        public string Thousandths() => "0." + Int(0, 999).ToString("000", CultureInfo.InvariantCulture);

        public void Shuffle<T>(List<T> list)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = Int(0, i);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        public byte[] Bytes(int count)
        {
            var bytes = new byte[count];
            for (var i = 0; i < count; i++)
            {
                bytes[i] = (byte)Next();
            }

            return bytes;
        }
    }

    internal sealed class Writer(Rng r)
    {
        private static readonly byte[] Pdf = Latin1("%PDF-1.7\n%âãÏÓ\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

        private DateTime _clock = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        public Rng Random => r;

        public List<string> AppLines(Func<DateTime, string> prefix, string format, int count = 20)
        {
            Reset();
            var lines = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var t = Tick();
                var message = string.Format(CultureInfo.InvariantCulture, r.Pick(Messages), r.Int(1, 900));
                lines.Add(prefix(t) + string.Format(CultureInfo.InvariantCulture, format, r.Pick(Levels), message));
            }

            return lines;
        }

        public List<string> SyslogLines(int count, bool pri = false, int firstProgram = 0, int programs = 0)
        {
            Reset();
            var pool = Programs.AsSpan(firstProgram, programs == 0 ? Programs.Length - firstProgram : programs).ToArray();
            var lines = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var t = Tick();
                var (program, pid, p) = r.Pick(pool);
                var tag = program + (pid ? $"[{r.Int(100, 65535)}]" : string.Empty);
                lines.Add($"{(pri ? $"<{p}>" : string.Empty)}{Bsd(t)} {r.Pick(Hosts)} {tag}: {SyslogMessage(program)}");
            }

            return lines;
        }

        public List<Sample> Nginx()
        {
            var big = Text(Lines(520, Combined));
            if (big.Length <= 70_000)
            {
                throw new InvalidOperationException("the large nginx sample must exceed the 64 KB head");
            }

            Sample N(string file, byte[] bytes, string layout = "combined", string band = "confident", string containers = "-") =>
                new(file, bytes, "log", "nginx", band, layout, "utf8", containers, "-", "-");

            return
            [
                N("access.log", Text(Lines(20, Combined))),
                N("access-crlf.log", Text(Lines(20, Combined), "\r\n")),
                N("access-bom.log", Text(Lines(20, Combined), bom: true)),
                N("access-ipv6.log", Text(Lines(20, () => Combined(ip: Ip6())))),
                N("access-auth-users.log", Text(Lines(20, () => Combined(user: r.Pick(["alice", "bob", "carol"]))))),
                N("access-long-user-agent.log", Text(Lines(20, () => Combined(agent: "Mozilla/5.0 " + string.Concat(Enumerable.Repeat("(compatible; padding) ", 60)))))),
                N("access-quoted-request.log", Text(Lines(20, () => Combined(agent: "agent \\\"quoted\\\" name")))),
                N("access-one-line.log", Text(Lines(1, Combined))),
                N("access-no-final-newline.log", Text(Lines(20, Combined))[..^1]),
                N("access-larger-than-head.log", big),
                N("access.log.3", Text(Lines(20, Combined))),
                N("access.log-20260301", Text(Lines(20, Combined))),
                N("access", Text(Lines(20, Combined))),
                N("access.log.gz", FixtureContainers.Gzip(Text(Lines(20, Combined))), containers: "gzip"),
                N("access.log.2.gz", FixtureContainers.Gzip(Text(Lines(20, Combined))), containers: "gzip"),
                N("access.log.bz2", FixtureContainers.Bzip2(Text(Lines(20, Combined))), containers: "bzip2"),
                N("access.log.xz", FixtureContainers.Xz(Text(Lines(20, Combined))), containers: "xz"),
                N("apache-common.log", Text(Lines(20, Common)), "common"),
                N("apache-common.log.gz", FixtureContainers.Gzip(Text(Lines(20, Common))), "common", containers: "gzip"),
                N("ingress-nginx.log", Text(Lines(20, Ingress)), "ingress-nginx"),
                N("ingress-nginx-crlf.log", Text(Lines(20, Ingress), "\r\n"), "ingress-nginx"),
                N("proxy-custom-format.log", Text(Lines(20, Custom)), "unknown", "probable"),
                N("proxy-custom-format.log.gz", FixtureContainers.Gzip(Text(Lines(20, Custom))), "unknown", "probable", "gzip"),
            ];
        }

        public List<Sample> Syslog()
        {
            var big = Text(SyslogLines(800));
            if (big.Length <= 70_000)
            {
                throw new InvalidOperationException("the large syslog sample must exceed the 64 KB head");
            }

            static Sample S(string file, byte[] bytes, string encoding = "utf8", string containers = "-") =>
                new(file, bytes, "log", "syslog", "confident", "-", encoding, containers, "-", "-");

            return
            [
                S("syslog", Text(SyslogLines(24))),
                S("syslog.1", Text(SyslogLines(24))),
                S("syslog.2.gz", FixtureContainers.Gzip(Text(SyslogLines(24))), containers: "gzip"),
                S("syslog.3.bz2", FixtureContainers.Bzip2(Text(SyslogLines(24))), containers: "bzip2"),
                S("syslog.4.xz", FixtureContainers.Xz(Text(SyslogLines(24))), containers: "xz"),
                S("messages", Text(SyslogLines(24))),
                S("messages-20260301", Text(SyslogLines(24))),
                S("messages-larger-than-head", big),
                S("auth.log", Text(SyslogLines(24, firstProgram: 0, programs: 2))),
                S("secure", Text(SyslogLines(24, firstProgram: 0, programs: 2))),
                S("kern.log", Text(SyslogLines(24, firstProgram: 2, programs: 1))),
                S("mail.log", Text(SyslogLines(24, firstProgram: 5))),
                S("cron.log", Text(SyslogLines(24, firstProgram: 4, programs: 1))),
                S("remote-with-pri.log", Text(SyslogLines(24, pri: true))),
                S("rsyslog-iso.log", Text(Lines(24, SyslogIso))),
                S("rfc5424.log", Text(Lines(24, () => Syslog5424(false)))),
                S("rfc5424-structured-data.log", Text(Lines(24, () => Syslog5424(true)))),
                S("cisco-sequence.log", Text(Lines(40, Cisco))),
                S("syslog-crlf.log", Text(SyslogLines(24), "\r\n")),
                S("syslog-bom.log", Text(SyslogLines(24), bom: true)),
                S("syslog-utf16le.log", [0xFF, 0xFE, .. Text(SyslogLines(24), encoding: Encoding.Unicode)], Utf16Le),
                S("syslog-utf16be.log", [0xFE, 0xFF, .. Text(SyslogLines(24), encoding: Encoding.BigEndianUnicode)], "utf16_big_endian"),
                S("syslog-utf16le-no-bom.log", Text(SyslogLines(24), encoding: Encoding.Unicode), Utf16Le),
            ];
        }

        public List<Sample> Generic()
        {
            static Sample G(string file, byte[] bytes, string encoding = "utf8", string containers = "-") =>
                new(file, bytes, "log", "generic", "fallback", "-", encoding, containers, "-", "-");

            Reset();
            var trace = new List<string>();
            for (var i = 0; i < 8; i++)
            {
                trace.Add($"{Iso(Tick(), ' ', 3, string.Empty)} ERROR [http-nio-8080-exec-{i}] c.e.web.Handler - request failed");
                trace.Add($"java.lang.IllegalStateException: order {r.Int(1, 999)} has no customer");
                trace.Add($"\tat com.example.shop.OrderService.load(OrderService.java:{r.Int(10, 300)})");
                trace.Add($"\tat com.example.web.Handler.handle(Handler.java:{r.Int(10, 300)})");
                trace.Add("Caused by: java.sql.SQLException: connection reset");
                trace.Add("\t... 12 more");
            }

            var traceback = new List<string>();
            for (var i = 0; i < 8; i++)
            {
                var t = Tick();
                traceback.Add($"{Iso(t, ' ', 0, string.Empty)},{t.Millisecond:000} ERROR worker: job {i} failed");
                traceback.Add("Traceback (most recent call last):");
                traceback.Add($"  File \"/srv/app/worker.py\", line {r.Int(10, 300)}, in run");
                traceback.Add("    handle(job)");
                traceback.Add("ValueError: invalid literal for int() with base 10: 'x'");
            }

            string Plain(DateTime t) => Iso(t, ' ', 3, string.Empty);
            return
            [
                G("app-iso-space.log", Text(AppLines(Plain, " {0}  [main] com.example.App - {1}"))),
                G("app-iso-comma-millis.log", Text(AppLines(t => $"{Iso(t, ' ', 0, string.Empty)},{t.Millisecond:000}", " {0} {1}"))),
                G("app-iso-utc.log", Text(AppLines(t => Iso(t, 'T', 3, "Z"), " level={0} msg=\"{1}\""))),
                G("app-iso-offset.log", Text(AppLines(t => Iso(t, 'T', 3, "+02:00"), " [{0}] {1}"))),
                G("app-bracketed.log", Text(AppLines(t => $"[{Plain(t)}]", " [pid 4242] {0} {1}"))),
                G("app-slash-date.log", Text(AppLines(t => t.ToString("yyyy'/'MM'/'dd HH':'mm':'ss", CultureInfo.InvariantCulture), " [{0}] 77#0: {1}"))),
                G("app-bsd-bracketed.log", Text(AppLines(t => $"[{Bsd(t)}]", " {0} worker: {1}"))),
                G("app-clf-time.log", Text(AppLines(t => $"backend [{Clf(t)}]", " {0} {1}"))),
                G("app-epoch-seconds.log", Text(AppLines(t => Epoch(t).ToString(CultureInfo.InvariantCulture), " {0} {1}"))),
                G("app-epoch-millis.log", Text(AppLines(t => $"{Epoch(t)}{t.Millisecond:000}", " {0} {1}"))),
                G("app-epoch-bracketed.log", Text(AppLines(t => $"[{Epoch(t)}.{t.Millisecond:000}]", " {0} {1}"))),
                G("app-logfmt.log", Text(AppLines(t => $"ts={Epoch(t)}", " level={0} msg=\"{1}\""))),
                G("app-logfmt-quoted-time.log", Text(AppLines(t => $"time=\"{Iso(t, 'T', 3, "Z")}\"", " level={0} msg=\"{1}\""))),
                G("app-json-lines.log", Text(AppLines(t => "{\"time\":\"" + Iso(t, 'T', 3, "Z") + "\"", ",\"level\":\"{0}\",\"msg\":\"{1}\"}}"))),
                G("app-csv.csv", Text(["time,level,message", .. AppLines(t => Iso(t, 'T', 3, "Z"), ",{0},{1}")])),
                G("app-java-stack-traces.log", Text(trace)),
                G("app-python-tracebacks.log", Text(traceback)),
                G("app-php-errors.log", Text(AppLines(t => $"[{t.Day:00}-{Months[t.Month - 1]}-{t.Year} {t:HH':'mm':'ss} UTC]", " PHP {0}:  {1} in /srv/www/index.php on line 12"))),
                G("app-no-timestamps.txt", Text(["service started", "listening on 0.0.0.0:8080", "loaded 14 routes", .. Enumerable.Range(0, 30).Select(i => $"handled request {i}")])),
                G("app-latin1.log", Text(AppLines(Plain, " {0} création du dépôt: {1}"), encoding: Encoding.Latin1)),
                G("app-crlf.log", Text(AppLines(Plain, " {0} {1}"), "\r\n")),
                G("app-utf16le.log", [0xFF, 0xFE, .. Text(AppLines(Plain, " {0} {1}"), encoding: Encoding.Unicode)], Utf16Le),
                G("app.log.gz", FixtureContainers.Gzip(Text(AppLines(Plain, " {0} {1}"))), containers: "gzip"),
                G("app.log.1", Text(AppLines(Plain, " {0} {1}"))),
            ];
        }

        public List<Sample> Adversarial()
        {
            static Sample Log(string file, byte[] bytes, string source, string band = "confident", string layout = "-", string encoding = "utf8", string containers = "-", string flags = "-") =>
                new(file, bytes, "log", source, band, layout, encoding, containers, "-", flags);
            static Sample Art(string file, byte[] bytes, string format, string containers = "-", string flags = "-") =>
                new(file, bytes, "artifact", "-", "none", "-", "utf8", containers, format, flags);

            Reset();
            var jsonAgent = Enumerable.Range(0, 20).Select(i => Combined(agent: "{\\\"client\\\":\\\"scanner\\\",\\\"v\\\":" + i.ToString(CultureInfo.InvariantCulture) + ",\\\"tags\\\":[\\\"a\\\",\\\"b\\\"]}")).ToList();
            List<string> mostlyNginx = [.. Lines(28, Combined), .. SyslogLines(12)];
            r.Shuffle(mostlyNginx);
            List<string> mostlySyslog = [.. SyslogLines(28), .. Lines(12, Combined)];
            r.Shuffle(mostlySyslog);

            Reset();
            var xml = new List<string> { "<?xml version=\"1.0\" encoding=\"utf-16\" standalone=\"yes\"?>", "<Events>" };
            for (var i = 0; i < 12; i++)
            {
                xml.Add("<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Security-Auditing'/>"
                    + $"<EventID>4624</EventID><TimeCreated SystemTime='{Iso(Tick(), 'T', 6, "Z")}'/><Computer>ws-{i:00}.corp.example.com</Computer></System>"
                    + $"<EventData><Data Name='TargetUserName'>alice</Data><Data Name='IpAddress'>192.0.2.{i + 1}</Data></EventData></Event>");
            }

            xml.Add("</Events>");
            var middle = SyslogLines(60);
            byte[] nulBlock = [.. Text(middle[..30]), .. new byte[4096], (byte)'\n', .. Text(middle[30..])];
            byte[] nulPadding = [.. Text(SyslogLines(60)), .. new byte[3000]];
            var whole = FixtureContainers.Gzip(Text(SyslogLines(400)));
            var noise = r.Bytes(600);
            string Plain(DateTime t) => Iso(t, ' ', 3, string.Empty);

            return
            [
                Log("nginx-json-user-agent.log", Text(jsonAgent), "nginx", layout: "combined"),
                Log("syslog-without-pri.log", Text(SyslogLines(24)), "syslog"),
                Log("event-viewer-export-utf16.xml", [0xFF, 0xFE, .. Text(xml, "\r\n", encoding: Encoding.Unicode)], "generic", "fallback", encoding: Utf16Le),
                Log("mixed-mostly-nginx.log", Text(mostlyNginx), "nginx", "probable", "combined"),
                Log("mixed-mostly-syslog.log", Text(mostlySyslog), "syslog", "probable"),
                Art("mixed-mostly-nginx.cfg", Text(mostlyNginx), "cfg"),
                Log("access-log-named.php", Text(Lines(20, Combined)), "nginx", layout: "combined", flags: "extension_mismatch"),
                Log("access-log-named.pdf", Text(Lines(20, Combined)), "nginx", layout: "combined", flags: "extension_mismatch"),
                Art("report-named.log", Pdf, "pdf"),
                Log("syslog-nul-block-in-the-middle.log", nulBlock, "syslog", flags: "binary_bytes"),
                Log("syslog-nul-padding-at-the-end.log", nulPadding, "syslog", flags: "binary_bytes"),
                Log("text-starting-with-newlines.log", [.. "\n\r\r\n"u8, .. Text(AppLines(Plain, " {0} {1}"))], "generic", "fallback"),
                Log("text-starting-with-mz.log", Text(["MZ-7 unit started", "MZ-7 unit calibrated", .. AppLines(Plain, " {0} MZ-7 {1}")]), "generic", "fallback"),
                Log("truncated.log.gz", whole[..(whole.Length / 2)], "syslog", containers: "gzip"),
                Art("header-only.gz", FixtureContainers.Gzip(new byte[100])[..10], "empty", "gzip"),
                Art("damaged.log.bz2", [.. "BZh9"u8, .. noise.AsSpan(0, 300)], "damaged-bzip2", "bzip2", "decode_error"),
                new("double.log.gz.gz", FixtureContainers.Gzip(FixtureContainers.Gzip(Text(SyslogLines(24)))), "container", "-", "none", "-", "utf8", "gzip>gzip", "-", "-"),
                Art("empty.log", [], "empty"),
                Art("empty.log.gz", FixtureContainers.Gzip([]), "empty", "gzip"),
                Art("settings.ini", "[server]\nport = 8080\nworkers = 4\n\n[log]\nlevel = info\n"u8.ToArray(), "ini"),
                Log("notes-without-extension", "todo: rotate the keys\nask ops about the proxy\n"u8.ToArray(), "generic", "fallback"),
                Art("random-bytes.dat", noise, "unknown-binary"),
                Art("startup-script", "#!/bin/sh\nset -e\nexec /usr/local/bin/app --config /etc/app.conf\n"u8.ToArray(), "script"),
                Art("index.php", "<?php\necho 'hello';\n"u8.ToArray(), "php"),
                Art("page-without-extension", "\n<!DOCTYPE html>\n<html><head><title>status</title></head><body>ok</body></html>\n"u8.ToArray(), "html"),
            ];
        }

        public static List<Sample> Artifacts()
        {
            static Sample A(string file, byte[] bytes, string format, string containers = "-") =>
                new(file, bytes, "artifact", "-", "none", "-", "utf8", containers, format, "-");
            var pem = Encoding.ASCII.GetBytes("-----BEGIN CERTIFICATE-----\n" + string.Concat(Enumerable.Repeat(new string('A', 64) + "\n", 4)) + "-----END CERTIFICATE-----\n");
            byte[] mz = [(byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF];

            return
            [
                A("document.pdf", Pdf, "pdf"),
                A("letter.doc", Pad(Convert.FromHexString("D0CF11E0A1B11AE1")), "ole2"),
                A("memo.rtf", "{\\rtf1\\ansi synthetic memo}\n"u8.ToArray(), "rtf"),
                A("page.ps", "%!PS-Adobe-3.0\nshowpage\n"u8.ToArray(), "postscript"),
                A("image.png", Pad(Convert.FromHexString("89504E470D0A1A0A0000000D49484452")), "png"),
                A("photo.jpg", Pad(Convert.FromHexString("FFD8FFE000104A464946")), "jpeg"),
                A("anim.gif", Pad([.. "GIF89a"u8, 1, 0, 1, 0]), "gif"),
                A("scan.tif", Pad([.. "II*"u8, 0, 8, 0, 0, 0]), "tiff"),
                A("bitmap.bmp", Pad([.. "BM"u8, 0x36, 0, 0, 0]), "bmp"),
                A("icon.ico", Pad([0, 0, 1, 0, 1, 0]), "ico"),
                A("picture.webp", Pad([.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBPVP8 "u8]), "webp"),
                A("tool.exe", Pad(mz), "pe"),
                A("renamed-tool.log", Pad(mz), "pe"),
                A("daemon", Pad([0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]), "elf"),
                A("app-macho", Pad(Convert.FromHexString("CFFAEDFE07000001")), "macho"),
                A("Main.class", Pad(Convert.FromHexString("CAFEBABE00000041")), "java-class-or-macho"),
                A("shortcut.lnk", Pad(Convert.FromHexString("4C0000000114020000000000C000000000000046")), "lnk"),
                A("history.sqlite", Pad([.. "SQLite format 3\0"u8, 0x10, 0, 1, 1]), "sqlite"),
                A("ntds.dit", Pad([0x12, 0x34, 0x56, 0x78, 0xEF, 0xCD, 0xAB, 0x89]), "ese"),
                A("SYSTEM.hiv", Pad([.. "regf"u8, 1, 0, 0, 0]), "registry-hive"),
                A("crash.dmp", Pad([.. "MDMP"u8, 0x93, 0xA7, 0, 0]), "minidump"),
                A("server.crt", pem, "pem"),
                A("server.cer", Pad([0x30, 0x82, 0x03, 0x0A, 0x30, 0x82, 0x01, 0xF2]), "der"),
                A("vault.kdbx", Pad(Convert.FromHexString("03D9A29A67FB4BB5")), "keepass"),
                A("clip.mp4", Pad([0, 0, 0, 0x18, .. "ftypmp42"u8]), "mp4"),
                A("sound.ogg", Pad([.. "OggS"u8, 0, 2, 0, 0]), "ogg"),
                A("disk.vmdk", Pad([.. "KDMV"u8, 1, 0, 0, 0]), "vmdk"),
                A("font.ttf", Pad([0, 1, 0, 0, 0, 0x0C, 0, 0x80]), "truetype"),
                A("bundle.rar", Pad([.. "Rar!"u8, 0x1A, 0x07, 1, 0]), "rar"),
                A("bundle.7z", Pad(Convert.FromHexString("377ABCAF271C0004")), "7z"),
                A("bundle.cab", Pad([.. "MSCF"u8, 0, 0, 0, 0]), "cab"),
                A("report.docx", FixtureContainers.Zip(("[Content_Types].xml", "<Types/>"u8.ToArray()), ("word/document.xml", "<w:document/>"u8.ToArray())), "ooxml"),
                A("sheet.ods", FixtureContainers.Zip(("mimetype", "application/vnd.oasis.opendocument.spreadsheet"u8.ToArray()), ("content.xml", "<office/>"u8.ToArray())), "opendocument"),
                A("lib.jar", FixtureContainers.Zip(("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n"u8.ToArray()), ("Main.class", [0xCA, 0xFE, 0xBA, 0xBE])), "jar"),
                A("site.css", "body { margin: 0; }\n"u8.ToArray(), "css"),
                A("app.js", "console.log('synthetic');\n"u8.ToArray(), "js"),
                A("config.yaml", "server:\n  port: 8080\n"u8.ToArray(), "yaml"),
                A("deploy.ps1", "Write-Output 'synthetic'\n"u8.ToArray(), "ps1"),
                A("cache.db", Pad([1, 2, 3, 4, .. " no known signature"u8]), "db"),
                A("document.pdf.gz", FixtureContainers.Gzip(Pdf), "pdf", "gzip"),
            ];
        }

        public static List<Sample> Unparsed()
        {
            static Sample U(string file, byte[] bytes, string format, string containers = "-") =>
                new(file, bytes, "unparsed", "-", "none", "-", "utf8", containers, format, "-");
            byte[] journal = [.. "LPKSHHRH"u8, 0, 0, 0, 0, 1, 0, 0, 0];
            return
            [
                U("system.journal", Pad(journal), "journal-binary"),
                U("user-1000.journal.gz", FixtureContainers.Gzip(Pad(journal)), "journal-binary", "gzip"),
                U("Security.evtx", Pad([.. "ElfFile\0"u8, 0, 0, 0, 0]), "evtx"),
            ];
        }

        public static List<Sample> Rejected()
        {
            static Sample X(string file, string hex, string format) =>
                new(file, Pad(Convert.FromHexString(hex), 64), "rejected", "-", "none", "-", "utf8", "-", format, "-");
            return
            [
                X("capture.pcap", "D4C3B2A1020004000000000000000000FFFF000001000000", "pcap"),
                X("capture-big-endian.pcap", "A1B2C3D4000200040000000000000000", "pcap"),
                X("capture-nanosecond.pcap", "4D3CB2A1020004000000000000000000", "pcap"),
                X("capture.pcapng", "0A0D0D0A1C0000004D3C2B1A01000000FFFFFFFFFFFFFFFF1C000000", "pcapng"),
            ];
        }

        public List<Sample> Containers()
        {
            static Sample C(string file, byte[] bytes, string chain) => new(file, bytes, "container", "-", "none", "-", "utf8", chain, "-", "-");
            (string, byte[])[] members = [("web-01/nginx/access.log", Text(Lines(20, Combined))), ("web-01/syslog", Text(SyslogLines(20)))];
            return
            [
                C("logs.zip", FixtureContainers.Zip(members), "zip"),
                C("empty.zip", FixtureContainers.Zip(), "zip"),
                C("logs.tar", FixtureContainers.Tar(members), "tar"),
                C("logs.tar.gz", FixtureContainers.Gzip(FixtureContainers.Tar(members)), "gzip>tar"),
                C("logs.tgz", FixtureContainers.Gzip(FixtureContainers.Tar(members)), "gzip>tar"),
                C("logs.zip.gz", FixtureContainers.Gzip(FixtureContainers.Zip(members)), "gzip>zip"),
            ];
        }

        public static byte[] Text(IEnumerable<string> lines, string eol = "\n", bool bom = false, Encoding? encoding = null)
        {
            var body = (encoding ?? new UTF8Encoding(false)).GetBytes(string.Concat(lines.Select(l => l + eol)));
            return bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
        }

        public static string Iso(DateTime t, char separator, int fraction, string zone)
        {
            var text = t.ToString($"yyyy'-'MM'-'dd'{separator}'HH':'mm':'ss", CultureInfo.InvariantCulture);
            if (fraction > 0)
            {
                text += "." + (t.Millisecond * 1000).ToString("000000", CultureInfo.InvariantCulture)[..fraction];
            }

            return text + zone;
        }

        private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

        private static byte[] Pad(byte[] head, int length = 256)
        {
            var bytes = new byte[length];
            head.CopyTo(bytes, 0);
            return bytes;
        }

        private static string Clf(DateTime t) => $"{t.Day:00}/{Months[t.Month - 1]}/{t.Year:0000}:{t:HH':'mm':'ss} +0000";

        private static string Bsd(DateTime t) => $"{Months[t.Month - 1]} {t.Day,2} {t:HH':'mm':'ss}";

        private static long Epoch(DateTime t) => new DateTimeOffset(t).ToUnixTimeSeconds();

        private void Reset() => _clock = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        private DateTime Tick() => _clock = _clock.AddSeconds(r.Int(0, 4)).AddMilliseconds(r.Int(0, 999));

        private List<string> Lines(int count, Func<string> line)
        {
            Reset();
            var lines = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                lines.Add(line());
            }

            return lines;
        }

        private string Ip() => r.Pick(["192.0.2.", "198.51.100.", "203.0.113."]) + r.Int(1, 60).ToString(CultureInfo.InvariantCulture);

        private string Ip6() => "2001:db8::" + r.Int(1, 4000).ToString("x", CultureInfo.InvariantCulture);

        private string Combined() => Combined(null);

        private string Combined(string? ip = null, string? agent = null, string? user = null) =>
            $"{ip ?? Ip()} - {user ?? r.Pick(Users)} [{Clf(Tick())}] \"{r.Pick(["GET", "GET", "POST", "HEAD"])} {r.Pick(Paths)} HTTP/1.1\" "
            + $"{r.Pick([200, 200, 200, 301, 302, 403, 404, 500, 502])} {r.Int(0, 50000)} \"{r.Pick(Referrers)}\" \"{agent ?? r.Pick(Agents)}\"";

        private string Common() =>
            $"{Ip()} - {r.Pick(Users)} [{Clf(Tick())}] \"{r.Pick(["GET", "POST"])} {r.Pick(Paths)} HTTP/1.0\" {r.Pick([200, 304, 404])} {r.Int(0, 9000)}";

        private string Ingress() =>
            $"{Ip()} - - [{Clf(Tick())}] \"GET {r.Pick(Paths)} HTTP/1.1\" {r.Pick([200, 200, 404, 502])} {r.Int(0, 5000)} \"-\" \"{r.Pick(Agents)}\" {r.Int(60, 2000)} {r.Thousandths()} "
            + $"[default-web-80] [] 10.244.1.{r.Int(2, 200)}:8080 {r.Int(0, 5000)} {r.Thousandths()} 200 {r.Next():x16}{r.Next():x16}";

        private string Custom() =>
            $"{Ip()} US - {r.Pick(Users)} [{Clf(Tick())}] \"GET {r.Pick(Paths)} HTTP/2.0\" 200 {r.Int(0, 9000)} \"{r.Pick(Referrers)}\" \"{r.Pick(Agents)}\" \"10.0.0.1, 10.0.0.2\" \"www.example.com\" "
            + $"sn=\"example.com\" rt={r.Thousandths()} ua=\"10.0.1.5:8080\" us=\"200\" ut=\"{r.Thousandths()}\" ul=\"{r.Int(0, 9000)}\" cs=MISS";

        private string SyslogMessage(string program)
        {
            var ip = Ip();
            var user = r.Pick(["root", "admin", "alice", "bob", "deploy", "oracle"]);
            return program switch
            {
                "sshd" => r.Pick(
                [
                    $"Failed password for invalid user {user} from {ip} port {r.Int(1024, 65535)} ssh2",
                    $"Accepted publickey for {user} from {ip} port {r.Int(1024, 65535)} ssh2",
                    $"Connection closed by {ip} port {r.Int(1024, 65535)} [preauth]",
                ]),
                "sudo" => $"{user} : TTY=pts/0 ; PWD=/home/{user} ; USER=root ; COMMAND=/usr/bin/systemctl restart nginx",
                "kernel" => $"[{r.Int(0, 99999)}.{r.Int(0, 999999):000000}] TCP: request_sock_TCP: Possible SYN flooding on port 443. Sending cookies.",
                "systemd" => $"Started Session {r.Int(1, 999)} of user {user}.",
                "CRON" => $"({user}) CMD (/usr/local/bin/backup.sh)",
                "postfix/smtpd" => $"connect from unknown[{ip}]",
                _ => $"imap-login: Login: user=<{user}>, method=PLAIN, rip={ip}, lip=10.0.0.5, TLS",
            };
        }

        private string SyslogIso()
        {
            var (program, pid, _) = r.Pick(Programs);
            return $"{Iso(Tick(), 'T', 6, "+02:00")} {r.Pick(Hosts)} {program}{(pid ? $"[{r.Int(100, 65535)}]" : string.Empty)}: {SyslogMessage(program)}";
        }

        private string Syslog5424(bool structuredData)
        {
            var (program, pid, pri) = r.Pick(Programs);
            var data = structuredData ? $"[meta sequenceId=\"{r.Int(1, 9999)}\"][origin ip=\"{Ip()}\"]" : "-";
            return $"<{pri}>1 {Iso(Tick(), 'T', 3, "Z")} {r.Pick(Hosts)} {program.Replace('/', '-')} {(pid ? r.Int(100, 65535).ToString(CultureInfo.InvariantCulture) : "-")} ID47 {data} {SyslogMessage(program)}";
        }

        private string Cisco()
        {
            var t = Tick();
            return $"<189>{r.Int(100, 9999)}: *{Bsd(t)}.{t.Millisecond:000}: %LINK-3-UPDOWN: Interface GigabitEthernet0/{r.Int(0, 24)}, changed state to {r.Pick(["up", "down"])}";
        }
    }
}
