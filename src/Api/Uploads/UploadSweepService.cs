using Microsoft.Extensions.Options;

namespace Urlog.Api.Uploads;

/// <summary>What one pass of the sweep did.</summary>
public sealed record UploadSweepResult(int Finished, int Expired, int Orphans);

/// <summary>
/// The upkeep of the uploads folders. One pass does two things per case. Uploads whose bytes are all here but
/// which never reached intake (the process stopped in between) are handed over. Uploads whose expiry has passed,
/// 24 hours after their last byte by default, are removed with their sidecar; so is a file of ours that has no
/// sidecar and has not been written for that long.
/// <para>
/// The removal opens no path outside <c>&lt;evidence root&gt;/&lt;case&gt;/.uploads</c>, and inside it leaves alone
/// whatever does not carry one of this server's upload ids.
/// </para>
/// </summary>
public sealed class UploadSweeper(UploadFolders folders, UploadFinaliser finaliser, TimeProvider clock, IOptions<UrlogOptions> options, ILogger<UploadSweeper> logger)
{
    public async Task<UploadSweepResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var expiry = options.Value.Upload.Expiry;
        int finished = 0, expired = 0, orphans = 0;
        foreach (var caseId in folders.Cases().ToList())
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in folders.Ids(caseId).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                known.Add(id);
                if (finaliser.IsRunning(caseId, id) || await folders.ReadAsync(caseId, id, cancellationToken).ConfigureAwait(false) is not { } info)
                {
                    continue;
                }

                var complete = info.State == UploadState.Receiving && File.Exists(folders.Data(caseId, id)) && folders.Received(caseId, id) == info.Length;
                if (info.State == UploadState.Finalising || complete)
                {
                    try
                    {
                        await finaliser.FinaliseAsync(caseId, id).ConfigureAwait(false);
                        finished++;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // The next pass tries again; one upload that cannot be finished does not stop the others.
                        Log.UploadFinishFailed(logger, id, caseId, e);
                    }

                    continue;
                }

                if (info.ExpiresAt <= clock.GetUtcNow())
                {
                    folders.Remove(caseId, id);
                    expired++;
                    Log.UploadExpired(logger, id, caseId, info.State);
                }
            }

            orphans += RemoveOrphans(caseId, known, expiry);
        }

        return new UploadSweepResult(finished, expired, orphans);
    }

    /// <summary>Files named like ours that no sidecar describes: bytes whose creation stopped half way, a sidecar that was being written.</summary>
    private int RemoveOrphans(long caseId, HashSet<string> known, TimeSpan expiry)
    {
        var directory = folders.Uploads(caseId);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        var before = clock.GetUtcNow() - expiry;
        foreach (var path in Directory.EnumerateFiles(directory).ToList())
        {
            var name = Path.GetFileName(path);
            var id = UploadFolders.IdOf(name);
            if (id is null)
            {
                Log.UploadStranger(logger, caseId, name);
                continue;
            }

            if (!known.Contains(id) && new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) <= before)
            {
                File.Delete(path);
                removed++;
            }
        }

        return removed;
    }
}

/// <summary>Runs the sweep once the store identity comparison has passed, then at its interval.</summary>
public sealed class UploadSweepService(UploadSweeper sweeper, StoreReadiness readiness, IOptions<UrlogOptions> options, ILogger<UploadSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Finishing an upload writes a record, and nothing writes before the two stores are known to belong together.
            await readiness.WhenReady.WaitAsync(stoppingToken).ConfigureAwait(false);
            while (true)
            {
                try
                {
                    var result = await sweeper.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    if (result is not { Finished: 0, Expired: 0, Orphans: 0 })
                    {
                        Log.UploadSweep(logger, result.Finished, result.Expired, result.Orphans);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.UploadSweepFailed(logger, e);
                }

                await Task.Delay(options.Value.Upload.SweepInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
