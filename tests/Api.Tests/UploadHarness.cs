using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Urlog.Api.Uploads;
using Urlog.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Urlog.Api.Tests;

/// <summary>
/// One host with one case and four people: alice owns it, dave writes, carol reads, bob is not a member. Plus the
/// tus requests the upload tests send, spelled out header by header so the tests show the protocol.
/// </summary>
internal sealed class UploadHarness : IAsyncDisposable
{
    public const string Password = "correct horse battery staple";
    public const long CaseId = 1;
    public const string Base = "/api/v1/cases/1/uploads";

    private UploadHarness(ApiFactory factory)
    {
        Factory = factory;
    }

    public ApiFactory Factory { get; }

    public ApiClient Alice { get; private set; } = null!;

    public UploadFolders Folders => Factory.Services.GetRequiredService<UploadFolders>();

    public UploadSweeper Sweeper => Factory.Services.GetRequiredService<UploadSweeper>();

    public static async Task<UploadHarness> StartAsync(IDictionary<string, string?>? settings = null, int busyTimeoutSeconds = MetadataStore.BusyTimeoutSeconds)
    {
        var harness = new UploadHarness(new ApiFactory(settings, busyTimeoutSeconds: busyTimeoutSeconds));
        foreach (var name in new[] { "alice", "bob", "carol", "dave" })
        {
            await harness.Factory.AddUserAsync(name, Password);
        }

        harness.Alice = await harness.LoginAsync("alice");
        Assert.Equal(HttpStatusCode.Created, (await harness.Alice.PostAsync("/api/v1/cases", new { name = "incident 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await harness.Alice.PostAsync("/api/v1/cases/1/members", new { user = "carol", role = "ro" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await harness.Alice.PostAsync("/api/v1/cases/1/members", new { user = "dave", role = "rw" })).StatusCode);
        return harness;
    }

    public async Task<ApiClient> LoginAsync(string name)
    {
        var client = Factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(name, Password)).StatusCode);
        return client;
    }

    /// <summary>A session ends after eight idle hours; alice logs in again, as a person does the next day.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        Factory.Clock.Advance(by);
        Alice.Dispose();
        Alice = await LoginAsync("alice");
    }

    /// <summary><c>Upload-Metadata</c>: comma-separated pairs of a key and the base64 of its value.</summary>
    public static string Metadata(params (string Key, string Value)[] pairs) =>
        string.Join(",", pairs.Select(p => p.Key + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Value))));

    public static Task<HttpResponseMessage> CreateAsync(ApiClient client, long length, string? metadata, string path = Base)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
        if (metadata is not null)
        {
            request.Headers.Add("Upload-Metadata", metadata);
        }

        return client.SendAsync(request);
    }

    /// <summary>Creates an upload and returns its URL, a path as the server gave it.</summary>
    public async Task<string> CreatedAsync(long length, string name = "access.log", ApiClient? client = null)
    {
        var response = await CreateAsync(client ?? Alice, length, Metadata(("filename", name)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    /// <param name="checksum">An <c>Upload-Checksum</c> value (<c>sha256 base64</c>), or null to send none.</param>
    public static Task<HttpResponseMessage> PatchAsync(ApiClient client, string url, long offset, byte[] bytes, string? checksum = null, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
        if (checksum is not null)
        {
            request.Headers.Add("Upload-Checksum", checksum);
        }

        return client.SendAsync(request, csrf);
    }

    public static Task<HttpResponseMessage> HeadAsync(ApiClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> TerminateAsync(ApiClient client, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        return client.SendAsync(request);
    }

    /// <summary>The <c>Urlog-Upload-State</c> header of a HEAD, and the offset and the length beside it.</summary>
    public static async Task<(string State, long Offset, long Length)> HeadStateAsync(ApiClient client, string url)
    {
        var head = await HeadAsync(client, url);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        return (head.Headers.GetValues(UploadEndpoints.StateHeader).Single(),
            long.Parse(head.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture),
            long.Parse(head.Headers.GetValues("Upload-Length").Single(), CultureInfo.InvariantCulture));
    }

    public static async Task<long> OffsetAsync(ApiClient client, string url)
    {
        var head = await HeadAsync(client, url);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        return long.Parse(head.Headers.GetValues("Upload-Offset").Single(), CultureInfo.InvariantCulture);
    }

    public static async Task<UploadStatus> StatusAsync(ApiClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiClient.ReadAsync<UploadStatus>(response);
    }

    /// <summary>The whole file in one upload of <paramref name="chunk"/>-byte requests; returns its URL.</summary>
    public async Task<string> UploadAsync(byte[] bytes, string name = "access.log", int chunk = 4096, string? metadata = null)
    {
        var created = await CreateAsync(Alice, bytes.Length, metadata ?? Metadata(("filename", name)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var url = created.Headers.Location!.OriginalString;
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            var part = bytes.AsSpan(offset, Math.Min(chunk, bytes.Length - offset)).ToArray();
            var patched = await PatchAsync(Alice, url, offset, part);
            Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);
        }

        return url;
    }

    public static string Id(string url) => url[(url.LastIndexOf('/') + 1)..];

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // SHA-1 is the one algorithm the tus checksum extension requires of a server. It guards a chunk in transit; evidence is hashed with SHA-256.
#pragma warning disable CA5350
    public static string Checksum(string algorithm, byte[] bytes) =>
        algorithm + " " + Convert.ToBase64String(algorithm == "sha1" ? SHA1.HashData(bytes) : SHA256.HashData(bytes));
#pragma warning restore CA5350

    /// <summary>Access log lines in the combined format, different for every <paramref name="seed"/>.</summary>
    public static byte[] Log(int lines, int seed = 0)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"203.0.113.{(i + seed) % 250 + 1} - - [01/Mar/2026:00:{i / 60 % 60:D2}:{i % 60:D2} +0000] \"GET /item/{seed}/{i} HTTP/1.1\" 200 {600 + i} \"-\" \"Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0\"\n");
        }

        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>Every file and folder under a directory with its size and last write, as text: equal before and after means untouched.</summary>
    public static string Listing(string directory, Func<string, bool>? include = null)
    {
        var lines = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, entry).Replace('\\', '/');
            if (include is not null && !include(relative))
            {
                continue;
            }

            var info = new FileInfo(entry);
            lines.Add(info.Exists
                ? string.Create(CultureInfo.InvariantCulture, $"{relative} {info.Length} {info.LastWriteTimeUtc:O} {info.Attributes}")
                : relative + "/");
        }

        return string.Join("\n", lines);
    }

    public async ValueTask DisposeAsync()
    {
        Alice.Dispose();
        await Factory.DisposeAsync();
    }
}

/// <summary>The body of <c>GET .../uploads/{id}</c>.</summary>
internal sealed record UploadStatus(string Id, string State, long Offset, long Length, string Name, long? FileId, string? Sha256, long? JobId, string? Message);
