using Microsoft.Extensions.Logging;

namespace Urlog.Core.Jobs;

/// <summary>The runner's log lines. None carries evidence bytes, a credential or a full evidence path.</summary>
internal static partial class JobLog
{
    /// <summary>The text of <see cref="CommitWaiting"/>, which the crash test waits for on the worker's output.</summary>
    public const string CommitWaitingText = "commit is waiting for the metadata lock";

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "job {JobId} started: file {FileId}, case {CaseId}, generation {Generation}, attempt {Attempt}, worker {Worker}")]
    public static partial void Started(ILogger logger, long jobId, long fileId, long caseId, int generation, int attempt, string worker);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "job {JobId} done: file {FileId}, generation {Generation}, {Rows} rows ({Rejects} rejected), {Bytes} decoded bytes in {Seconds:F2} s = {MegabytesPerSecond:F1} MB/s (file read to last ack); hash {HashSeconds:F2} s inside the read; {TotalSeconds:F2} s with the commit")]
    public static partial void Done(ILogger logger, long jobId, long fileId, int generation, long rows, long rejects, long bytes, double seconds, double megabytesPerSecond, double hashSeconds, double totalSeconds);

    [LoggerMessage(EventId = 102, Level = LogLevel.Warning, Message = "job {JobId} failed: file {FileId}: {Reason}")]
    public static partial void Failed(ILogger logger, long jobId, long fileId, string reason);

    [LoggerMessage(EventId = 103, Level = LogLevel.Error, Message = "job {JobId}: file {FileId} quarantined: the intake record says sha256 {Expected}, the bytes read hash to {Actual}")]
    public static partial void Quarantined(ILogger logger, long jobId, long fileId, string expected, string actual);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "job {JobId}: " + CommitWaitingText)]
    public static partial void CommitWaiting(ILogger logger, long jobId);

    [LoggerMessage(EventId = 105, Level = LogLevel.Warning, Message = "job {JobId}: file {FileId} is committed in the metadata store but the event store did not take the generation ({Reason}); the sync will write it")]
    public static partial void CommitNotMirrored(ILogger logger, long jobId, long fileId, string reason);

    [LoggerMessage(EventId = 106, Level = LogLevel.Warning, Message = "job {JobId}: the stop did not clean up ({Reason}); the job stays in parsing for the next start")]
    public static partial void StopNotClean(ILogger logger, long jobId, string reason);

    [LoggerMessage(EventId = 107, Level = LogLevel.Warning, Message = "job {JobId}: the rows of the failed attempt were not deleted ({Reason}); they are never visible and the next attempt removes them")]
    public static partial void DeleteAfterFailure(ILogger logger, long jobId, string reason);

    [LoggerMessage(EventId = 108, Level = LogLevel.Debug, Message = "job {JobId}: progress write skipped ({Reason})")]
    public static partial void ProgressSkipped(ILogger logger, long jobId, string reason);

    [LoggerMessage(EventId = 109, Level = LogLevel.Error, Message = "job {JobId} left buffers out ({Chunks} chunk buffers, {Batches} batches); the slot's pools are replaced")]
    public static partial void PoolsUnbalanced(ILogger logger, long jobId, int chunks, int batches);

    [LoggerMessage(EventId = 110, Level = LogLevel.Critical, Message = "runner slot {Slot} stopped")]
    public static partial void SlotStopped(ILogger logger, int slot, Exception exception);

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning, Message = "job {JobId}: file {FileId}: the event store refused the insert for memory (241) on attempt {Attempt}: {Outcome}; no job is claimed for {Seconds:F0} s")]
    public static partial void MemoryLimit(ILogger logger, long jobId, long fileId, int attempt, string outcome, double seconds);

    [LoggerMessage(EventId = 120, Level = LogLevel.Warning, Message = "the event store is not ready ({Reason}); no job is claimed, next try in {Seconds:F0} s")]
    public static partial void StoreNotReady(ILogger logger, string reason, double seconds);

    [LoggerMessage(EventId = 121, Level = LogLevel.Warning, Message = "job {JobId} (file {FileId}) was left in parsing by worker {Worker}, attempt {Attempt}: {Outcome}")]
    public static partial void Recovered(ILogger logger, long jobId, long fileId, string worker, int attempt, string outcome);

    [LoggerMessage(EventId = 122, Level = LogLevel.Information, Message = "file_generation synced from the metadata store: {Written} of {Files} files written")]
    public static partial void GenerationsSynced(ILogger logger, int written, int files);

    [LoggerMessage(EventId = 123, Level = LogLevel.Warning, Message = "case {CaseId} is in purging; its purge has not completed")]
    public static partial void PurgeWaiting(ILogger logger, long caseId);

    [LoggerMessage(EventId = 130, Level = LogLevel.Warning, Message = "ingest paused: {Reason}")]
    public static partial void Paused(ILogger logger, string reason);

    [LoggerMessage(EventId = 131, Level = LogLevel.Information, Message = "ingest resumed: {FreeBytes} bytes free on {Volume}")]
    public static partial void Resumed(ILogger logger, string volume, long freeBytes);

    [LoggerMessage(EventId = 132, Level = LogLevel.Warning, Message = "ingest is paused by the disk watermark and nothing runs; leaving")]
    public static partial void PausedExit(ILogger logger);

    [LoggerMessage(EventId = 133, Level = LogLevel.Warning, Message = "the event store's disks could not be read ({Reason}); the watermark covers the local volumes only")]
    public static partial void StoreDisksUnread(ILogger logger, string reason);

    [LoggerMessage(EventId = 134, Level = LogLevel.Warning, Message = "runner maintenance failed: {Reason}")]
    public static partial void MaintenanceFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 135, Level = LogLevel.Information, Message = "nightly backup {Path}, sha256 {Sha256}")]
    public static partial void BackupTaken(ILogger logger, string path, string sha256);
}
