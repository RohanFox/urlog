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

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "not serving yet, only the health route answers: {Reason}")]
    public static partial void StoreWaiting(ILogger logger, string reason);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "not serving, only the health route answers: {Reason}")]
    public static partial void StoreMismatch(ILogger logger, string reason);

    [LoggerMessage(EventId = 39, Level = LogLevel.Error, Message = "a search of case {Case} failed outside the query itself")]
    public static partial void SearchFailed(ILogger logger, long @case, Exception exception);

    [LoggerMessage(EventId = 40, Level = LogLevel.Error, Message = "the audit row of a query on case {Case} could not be written after its answer was sent")]
    public static partial void QueryAuditFailed(ILogger logger, long @case, Exception exception);

    [LoggerMessage(EventId = 22, Level = LogLevel.Warning, Message = "field statistics of case {Case} could not be read: {Reason}")]
    public static partial void FieldStatsUnavailable(ILogger logger, long @case, string reason);

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

    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "upload {Upload} of case {Case} recorded as file {File}, {Bytes} bytes")]
    public static partial void UploadRecorded(ILogger logger, string upload, long @case, long file, long bytes);

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "upload {Upload} of case {Case} was not taken by intake: {Reason}")]
    public static partial void UploadFailed(ILogger logger, string upload, long @case, string reason);

    [LoggerMessage(EventId = 32, Level = LogLevel.Error, Message = "upload {Upload} of case {Case} could not be handed to intake; the next sweep tries again")]
    public static partial void UploadFinishFailed(ILogger logger, string upload, long @case, Exception exception);

    [LoggerMessage(EventId = 33, Level = LogLevel.Information, Message = "upload {Upload} of case {Case} expired in state {State} and was removed")]
    public static partial void UploadExpired(ILogger logger, string upload, long @case, Uploads.UploadState state);

    [LoggerMessage(EventId = 34, Level = LogLevel.Warning, Message = "uploads folder of case {Case}: '{Name}' is not an upload of this server and is left alone")]
    public static partial void UploadStranger(ILogger logger, long @case, string name);

    [LoggerMessage(EventId = 35, Level = LogLevel.Information, Message = "upload sweep: {Finished} handed to intake, {Expired} expired, {Orphans} leftover file(s) removed")]
    public static partial void UploadSweep(ILogger logger, int finished, int expired, int orphans);

    [LoggerMessage(EventId = 36, Level = LogLevel.Error, Message = "upload sweep failed; it runs again at its interval")]
    public static partial void UploadSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 37, Level = LogLevel.Information, Message = "upload {Upload} of case {Case}: the metadata file is locked by another writer; the record waits for it")]
    public static partial void UploadRecordWaiting(ILogger logger, string upload, long @case);

    [LoggerMessage(EventId = 38, Level = LogLevel.Warning, Message = "upload {Upload} of case {Case}: the metadata file stayed locked for {Waited} s; the bytes are stored, the record is tried again every {Retry} s")]
    public static partial void UploadPending(ILogger logger, string upload, long @case, int waited, double retry);

    [LoggerMessage(EventId = 60, Level = LogLevel.Warning, Message = "disk use of case {Case} could not be measured: {Reason}")]
    public static partial void UsageUnavailable(ILogger logger, long @case, string reason);

    [LoggerMessage(EventId = 61, Level = LogLevel.Information, Message = "'{Name}' changed their password; {Sessions} other session(s) ended")]
    public static partial void PasswordChanged(ILogger logger, string name, int sessions);

    [LoggerMessage(EventId = 62, Level = LogLevel.Information, Message = "'{Name}' gave a wrong current password while changing it, from {Address}")]
    public static partial void PasswordChangeRefused(ILogger logger, string name, string address);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "logout '{Name}'")]
    public static partial void Logout(ILogger logger, string name);
}
