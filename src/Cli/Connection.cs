using System.Globalization;
using ClickHouse.Driver.Tcp;
using Urlog.Core.Ingest;
using Urlog.Query;

namespace Urlog.Cli;

/// <summary>
/// Where ClickHouse is and who connects. Three roles, three accounts (design record "Security"):
/// <list type="bullet">
/// <item><b>ingest</b>, <c>urlog_ingest</c>: the insert data plane and the write path's control plane (lightweight
/// delete, generation commit, partition drop). <c>--user</c>/<c>--password</c>, else <c>URLOG_CH_USER</c>/<c>URLOG_CH_PASSWORD</c>.</item>
/// <item><b>query</b>, <c>urlog_query</c>: every query, read-only, over HTTP (ADR 0003).
/// <c>--query-user</c>/<c>--query-password</c>, else <c>URLOG_CH_QUERY_USER</c>/<c>URLOG_CH_QUERY_PASSWORD</c>.</item>
/// <item><b>admin</b>: <c>urlog migrate</c> and <c>bench query --cold</c> only; the definer of <c>events_v</c>.
/// <c>--admin-user</c>/<c>--admin-password</c>, else <c>URLOG_CH_ADMIN_USER</c>/<c>URLOG_CH_ADMIN_PASSWORD</c>.</item>
/// </list>
/// Before the per-role accounts <c>--user</c>/<c>URLOG_CH_USER</c> meant the admin account; it now means the ingest account, so every
/// command that connects says which account it used (<see cref="Announce"/>: the name, never the password).
/// Host and ports: <c>--clickhouse</c>, <c>--native-port</c>, <c>--http-port</c>, else <c>URLOG_CH_HOST</c>,
/// <c>URLOG_CH_TCP_PORT</c>, <c>URLOG_CH_HTTP_PORT</c>. The defaults are compose.dev.yml's development stubs.
/// </summary>
internal sealed class Connection
{
    public Connection(CliArgs args)
    {
        Host = args.Env("clickhouse", "URLOG_CH_HOST", "127.0.0.1");
        NativePort = int.Parse(args.Env("native-port", "URLOG_CH_TCP_PORT", "9000"), CultureInfo.InvariantCulture);
        HttpPort = int.Parse(args.Env("http-port", "URLOG_CH_HTTP_PORT", "8123"), CultureInfo.InvariantCulture);
        IngestUser = args.Env("user", "URLOG_CH_USER", "urlog_ingest");
        IngestPassword = args.Env("password", "URLOG_CH_PASSWORD", "urlog_ingest");
        QueryUser = args.Env("query-user", "URLOG_CH_QUERY_USER", "urlog_query");
        QueryPassword = args.Env("query-password", "URLOG_CH_QUERY_PASSWORD", "urlog_query");
        AdminUser = args.Env("admin-user", "URLOG_CH_ADMIN_USER", "default");
        AdminPassword = args.Env("admin-password", "URLOG_CH_ADMIN_PASSWORD", "clickhouse");
    }

    public string Host { get; }

    public int NativePort { get; }

    public int HttpPort { get; }

    /// <summary>The event store as messages name it: <c>host:port</c> of the native endpoint.</summary>
    public string Server => string.Create(CultureInfo.InvariantCulture, $"{Host}:{NativePort}");

    public string IngestUser { get; }

    public string IngestPassword { get; }

    public string QueryUser { get; }

    public string QueryPassword { get; }

    public string AdminUser { get; }

    public string AdminPassword { get; }

    /// <summary>
    /// The one line a command prints for each account it connects as: role, account name and endpoint. Never the
    /// password. On stderr for commands whose stdout is a result (<c>query</c>, <c>bench</c>).
    /// </summary>
    public static string Announce(string role, string user, string host, int port, string transport) =>
        $"account   {user} ({role}) at {host}:{port.ToString(CultureInfo.InvariantCulture)} {transport}";

    public string AnnounceIngest() => Announce("ingest", IngestUser, Host, NativePort, "native");

    public string AnnounceAdmin() => Announce("admin", AdminUser, Host, NativePort, "native");

    public string AnnounceQuery() => Announce("query", QueryUser, Host, HttpPort, "http");

    /// <summary>The driver's TCP client as the admin account: migrations and cache drops, nothing at ingest or query time.</summary>
    public ClickHouseTcpClient Admin() => Tcp(AdminUser, AdminPassword);

    /// <summary>The driver's TCP client as the ingest account: the write path's control plane (<see cref="EventStoreAdmin"/>).</summary>
    public ClickHouseTcpClient Ingest() => Tcp(IngestUser, IngestPassword);

    /// <summary>The insert data plane (our Native client) as the ingest account, into <c>events</c>.</summary>
    public NativeInserterOptions Inserter(int writers) => new()
    {
        Host = Host,
        Port = NativePort,
        User = IngestUser,
        Password = IngestPassword,
        Database = "default",
        Table = "events",
        Writers = writers,
    };

    /// <summary>The query path: HTTP as the read-only user.</summary>
    public ClickHouseQueryClient Query() => new(Host, HttpPort, QueryUser, QueryPassword);

    private ClickHouseTcpClient Tcp(string user, string password) =>
        new($"Host={Host};Port={NativePort.ToString(CultureInfo.InvariantCulture)};Username={user};Password={password};Database=default;Compression=lz4");
}
