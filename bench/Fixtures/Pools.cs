using System.Text;

namespace Urlog.Bench.Fixtures;

/// <summary>Pre-rendered value pools shared by the generators. Built once per generator from its own RNG stream.</summary>
public static class Pools
{
    public static readonly byte[][] Months =
    [
        "Jan"u8.ToArray(), "Feb"u8.ToArray(), "Mar"u8.ToArray(), "Apr"u8.ToArray(), "May"u8.ToArray(), "Jun"u8.ToArray(),
        "Jul"u8.ToArray(), "Aug"u8.ToArray(), "Sep"u8.ToArray(), "Oct"u8.ToArray(), "Nov"u8.ToArray(), "Dec"u8.ToArray(),
    ];

    public static readonly string[] Users =
    [
        "root", "admin", "deploy", "www-data", "postgres", "backup", "jsmith", "mgarcia", "akumar", "lchen",
        "operator", "svc-monitor", "git", "jenkins", "ansible", "oracle", "ubuntu", "ec2-user", "test", "guest",
    ];

    public static readonly string[] Hosts =
    [
        "web-01", "web-02", "web-03", "api-01", "api-02", "db-01", "db-02", "mail-01", "mail-02", "proxy-01",
        "bastion", "vpn-01", "dc-01", "files-01", "ci-01", "monitor-01", "cache-01", "cache-02", "lb-01", "lb-02",
        "k8s-node-1", "k8s-node-2", "backup-01", "legacy-app",
    ];

    public static readonly string[] Commands =
    [
        "systemctl restart nginx", "apt-get update", "cat /etc/shadow", "vim /etc/ssh/sshd_config", "docker ps",
        "journalctl -u postfix", "tail -f /var/log/auth.log", "useradd -m attacker", "chmod 4755 /tmp/x",
        "curl http://169.254.169.254/latest/meta-data/", "python3 -c import pty;pty.spawn(\"/bin/bash\")",
    ];

    public static readonly string[] PathSegments =
    [
        "api", "v1", "v2", "users", "login", "logout", "static", "assets", "img", "css", "js", "admin", "wp-admin",
        "wp-login.php", "index.html", "index.php", "search", "products", "cart", "checkout", "orders", "health",
        "metrics", "robots.txt", "favicon.ico", "sitemap.xml", ".env", ".git", "config", "upload", "download",
        "report", "export", "dashboard", "profile", "settings", "docs", "blog", "2026", "03", "page",
    ];

    public static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148",
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36",
        "curl/8.11.0",
        "python-requests/2.32.3",
        "Go-http-client/2.0",
        "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
        "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",
        "sqlmap/1.8.9#stable (https://sqlmap.org)",
        "Nikto/2.5.0",
        "masscan/1.3",
        "Mozilla/5.0 zgrab/0.x",
        "okhttp/4.12.0",
        "Wget/1.21.4",
        "PostmanRuntime/7.43.0",
        "-",
        "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1)",
        "Apache-HttpClient/4.5.14 (Java/17.0.12)",
    ];

    public static readonly string[] Referers =
    [
        "-", "-", "-", "-", "-", "-", "https://www.google.com/", "https://example.com/", "https://example.com/products",
        "https://example.com/login", "https://duckduckgo.com/", "https://t.co/x9Yz", "http://example.com/index.php?id=1",
        "https://example.com/search?q=shell", "android-app://com.google.android.gm/",
    ];

    public static byte[][] Render(IEnumerable<string> values) => values.Select(v => Encoding.UTF8.GetBytes(v)).ToArray();

    /// <summary>Builds a pool of IPv4 addresses as ASCII bytes.</summary>
    public static byte[][] Ipv4Pool(Xoshiro256 rng, int count)
    {
        var pool = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            // Mix of private ranges (internal hosts) and public-looking addresses.
            var kind = rng.Next(10);
            var a = kind switch
            {
                0 => 10,
                1 => 192,
                2 => 172,
                _ => rng.Next(1, 224),
            };
            var b = kind == 1 ? 168 : kind == 2 ? rng.Next(16, 32) : rng.Next(0, 256);
            pool[i] = Encoding.ASCII.GetBytes($"{a}.{b}.{rng.Next(0, 256)}.{rng.Next(1, 255)}");
        }

        return pool;
    }

    /// <summary>Builds a pool of IPv6 addresses as ASCII bytes (compressed and full forms).</summary>
    public static byte[][] Ipv6Pool(Xoshiro256 rng, int count)
    {
        var pool = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var s = rng.Next(4) switch
            {
                0 => $"2001:db8:{rng.Next(0x10000):x}:{rng.Next(0x10000):x}::{rng.Next(0x10000):x}",
                1 => $"fe80::{rng.Next(0x10000):x}:{rng.Next(0x10000):x}:{rng.Next(0x10000):x}:{rng.Next(0x10000):x}",
                2 => $"::ffff:{rng.Next(1, 224)}.{rng.Next(256)}.{rng.Next(256)}.{rng.Next(1, 255)}",
                _ => $"2a02:{rng.Next(0x10000):x}:{rng.Next(0x10000):x}:{rng.Next(0x10000):x}:{rng.Next(0x10000):x}:{rng.Next(0x10000):x}:{rng.Next(0x10000):x}:{rng.Next(0x10000):x}",
            };
            pool[i] = Encoding.ASCII.GetBytes(s);
        }

        return pool;
    }

    /// <summary>Builds URL paths from segments, with the occasional file extension.</summary>
    public static byte[][] PathPool(Xoshiro256 rng, int count)
    {
        var pool = new byte[count][];
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.Clear();
            var segments = rng.Next(1, 5);
            for (var s = 0; s < segments; s++)
            {
                sb.Append('/').Append(PathSegments[rng.Next(PathSegments.Length)]);
            }

            if (rng.Chance(0.3))
            {
                sb.Append('/').Append(rng.Next(1, 100000));
            }

            pool[i] = Encoding.ASCII.GetBytes(sb.ToString());
        }

        return pool;
    }
}
