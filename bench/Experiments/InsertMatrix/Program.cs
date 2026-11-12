using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClickHouse.Driver.Tcp;
using Urlog.Bench;
using Urlog.Bench.Fixtures;
using Urlog.Core.Ingest;
using Urlog.Core.Ingest.Native;
using Urlog.Core.Pipeline;
using Urlog.Core.Sources;

namespace Urlog.Experiments.InsertMatrix;

/// <summary>
/// The insert-path experiment matrix. Each cell inserts a fixed row budget (the same pre-built blocks, cycled) through one
/// path with N concurrent connections into a table with or without the text index, and records client throughput,
/// client CPU and allocation, per-insert latency, ClickHouse CPU and memory (docker stats), the merge backlog and
/// active parts during the cell, and how long merges take to drain afterwards. Rows are counted back after every cell.
/// Paths: <c>native</c> (one block per INSERT), <c>native-x&lt;k&gt;</c> (k blocks per INSERT), <c>native-sorted</c>
/// (unsorted blocks permuted on the client, see <see cref="BlockOrder"/>), <c>native-nolz4</c>, <c>inserter</c> (the
/// production <see cref="NativeInserter"/> with Writers = conns and <c>--rows-per-insert</c>, for sustained runs), <c>driver</c> (column API,
/// MaxRowsPerBlock = block size), <c>driver50k</c> (the driver's default wire blocks).
/// </summary>
internal static class Program
{
    private const string Container = "urlog-clickhouse";

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        Console.WriteLine($"InsertMatrix: fixture={options.Fixture} rows/cell={options.RowsPerCell:N0} blocks={string.Join(",", options.BlockSizes)} conns={string.Join(",", options.Connections)} paths={string.Join(",", options.Paths)} index={string.Join(",", options.Index.Select(i => i ? "on" : "off"))}");
        Console.WriteLine($"client: {Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}, GC server={System.Runtime.GCSettings.IsServerGC}; ClickHouse at {BenchEnv.Host}:{BenchEnv.TcpPort}");

