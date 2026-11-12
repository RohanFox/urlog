using System.Globalization;
using System.Text;

namespace Urlog.Core.Jobs;

/// <summary>
/// One job runner per metadata file. The runner keeps <c>&lt;db&gt;.runner.lock</c> open without sharing for as long as
/// it lives; the operating system releases it when the process ends, however it ends. Holding it is what lets the
/// startup reconciler treat every job in <c>parsing</c> as abandoned: no other runner can be alive.
/// </summary>
public sealed class RunnerLock : IDisposable
{
    private readonly FileStream _file;

    private RunnerLock(FileStream file, string path)
    {
        _file = file;
        Path = path;
    }

    public string Path { get; }

    public static string PathFor(string databasePath) => databasePath + ".runner.lock";

    /// <summary>Takes the lock, or returns null when another process (or another runner in this one) holds it.</summary>
    public static RunnerLock? TryAcquire(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path = PathFor(System.IO.Path.GetFullPath(databasePath));
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        // Who holds it, for whoever finds the file. Advisory text: the open handle is the lock.
        file.SetLength(0);
        file.Write(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Environment.MachineName} pid {Environment.ProcessId}\n")));
        file.Flush();
        return new RunnerLock(file, path);
    }

    /// <summary>
    /// Takes the lock, trying again for up to <paramref name="wait"/>. The operating system releases a dead
    /// process's handle a moment after the process is gone (measured under load: not always by the time its exit is
    /// reported), so a runner restarted straight after a kill must not take the first refusal as another runner.
    /// </summary>
    public static async Task<RunnerLock?> AcquireAsync(string databasePath, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var held = TryAcquire(databasePath);
            if (held is not null || waited.Elapsed >= wait)
            {
                return held;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Another job runner holds the metadata file's lock.</summary>
public sealed class RunnerLockedException : Exception
{
    public RunnerLockedException()
    {
    }

    public RunnerLockedException(string message)
        : base(message)
    {
    }

    public RunnerLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
