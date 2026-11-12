using System.Diagnostics;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Urlog.Core.Tests;

/// <summary>
/// The runner lock between real processes, on whatever operating system the tests run on: a second
/// <c>urlog worker</c> on the same metadata file is refused while the first lives, and the lock is free the moment
/// the first is killed from outside. No event store is needed: a worker that cannot reach one holds the lock and
/// waits, which is the state under test.
/// </summary>
public sealed class RunnerLockProcessTests
{
    private const string NotReady = "the event store is not ready";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASecondWorkerIsRefusedWhileTheFirstLivesAndTheLockIsFreeAfterAKill()
    {
        var directory = Directory.CreateTempSubdirectory("urlog-lock-");
        try
        {
            var db = Path.Combine(directory.FullName, "metadata.db");
            await new MetadataStore(db).MigrateAsync(Ct);
            SqliteConnection.ClearAllPools();

            using var first = StartWorker(db);
            await WaitForLineAsync(first, NotReady);

            // In-process too: the handle is the lock, whoever asks.
            Assert.Null(RunnerLock.TryAcquire(db));

            using var second = StartWorker(db);
            // It waits its five seconds for the lock first, then gives up.
            await second.Process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(2, second.Process.ExitCode);
            Assert.Contains("another job runner holds", await second.Process.StandardError.ReadToEndAsync(Ct), StringComparison.Ordinal);
            Assert.False(first.Process.HasExited);

            first.Process.Kill(entireProcessTree: true);
            await first.Process.WaitForExitAsync(Ct);

            // No release code ran in the killed process; the operating system drops the handle, a moment after the
            // exit is reported when the box is busy. The runner's own wait (5 s by default) must cover it.
            var release = Stopwatch.StartNew();
            using (var free = await RunnerLock.AcquireAsync(db, TimeSpan.FromSeconds(5), Ct))
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"lock free {release.ElapsedMilliseconds} ms after the killed worker's exit was reported");
                Assert.NotNull(free);
            }

            using var third = StartWorker(db);
            await WaitForLineAsync(third, NotReady);
            third.Process.Kill(entireProcessTree: true);
            await third.Process.WaitForExitAsync(Ct);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The built <c>urlog</c> next to this test project's output, same configuration.</summary>
    internal static string CliPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = dir.Parent!.Name; // .../bin/<configuration>/net10.0/
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir!.FullName, "src", "Cli", "bin", configuration, "net10.0", "urlog.dll");
        Assert.True(File.Exists(path), $"urlog.dll is not built at {path}");
        return path;
    }

    private static Worker StartWorker(string db)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // A port nothing listens on: the worker takes the lock, migrates, then waits for an event store that never answers.
        foreach (var argument in new[] { CliPath(), "worker", "--db", db, "--clickhouse", "127.0.0.1", "--native-port", "1", "--min-free-gb", "0", "--min-free-percent", "0" })
        {
            start.ArgumentList.Add(argument);
        }

        return new Worker(Process.Start(start)!);
    }

    private static async Task WaitForLineAsync(Worker worker, string text)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (await worker.Process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Contains(text, StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail($"the worker ended without printing '{text}': {await worker.Process.StandardError.ReadToEndAsync(Ct)}");
    }

    private sealed class Worker(Process process) : IDisposable
    {
        public Process Process => process;

        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }
    }
}