        using var admin = new ClickHouseTcpClient(BenchEnv.TcpConnectionString);
        var version = (string?)admin.ExecuteScalarAsync("SELECT version()").AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"server: ClickHouse {version}");

        using var docker = new DockerStatsSampler(Container);
        var clock = new Stopwatch();
        clock.Start();
        using var server = new ServerSampler(BenchEnv.TcpConnectionString, clock);

        var results = new List<CellResult>();
        var csv = options.Out ?? Path.Combine(Path.GetTempPath(), $"insert-matrix-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(csv, CellResult.CsvHeader + "\n");
        Console.WriteLine($"csv: {csv}");

        try
        {
            foreach (var index in options.Index)
            {
                var table = index ? "insert_matrix_idx" : "insert_matrix_noidx";
                Execute(admin, $"DROP TABLE IF EXISTS {table}");
                Execute(admin, Ddl(table, index));
                server.Table = table;

                foreach (var blockSize in options.BlockSizes)
                {
                    var distinct = Math.Max(2, options.DistinctRows / blockSize);
                    var sw = Stopwatch.StartNew();
                    using var blocks = options.Fixture == "syslog" ? EventBatchSet.Syslog(blockSize, distinct) : EventBatchSet.Nginx(blockSize, distinct);
                    Console.WriteLine($"\nblocks: {distinct} x {blockSize:N0} rows of {blocks.Shape} in {sw.Elapsed.TotalSeconds:F1} s; {blocks.RawBytes / 1e6:N1} MB raw, {blocks.Extras / (double)blocks.Rows:F1} extras/row, {blocks.SortedBlocks}/{distinct} blocks sorted by (case_id, host, ts)");

                    IColumn[][]? driverColumns = null;
                    if (options.Paths.Any(p => p.StartsWith("driver", StringComparison.Ordinal)))
                    {
                        sw.Restart();
                        var before = GC.GetTotalAllocatedBytes(true);
                        driverColumns = blocks.Batches.Select(DriverColumns.Convert).ToArray();
                        var allocated = GC.GetTotalAllocatedBytes(true) - before;
                        Console.WriteLine($"driver columns: EventBatch -> IColumn[] for {distinct} blocks in {sw.Elapsed.TotalMilliseconds / distinct:F0} ms/block, {allocated / (double)blocks.Rows:F0} B/row allocated (setup only, not in the cells)");
                    }

                    if (options.Verify && blockSize == options.BlockSizes[0])
                    {
                        Verify(admin, blocks.Batches[0], driverColumns?[0], index);
                    }

                    foreach (var path in options.Paths)
                    {
                        foreach (var connections in options.Connections)
                        {
                            var cell = RunCell(admin, server, docker, table, index, path, connections, blocks, driverColumns, options.RowsPerCell, options.RowsPerInsert);
                            results.Add(cell);
                            Console.WriteLine(cell.ToMarkdownRow());
                            File.AppendAllText(csv, cell.ToCsvRow() + "\n");
                        }
                    }
                }

                if (!options.Keep)
                {
                    Execute(admin, $"DROP TABLE IF EXISTS {table}");
                }
            }
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine(CellResult.MarkdownHeader);
            foreach (var r in results)
            {
                Console.WriteLine(r.ToMarkdownRow());
            }

            Console.WriteLine($"\ncsv: {csv}");
        }

        return 0;
    }

    private static NativeInserterOptions Endpoint(bool compress) => new()
    {
        Host = BenchEnv.Host,
        Port = int.Parse(BenchEnv.TcpPort, CultureInfo.InvariantCulture),
        Database = "default",
        User = BenchEnv.User,
        Password = BenchEnv.Password,
        Compress = compress,
    };

    private static CellResult RunCell(ClickHouseTcpClient admin, ServerSampler server, DockerStatsSampler docker, string table, bool index, string path, int connections, EventBatchSet blocks, IColumn[][]? driverColumns, long rowsPerCell, int rowsPerInsert)
    {
        var blockSize = blocks.BlockSize;
        var totalBlocks = (int)Math.Ceiling(rowsPerCell / (double)blockSize);
        var distinct = blocks.Batches.Count;
        var sql = $"INSERT INTO {table} VALUES";
        var sqlBytes = Encoding.UTF8.GetBytes(sql);
        var compress = !path.EndsWith("-nolz4", StringComparison.Ordinal);
        var sortBlocks = path == "native-sorted";
        var blocksPerInsert = path.StartsWith("native-x", StringComparison.Ordinal) ? int.Parse(path.AsSpan("native-x".Length), CultureInfo.InvariantCulture) : 1;
        var latenciesMs = new double[totalBlocks];
        var next = -1;
        var sorted = 0;
        var errors = new List<Exception>();
        var warmedUp = new CountdownEvent(path == "inserter" ? 1 : connections);
        var go = new ManualResetEventSlim(false);

        ClickHouseTcpClient? driver = null;
        ClickHouseTcpInsertOptions? insertOptions = null;
        if (path.StartsWith("driver", StringComparison.Ordinal))
        {
            var o = ClickHouseTcpClientOptions.FromConnectionString(BenchEnv.TcpConnectionString) with { MinPoolSize = connections, MaxPoolSize = connections };
            driver = new ClickHouseTcpClient(o);
            insertOptions = path == "driver50k" ? new ClickHouseTcpInsertOptions() : new ClickHouseTcpInsertOptions { MaxRowsPerBlock = blockSize };
        }

        var useInserter = path == "inserter";
        var progress = new List<(long Seconds, long RowsIn30s, long Inserts)>();
        NativeInserter? inserter = null;
        if (useInserter)
        {
            inserter = new NativeInserter(Endpoint(compress) with { Table = table, Writers = connections, SortBlocks = sortBlocks, MaxRowsPerInsert = rowsPerInsert }, queueCapacity: 2, returnBatches: false);
        }

        var threads = new Thread[useInserter ? 1 : connections];
        for (var t = 0; t < threads.Length; t++)
        {
            var id = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    if (inserter is not null)
                    {
                        // The production sink: one submitting thread (the parsers' role), Writers connections inside.
                        warmedUp.Signal();
                        go.Wait();
                        var started = Stopwatch.StartNew();
                        var lastReport = 0L;
                        var lastRows = 0L;
                        for (var i = 0; i < totalBlocks; i++)
                        {
                            inserter.Submit(blocks.Batches[i % distinct]);
                            var bucket = (long)(started.Elapsed.TotalSeconds / 30);
                            if (bucket > lastReport)
                            {
                                var rowsNow = inserter.Rows;
                                lock (progress)
                                {
                                    progress.Add((bucket * 30, rowsNow - lastRows, inserter.Inserts));
                                }

                                lastRows = rowsNow;
                                lastReport = bucket;
                            }
                        }

                        inserter.CompleteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    }
                    else if (driver is not null)
                    {
                        // warm-up: one block per connection opens the pooled connection and JITs the codecs
                        driver.InsertAsync(sql, driverColumns![id % distinct], insertOptions, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                        warmedUp.Signal();
                        go.Wait();
                        int i;
                        while ((i = Interlocked.Increment(ref next)) < totalBlocks)
                        {
                            var start = Stopwatch.GetTimestamp();
                            driver.InsertAsync(sql, driverColumns![i % distinct], insertOptions, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                            latenciesMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        }
                    }
                    else
                    {
                        using var connection = NativeConnection.Connect(Endpoint(compress));
                        var serializer = new NativeBlockSerializer();
                        var scratch = Array.Empty<int>();
                        connection.BeginInsert(sqlBytes);
                        connection.WriteBlock(serializer, blocks.Batches[id % distinct]);
                        connection.EndInsert();
                        warmedUp.Signal();
                        go.Wait();
                        int first;
                        while ((first = Interlocked.Add(ref next, blocksPerInsert) - blocksPerInsert + 1) < totalBlocks)
                        {
                            var count = Math.Min(blocksPerInsert, totalBlocks - first);
                            var start = Stopwatch.GetTimestamp();
                            connection.BeginInsert(sqlBytes);
                            for (var j = 0; j < count; j++)
                            {
                                var batch = blocks.Batches[(first + j) % distinct];
                                ReadOnlySpan<int> order = default;
                                if (sortBlocks && !BlockOrder.IsSorted(batch))
                                {
                                    var n = BlockOrder.Compute(batch, ref scratch); // Compute may replace scratch: slice after the call
                                    order = scratch.AsSpan(0, n);
                                    Interlocked.Increment(ref sorted);
                                }

                                connection.WriteBlock(serializer, batch, order);
                            }

                            connection.EndInsert();
                            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            for (var j = 0; j < count; j++)
                            {
                                latenciesMs[first + j] = ms;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    lock (errors)
                    {
                        errors.Add(e);
                    }

                    if (!warmedUp.IsSet)
                    {
                        warmedUp.Signal();
                    }

                    Volatile.Write(ref next, totalBlocks); // stop the others
                }
            })
            { IsBackground = true, Name = $"{path}-{t}" };
        }

        Execute(admin, $"TRUNCATE TABLE {table}");
        foreach (var t in threads)
        {
            t.Start();
        }

        warmedUp.Wait();
        if (errors.Count > 0)
        {
            go.Set();
            foreach (var t in threads)
            {
                t.Join();
            }

            throw new AggregateException($"{path} x{connections} failed in warm-up", errors);
        }

        Execute(admin, $"TRUNCATE TABLE {table}");
        WaitForIdleMerges(server, table, TimeSpan.FromSeconds(30));

        var process = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocated0 = GC.GetTotalAllocatedBytes(true);
        var cpu0 = process.TotalProcessorTime;
        var gen0 = GC.CollectionCount(0);
        var from = docker.Now;
        var sw = Stopwatch.StartNew();
        go.Set();
        foreach (var t in threads)
        {
            t.Join();
        }

        sw.Stop();
        var to = docker.Now;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpu0).TotalSeconds / sw.Elapsed.TotalSeconds;
        var allocated = GC.GetTotalAllocatedBytes(true) - allocated0;
        var gen0Count = GC.CollectionCount(0) - gen0;
        driver?.Dispose();
        if (inserter is not null)
        {
            Console.WriteLine($"inserter: {inserter.Rows:N0} rows acknowledged in {inserter.Inserts} INSERTs from {inserter.Blocks} blocks ({inserter.BlocksSorted} permuted), {inserter.BytesSent / 1e6:N0} MB on the wire for {inserter.PayloadBytes / 1e6:N0} MB of payload");
            inserter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (sw.Elapsed.TotalSeconds >= 60)
        {
            if (progress.Count > 0)
            {
                Console.WriteLine("throughput (per 30 s): seconds, rows acknowledged in the window, rows/s, INSERTs so far; a stalled writer shows as a window far below the others");
                foreach (var (seconds, rowsIn30s, insertsSoFar) in progress)
                {
                    Console.WriteLine($"  {seconds,6} s  rows {rowsIn30s,12:N0}  {rowsIn30s / 30.0 / 1000,8:N0}k rows/s  inserts {insertsSoFar}");
                }
            }

            Console.WriteLine("timeline (per 30 s): seconds, merges max, parts max, server memory max GiB");
            foreach (var (seconds, mergesMax30, partsMax30, mem30) in server.Timeline(from, to, 30))
            {
                Console.WriteLine($"  {seconds,6:F0} s  merges {mergesMax30,3}  parts {partsMax30,4}  mem {mem30:F1} GiB");
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException($"{path} x{connections} failed", errors);
        }

        var rows = (long)totalBlocks * blockSize;
        long rawBytes = 0;
        for (var i = 0; i < totalBlocks; i++)
        {
            rawBytes += blocks.Batches[i % distinct].RawBytes;
        }

        var counted = Convert.ToInt64(admin.ExecuteScalarAsync($"SELECT count() FROM {table}").AsTask().GetAwaiter().GetResult(), CultureInfo.InvariantCulture);
        if (counted != rows)
        {
            throw new InvalidOperationException($"{path} x{connections}: inserted {rows} rows, table holds {counted}");
        }

        var (cpuAvg, cpuMax, memMax, cpuSamples) = docker.Window(from, to);
        var (mergesMax, partsMax, serverMemMax, _) = server.Window(from, to);
        var atEnd = server.SnapshotAsync(table).GetAwaiter().GetResult();
        var drain = Stopwatch.StartNew();
        var drained = WaitForIdleMerges(server, table, TimeSpan.FromSeconds(180));
        drain.Stop();
        var afterDrain = server.SnapshotAsync(table).GetAwaiter().GetResult();

        Array.Sort(latenciesMs);
        return new CellResult
        {
            Path = sortBlocks ? $"native-sorted ({sorted} permuted)" : inserter is not null ? $"inserter ({connections} writers, {rowsPerInsert / 1000}k/INSERT)" : path,
            Connections = connections,
            BlockSize = blockSize,
            Index = index,
            Rows = rows,
            Seconds = sw.Elapsed.TotalSeconds,
            RowsPerSecond = rows / sw.Elapsed.TotalSeconds,
            MegabytesPerSecond = rawBytes / 1e6 / sw.Elapsed.TotalSeconds,
            ClientCores = cpu,
            BytesPerRow = allocated / (double)rows,
            Gen0 = gen0Count,
            LatencyMeanMs = latenciesMs.Average(),
            LatencyP95Ms = latenciesMs[(int)(latenciesMs.Length * 0.95)],
            ServerCpuAvg = cpuAvg,
            ServerCpuMax = cpuMax,
            ServerMemMaxGiB = double.IsNaN(serverMemMax) ? memMax : Math.Max(serverMemMax, memMax),
            CpuSamples = cpuSamples,
            MergesMax = mergesMax,
            MergesAtEnd = atEnd.Merges,
            PartsMax = partsMax,
            PartsAtEnd = atEnd.Parts,
            DrainSeconds = drained ? drain.Elapsed.TotalSeconds : double.PositiveInfinity,
            PartsAfterDrain = afterDrain.Parts,
        };
    }

    /// <summary>Inserts the same block through both paths into two scratch tables and compares a content fingerprint over every column.</summary>
    private static void Verify(ClickHouseTcpClient admin, EventBatch batch, IColumn[]? driverColumns, bool index)
    {
        const string a = "insert_verify_native";
        const string b = "insert_verify_driver";
        Execute(admin, $"DROP TABLE IF EXISTS {a}");
        Execute(admin, $"DROP TABLE IF EXISTS {b}");
        Execute(admin, Ddl(a, index));
        Execute(admin, Ddl(b, index));

        using (var connection = NativeConnection.Connect(Endpoint(compress: true)))
        {
            var serializer = new NativeBlockSerializer();
            connection.BeginInsert(Encoding.UTF8.GetBytes($"INSERT INTO {a} VALUES"));
            connection.WriteBlock(serializer, batch);
            connection.EndInsert();
            Console.WriteLine($"verify: {connection.ServerVersion}, protocol {connection.ServerRevision}; header: {string.Join(", ", connection.LastHeader.Select(h => $"{h.Name} {h.Type}"))}");
            Console.WriteLine($"verify: native sent {connection.BytesSent / 1e6:N1} MB on the wire for {connection.PayloadBytes / 1e6:N1} MB of block payload ({batch.RowCount:N0} rows, {connection.PayloadBytes / (double)batch.RowCount:F0} B/row uncompressed, {connection.FramesWritten} frames)");
        }

        const string fingerprint = "SELECT concat(toString(count()), ';', toString(sum(cityHash64(case_id, host_id, file_id, file_offset, parse_generation, ts, host, program, severity, event_code, event_action, event_outcome, user_name, source_ip, destination_ip, process_name, url_path, http_status, message, raw, extras, http_method, http_version, http_response_body_bytes, http_referrer, user_agent, url_query, syslog_facility, process_pid))), ';', toString(uniqExact(host)), ';', toString(sum(length(raw))), ';', toString(sum(length(extras)))) FROM ";
        var native = (string?)admin.ExecuteScalarAsync(fingerprint + a).AsTask().GetAwaiter().GetResult();
        if (!BlockOrder.IsSorted(batch))
        {
            // The permuted write must produce the same rows (the fingerprint is order-independent) and a part the server sees as sorted.
            const string s = "insert_verify_sorted";
            Execute(admin, $"DROP TABLE IF EXISTS {s}");
            Execute(admin, Ddl(s, index));
            using (var connection = NativeConnection.Connect(Endpoint(compress: true)))
            {
                var scratch = Array.Empty<int>();
                var n = BlockOrder.Compute(batch, ref scratch);
                connection.BeginInsert(Encoding.UTF8.GetBytes($"INSERT INTO {s} VALUES"));
                connection.WriteBlock(new NativeBlockSerializer(), batch, scratch.AsSpan(0, n));
                connection.EndInsert();
            }

            var sortedFingerprint = (string?)admin.ExecuteScalarAsync(fingerprint + s).AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"verify: unsorted block, permuted write fingerprint {sortedFingerprint}");
            if (sortedFingerprint != native)
            {
                throw new InvalidOperationException("the permuted write produced different rows than the plain write");
            }

            Execute(admin, $"DROP TABLE {s}");
        }

        if (driverColumns is null)
        {
            Console.WriteLine($"verify: native fingerprint {native} (no driver path requested, nothing to compare)");
        }
        else
        {
            admin.InsertAsync($"INSERT INTO {b} VALUES", driverColumns, new ClickHouseTcpInsertOptions { MaxRowsPerBlock = batch.RowCount }).AsTask().GetAwaiter().GetResult();
            var driver = (string?)admin.ExecuteScalarAsync(fingerprint + b).AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"verify: native {native}\nverify: driver {driver}");
            if (native != driver)
            {
                var diff = (string?)admin.ExecuteScalarAsync($"SELECT toString(count()) FROM (SELECT * FROM {a} EXCEPT SELECT * FROM {b})").AsTask().GetAwaiter().GetResult();
                throw new InvalidOperationException($"native and driver paths wrote different rows ({diff} rows differ); fix the serializer before measuring");
            }

            Console.WriteLine("verify: OK, both paths wrote identical rows");
        }

        Execute(admin, $"DROP TABLE {a}");
        Execute(admin, $"DROP TABLE {b}");
    }

    private static bool WaitForIdleMerges(ServerSampler server, string table, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            var (merges, _, _) = server.SnapshotAsync(table).GetAwaiter().GetResult();
            if (merges == 0)
            {
                return true;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    private static void Execute(ClickHouseTcpClient client, string sql) => client.ExecuteAsync(sql).AsTask().GetAwaiter().GetResult();

    /// <summary>The pinned schema (design record, "ClickHouse schema"), with or without the text index, from the column metadata.</summary>
    private static string Ddl(string table, bool index) => EventSchema.CreateTable(table, index);

    private sealed record Options(string Fixture, long RowsPerCell, int[] BlockSizes, int[] Connections, string[] Paths, bool[] Index, int DistinctRows, string? Out, bool Verify, bool Keep, int RowsPerInsert)
    {
        public static Options Parse(string[] args)
        {
            var fixture = "nginx";
            long rows = 4_000_000;
            var blocks = new[] { 250_000, 500_000 };
            var conns = new[] { 1, 2, 4, 8 };
            var paths = new[] { "native", "driver" };
            var index = new[] { false, true };
            var distinctRows = 2_000_000;
            string? output = null;
            var verify = true;
            var keep = false;
            var rowsPerInsert = new NativeInserterOptions().MaxRowsPerInsert;
            foreach (var arg in args)
            {
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                var key = eq < 0 ? arg : arg[..eq];
                var value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key)
                {
                    case "--fixture":
                        fixture = value;
                        break;
                    case "--rows":
                        rows = long.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--blocks":
                        blocks = value.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        break;
                    case "--conns":
                        conns = value.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        break;
                    case "--paths":
                        paths = value.Split(',');
                        break;
                    case "--index":
                        index = value.Split(',').Select(v => v == "on").ToArray();
                        break;
                    case "--distinct-rows":
                        distinctRows = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    case "--out":
                        output = value;
                        break;
                    case "--no-verify":
                        verify = false;
                        break;
                    case "--keep":
                        keep = true;
                        break;
                    case "--rows-per-insert":
                        rowsPerInsert = int.Parse(value, CultureInfo.InvariantCulture); // the inserter path only (the rows-per-INSERT lever: 1 M)
                        break;
                    default:
                        throw new ArgumentException($"unknown option {arg}");
                }
            }

            return new Options(fixture, rows, blocks, conns, paths, index, distinctRows, output, verify, keep, rowsPerInsert);
        }
    }
}

internal sealed record CellResult
{
    public const string MarkdownHeader =
        "| path | conns | block | index | rows/s | MB/s | client cores | B/row | gen0 | lat mean / p95 ms | CH CPU avg / max % | CH mem max GiB | merges max / end | parts max / end / drained | drain s |\n" +
        "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|";

    public const string CsvHeader = "path,conns,block,index,rows,seconds,rows_per_s,mb_per_s,client_cores,bytes_per_row,gen0,lat_mean_ms,lat_p95_ms,ch_cpu_avg,ch_cpu_max,ch_mem_max_gib,cpu_samples,merges_max,merges_end,parts_max,parts_end,drain_s,parts_after_drain";

    public required string Path { get; init; }

    public required int Connections { get; init; }

    public required int BlockSize { get; init; }

    public required bool Index { get; init; }

    public required long Rows { get; init; }

    public required double Seconds { get; init; }

    public required double RowsPerSecond { get; init; }

    public required double MegabytesPerSecond { get; init; }

    public required double ClientCores { get; init; }

    public required double BytesPerRow { get; init; }

    public required int Gen0 { get; init; }

    public required double LatencyMeanMs { get; init; }

    public required double LatencyP95Ms { get; init; }

    public required double ServerCpuAvg { get; init; }

    public required double ServerCpuMax { get; init; }

    public required double ServerMemMaxGiB { get; init; }

    public required int CpuSamples { get; init; }

    public required int MergesMax { get; init; }

    public required int MergesAtEnd { get; init; }

    public required int PartsMax { get; init; }

    public required int PartsAtEnd { get; init; }

    public required double DrainSeconds { get; init; }

    public required int PartsAfterDrain { get; init; }

    public string ToMarkdownRow() =>
        $"| {Path} | {Connections} | {BlockSize / 1000}k | {(Index ? "on" : "off")} | **{RowsPerSecond / 1000:N0}k** | {MegabytesPerSecond:N0} | {ClientCores:F2} | {BytesPerRow:F1} | {Gen0} | {LatencyMeanMs:N0} / {LatencyP95Ms:N0} | {ServerCpuAvg:N0} / {ServerCpuMax:N0} | {ServerMemMaxGiB:F1} | {MergesMax} / {MergesAtEnd} | {PartsMax} / {PartsAtEnd} / {PartsAfterDrain} | {(double.IsInfinity(DrainSeconds) ? ">180" : DrainSeconds.ToString("F1", CultureInfo.InvariantCulture))} |";

    public string ToCsvRow() => string.Join(",", new object[]
    {
        Path, Connections, BlockSize, Index ? "on" : "off", Rows, Seconds.ToString("F3", CultureInfo.InvariantCulture), RowsPerSecond.ToString("F0", CultureInfo.InvariantCulture),
        MegabytesPerSecond.ToString("F1", CultureInfo.InvariantCulture), ClientCores.ToString("F2", CultureInfo.InvariantCulture), BytesPerRow.ToString("F2", CultureInfo.InvariantCulture), Gen0,
        LatencyMeanMs.ToString("F1", CultureInfo.InvariantCulture), LatencyP95Ms.ToString("F1", CultureInfo.InvariantCulture), ServerCpuAvg.ToString("F1", CultureInfo.InvariantCulture),
        ServerCpuMax.ToString("F1", CultureInfo.InvariantCulture), ServerMemMaxGiB.ToString("F2", CultureInfo.InvariantCulture), CpuSamples, MergesMax, MergesAtEnd, PartsMax, PartsAtEnd,
        DrainSeconds.ToString("F1", CultureInfo.InvariantCulture), PartsAfterDrain,
    });
}
