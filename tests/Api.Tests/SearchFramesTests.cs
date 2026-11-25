using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Urlog.Api.Search;
using Urlog.Query;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Api.Tests;

/// <summary>
/// The search stream and its refusals are written by hand on the server, so no description of the routes
/// carries their shape. <c>web/src/api/samples/search-frames.json</c> holds one answer of each kind as the real
/// handlers write it against the stand-in event store; this test writes them again and compares, and a test of
/// the web client reads the same file through its decoders. A changed member fails here until the file is
/// written again (<c>URLOG_UPDATE_SAMPLES=1</c>), and the new file then fails the client where it read the old
/// shape. What varies between runs is fixed before the comparison: the duration, and the cursor's text.
/// </summary>
public sealed class SearchFramesTests
{
    private const string Password = "correct horse battery";

    private static readonly DateTime First = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sample([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "web", "src", "api", "samples", "search-frames.json"));

    [Fact]
    public async Task TheTrackedFramesAreTheOnesTheHandlersWrite()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Urlog:Query:PageSize"] = "2",
            ["Urlog:Query:MaxPagedRows"] = "4",
            ["Urlog:Query:MaxTableRows"] = "2",
            ["Urlog:Query:UserSlots"] = "3",
            ["Urlog:Query:SlotWait"] = "00:00:00.100",
        };
        await using var factory = new ApiFactory(settings);
        await factory.AddUserAsync("alice", Password);
        using var owner = factory.Client();
        await owner.LoginAsync("alice", Password);
        var created = await ApiClient.ReadAsync<JsonElement>(await owner.PostAsync("/api/v1/cases", new { name = "incident" }));
        var caseId = created.GetProperty("id").GetInt64();
        for (var i = 0; i < 6; i++)
        {
            factory.Search.Events.Add(new FakeEvent(First.AddSeconds(i), 1, (ulong)(i * 100), $"line {i}", i % 3 == 0 ? "error" : "info"));
        }

        await using (var db = factory.Open())
        {
            await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(
                s => s.SetProperty(c => c.TsMin, First).SetProperty(c => c.TsMax, First.AddSeconds(5)).SetProperty(c => c.UndatedRows, 12).SetProperty(c => c.FutureRows, 1), Ct);
        }

        var search = $"/api/v1/cases/{caseId}/search";
        var samples = new JsonObject();

        // A listing: meta, the three kinds, done. Then its second page, which reaches the paging limit.
        var listing = await owner.PostAsync(search, new { query = "nosuchfield=1 OR line" });
        var (frames, cursor) = await FramesAsync(listing);
        samples["listing"] = frames;
        (samples["page_at_the_limit"], _) = await FramesAsync(await owner.PostAsync(search, new { query = "nosuchfield=1 OR line", cursor }));

        // An aggregation, cut at the table limit, with a row whose values(f) was capped.
        factory.Search.OnQuery = q => new QueryTable([.. q.Columns], [["web-01", "12"], ["web-02", "7"], ["web-03", "1"]], TimeSpan.Zero) { CappedRows = [1] };
        (samples["aggregation"], _) = await FramesAsync(await owner.PostAsync(search, new { query = "| stats count by host" }));

        // A statement that fails after the stream began.
        factory.Search.Failures["page"] = new QueryFailedException(159, "Code: 159. DB::Exception: Timeout exceeded: elapsed 60.0 seconds", new InvalidOperationException());
        (samples["failed_in_the_stream"], _) = await FramesAsync(await owner.PostAsync(search, new { query = "" }));
        factory.Search.Failures.Clear();

        // Refusals before the first byte.
        var refusals = new JsonObject
        {
            ["query_syntax"] = await RefusalAsync(await owner.PostAsync(search, new { query = "host=web-01 | eval x=1" })),
            ["bad_request"] = await RefusalAsync(await owner.PostAsync(search, new { query = "", from = "yesterday" })),
            ["bad_cursor"] = await RefusalAsync(await owner.PostAsync(search, new { query = "", cursor = "not-a-cursor" })),
        };

        // Busy: a listing in flight holds all of the user's slots, and the next one finds none within the wait.
        factory.Search.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = owner.PostAsync(search, new { query = "" });
        var slots = (QuerySlots)factory.Services.GetService(typeof(QuerySlots))!;
        for (var i = 0; i < 500 && slots.InUse < 3; i++)
        {
            await Task.Delay(10, Ct);
        }

        refusals["busy"] = await RefusalAsync(await owner.PostAsync(search, new { query = "error" }));
        factory.Search.Gate.SetResult();
        (await held).Dispose();
        samples["refusals"] = refusals;

        var built = samples.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
        var path = Sample();
        if (Environment.GetEnvironmentVariable("URLOG_UPDATE_SAMPLES") == "1")
        {
            await File.WriteAllTextAsync(path, built, new UTF8Encoding(false), Ct);
        }

        Assert.True(File.Exists(path), $"{path} is missing: run this test once with URLOG_UPDATE_SAMPLES=1");
        var tracked = (await File.ReadAllTextAsync(path, Ct)).ReplaceLineEndings("\n");
        Assert.True(tracked == built, "web/src/api/samples/search-frames.json is not what the search handlers write. A frame or a refusal changed: run this test with URLOG_UPDATE_SAMPLES=1, then fix the web client where its tests fail.");
    }

    /// <summary>The events of a stream in a fixed order (meta, the kinds between by name, the end), and the cursor of its page.</summary>
    private static async Task<(JsonArray Frames, string? Cursor)> FramesAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await SearchStreams.ReadAsync(response);
        string? cursor = null;
        var frames = new JsonArray();
        foreach (var e in events.OrderBy(e => e.Name switch { "meta" => 0, "done" or "error" => 2, _ => 1 }).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            var data = JsonNode.Parse(e.Data.GetRawText())!.AsObject();
            if (data["duration_ms"] is not null)
            {
                data["duration_ms"] = 0;
            }

            if (data["next"] is JsonValue next)
            {
                cursor = next.GetValue<string>();
                data["next"] = "CURSOR";
            }

            frames.Add(new JsonObject { ["event"] = e.Name, ["data"] = data });
        }

        return (frames, cursor);
    }

    private static async Task<JsonObject> RefusalAsync(HttpResponseMessage response)
    {
        var refusal = new JsonObject
        {
            ["status"] = (int)response.StatusCode,
            ["retry_after"] = response.Headers.TryGetValues("Retry-After", out var values) ? values.Single() : null,
            ["body"] = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct)),
        };
        return refusal;
    }
}
