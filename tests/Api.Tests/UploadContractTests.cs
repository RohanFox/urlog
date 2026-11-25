using System.Runtime.CompilerServices;
using System.Text.Json;
using Urlog.Api.Uploads;

namespace Urlog.Api.Tests;

/// <summary>
/// The upload protocol is not part of the description of the routes, so the names its client depends on are
/// pinned here: <c>web/src/api/samples/upload-contract.json</c> holds them, this test holds the backend to the
/// file, and a test of the web client holds the client to the same file.
/// </summary>
public sealed class UploadContractTests
{
    private static string Sample([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "web", "src", "api", "samples", "upload-contract.json"));

    [Fact]
    public void TheNamesTheWebClientReadsAreTheOnesTheBackendWrites()
    {
        using var sample = JsonDocument.Parse(File.ReadAllText(Sample()));
        var root = sample.RootElement;
        var pending = Problems.UploadPending(5);

        Assert.Equal(UploadEndpoints.StateHeader, root.GetProperty("state_header").GetString());
        Assert.Equal(pending.Type, root.GetProperty("pending_problem_type").GetString());
        Assert.Equal(pending.Status, root.GetProperty("pending_status").GetInt32());
        Assert.Equal(
            Enum.GetValues<UploadState>().Select(UploadEndpoints.StateName),
            root.GetProperty("states").EnumerateArray().Select(s => s.GetString()));
    }
}
