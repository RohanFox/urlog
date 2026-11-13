using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using static Urlog.Api.Tests.UploadHarness;

namespace Urlog.Api.Tests;

/// <summary>
/// The request body limit on a real Kestrel listener, which is where it is enforced: the upload route takes a
/// chunk larger than the server's limit, and every other route still refuses a body of that size.
/// </summary>
public sealed class UploadBodyLimitTests
{
    /// <summary>Kestrel's default <c>MaxRequestBodySize</c>, which this host does not change.</summary>
    private const int Limit = 30_000_000;

    [Fact]
    public async Task TheUploadRouteAloneTakesABodyOverTheServersLimit()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
        });
        factory.UseKestrel(0);
        factory.StartServer();
        await factory.AddUserAsync("alice", Password);
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var alice = new ApiClient(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) }, "203.0.113.10");
        Assert.Equal(HttpStatusCode.OK, (await alice.LoginAsync("alice", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsync("/api/v1/cases", new { name = "incident 1" })).StatusCode);

        // One chunk a megabyte over the limit.
        var bytes = new byte[Limit + (1 << 20)];
        Random.Shared.NextBytes(bytes);
        var created = await CreateAsync(alice, bytes.Length, Metadata(("filename", "blob.bin")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var url = created.Headers.Location!.OriginalString;
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(alice, url, 0, bytes)).StatusCode);
        var status = await StatusAsync(alice, url);
        Assert.Equal(("done", Sha256(bytes)), (status.State, status.Sha256));

        // A body of that size to a route that is not the upload route: refused for its size.
        var json = System.Text.Encoding.ASCII.GetBytes("{\"name\":\"" + new string('a', Limit + (1 << 20)) + "\"}");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases") { Content = new ByteArrayContent(json) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        // The client asks before it sends, so the refusal is read as an answer and not as a broken connection.
        request.Headers.ExpectContinue = true;
        var refused = await alice.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);

        // And the status route beside the upload route keeps the limit too.
        var beside = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/1/members") { Content = new ByteArrayContent(json) };
        beside.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        beside.Headers.ExpectContinue = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await alice.SendAsync(beside)).StatusCode);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(factory.EvidenceRoot, "1", "web")));
    }
}
