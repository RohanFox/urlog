using System.Net;
using System.Text.Json;
using Urlog.Core.Detection;
using Urlog.Core.Metadata;

namespace Urlog.Api.Tests;

/// <summary>
/// What a person is told about control bytes and about files kept as "not a log": a log that holds control
/// bytes says so in its flags and is counted on the dashboard; a file that was kept and not parsed is counted
/// by its state and listed with the reason; and one call names its source, after which it is parsed as text.
/// </summary>
public sealed class ControlBytesApiTests
{
    private const string Password = "correct horse battery";

    private static async Task<(ApiFactory Factory, ApiClient Alice, long CaseId)> SceneAsync()
    {
        var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        var alice = factory.Client();
        await alice.LoginAsync("alice", Password);
        var created = await ApiClient.ReadAsync<JsonElement>(await alice.PostAsync("/api/v1/cases", new { name = "incident" }));
        return (factory, alice, created.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task ALogThatHoldsControlBytesSaysSoOnTheFileAndIsCountedOnTheDashboard()
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;
        await factory.AddFileAsync(caseId, "app.log", "a", f =>
        {
            f.DetectedSource = "generic";
            f.DetectedControlBytes = true;
        });
        await factory.AddFileAsync(caseId, "clean.log", "b");

        var files = (await ApiClient.ReadAsync<JsonElement>(await alice.GetAsync($"/api/v1/cases/{caseId}/files"))).GetProperty("files");
        string[] Flags(int i) => [.. files[i].GetProperty("flags").EnumerateArray().Select(f => f.GetString()!)];
        Assert.Contains("control_bytes", Flags(0));
        Assert.DoesNotContain("control_bytes", Flags(1));

        var dashboard = await ApiClient.ReadAsync<JsonElement>(await alice.GetAsync($"/api/v1/cases/{caseId}/dashboard"));
        Assert.Equal(1, dashboard.GetProperty("files").GetProperty("flags").GetProperty("control_bytes").GetInt32());
    }

    [Fact]
    public async Task AFileKeptAsNotALogIsCountedListedWithItsReasonAndParsedAsTextByOneCall()
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;
        var kept = await factory.AddFileAsync(caseId, "blob.dat", "x", f =>
        {
            f.DetectedKind = FileKind.Artifact;
            f.DetectedSource = null;
            f.DetectedFormat = FormatDetector.UnknownBinary;
            f.State = FileState.Artifact;
        });

        var dashboard = await ApiClient.ReadAsync<JsonElement>(await alice.GetAsync($"/api/v1/cases/{caseId}/dashboard"));
        Assert.Equal(1, dashboard.GetProperty("files").GetProperty("by_state").GetProperty("artifact").GetInt32());

        var listed = (await ApiClient.ReadAsync<JsonElement>(await alice.GetAsync($"/api/v1/cases/{caseId}/files?state=artifact"))).GetProperty("files")[0];
        Assert.Equal(("artifact", "unknown-binary"), (listed.GetProperty("detection").GetProperty("kind").GetString(), listed.GetProperty("detection").GetProperty("format").GetString()));

        // The one action: a person says "this is text", and the file waits for the parser like any log.
        var answer = await alice.SendAsync(HttpMethod.Put, $"/api/v1/cases/{caseId}/files/{kept.Id}/override", new { source = "generic" }, listed.GetProperty("version").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        var after = (await ApiClient.ReadAsync<JsonElement>(await alice.GetAsync($"/api/v1/cases/{caseId}/files"))).GetProperty("files")[0];
        Assert.Equal(("received", "generic"), (after.GetProperty("state").GetString(), after.GetProperty("override").GetProperty("source").GetString()));
        // What the detector found stays on the record beside what the person decided.
        Assert.Equal("unknown-binary", after.GetProperty("detection").GetProperty("format").GetString());
    }
}
