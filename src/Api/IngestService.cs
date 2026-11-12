using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Microsoft.Extensions.Options;

namespace Urlog.Api;

/// <summary>
/// The job runner inside the backend: workers are threads of this process (design record, "Consistency"). It runs as
/// the ingest account and holds the metadata file's runner lock; when a <c>urlog worker</c> already holds it, the
/// backend serves without ingesting and says so. Stopping the host stops the running jobs and puts them back in the
/// queue.
/// </summary>
public sealed class IngestService(MetadataStore store, IOptions<UrlogOptions> options, IWebHostEnvironment environment, ILoggerFactory loggers, ILogger<IngestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var urlog = options.Value;
        var ingest = urlog.Ingest;
        var clickHouse = urlog.ClickHouse;
        var parallelism = ingest.Parallelism > 0 ? ingest.Parallelism : Math.Min(Environment.ProcessorCount, 8);
        var runnerOptions = new JobRunnerOptions
        {
            MaxConcurrentJobs = ingest.MaxConcurrentJobs,
            Pipeline = ingest.SmallBox ? PipelineOptions.SmallBox : PipelineOptions.Sized(parallelism, ingest.Writers),
            EvidenceRoot = string.IsNullOrWhiteSpace(ingest.EvidenceRoot) ? null : Rooted(ingest.EvidenceRoot),
            ProgressInterval = ingest.ProgressInterval,
            Watermark = new WatermarkOptions { MinFreeBytes = ingest.Watermark.MinFreeBytes, MinFreePercent = ingest.Watermark.MinFreePercent, Paths = [.. ingest.Watermark.Paths] },
            NightlyBackupAtUtc = ingest.NightlyBackupAtUtc,
            BackupDirectory = string.IsNullOrWhiteSpace(ingest.BackupDirectory) ? null : Rooted(ingest.BackupDirectory),
        };

        var catalog = new SourceCatalog();
        catalog.Add(Urlog.Sources.LogSourceRegistry.Registrations);
        var events = ClickHouseJobEventStore.Create(clickHouse.Host, clickHouse.NativePort, clickHouse.User, clickHouse.Password, runnerOptions.Pipeline.Writers);
        Log.IngestStarting(logger, clickHouse.User, clickHouse.Host, clickHouse.NativePort, runnerOptions.MaxConcurrentJobs);
        try
        {
            await new JobRunner(store, events, catalog, runnerOptions, loggers: loggers).RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (RunnerLockedException e)
        {
            Log.IngestNotStarted(logger, e.Message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private string Rooted(string path) => Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path);
}
