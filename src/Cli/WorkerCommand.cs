using System.Diagnostics;
using System.Globalization;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog worker --db &lt;metadata.db&gt; [--once] [--jobs n] [--parallelism n] [--writers n] [--small-box]
/// [--evidence-root dir] [--progress-interval ms] [--commit-timeout s] [--min-free-gb n] [--min-free-percent n]</c>:
/// the job runner outside the backend, for a
/// deployment that ingests from the command line. The same <see cref="JobRunner"/> the backend hosts, as the ingest
/// account. One runner per metadata file: a second worker (or a backend with ingest on) is refused.
/// </summary>
internal static class WorkerCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var store = new MetadataStore(VerifyAuditCommand.MetadataPath(args, mustExist: true));
        var connection = new Connection(args);
        Console.WriteLine(connection.AnnounceIngest());

        var parallelism = args.Int("parallelism", Math.Min(Environment.ProcessorCount, 8));
        var writers = args.Int("writers", 4);
        var pipeline = args.Flag("small-box") ? PipelineOptions.SmallBox : PipelineOptions.Sized(parallelism, writers);
        var once = args.Flag("once");
        var options = new JobRunnerOptions
        {
            MaxConcurrentJobs = args.Int("jobs", 1),
            Pipeline = pipeline,
            ExitWhenIdle = once,
            EvidenceRoot = args.Option("evidence-root"),
            // Seekable copies of compressed and nested archives; beside the metadata file when no root is given.
            CacheRoot = args.Option("cache-root") ?? (args.Option("evidence-root") is null ? Path.Combine(Path.GetDirectoryName(store.Path)!, "archive-cache") : null),
            ProgressInterval = TimeSpan.FromMilliseconds(args.Int("progress-interval", 2000)),
            CommitBusyTimeout = TimeSpan.FromSeconds(args.Int("commit-timeout", 60)),
            IdlePoll = TimeSpan.FromMilliseconds(once ? 100 : 1000),
            // A one-shot run takes no nightly backup; a long-running worker does, at 02:00 UTC.
            NightlyBackupAtUtc = once ? null : new TimeOnly(2, 0),
            BackupDirectory = Environment.GetEnvironmentVariable("URLOG_BACKUP_DIR"),
            Watermark = new WatermarkOptions { MinFreeBytes = (long)args.Int("min-free-gb", 20) << 30, MinFreePercent = args.Int("min-free-percent", 10) },
        };
        try
        {
            options.Validate();
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new CliException(e.Message);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"worker    {options.MaxConcurrentJobs} job(s) at a time, {pipeline.Parallelism} parsers, {pipeline.Writers} writers, chunks {pipeline.ChunkBytes >> 20} MiB, batches {pipeline.BatchRows:N0} rows; about {options.MaxConcurrentJobs * pipeline.EstimatePoolBytes(223) / 1e6:N0} MB of pooled buffers on nginx-shaped data"));

        var events = new ClickHouseJobEventStore(connection.Ingest, connection.Inserter(pipeline.Writers));
        using var loggers = new ConsoleLoggerFactory();
        var runner = new JobRunner(store, events, FileDetection.Catalog(), options, loggers: loggers);
        try
        {
            await runner.RunAsync(ct).ConfigureAwait(false);
        }
        catch (RunnerLockedException e)
        {
            throw new CliException(e.Message);
        }
        catch (Urlog.Core.Ingest.EventStoreSchemaException e)
        {
            throw new CliException($"{e.Message} (connected as '{connection.IngestUser}')");
        }

        using var self = Process.GetCurrentProcess();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"memory    peak working set {self.PeakWorkingSet64 / 1e6:N0} MB"));
        return ct.IsCancellationRequested ? 130 : 0;
    }

    /// <summary>The runner's log lines on stdout, one per line, flushed at once (a test reads them as they come).</summary>
    private sealed class ConsoleLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new ConsoleLogger();

        public void Dispose()
        {
        }
    }

    private sealed class ConsoleLogger : ILogger
    {
        private static readonly Lock Gate = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var level = logLevel switch
            {
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                _ => "error",
            };
            var line = $"{level,-9} {formatter(state, exception)}{(exception is null ? string.Empty : ": " + exception.Message)}";
            lock (Gate)
            {
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            }
        }
    }
}
