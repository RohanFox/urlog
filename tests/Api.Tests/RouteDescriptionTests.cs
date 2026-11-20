using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Urlog.Api.Tests;

/// <summary>
/// <c>web/openapi.json</c> is the description of the routes the web client's types are generated from. It is the
/// document the host builds from its live route table, written to the repository; this test builds it again and
/// compares, so a route, a parameter or a record that changes fails here until the file is written again
/// (<c>URLOG_UPDATE_OPENAPI=1</c>), and the new file then fails the client's type check wherever it used the old shape.
/// </summary>
public sealed class RouteDescriptionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TrackedFile([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "web", "openapi.json"));

    [Fact]
    public async Task TheTrackedDescriptionIsTheOneTheHostBuilds()
    {
        await using var factory = new ApiFactory();
        using var client = factory.Client();
        var provider = factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(ApiHost.RouteDescription);
        var document = await provider.GetOpenApiDocumentAsync(Ct);

        // Where the host listens is not part of the contract, and differs between a test host and a real one.
        document.Servers?.Clear();
        var built = (await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, Ct)).ReplaceLineEndings("\n");
        if (!built.EndsWith('\n'))
        {
            built += "\n";
        }

        var path = TrackedFile();
        if (Environment.GetEnvironmentVariable("URLOG_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, built, new UTF8Encoding(false), Ct);
        }

        Assert.True(File.Exists(path), $"{path} is missing: run this test once with URLOG_UPDATE_OPENAPI=1");
        var tracked = (await File.ReadAllTextAsync(path, Ct)).ReplaceLineEndings("\n");
        Assert.True(tracked == built, "web/openapi.json is not the description the host builds. A route or a record changed: run this test with URLOG_UPDATE_OPENAPI=1, then fix the web client where its type check fails.");
    }

    [Fact]
    public async Task NoRouteServesTheDescription()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", "correct horse battery");
        using var client = factory.Client();
        await client.LoginAsync("alice", "correct horse battery");
        foreach (var path in new[] { "/openapi/v1.json", "/api/v1/openapi.json", "/swagger/v1/swagger.json" })
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }
    }
}
