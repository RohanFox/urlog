using System.Diagnostics;
using System.Security.Cryptography;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace Urlog.Core.Lifecycle;

/// <summary>What the sweep is told when it asks whether it may read.</summary>
public enum SweepSignal
{
    /// <summary>Nothing else needs the disk.</summary>
    Go,

    /// <summary>A job is running: hold, keep the place, ask again.</summary>
    Wait,

    /// <summary>Stop now; the file in hand is read again from its start by a later sweep.</summary>
    Stop,
}

/// <summary>What one run of the sweep did.</summary>
/// <param name="Complete">False when the run was stopped before every due file was read.</param>
public sealed record SweepReport(int Ok, int Mismatched, int Missing, int Unreadable, long Bytes, bool Complete, TimeSpan Elapsed);

/// <summary>
/// The integrity sweep (design record, "Evidence handling", rule 5): every stored file that arrived is read
/// again, hashed, and compared with its intake record, and the result is written to the audit chain.
/// <para>
/// It never competes with an ingest for the disk. It reads one file at a time, sequentially, and asks before
/// every file and after every <see cref="LifecycleOptions.SweepYieldBytes"/> of one whether a job is running;
/// while one is, it holds with the file open and its hash state kept, and goes on from the same byte afterwards.
/// Before it starts to hold it writes the results it has, so nothing that was read waits unrecorded for a job to
/// end, and a process killed during the hold loses nothing already read.
/// Files are taken least recently verified first, so a sweep that was held or stopped goes on where the last
/// result is oldest.
/// </para>
/// <para>
/// A file whose bytes or size differ from the record, or that is gone, is quarantined with
/// <c>integrity_mismatch</c>; its bytes are not touched, and rows parsed from it before stay visible. A file that
/// cannot be read at all (permissions, an I/O error, a volume that is not there) gets no result: a failure of the
/// machine is not evidence about the file.
/// </para>
/// </summary>
public sealed class IntegritySweep(MetadataStore store, MemberStore members, string? evidenceRoot, LifecycleOptions options, ILogger logger)
{
    private static readonly TimeSpan HoldPoll = TimeSpan.FromMilliseconds(200);
    private long _bytesRead;

    /// <summary>Bytes of evidence this sweep has read since it was made, whole files and parts of files.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>Counts every read of file bytes; tests assert that nothing is read while a job runs.</summary>
    public Action<long, int>? OnRead { get; init; }

