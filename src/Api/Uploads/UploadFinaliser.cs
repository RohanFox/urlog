using System.Collections.Concurrent;
using Urlog.Core.Intake;
using Urlog.Core.Metadata;
using Urlog.Sources.Nginx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Uploads;

/// <summary>
/// Hands a complete upload to intake. The bytes are renamed from <c>.uploads/&lt;id&gt;</c> to <c>web/&lt;id&gt;</c> on the
/// same volume, which reads nothing, and then <see cref="IntakePipeline.AcceptAsync"/> does what it does for every
/// delivery path: the hash before anything reads the content, read-only, detection, the hints, and one transaction
/// with the record, its audit row and its job. Nothing of that sequence is done here.
/// <para>
/// When intake records nothing for the file (the case holds the same bytes already, or the file is refused), no
/// record points at <c>web/&lt;id&gt;</c> and it is removed; that is the one delete outside the uploads folder. The
/// sidecar keeps the outcome until the upload expires.
/// </para>
/// <para>
/// One upload has one hand-over in flight in this process, whoever asks: the request that brought the last byte,
/// the sweep and the retry all come through <see cref="FinaliseAsync"/>, and a caller that arrives while a
/// hand-over runs waits for that one and gets its answer. A stop at any point is finished by the next call: a file
/// that is already recorded is found by its stored path and not handed to intake again.
/// </para>
/// <para>
/// The record waits for a metadata file another writer holds, as long as the job runner's commit does. Past that
/// the upload stays <c>finalising</c> with its bytes under their final name, the call ends in
/// <see cref="UploadPendingException"/>, and the upload is listed in <see cref="PendingHandOvers"/> to be handed
/// over again in seconds.
/// </para>
/// </summary>
public sealed class UploadFinaliser(UploadFolders folders, IntakePipeline intake, MetadataStore store, PendingHandOvers pending, IOptions<UrlogOptions> options, ILogger<UploadFinaliser> logger)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<UploadInfo?>>> _running = new(StringComparer.Ordinal);

    /// <summary>True while this process is handing the upload to intake.</summary>
    public bool IsRunning(long caseId, string id) => _running.ContainsKey(Key(caseId, id));

    /// <summary>The upload after intake has answered, or as it is when its bytes are not complete; null when there is no such upload.</summary>
    /// <exception cref="UploadPendingException">The metadata file stayed locked; the upload is listed for another hand-over.</exception>
    public async Task<UploadInfo?> FinaliseAsync(long caseId, string id)
    {
        var key = Key(caseId, id);
        // The first caller starts the hand-over; every caller that finds it in the table shares its task.
        var run = _running.GetOrAdd(key, _ => new Lazy<Task<UploadInfo?>>(() => RunAsync(caseId, id)));
        try
        {
            var info = await run.Value.ConfigureAwait(false);
            pending.Remove(caseId, id);
            return info;
        }
        catch (UploadPendingException)
        {
            pending.Add(caseId, id);
            throw;
        }
        catch
        {
            pending.Remove(caseId, id);
            throw;
        }
        finally
        {
            _running.TryRemove(new KeyValuePair<string, Lazy<Task<UploadInfo?>>>(key, run));
        }
    }

    private static string Key(long caseId, string id) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{caseId}/{id}");

    private async Task<UploadInfo?> RunAsync(long caseId, string id)
    {
        // Not the request's token: a client that has gone does not stop the record being written.
        var none = CancellationToken.None;
        var info = await folders.ReadAsync(caseId, id, none).ConfigureAwait(false);
        if (info is null || info.State is UploadState.Done or UploadState.Duplicate or UploadState.Rejected or UploadState.Failed)
        {
            return info;
        }

        var data = folders.Data(caseId, id);
        var finished = folders.Finished(caseId, id);
        if (File.Exists(data))
        {
            if (new FileInfo(data).Length != info.Length)
            {
                return info;
            }

            info = info with { State = UploadState.Finalising };
            await folders.WriteAsync(info, none).ConfigureAwait(false);
            FlushToDisk(data);
            Directory.CreateDirectory(Path.GetDirectoryName(finished)!);
            File.Move(data, finished);
        }
        else if (!File.Exists(finished))
        {
            return await EndAsync(info with { State = UploadState.Failed, Message = "the uploaded bytes are no longer on disk" }).ConfigureAwait(false);
        }

        var stored = UploadFolders.StoredPath(id);
        var db = store.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var recorded = await db.Files.AsNoTracking().Where(f => f.CaseId == caseId && f.StoredPath == stored)
                .Select(f => new { f.Id, f.Sha256 }).FirstOrDefaultAsync(none).ConfigureAwait(false);
            if (recorded is not null)
            {
                return await EndAsync(info with { State = UploadState.Done, FileId = recorded.Id, Sha256 = recorded.Sha256 }).ConfigureAwait(false);
            }
        }

        IntakeOutcome outcome;
        try
        {
            outcome = await intake.AcceptAsync(new IntakeRequest
            {
                CaseId = caseId,
                Path = finished,
                Source = FileSource.Web,
                Uploader = info.UserName,
                UploaderUserId = info.UserId,
                OriginalName = info.Fields.Name,
                Host = info.Fields.Host,
                Timezone = info.Fields.Timezone,
                Year = info.Fields.Year,
                Options = info.Fields.LogFormat is { } format ? new Dictionary<string, string>(StringComparer.Ordinal) { [NginxSource.LogFormatOption] = format } : null,
                RecordWaiting = () => Log.UploadRecordWaiting(logger, id, caseId),
            }, none).ConfigureAwait(false);
        }
        catch (MetadataBusyException e)
        {
            // Nothing was written and nothing is undone: the file keeps its final name and the upload stays finalising.
            var retry = options.Value.Upload.HandOverRetry;
            Log.UploadPending(logger, id, caseId, (int)e.Waited.TotalSeconds, retry.TotalSeconds);
            throw new UploadPendingException($"upload {id}: the metadata store is busy; the record is pending", Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)), e);
        }
        catch (Exception e) when (e is IntakeRefusedException or MetadataException)
        {
            // Intake wrote nothing. The bytes go back where the sweep finds them.
            MakeWritable(finished);
            File.Move(finished, data);
            Log.UploadFailed(logger, id, caseId, e.Message);
            return await EndAsync(info with { State = UploadState.Failed, Message = e.Message }).ConfigureAwait(false);
        }

        if (outcome.Rejected)
        {
            RemoveUnrecorded(finished);
            return await EndAsync(info with { State = UploadState.Rejected, Sha256 = outcome.Sha256, Message = outcome.Detection.Message ?? "the file was refused" }).ConfigureAwait(false);
        }

        if (outcome.Duplicate && !string.Equals(outcome.File!.StoredPath, stored, StringComparison.Ordinal))
        {
            RemoveUnrecorded(finished);
            return await EndAsync(info with
            {
                State = UploadState.Duplicate,
                FileId = outcome.File.Id,
                Sha256 = outcome.Sha256,
                Message = $"the case already holds these bytes as '{outcome.File.OriginalName}'",
            }).ConfigureAwait(false);
        }

        Log.UploadRecorded(logger, id, caseId, outcome.File!.Id, outcome.Size);
        return await EndAsync(info with { State = UploadState.Done, FileId = outcome.File.Id, Sha256 = outcome.Sha256, JobId = outcome.Job?.Id }).ConfigureAwait(false);
    }

    private async Task<UploadInfo> EndAsync(UploadInfo info)
    {
        await folders.WriteAsync(info, CancellationToken.None).ConfigureAwait(false);
        return info;
    }

    /// <summary>The rename must not outlive the bytes: they are on the disk before the file gets its final name.</summary>
    private static void FlushToDisk(string path)
    {
        using var data = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        data.Flush(flushToDisk: true);
    }

    /// <summary>Intake made the file read-only before it found there was nothing to record.</summary>
    private static void RemoveUnrecorded(string path)
    {
        MakeWritable(path);
        File.Delete(path);
    }

    private static void MakeWritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
    }
}
