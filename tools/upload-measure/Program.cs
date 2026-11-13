// A tus 1.0 client for one measurement: a large upload in chunks, cut off in the middle of a chunk at given
// points and resumed, while the backend's working set is sampled once a second. It is the done measurement of the
// web upload (docs/design-record.md, "Intake", Web upload) and is started by tools/upload-measure.ps1.
//
//   upload-measure --url http://127.0.0.1:5081 --user <name> --source <file> --bytes 21474836480 --chunk-mib 16
//                  --cut-gib 7,14 --pid <backend pid> --samples <csv>
//
// The password comes from URLOG_PASSWORD. The source file is read again and again until --bytes are sent as one
// upload, so no file of the full size is needed on the sending side and the size is exact whatever the source's. Exit code 0 when the upload completed, the server's SHA-256 equals the
// one computed here while sending, and every cut resumed at the start of its chunk.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var options = Options.Parse(args);
var password = Environment.GetEnvironmentVariable("URLOG_PASSWORD") ?? throw new InvalidOperationException("URLOG_PASSWORD is not set");
using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, PooledConnectionLifetime = Timeout.InfiniteTimeSpan })
{
    BaseAddress = new Uri(options.Url),
    Timeout = TimeSpan.FromMinutes(30),
};

// Session.
using var login = await http.PostAsJsonAsync("/api/v1/auth/login", new { name = options.User, password }).ConfigureAwait(false);
login.EnsureSuccessStatusCode();
var cookie = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("urlog_session=", StringComparison.Ordinal)).Split(';')[0];
using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync().ConfigureAwait(false));
var csrf = loginBody.RootElement.GetProperty("csrf_token").GetString()!;

