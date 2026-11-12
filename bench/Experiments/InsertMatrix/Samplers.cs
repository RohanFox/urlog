using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ClickHouse.Driver.Tcp;

namespace Urlog.Experiments.InsertMatrix;

/// <summary>ClickHouse container CPU and memory from <c>docker stats</c> (streaming, about one sample per second).</summary>
internal sealed partial class DockerStatsSampler : IDisposable
{
    private readonly List<(long Ticks, double CpuPercent, double MemoryGiB)> _samples = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _process;
    private readonly Thread _thread;

    public DockerStatsSampler(string container)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo("docker", $"stats --format \"{{{{.CPUPerc}}}};{{{{.MemUsage}}}}\" {container}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        _process.Start();
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "docker-stats" };
        _thread.Start();
    }

    public long Now => _clock.ElapsedTicks;

    public int SampleCount
    {
        get
        {
            lock (_samples)
            {
                return _samples.Count;
            }
        }
    }

    public (double CpuAvg, double CpuMax, double MemMaxGiB, int Samples) Window(long fromTicks, long toTicks)
    {
        lock (_samples)
        {
            var inWindow = _samples.Where(s => s.Ticks >= fromTicks && s.Ticks <= toTicks).ToList();
            if (inWindow.Count == 0)
            {
                return (double.NaN, double.NaN, double.NaN, 0);
            }

            return (inWindow.Average(s => s.CpuPercent), inWindow.Max(s => s.CpuPercent), inWindow.Max(s => s.MemoryGiB), inWindow.Count);
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }

        _process.Dispose();
    }

    [GeneratedRegex(@"([\d.]+)%;([\d.]+)\s*(B|KiB|MiB|GiB)")]
    private static partial Regex Line();

    private void ReadLoop()
    {
        var output = _process.StandardOutput;
        while (output.ReadLine() is { } line)
        {
            var m = Line().Match(line);
            if (!m.Success)
            {
                continue;
            }

            var cpu = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var mem = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var gib = m.Groups[3].Value switch
            {
                "GiB" => mem,
                "MiB" => mem / 1024,
                "KiB" => mem / (1024 * 1024),
                _ => mem / (1024.0 * 1024 * 1024),
            };
            lock (_samples)
            {
                _samples.Add((_clock.ElapsedTicks, cpu, gib));
            }
        }
    }
}

/// <summary>Merges in flight, active parts and server memory for one table, polled every 250 ms over a separate connection.</summary>
internal sealed class ServerSampler : IDisposable
{
    private readonly List<(long Ticks, int Merges, int Parts, double MemoryGiB)> _samples = [];
    private readonly Stopwatch _clock;
    private readonly ClickHouseTcpClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private volatile string _table = "";

    public ServerSampler(string connectionString, Stopwatch sharedClock)
    {
        _client = new ClickHouseTcpClient(connectionString);
        _clock = sharedClock;
        _thread = new Thread(Loop) { IsBackground = true, Name = "server-sampler" };
        _thread.Start();
    }

    public string Table
    {
        get => _table;
        set => _table = value;
    }

    public (int MergesMax, int PartsMax, double MemMaxGiB, int Samples) Window(long fromTicks, long toTicks)
    {
        lock (_samples)
        {
            var inWindow = _samples.Where(s => s.Ticks >= fromTicks && s.Ticks <= toTicks).ToList();
            if (inWindow.Count == 0)
            {
                return (0, 0, double.NaN, 0);
            }

            return (inWindow.Max(s => s.Merges), inWindow.Max(s => s.Parts), inWindow.Max(s => s.MemoryGiB), inWindow.Count);
        }
    }

    /// <summary>Per-bucket maxima of merges, parts and memory between two instants, for long cells (the 5-minute run).</summary>
    public List<(double Seconds, int MergesMax, int PartsMax, double MemMaxGiB)> Timeline(long fromTicks, long toTicks, int bucketSeconds)
    {
        var bucketTicks = (long)(Stopwatch.Frequency * (double)bucketSeconds);
        var result = new List<(double, int, int, double)>();
        lock (_samples)
        {
            for (var start = fromTicks; start < toTicks; start += bucketTicks)
            {
                var end = start + bucketTicks;
                var inBucket = _samples.Where(s => s.Ticks >= start && s.Ticks < end).ToList();
                if (inBucket.Count > 0)
                {
                    result.Add(((end - fromTicks) / (double)Stopwatch.Frequency, inBucket.Max(s => s.Merges), inBucket.Max(s => s.Parts), inBucket.Max(s => s.MemoryGiB)));
                }
            }
        }

        return result;
    }

    public async Task<(int Merges, int Parts, long Rows)> SnapshotAsync(string table)
    {
        var text = (string?)await _client.ExecuteScalarAsync(
            $"SELECT concat(toString((SELECT count() FROM system.merges WHERE database = currentDatabase() AND table = '{table}')), ';', " +
            $"toString((SELECT count() FROM system.parts WHERE database = currentDatabase() AND table = '{table}' AND active)), ';', " +
            $"toString((SELECT sum(rows) FROM system.parts WHERE database = currentDatabase() AND table = '{table}' AND active)))");
        var parts = (text ?? "0;0;0").Split(';');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(5));
        _client.Dispose();
        _stop.Dispose();
    }

    private void Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var table = _table;
            if (table.Length > 0)
            {
                try
                {
                    var text = (string?)_client.ExecuteScalarAsync(
                        $"SELECT concat(toString((SELECT count() FROM system.merges WHERE database = currentDatabase() AND table = '{table}')), ';', " +
                        $"toString((SELECT count() FROM system.parts WHERE database = currentDatabase() AND table = '{table}' AND active)), ';', " +
                        "toString((SELECT value FROM system.metrics WHERE metric = 'MemoryTracking')))",
                        cancellationToken: _stop.Token).AsTask().GetAwaiter().GetResult();
                    var f = (text ?? "0;0;0").Split(';');
                    lock (_samples)
                    {
                        _samples.Add((_clock.ElapsedTicks, int.Parse(f[0], CultureInfo.InvariantCulture), int.Parse(f[1], CultureInfo.InvariantCulture), long.Parse(f[2], CultureInfo.InvariantCulture) / (1024.0 * 1024 * 1024)));
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ClickHouseTcpException e)
                {
                    Console.Error.WriteLine($"sampler: {e.Message}");
                }
            }

            if (_stop.Token.WaitHandle.WaitOne(250))
            {
                return;
            }
        }
    }
}
