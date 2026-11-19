using Urlog.Core.Metadata;

namespace Urlog.Core.Audit;

/// <summary>
/// The action vocabulary of the audit chain (design record, "Audit log"): lowercase snake_case
/// text in <c>audit.action</c>. Lifecycle actions are <c>case_</c> + the <see cref="CaseLifecycle"/> operation name.
/// </summary>
public static class AuditActions
{
    /// <summary>The actor of rows the tool writes on its own behalf (genesis, scheduled backups, sweeps).</summary>
    public const string SystemActor = "system";

    public const string Genesis = "genesis";

    public const string Login = "login";
    public const string Logout = "logout";
    public const string LoginFailed = "login_failed";

    /// <summary>The first refusal of a per-address window of the /login throttle; later refusals in the window write nothing.</summary>
    public const string LoginThrottled = "login_throttled";

    public const string CaseCreate = "case_create";
    public const string CaseShare = "case_share";
    public const string CaseUnshare = "case_unshare";

    /// <summary>A member's role changed; <c>case_share</c> and <c>case_unshare</c> are add and remove.</summary>
    public const string CaseRoleChange = "case_role_change";
    public const string CaseClose = "case_close";
    public const string CaseReopen = "case_reopen";
    public const string CaseArchive = "case_archive";

    /// <summary>The partitions of an archived case are dropped and its stored files removed: the archive is all there is of it.</summary>
    public const string CaseArchiveComplete = "case_archive_complete";

    /// <summary>An archive was given up: its attempts are used, or its written archive no longer matches what was committed.</summary>
    public const string CaseArchiveFailed = "case_archive_failed";

    /// <summary>A re-import was refused and nothing was restored: the reason, and the file when one can be named.</summary>
    public const string CaseReimportRefused = "case_reimport_refused";
    public const string CaseReimport = "case_reimport";
    public const string CaseSoftDelete = "case_soft_delete";
    public const string CaseRestore = "case_restore";

    /// <summary>
    /// An administrator asked for a purge, or a second one confirmed it (<c>step</c> in the details). The purge
    /// itself starts with <c>case_purge</c>, which marks the case.
    /// </summary>
    public const string CasePurgeRequest = "case_purge_request";
    public const string CasePurge = "case_purge";
    public const string CasePurgeComplete = "case_purge_complete";

    public const string FileIntake = "file_intake";
    public const string FileDuplicate = "file_duplicate";
    public const string FileOverride = "file_override";

    /// <summary>A person changed a file's host, zone or year: each with its old and new value, and the re-parse it queued.</summary>
    public const string FileUpdate = "file_update";

    /// <summary>One call gave many files a host. The target is the case; the row lists every changed file with its SHA-256.</summary>
    public const string FileBulkHost = "file_bulk_host";

    /// <summary>One call set the override of many files. The target is the case; the row lists every changed file.</summary>
    public const string FileBulkOverride = "file_bulk_override";

    /// <summary>One call asked for many files to be detected again. The target is the case.</summary>
    public const string FileBulkRedetect = "file_bulk_redetect";

    /// <summary>A file was detected again: the result before and after. The record now holds the new one.</summary>
    public const string FileRedetect = "file_redetect";

    /// <summary>A host was added to a case by a person. The target is the case; the host's id and name are in the details.</summary>
    public const string HostCreate = "host_create";

    /// <summary>A host's name, zone or default source options changed: old and new values, and the re-parses queued.</summary>
    public const string HostUpdate = "host_update";

    /// <summary>A host was merged into another one of its case: both names and ids, and the number of files moved.</summary>
    public const string HostMerge = "host_merge";

    /// <summary>A person queued the work of a failed or cancelled job again: the old job and the new one. The target is the file.</summary>
    public const string JobRetry = "job_retry";

    /// <summary>
    /// A file was refused and nothing of it is stored (a packet capture, alone or inside an archive): its name,
    /// size, SHA-256 and what it was taken for. The target is the case, since no file record exists.
    /// </summary>
    public const string FileRejected = "file_rejected";

    /// <summary>An administrator queued the expansion of one archive again with limits of their own; the limits are in the details.</summary>
    public const string FileExpandRerun = "file_expand_rerun";
    public const string FileReparse = "file_reparse";

    /// <summary>A queued job was cancelled by the archive of its case: the job, its kind and the reason. The target is the file.</summary>
    public const string JobCancelled = "job_cancelled";
    /// <summary>
    /// A parse was committed from a compressed input that ends before its format says it should: both offsets of
    /// the end in the details. Written in the commit's transaction, so no truncated file is visible without it.
    /// </summary>
    public const string InputTruncated = "input_truncated";
    public const string IntegrityOk = "integrity_ok";
    public const string IntegrityMismatch = "integrity_mismatch";

    public const string UserCreate = "user_create";
    public const string UserRoleChange = "user_role_change";

    /// <summary>The failure threshold reached: <c>locked_until</c> in the details; the expiry itself writes no row.</summary>
    public const string UserLockout = "user_lockout";

    /// <summary>An admin or the CLI ended a lockout early.</summary>
    public const string UserUnlock = "user_unlock";

    /// <summary>An admin or the CLI set a new password; the hash is never in the details.</summary>
    public const string UserPasswordReset = "user_password_reset";

    /// <summary>A user changed their own password, knowing the current one; neither is in the details.</summary>
    public const string UserPasswordChange = "user_password_change";

    /// <summary>Query execution: text and its SHA-256, case, user, duration, whether the gate accepted it; never results.</summary>
    public const string Query = "query";

    /// <summary>A <c>VACUUM INTO</c> backup taken, with the backup's own SHA-256.</summary>
    public const string Backup = "backup";

    /// <summary>The disk watermark stopped the ingest queue: the volume, its free bytes and the mark in the details.</summary>
    public const string IngestPaused = "ingest_paused";

    /// <summary>Free space is back above the mark and the queue runs again.</summary>
    public const string IngestResumed = "ingest_resumed";

    /// <summary>The first store identity of a new metadata file and event store: the value and the server in the details.</summary>
    public const string StoreIdentityCreated = "store_identity_created";

    /// <summary>
    /// The metadata file was joined to an event store by hand: the identity it held before, the one it holds now,
    /// the server, and the counts the operator confirmed on.
    /// </summary>
    public const string StorePaired = "store_paired";

    /// <summary><c>close</c> -> <c>case_close</c>, and so on for every <see cref="CaseLifecycle"/> operation.</summary>
    public static string ForCaseOperation(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return "case_" + operation;
    }
}

/// <summary>What <c>audit.target_type</c> may hold. <c>target_id</c> is the row id of that table at the time, a plain number.</summary>
public static class AuditTargets
{
    public const string Case = "case";
    public const string File = "file";
    public const string User = "user";
    public const string Query = "query";
    public const string Backup = "backup";

    /// <summary>The event store the metadata file belongs to; no id.</summary>
    public const string Store = "store";
}
