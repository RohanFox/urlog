namespace Urlog.Bench;

/// <summary>
/// Where the Insert and EndToEnd benches find ClickHouse. Defaults match compose.dev.yml; override with
/// URLOG_CH_HOST, URLOG_CH_TCP_PORT, URLOG_CH_HTTP_PORT, URLOG_CH_ADMIN_USER, URLOG_CH_ADMIN_PASSWORD.
/// The benches create and drop scratch tables, so they run as the admin account; URLOG_CH_USER is the ingest
/// account (INSERT on <c>events</c> only, no DDL) and is not read here.
/// </summary>
public static class BenchEnv
{
    public static string Host => Environment.GetEnvironmentVariable("URLOG_CH_HOST") ?? "127.0.0.1";

    public static string TcpPort => Environment.GetEnvironmentVariable("URLOG_CH_TCP_PORT") ?? "9000";

    public static string HttpPort => Environment.GetEnvironmentVariable("URLOG_CH_HTTP_PORT") ?? "8123";

    public static string User => Environment.GetEnvironmentVariable("URLOG_CH_ADMIN_USER") ?? "default";

    public static string Password => Environment.GetEnvironmentVariable("URLOG_CH_ADMIN_PASSWORD") ?? "clickhouse";

    public static string TcpConnectionString =>
        $"Host={Host};Port={TcpPort};Username={User};Password={Password};Database=default;Compression=lz4";

    /// <summary>async_insert is on by default in the HTTP client; the insert budget is defined with it off (design record, "Throughput SLO and benchmark harness").</summary>
    public static string HttpConnectionString =>
        $"Host={Host};Port={HttpPort};Username={User};Password={Password};Database=default;set_async_insert=0";
}