using var createCase = Request(HttpMethod.Post, "/api/v1/cases");
createCase.Content = JsonContent.Create(new { name = "upload measurement " + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) });
using var caseResponse = await http.SendAsync(createCase).ConfigureAwait(false);
caseResponse.EnsureSuccessStatusCode();
using var caseBody = JsonDocument.Parse(await caseResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
var caseId = caseBody.RootElement.GetProperty("id").GetInt64();

var sourceLength = new FileInfo(options.Source).Length;
var total = options.Bytes;
var chunkSize = options.ChunkMiB << 20;
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {caseId}; {total:N0} bytes (a source of {sourceLength:N0} read {(double)total / sourceLength:F2} times) in chunks of {chunkSize:N0}"));

// The backend's memory, once a second, from the moment the upload exists.
using var stop = new CancellationTokenSource();
long offsetNow = 0;
var samples = new List<(double Seconds, long Offset, long WorkingSet, long Private)>();
var clock = Stopwatch.StartNew();
var sampler = Task.Run(async () =>
{
    using var backend = Process.GetProcessById(options.Pid);
    while (!stop.IsCancellationRequested)
    {
        backend.Refresh();
        samples.Add((clock.Elapsed.TotalSeconds, Interlocked.Read(ref offsetNow), backend.WorkingSet64, backend.PrivateMemorySize64));
        try
        {
            await Task.Delay(1000, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
});

using var create = Request(HttpMethod.Post, $"/api/v1/cases/{caseId}/uploads");
create.Headers.Add("Tus-Resumable", "1.0.0");
create.Headers.Add("Upload-Length", total.ToString(CultureInfo.InvariantCulture));
create.Headers.Add("Upload-Metadata", "filename " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFileName(options.Source))));
using var created = await http.SendAsync(create).ConfigureAwait(false);
if (created.StatusCode != HttpStatusCode.Created)
{
    throw new InvalidOperationException($"creation refused: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync().ConfigureAwait(false)}");
}

var location = created.Headers.Location!.OriginalString;
Console.WriteLine($"upload {location}");

var cuts = new Queue<long>(options.CutGiB.Select(g => (long)(g * (1L << 30))).Order());
var resumes = new List<string>();
using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
var buffer = new byte[chunkSize];
long offset = 0;
double lastPatchSeconds = 0;
var ok = true;
using (var source = new FileStream(options.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
{
    while (offset < total)
    {
        var count = (int)Math.Min(chunkSize, total - offset);
        await FillAsync(source, buffer.AsMemory(0, count)).ConfigureAwait(false);
        var checksum = "sha256 " + Convert.ToBase64String(SHA256.HashData(buffer.AsSpan(0, count)));

        if (cuts.Count > 0 && offset + count > cuts.Peek())
        {
            // A connection that dies in the middle of this chunk: half of it is sent, then the socket is torn down.
            cuts.Dequeue();
            using var broken = Patch(location, offset, new CutContent(buffer, count / 2, count), checksum);
            try
            {
                using var never = await http.SendAsync(broken).ConfigureAwait(false);
                Console.WriteLine($"the cut request was answered with {(int)never.StatusCode}; expected a broken connection");
                ok = false;
            }
            catch (HttpRequestException)
            {
            }

            var (resumed, waited) = await OffsetAsync(location).ConfigureAwait(false);
            resumes.Add(string.Create(CultureInfo.InvariantCulture, $"cut at {offset + count / 2:N0}, server offset {resumed:N0}, chunk start {offset:N0}, upload free again after {waited:F1} s"));
            Console.WriteLine(resumes[^1]);
            if (resumed != offset)
            {
                ok = false;
                throw new InvalidOperationException("the server did not resume at the start of the cut chunk");
            }
        }

        var patchClock = Stopwatch.StartNew();
        using var patch = Patch(location, offset, new ByteArrayContent(buffer, 0, count), checksum);
        using var patched = await http.SendAsync(patch).ConfigureAwait(false);
        if (patched.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"chunk at {offset} refused: {(int)patched.StatusCode} {await patched.Content.ReadAsStringAsync().ConfigureAwait(false)}");
        }

        lastPatchSeconds = patchClock.Elapsed.TotalSeconds;
        whole.AppendData(buffer, 0, count);
        offset += count;
        Interlocked.Exchange(ref offsetNow, offset);
    }
}

var elapsed = clock.Elapsed.TotalSeconds;
await Task.Delay(2000).ConfigureAwait(false);
await stop.CancelAsync().ConfigureAwait(false);
await sampler.ConfigureAwait(false);

var sha = Convert.ToHexStringLower(whole.GetHashAndReset());
using var statusRequest = Request(HttpMethod.Get, location);
using var statusResponse = await http.SendAsync(statusRequest).ConfigureAwait(false);
statusResponse.EnsureSuccessStatusCode();
using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
var state = status.RootElement.GetProperty("state").GetString();
var serverSha = status.RootElement.GetProperty("sha256").GetString();

if (options.Samples is not null)
{
    var csv = new StringBuilder("seconds,offset_bytes,working_set_bytes,private_bytes\n");
    foreach (var s in samples)
    {
        csv.Append(CultureInfo.InvariantCulture, $"{s.Seconds:F1},{s.Offset},{s.WorkingSet},{s.Private}\n");
    }

    await File.WriteAllTextAsync(options.Samples, csv.ToString()).ConfigureAwait(false);
}

const double MiB = 1 << 20;
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"sent      {total:N0} bytes in {elapsed:F1} s, {total / MiB / elapsed:F1} MiB/s, {resumes.Count} resume(s)"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"last      the last chunk's request took {lastPatchSeconds:F1} s (it answers after intake has hashed and recorded the file)"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"memory    working set {samples.Min(s => s.WorkingSet) / MiB:F0} to {samples.Max(s => s.WorkingSet) / MiB:F0} MiB, last {samples[^1].WorkingSet / MiB:F0}; range {(samples.Max(s => s.WorkingSet) - samples.Min(s => s.WorkingSet)) / MiB:F0} MiB over {samples.Count} samples"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"memory    private bytes {samples.Min(s => s.Private) / MiB:F0} to {samples.Max(s => s.Private) / MiB:F0} MiB"));
Console.WriteLine($"state     {state}");
Console.WriteLine($"sha256    {sha} (computed while sending)");
Console.WriteLine($"sha256    {serverSha} (the server's record)");
Console.WriteLine($"stored    case {caseId}, web/{location[(location.LastIndexOf('/') + 1)..]}");
ok = ok && state == "done" && string.Equals(sha, serverSha, StringComparison.Ordinal);
Console.WriteLine(ok ? "result    ok" : "result    FAILED");
return ok ? 0 : 1;

HttpRequestMessage Request(HttpMethod method, string path)
{
    var request = new HttpRequestMessage(method, path);
    request.Headers.Add("Cookie", cookie);
    request.Headers.Add("X-CSRF-Token", csrf);
    return request;
}

HttpRequestMessage Patch(string path, long at, HttpContent content, string checksum)
{
    var request = Request(HttpMethod.Patch, path);
    request.Headers.Add("Tus-Resumable", "1.0.0");
    request.Headers.Add("Upload-Offset", at.ToString(CultureInfo.InvariantCulture));
    request.Headers.Add("Upload-Checksum", checksum);
    request.Content = content;
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
    return request;
}

// The offset after a cut. Until the server has noticed that the connection is gone, the cut request still holds
// the upload and the answer is 423; a client asks again, as this one does.
async Task<(long Offset, double WaitedSeconds)> OffsetAsync(string path)
{
    var waited = Stopwatch.StartNew();
    while (true)
    {
        using var head = Request(HttpMethod.Head, path);
        head.Headers.Add("Tus-Resumable", "1.0.0");
        using var response = await http.SendAsync(head).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Locked && waited.Elapsed < TimeSpan.FromSeconds(120))
        {
            await Task.Delay(250).ConfigureAwait(false);
            continue;
        }

        response.EnsureSuccessStatusCode();
        return (long.Parse(response.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture), waited.Elapsed.TotalSeconds);
    }
}

// The source file end to end, again and again: the upload is the file repeated.
static async Task FillAsync(FileStream source, Memory<byte> target)
{
    while (target.Length > 0)
    {
        var read = await source.ReadAsync(target).ConfigureAwait(false);
        if (read == 0)
        {
            source.Position = 0;
            continue;
        }

        target = target[read..];
    }
}

/// <summary>A request body that declares its whole length, sends the first part and then fails, which closes the connection.</summary>
internal sealed class CutContent(byte[] bytes, int send, int declared) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await stream.WriteAsync(bytes.AsMemory(0, send)).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
        throw new IOException("connection cut by the measurement");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = declared;
        return true;
    }
}

internal sealed record Options(string Url, string User, string Source, long Bytes, int ChunkMiB, double[] CutGiB, int Pid, string? Samples)
{
    public static Options Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            map[args[i].TrimStart('-')] = args[i + 1];
        }

        string Need(string key) => map.TryGetValue(key, out var value) ? value : throw new ArgumentException($"--{key} is required");
        var cuts = map.TryGetValue("cut-gib", out var list) && list.Length > 0
            ? list.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()
            : [];
        return new Options(
            Need("url"), Need("user"), Need("source"),
            long.Parse(Need("bytes"), CultureInfo.InvariantCulture),
            int.Parse(map.GetValueOrDefault("chunk-mib", "16"), CultureInfo.InvariantCulture),
            cuts, int.Parse(Need("pid"), CultureInfo.InvariantCulture), map.GetValueOrDefault("samples"));
    }
}
