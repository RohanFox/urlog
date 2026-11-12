namespace Urlog.Api;

/// <summary>
/// The CLI's environment variables as configuration, so one set of variables drives <c>urlog</c> and the API:
/// <c>URLOG_DB</c> -> <c>Urlog:Metadata:Path</c>, <c>URLOG_CH_*</c> -> <c>Urlog:ClickHouse:*</c> (the job runner's
/// ingest account today, the search endpoints' query account later).
/// <c>User</c>/<c>Password</c> are the ingest account (<c>urlog_ingest</c>) and <c>QueryUser</c>/<c>QueryPassword</c>
/// the read-only one; the backend runs with those two. <c>AdminUser</c>/<c>AdminPassword</c> are absent by default:
/// the backend migrates at startup only when they are configured (deploy default is <c>urlog migrate</c>).
/// Added after the default sources, so it wins over appsettings.json and the <c>Urlog__*</c> form.
/// </summary>
public sealed class UrlogEnvironment : IConfigurationSource
{
    private static readonly (string Variable, string Key)[] Map =
    [
        ("URLOG_DB", "Urlog:Metadata:Path"),
        ("URLOG_CH_HOST", "Urlog:ClickHouse:Host"),
        ("URLOG_CH_TCP_PORT", "Urlog:ClickHouse:NativePort"),
        ("URLOG_CH_HTTP_PORT", "Urlog:ClickHouse:HttpPort"),
        ("URLOG_CH_USER", "Urlog:ClickHouse:User"),
        ("URLOG_CH_PASSWORD", "Urlog:ClickHouse:Password"),
        ("URLOG_CH_QUERY_USER", "Urlog:ClickHouse:QueryUser"),
        ("URLOG_CH_QUERY_PASSWORD", "Urlog:ClickHouse:QueryPassword"),
        ("URLOG_CH_ADMIN_USER", "Urlog:ClickHouse:AdminUser"),
        ("URLOG_CH_ADMIN_PASSWORD", "Urlog:ClickHouse:AdminPassword"),
    ];

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider();

    private sealed class Provider : ConfigurationProvider
    {
        public override void Load()
        {
            foreach (var (variable, key) in Map)
            {
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
                {
                    Data[key] = value;
                }
            }
        }
    }
}
