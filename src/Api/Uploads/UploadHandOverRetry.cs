using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Uploads;

/// <summary>
/// The upload's bytes are complete and stored under their final name, and its record is not written yet because
/// the metadata file stayed locked. The route answers 503 with <c>Retry-After</c>; the record follows without the
/// client, which asks HEAD or the status route and sends nothing again.
/// </summary>
public sealed class UploadPendingException : Exception
{
    public UploadPendingException()
    {
    }

    public UploadPendingException(string message)
        : base(message)
    {
    }

    public UploadPendingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UploadPendingException(string message, int retryAfterSeconds, Exception innerException)
        : base(message, innerException)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int RetryAfterSeconds { get; } = 1;
}

/// <summary>
/// The uploads whose hand-over to intake gave up on a locked metadata file, kept in memory. An upload is listed
/// once, under its case and id, and leaves the list with the first hand-over that ends any other way, whoever
/// ran it. A restart empties the list; the sweep at start finishes what was on it.
/// </summary>
public sealed class PendingHandOvers
{
    private readonly ConcurrentDictionary<(long CaseId, string Id), byte> _pending = new();

    public int Count => _pending.Count;

    /// <summary>False when the upload was listed already.</summary>
    public bool Add(long caseId, string id) => _pending.TryAdd((caseId, id), 0);

    public bool Remove(long caseId, string id) => _pending.TryRemove((caseId, id), out _);

    public bool Contains(long caseId, string id) => _pending.ContainsKey((caseId, id));

    public IReadOnlyList<(long CaseId, string Id)> Snapshot() => [.. _pending.Keys];
}

/// <summary>
/// Hands the listed uploads to intake again every <see cref="UploadOptions.HandOverRetry"/>, seconds where the
/// sweep's interval is minutes. The hand-over itself removes an upload from the list when it ends, so nothing is
/// tried again once the upload is recorded, a duplicate, refused or failed.
/// </summary>
public sealed class UploadHandOverRetryService(PendingHandOvers pending, UploadFinaliser finaliser, IOptions<UrlogOptions> options, ILogger<UploadHandOverRetryService> logger) : BackgroundService
{
    /// <summary>One round over the list as it is now; the number of uploads that left it.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var finished = 0;
        foreach (var (caseId, id) in pending.Snapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await finaliser.FinaliseAsync(caseId, id).ConfigureAwait(false);
                finished++;
            }
            catch (UploadPendingException)
            {
                // Still locked: the upload stays listed for the next round.
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Not a locked file, so trying every few seconds would not help; the sweep has it from here.
                finished++;
                Log.UploadFinishFailed(logger, id, caseId, e);
            }
        }

        return finished;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.Value.Upload.HandOverRetry, stoppingToken).ConfigureAwait(false);
                if (pending.Count > 0)
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
