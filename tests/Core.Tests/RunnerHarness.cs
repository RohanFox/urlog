using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Urlog.Core.Audit;
using Urlog.Core.Ingest;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Core.Tests;

/// <summary>
/// An event store in memory for the runner's tests: rows counted per (file, generation), the visible generation per
/// file, the two deletes with the real predicates, and a disk whose free space a test sets.
/// </summary>
internal sealed class FakeEventStore : IJobEventStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(ulong File, uint Generation), long> _rows = [];
    private readonly Dictionary<ulong, uint> _current = [];
    private readonly List<string> _calls = [];
    private readonly Dictionary<(ulong Case, int Month), long> _partitions = [];
    private readonly HashSet<ulong> _fieldStats = [];
    private readonly HashSet<(ulong Case, ulong File, uint Generation)> _histogram = [];
    private int _identityReads;

    /// <summary>Called at the start of every call the purge makes, with its name; a test throws from it to stop a purge there.</summary>
    public Action<string>? BeforePurgeCall { get; set; }

    /// <summary>The delete of generation rows returns and removes nothing, as a delete that is still being applied would look.</summary>
    public bool GenerationDeleteStuck { get; set; }

    /// <summary>A partition that comes back after every drop, as a writer nobody stopped would make it.</summary>
    public (ulong Case, int Month)? PartitionThatReturns { get; set; }

    public long FreeBytes { get; set; } = 500L << 30;

    public long TotalBytes { get; set; } = 1000L << 30;

    public string Endpoint => "fake-store:9000";

    /// <summary>The identity this store carries; null for none. The harness gives its metadata file the same one.</summary>
    public string? StoreId { get; set; }

    /// <summary>How often the identity was read.</summary>
    public int IdentityReads => Volatile.Read(ref _identityReads);

    /// <summary>Thrown by the next fill of the stored histogram, once.</summary>
    public Exception? FailNextFill { get; set; }

    /// <summary>The generations of the file that have a stored histogram.</summary>
    public uint[] HistogramGenerations(ulong file)
    {
        lock (_lock)
        {
            return [.. _histogram.Where(k => k.File == file).Select(k => k.Generation).Order()];
        }
    }

    /// <summary>Whether the case has any stored histogram left.</summary>
    public bool HasHistogram(ulong caseId)
    {
        lock (_lock)
        {
            return _histogram.Any(k => k.Case == caseId);
        }
    }

    /// <summary>Thrown by the next generation commit, once.</summary>
    public Exception? FailNextCommit { get; set; }

    /// <summary>Batches the sinks refuse with this before they take any more: each refusal uses one up.</summary>
    public int RefuseSubmits { get; set; }

    /// <summary>What a refused submit throws.</summary>
    public Func<Exception> Refusal { get; set; } = () => new IOException("the store refused the batch");

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public long Rows(ulong file, uint generation)
    {
        lock (_lock)
        {
            return _rows.GetValueOrDefault((file, generation));
        }
    }

    /// <summary>Every generation of the file that has rows.</summary>
    public uint[] Generations(ulong file)
    {
        lock (_lock)
        {
            return [.. _rows.Where(r => r.Key.File == file && r.Value > 0).Select(r => r.Key.Generation).Order()];
        }
    }

    /// <summary>What <c>events_v</c> would show: the rows of the committed generation, nothing without a commit.</summary>
    public long Visible(ulong file)
    {
        lock (_lock)
        {
            return _current.TryGetValue(file, out var generation) ? _rows.GetValueOrDefault((file, generation)) : 0;
        }
    }

    public uint Current(ulong file)
    {
        lock (_lock)
        {
            return _current.GetValueOrDefault(file);
        }
    }

    public void Seed(ulong file, uint generation, long rows, uint? current = null)
    {
        lock (_lock)
        {
            _rows[(file, generation)] = rows;
            if (current is { } c)
            {
                _current[file] = c;
            }
        }
    }

    /// <summary>Rows in one month of a case, as the insert path would have left them.</summary>
    public void SeedPartition(ulong caseId, int month, long rows)
    {
        lock (_lock)
        {
            _partitions[(caseId, month)] = rows;
        }
    }

    public void SeedFieldStats(ulong caseId)
    {
        lock (_lock)
        {
            _fieldStats.Add(caseId);
        }
    }

    public int[] Partitions(ulong caseId)
    {
        lock (_lock)
        {
            return [.. _partitions.Keys.Where(k => k.Case == caseId).Select(k => k.Month).Order()];
        }
    }

    public bool HasFieldStats(ulong caseId)
    {
        lock (_lock)
        {
            return _fieldStats.Contains(caseId);
        }
    }

    public Task RequireSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> ReadStoreIdentityAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _identityReads);
        return Task.FromResult(StoreId);
    }

    public Task<IReadOnlyList<CasePartition>> CasePartitionsAsync(ulong caseId, CancellationToken cancellationToken)
    {
        BeforePurgeCall?.Invoke("partitions");
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<CasePartition>>([.. _partitions.Where(p => p.Key.Case == caseId).OrderBy(p => p.Key.Month).Select(p => new CasePartition(p.Key.Month, p.Value, p.Value * 40))]);
        }
    }

    public Task DropCasePartitionAsync(ulong caseId, int yyyymm, CancellationToken cancellationToken)
    {
        BeforePurgeCall?.Invoke("drop");
        lock (_lock)
        {
            _calls.Add($"drop {caseId} {yyyymm}");
            if (PartitionThatReturns != (caseId, yyyymm))
            {
                _partitions.Remove((caseId, yyyymm));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>What a statistics run does in place of the store: throw to fail it, or return the events it counted.</summary>
    public Func<ulong, ulong>? OnFieldStats { get; set; }

    /// <summary>The <c>computed_at</c> of every statistics run, in order, with its case.</summary>
    public List<(ulong Case, DateTime ComputedAt)> StatsRuns { get; } = [];

    public Task<FieldStatsSet> RefreshFieldStatsAsync(ulong caseId, IReadOnlyList<StatsField> fields, FieldStatsLimits limits, DateTime computedAt, CancellationToken cancellationToken)
    {
        var events = OnFieldStats?.Invoke(caseId) ?? 0;
        lock (_lock)
        {
            _calls.Add($"refresh-field-stats {caseId}");
            StatsRuns.Add((caseId, computedAt));
            _fieldStats.Add(caseId);
        }

        return Task.FromResult(new FieldStatsSet(events, [new FieldStatsRow(string.Empty, string.Empty, events)], []));
    }

    public Task DeleteCaseFieldStatsAsync(ulong caseId, CancellationToken cancellationToken)
    {
        BeforePurgeCall?.Invoke("field-stats");
        lock (_lock)
        {
            _calls.Add($"field-stats {caseId}");
            _fieldStats.Remove(caseId);
        }

        return Task.CompletedTask;
    }

    public Task ClearFileGenerationsAsync(IReadOnlyList<ulong> fileIds, CancellationToken cancellationToken)
    {
        BeforePurgeCall?.Invoke("clear-generations");
        lock (_lock)
        {
            _calls.Add($"clear-generations {string.Join(',', fileIds)}");
            if (!GenerationDeleteStuck)
            {
                foreach (var id in fileIds)
                {
                    _current.Remove(id);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<long> CountFileGenerationsAsync(IReadOnlyList<ulong> fileIds, CancellationToken cancellationToken)
    {
        BeforePurgeCall?.Invoke("count-generations");
        lock (_lock)
        {
            return Task.FromResult((long)fileIds.Count(_current.ContainsKey));
        }
    }

    public Task DeleteFileGenerationsExceptAsync(ulong caseId, ulong fileId, uint keep, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add($"delete-except {fileId} keep {keep}");
            foreach (var key in _rows.Keys.Where(k => k.File == fileId && k.Generation != keep).ToList())
            {
                _rows.Remove(key);
            }

            _histogram.RemoveWhere(k => k.File == fileId && k.Generation != keep);
        }

        return Task.CompletedTask;
    }

    public Task DeleteFileGenerationsBelowAsync(ulong caseId, ulong fileId, uint generation, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add($"delete-below {fileId} {generation}");
            foreach (var key in _rows.Keys.Where(k => k.File == fileId && k.Generation < generation).ToList())
            {
                _rows.Remove(key);
            }

            _histogram.RemoveWhere(k => k.File == fileId && k.Generation < generation);
        }

        return Task.CompletedTask;
    }

    public Task FillHistogramAsync(ulong caseId, ulong fileId, uint generation, long fromMilliseconds, long toMilliseconds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (FailNextFill is { } failure)
            {
                FailNextFill = null;
                throw failure;
            }

            _calls.Add($"fill-histogram {fileId} {generation}");
            if (!_histogram.Add((caseId, fileId, generation)))
            {
                throw new InvalidOperationException($"the histogram of file {fileId} generation {generation} was filled twice");
            }
        }

        return Task.CompletedTask;
    }

    public Task DropCaseHistogramAsync(ulong caseId, CancellationToken cancellationToken)
    {
        BeforePurgeCall?.Invoke("drop-histogram");
        lock (_lock)
        {
            _calls.Add($"drop-histogram {caseId}");
            _histogram.RemoveWhere(k => k.Case == caseId);
        }

        return Task.CompletedTask;
    }

    public Task<long> CaseHistogramRowsAsync(ulong caseId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult((long)_histogram.Count(k => k.Case == caseId));
        }
    }

    public Task CommitFileGenerationsAsync(IReadOnlyList<KeyValuePair<ulong, uint>> generations, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (FailNextCommit is { } failure)
            {
                FailNextCommit = null;
                throw failure;
            }

            foreach (var (file, generation) in generations)
            {
                _calls.Add($"commit {file} {generation}");
                _current[file] = generation;
            }
        }

        return Task.CompletedTask;
    }

    public Task<Dictionary<ulong, uint>> ReadFileGenerationsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(new Dictionary<ulong, uint>(_current));
        }
    }

    public Task<uint> ReadFileGenerationAsync(ulong fileId, CancellationToken cancellationToken) => Task.FromResult(Current(fileId));

    public Task<IReadOnlyList<DiskSpace>> DiskSpaceAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiskSpace>>([new DiskSpace("clickhouse:default", FreeBytes, TotalBytes)]);

    public IJobSink OpenSink(PipelineOptions pipeline) => new Sink(this);

    private sealed class Sink(FakeEventStore store) : IJobSink
    {
        private long _acked;

        public long AckedRows => Interlocked.Read(ref _acked);

        public void Submit(EventBatch batch)
        {
            lock (store._lock)
            {
                if (store.RefuseSubmits > 0)
                {
                    store.RefuseSubmits--;
                    batch.Return();
                    throw store.Refusal();
                }
            }

            if (batch.RowCount > 0)
            {
                lock (store._lock)
                {
                    var key = (batch.FileId[0], batch.ParseGeneration[0]);
                    store._rows[key] = store._rows.GetValueOrDefault(key) + batch.RowCount;
                }

                Interlocked.Add(ref _acked, batch.RowCount);
            }

            batch.Return();
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A local volume whose free space a test sets.</summary>
internal sealed class FakeDiskProbe : IDiskProbe
{
    public long FreeBytes { get; set; } = 500L << 30;

    public long TotalBytes { get; set; } = 1000L << 30;

    public DiskSpace Measure(string path) => new(path, FreeBytes, TotalBytes);
}

/// <summary>
/// The numbered-line source with a hook before every chunk: tests hold a parse open, count who is parsing at once,
/// or throw on a chosen chunk.
/// </summary>
internal sealed class HookedSource(Action<ParseContext> beforeChunk, bool stateful = true) : ILogSource
{
    public const string Id = "hooked";

    private readonly NumberedLineSource _inner = new(stateful);

    public SourceInfo Info { get; } = new(Id, [], "hooked lines", 1, stateful);

    public Framing Framing => _inner.Framing;

    public FieldSchema Schema => _inner.Schema;

    public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => 0f;

    public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch)
    {
        beforeChunk(ctx);
        _inner.Parse(framedChunk, ctx, batch);
    }
}

/// <summary>The numbered-line source declared as a format with naive timestamps, for the tests of the DST flag.</summary>
internal sealed class NaiveLineSource : ILogSource
{
    public const string Id = "naive-lines";

    private readonly NumberedLineSource _inner = new();

    public SourceInfo Info { get; } = new(Id, [], "numbered lines read with the zone hint", 1, NaiveTimestamps: true);

    public Framing Framing => _inner.Framing;

    public FieldSchema Schema => _inner.Schema;

    public float Sniff(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, SniffContext ctx) => 0f;

    public void Parse(ReadOnlySequence<byte> framedChunk, ParseContext ctx, IBatchWriter batch) => _inner.Parse(framedChunk, ctx, batch);
}

/// <summary>A migrated metadata file, a directory for evidence files, a fake event store, and the calls a runner test makes.</summary>
internal sealed class RunnerHarness : IAsyncDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("urlog-runner-");
    private long _ownerId;

    private RunnerHarness()
    {
        Store = new MetadataStore(Path.Combine(_directory.FullName, "metadata.db"));
    }

    public MetadataStore Store { get; }

    public FakeEventStore Events { get; } = new();

    public FakeDiskProbe Disk { get; } = new();

    /// <summary>Called before every chunk of the <c>hooked</c> source.</summary>
    public Action<ParseContext> BeforeChunk { get; set; } = _ => { };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Small chunks and batches, so a few kilobytes of lines are several chunks and several batches.</summary>
    public static PipelineOptions SmallPipeline { get; } = new() { ChunkBytes = 4096, MaxRecordBytes = 4096, BatchRows = 50, Parallelism = 2, InFlightChunks = 4, InFlightBatches = 2, Writers = 1 };

    public static async Task<RunnerHarness> CreateAsync()
    {
        var harness = new RunnerHarness();
        await harness.Store.MigrateAsync(Ct);
        await using var db = harness.Store.CreateDbContext();
        var user = new User { Name = "alice", PasswordHash = "argon2id$placeholder", CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        harness._ownerId = user.Id;
        // A pair: the metadata file and the fake store carry one identity.
        harness.Events.StoreId = StoreIdentity.NewId();
        db.StoreIdentity.Add(new StoreIdentityRow { StoreId = harness.Events.StoreId, CreatedAt = DateTime.UtcNow, Origin = StoreIdentityOrigin.Created });
        await db.SaveChangesAsync(Ct);
        return harness;
    }

    public static JobRunnerOptions Options(bool once = true, int jobs = 1) => new()
    {
        MaxConcurrentJobs = jobs,
        Pipeline = SmallPipeline,
        ExitWhenIdle = once,
        IdlePoll = TimeSpan.FromMilliseconds(20),
        ProgressInterval = TimeSpan.FromMilliseconds(50),
        WatermarkInterval = TimeSpan.FromMilliseconds(50),
        CommitBusyTimeout = TimeSpan.FromSeconds(20),
        MemoryLimitBackoff = TimeSpan.FromMilliseconds(300),
        LockWait = TimeSpan.FromMilliseconds(200),
        WorkerName = "test",
    };

    /// <summary>Where the harness keeps its files: evidence that "arrived" is written here.</summary>
    public string Root => _directory.FullName;

    /// <summary>The cache directory the harness's runners use for seekable copies of containers.</summary>
    public string CacheRoot => Path.Combine(_directory.FullName, "archive-cache");

    /// <summary>The intake pipeline over the harness's metadata file and the sources its runners know.</summary>
    public Urlog.Core.Intake.IntakePipeline Intake(string? evidenceRoot = null, Urlog.Core.Intake.Archives.ArchiveLimits? limits = null) => new(Store, Catalog(), evidenceRoot, limits: limits);

    /// <summary>Writes <paramref name="content"/> under the harness directory and returns its path.</summary>
    public async Task<string> WriteAsync(string name, byte[] content)
    {
        var path = Path.Combine(_directory.FullName, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, Ct);
        return path;
    }

    public JobRunner Runner(JobRunnerOptions? options = null)
    {
        options ??= Options();
        return new JobRunner(Store, Events, Catalog(), options.CacheRoot is null ? options with { CacheRoot = CacheRoot } : options, Disk);
    }

    private SourceCatalog Catalog()
    {
        var catalog = new SourceCatalog();
        catalog.Add(LogSourceRegistry.Registrations);
        catalog.Add([new SourceRegistration(HookedSource.Id, [], () => new HookedSource(ctx => BeforeChunk(ctx)))]);
        catalog.Add([new SourceRegistration(NaiveLineSource.Id, [], () => new NaiveLineSource())]);
        // The fallback id the runner resolves a file without a source to; the numbered-line source stands in for it.
        catalog.Add([new SourceRegistration(Urlog.Core.Detection.FormatDetector.GenericSourceId, [], () => new NumberedLineSource())]);
        return catalog;
    }

    public MetadataDb Open() => Store.CreateDbContext();

    public async Task<CaseRecord> AddCaseAsync(string name)
    {
        await using var db = Open();
        return await CaseOperations.CreateAsync(db, Store.Audit, new AuditActor("alice"), name, _ownerId, DateTime.UtcNow, Ct);
    }

    /// <summary>Writes <paramref name="content"/> to a file, records it as intake would (hash first) and queues its parse.</summary>
    public async Task<(EvidenceFile File, Job Job, string Path)> AddFileAsync(long caseId, byte[] content, string name, string? source = "test-lines", string? compression = null, string? timezone = null, DateTime? fileTime = null)
    {
        var path = Path.Combine(_directory.FullName, $"{caseId}-{name}");
        await File.WriteAllBytesAsync(path, content, Ct);
        await using var db = Open();
        var host = await HostOperations.GetOrCreateAsync(db, caseId, "web-01", DateTime.UtcNow, Ct);
        var file = new EvidenceFile
        {
            CaseId = caseId,
            HostId = host.Id,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Size = content.Length,
            OriginalName = name,
            StoredPath = path,
            Source = FileSource.Cli,
            Uploader = "alice",
            ReceivedAt = DateTime.UtcNow,
            Encoding = "utf8",
            Compression = compression,
            Timezone = timezone,
            FileTime = fileTime,
            DetectedSource = source,
        };
        var added = (await FileOperations.AddAsync(db, Store.Audit, new AuditActor("alice"), file, Ct)).File;
        var job = await JobOperations.EnqueueAsync(db, added.Id, DateTime.UtcNow, Ct);
        return (added, job, path);
    }

    public async Task<Job> JobAsync(long id)
    {
        await using var db = Open();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct);
    }

    public async Task<EvidenceFile> FileAsync(long id)
    {
        await using var db = Open();
        return await db.Files.AsNoTracking().SingleAsync(f => f.Id == id, Ct);
    }

    public async Task<List<AuditRow>> AuditAsync()
    {
        await using var db = Open();
        return await db.Audit.AsNoTracking().OrderBy(a => a.Seq).ToListAsync(Ct);
    }

    /// <summary>Polls until <paramref name="condition"/> holds; fails the test after <paramref name="seconds"/>.</summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(20, Ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            // Cache copies and evidence under a root are read-only on disk, which a recursive delete does not get past.
            foreach (var file in _directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            _directory.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A straggling handle on Windows: the temp directory is cleaned by the OS eventually.
        }

        return ValueTask.CompletedTask;
    }
}
