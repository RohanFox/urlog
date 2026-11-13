using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Urlog.Core.Metadata;

/// <summary>
/// How a write that must not be lost waits for the metadata file while another writer holds it. The store's own
/// busy timeout (<see cref="MetadataStore.BusyTimeoutSeconds"/>) ends one attempt; this policy starts the write
/// again, from its beginning, until <see cref="Timeout"/> has passed since the first attempt began. The job
/// runner's terminal writes and the intake record of a web upload share it (design record, "Consistency").
/// </summary>
public sealed record BusyRetry
{
    /// <summary>60 s: a write that took minutes of work to reach does not fail on a lock held for a few seconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(100);

    public static BusyRetry Default { get; } = new();

    /// <summary>Counted from the start of the first attempt; an attempt that ends busy after this is the last.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>The wait between two attempts, on top of the store's busy timeout inside each.</summary>
    public TimeSpan Pause { get; init; } = DefaultPause;

    /// <summary>True when the failure is SQLite reporting the database busy or locked, at any depth.</summary>
    public static bool IsBusy(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs <paramref name="write"/> until it does not end busy. Every attempt is a new call: the caller opens its
    /// context and its transaction inside, so nothing read by a failed attempt is carried into the next.
    /// </summary>
    /// <param name="onFirstWait">Called once, after the first attempt that ended busy.</param>
    /// <exception cref="MetadataBusyException">The file stayed locked past <see cref="Timeout"/>.</exception>
    public async Task<T> RunAsync<T>(Func<Task<T>> write, Action? onFirstWait = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        var waited = Stopwatch.StartNew();
        var announced = false;
        while (true)
        {
            try
            {
                return await write().ConfigureAwait(false);
            }
            catch (Exception e) when (IsBusy(e))
            {
                if (waited.Elapsed >= Timeout)
                {
                    throw new MetadataBusyException(waited.Elapsed, e);
                }

                if (!announced)
                {
                    announced = true;
                    onFirstWait?.Invoke();
                }
            }

            await Task.Delay(Pause, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The metadata file stayed locked by another writer for as long as the caller was prepared to wait; nothing was written.</summary>
public sealed class MetadataBusyException : Exception
{
    public MetadataBusyException()
    {
    }

    public MetadataBusyException(string message)
        : base(message)
    {
    }

    public MetadataBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MetadataBusyException(TimeSpan waited, Exception innerException)
        : base(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the metadata store stayed locked for {waited.TotalSeconds:F0} s"), innerException)
    {
        Waited = waited;
    }

    public TimeSpan Waited { get; }
}
