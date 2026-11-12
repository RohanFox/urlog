// Reproduction: a cancelled ChannelWriter<T>.WriteAsync on a full bounded channel can throw
// OperationCanceledException although its item was queued and is later read.
//
// Measured on .NET 10.0.14 (Windows 11 x64, 16 logical cores), 200,000 runs each:
//   SingleWriter = true    6,236 runs delivered the item whose WriteAsync threw (3.1 %)
//   SingleWriter = false   5,675 runs (2.8 %)
//
// Each run: a bounded channel of capacity 2 in Wait mode, one writer writing 0, 1, 2, ... with a
// cancellable token, three ReadAllAsync readers, and the reader that sees item 150 cancels the token.
// The writer remembers the item whose WriteAsync threw. A run counts when a reader, or the drain
// after every task has ended, also holds that item.
//
// The chunk pipeline's reader does not rely on this any more (src/Core/Pipeline/ParsePipeline.cs
// hands off with TryWrite). This program is not part of any test run. Run it after a runtime bump
// and when reporting upstream:
//
//   dotnet run -c Release --project tools/repro-channel-cancel [runs]
//
// Exit code 1 when any run delivered a refused item, 0 when none did.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;

var runs = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 200_000;
Console.WriteLine($"runtime   {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}), {Environment.ProcessorCount} logical cores");
Console.WriteLine($"runs      {runs:N0} per mode");

var total = 0;
foreach (var singleWriter in new[] { true, false })
{
    var delivered = 0;
    for (var i = 0; i < runs; i++)
    {
        if (await RefusedItemWasDeliveredAsync(singleWriter).ConfigureAwait(false))
        {
            delivered++;
        }
    }

    total += delivered;
    Console.WriteLine($"SingleWriter = {singleWriter,-5}  {delivered:N0} runs delivered the item whose WriteAsync threw ({100.0 * delivered / runs:F1} %)");
}

return total == 0 ? 0 : 1;

static async Task<bool> RefusedItemWasDeliveredAsync(bool singleWriter)
{
    var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(2)
    {
        SingleWriter = singleWriter,
        SingleReader = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    using var cts = new CancellationTokenSource();
    var token = cts.Token;
    var seen = new ConcurrentBag<int>();
    var refused = -1;

    var writer = Task.Run(async () =>
    {
        for (var n = 0; ; n++)
        {
            try
            {
                await channel.Writer.WriteAsync(n, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                refused = n;
                return;
            }
        }
    });

    var readers = new Task[3];
    for (var r = 0; r < readers.Length; r++)
    {
        readers[r] = Task.Run(async () =>
        {
            try
            {
                await foreach (var n in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    seen.Add(n);
                    if (n == 150)
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected: the token was cancelled
            }
        });
    }

    await writer.ConfigureAwait(false);
    await Task.WhenAll(readers).ConfigureAwait(false);
    while (channel.Reader.TryRead(out var left))
    {
        seen.Add(left);
    }

    return seen.Contains(refused);
}