    /// <summary>
    /// Reads every file last verified before <paramref name="before"/>.
    /// </summary>
    /// <param name="signal">Asked before each file and inside each; see <see cref="SweepSignal"/>.</param>
    public async Task<SweepReport> RunAsync(DateTime before, Func<SweepSignal> signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var started = Stopwatch.GetTimestamp();
        var verified = new List<long>(options.SweepBatch);
        int ok = 0, mismatched = 0, missing = 0, unreadable = 0;
        long bytes = 0;
        var complete = false;
        var buffer = new byte[1 << 20];

        // Asked before every file and inside one. On the first "wait" the results in hand are written, then it holds.
        Task<bool> MayReadAsync() => HoldAsync(
            signal,
            async () =>
            {
                ok += await FlushAsync(verified, cancellationToken).ConfigureAwait(false);
                LifecycleLog.SweepHolds(logger, ok, mismatched + missing, BytesRead);
            },
            held => LifecycleLog.SweepGoesOn(logger, held.TotalSeconds, BytesRead),
            cancellationToken);

        try
        {
            // The list is taken once: a file is read at most once in a run, whatever is written to it meanwhile.
            List<long> ids;
            await using (var db = store.CreateDbContext())
            {
                ids = await IntegrityOperations.DueIdsAsync(db, before, cancellationToken).ConfigureAwait(false);
            }

            foreach (var chunk in ids.Chunk(options.SweepBatch))
            {
                List<SweepCandidate> due;
                await using (var db = store.CreateDbContext())
                {
                    due = await IntegrityOperations.LoadAsync(db, chunk, cancellationToken).ConfigureAwait(false);
                }

                foreach (var candidate in due)
                {
                    var file = candidate.File;
                    if (!await MayReadAsync().ConfigureAwait(false))
                    {
                        return Report();
                    }

                    var read = await ReadAsync(file, buffer, MayReadAsync, cancellationToken).ConfigureAwait(false);
                    bytes += read.Bytes;
                    switch (read.Outcome)
                    {
                        case ReadOutcome.Stopped:
                            return Report();
                        case ReadOutcome.Unreadable:
                            // No result: its last one stays the oldest, so the next sweep takes it first.
                            unreadable++;
                            LifecycleLog.SweepUnreadable(logger, file.Id, file.CaseId, read.Reason ?? string.Empty);
                            break;
                        case ReadOutcome.Missing:
                            await MismatchAsync(file, null, cancellationToken).ConfigureAwait(false);
                            missing++;
                            break;
                        default:
                            if (string.Equals(read.Sha256, file.Sha256, StringComparison.Ordinal) && read.Bytes == file.Size)
                            {
                                verified.Add(file.Id);
                                if (verified.Count >= options.SweepBatch)
                                {
                                    ok += await FlushAsync(verified, cancellationToken).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                await MismatchAsync(file, new IntegrityMismatch(read.Sha256!, read.Bytes), cancellationToken).ConfigureAwait(false);
                                mismatched++;
                            }

                            break;
                    }
                }

                ok += await FlushAsync(verified, cancellationToken).ConfigureAwait(false);
            }

            complete = true;
            return Report();
        }
        finally
        {
            if (verified.Count > 0)
            {
                // Results already read are not thrown away by a stop.
                ok += await FlushAsync(verified, CancellationToken.None).ConfigureAwait(false);
            }

            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var megabytes = bytes / 1e6;
            LifecycleLog.SweepEnded(logger, ok, mismatched, missing, unreadable, megabytes, seconds, complete ? "every due file was read" : "stopped before the end; the rest is read by the next run");
        }

        SweepReport Report() => new(ok + verified.Count, mismatched, missing, unreadable, bytes, complete, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>The instant the current sweep is due from: the latest <see cref="LifecycleOptions.SweepAtUtc"/> at or before <paramref name="nowUtc"/>. Null when the schedule is off.</summary>
    public static DateTime? DueFrom(TimeOnly? at, DateTime nowUtc)
    {
        if (at is not { } time)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(nowUtc).ToDateTime(time, DateTimeKind.Utc);
        return today <= nowUtc ? today : today.AddDays(-1);
    }

    private static async Task<bool> HoldAsync(Func<SweepSignal> signal, Func<Task> beforeHold, Action<TimeSpan> afterHold, CancellationToken cancellationToken)
    {
        long heldSince = 0;
        while (true)
        {
            switch (signal())
            {
                case SweepSignal.Go:
                    if (heldSince != 0)
                    {
                        afterHold(Stopwatch.GetElapsedTime(heldSince));
                    }

                    return true;
                case SweepSignal.Stop:
                    return false;
                default:
                    if (heldSince == 0)
                    {
                        heldSince = Stopwatch.GetTimestamp();
                        await beforeHold().ConfigureAwait(false);
                    }

                    await Task.Delay(HoldPoll, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task<(ReadOutcome Outcome, string? Sha256, long Bytes, string? Reason)> ReadAsync(EvidenceFile file, byte[] buffer, Func<Task<bool>> mayRead, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = members.StoredPath(file);
        }
        catch (InvalidOperationException e)
        {
            return (ReadOutcome.Unreadable, null, 0, e.Message);
        }

        if (!File.Exists(path))
        {
            // Gone, unless what is gone is the volume or the evidence root: that says nothing about this file.
            return AnchorExists(file, path)
                ? (ReadOutcome.Missing, null, 0, null)
                : (ReadOutcome.Unreadable, null, 0, "the directory the stored files live under is not there");
        }

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous);
            long total = 0, sinceAsked = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                OnRead?.Invoke(file.Id, read);
                Interlocked.Add(ref _bytesRead, read);
                hash.AppendData(buffer, 0, read);
                total += read;
                sinceAsked += read;
                if (sinceAsked >= options.SweepYieldBytes)
                {
                    sinceAsked = 0;
                    if (!await mayRead().ConfigureAwait(false))
                    {
                        return (ReadOutcome.Stopped, null, 0, null);
                    }
                }
            }

            return (ReadOutcome.Read, Convert.ToHexStringLower(hash.GetHashAndReset()), total, null);
        }
        catch (FileNotFoundException)
        {
            return (ReadOutcome.Missing, null, 0, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (ReadOutcome.Unreadable, null, 0, e.Message);
        }
    }

    private bool AnchorExists(EvidenceFile file, string path) =>
        Path.IsPathRooted(file.StoredPath)
            ? Path.GetPathRoot(path) is { Length: > 0 } volume && Directory.Exists(volume)
            : !string.IsNullOrWhiteSpace(evidenceRoot) && Directory.Exists(evidenceRoot);

    private async Task MismatchAsync(EvidenceFile file, IntegrityMismatch? actual, CancellationToken cancellationToken)
    {
        await BusyRetry.Default.RunAsync(
            async () =>
            {
                await using var db = store.CreateDbContext();
                return await IntegrityOperations.RecordMismatchAsync(db, store.Audit, file.Id, actual, store.Clock, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        LifecycleLog.SweepMismatch(logger, file.Id, file.CaseId, file.Sha256, actual?.ActualSha256 ?? IntegrityOperations.Missing);
    }

    private async Task<int> FlushAsync(List<long> verified, CancellationToken cancellationToken)
    {
        if (verified.Count == 0)
        {
            return 0;
        }

        var ids = verified.ToArray();
        var written = await BusyRetry.Default.RunAsync(
            async () =>
            {
                await using var db = store.CreateDbContext();
                return await IntegrityOperations.RecordVerifiedAsync(db, store.Audit, ids, store.Clock, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        verified.Clear();
        return written;
    }

    private enum ReadOutcome
    {
        Read,
        Missing,
        Unreadable,
        Stopped,
    }
}
