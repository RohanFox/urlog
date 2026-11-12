using Urlog.Core.Metadata;

namespace Urlog.Api;

/// <summary>
/// Every log line the host writes about accounts. Nothing here takes a password, a hash, a cookie token or a CSRF token;
/// tests/Api.Tests captures every line and asserts none of those values appears.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "metadata store {Path} migrated; {Users} user(s)")]
    public static partial void Migrated(ILogger logger, string path, int users);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "the users table is empty: nobody can log in until `urlog user create --db {Path} --name <name> --admin` has run")]
    public static partial void NoUsers(ILogger logger, string path);

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "ingest: job runner starting as {User} at {Host}:{Port}, {Jobs} job(s) at a time")]
    public static partial void IngestStarting(ILogger logger, string user, string host, int port, int jobs);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "ingest: this process runs no jobs: {Reason}")]
    public static partial void IngestNotStarted(ILogger logger, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "login {Outcome} for '{Name}' from {Address}")]
    public static partial void Login(ILogger logger, LoginOutcome outcome, string name, string address);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "login throttled for {Address}: {Attempts} attempts in {Window}; retry after {RetryAfter} s")]
    public static partial void Throttled(ILogger logger, string address, int attempts, TimeSpan window, int retryAfter);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "route {Route} has a case id but no case-access requirement: refused (fail closed)")]
    public static partial void UnguardedRoute(ILogger logger, string route);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "forwarded headers trusted from {Proxies}")]
    public static partial void TrustedProxies(ILogger logger, string proxies);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "logout '{Name}'")]
    public static partial void Logout(ILogger logger, string name);
}
