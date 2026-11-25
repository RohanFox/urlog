using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Query;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Api.Tests;

/// <summary>
/// The stored bytes of one event's line through the case's routes: exactly the bytes, never as anything a
/// browser shows, to members of the case alone, only for an event named by its key, and with an audit row that
/// says what was sent without holding any of it.
/// </summary>
public sealed class RawBytesTests
{
    private const string Password = "correct horse battery";

    private static readonly DateTime At = new(2026, 3, 1, 0, 0, 7, DateTimeKind.Utc);

    /// <summary>A line with bytes that are not UTF-8: a Latin-1 letter, a stray continuation byte, an overlong form, 0xFF, and markup.</summary>
    private static readonly byte[] Line = [.. "caf"u8, 0xE9, .. " <script>alert(1)</script> "u8, 0x80, 0xC0, 0xAF, 0xFF, 0x00, .. " end"u8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ApiFactory Factory, ApiClient Alice, long CaseId)> SceneAsync()
    {
        var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        var alice = factory.Client();
        await alice.LoginAsync("alice", Password);
        var created = await ApiClient.ReadAsync<JsonElement>(await alice.PostAsync("/api/v1/cases", new { name = "incident" }));
        var caseId = created.GetProperty("id").GetInt64();
        factory.Search.Events.Add(new FakeEvent(At, 4, 900, "the text of the line"));
        factory.Search.Events.Add(new FakeEvent(At, 4, 1000, "another line"));
        factory.Search.RawBytes[(4, 900)] = Line;
        return (factory, alice, caseId);
    }

    /// <summary>The span of the case as a parse would have recorded it, so a search without a range covers the events.</summary>
    private static async Task SpanAsync(ApiFactory factory, long caseId)
    {
        await using var db = factory.Open();
        await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.TsMin, At.AddSeconds(-7)).SetProperty(c => c.TsMax, At), Ct);
    }

    private static string Url(long caseId, long file = 4, long offset = 900, string query = "?ts=2026-03-01T00:00:07.000Z&host=") =>
        $"/api/v1/cases/{caseId}/files/{file}/events/{offset}/raw{query}";

    private static async Task<List<JsonElement>> AuditAsync(ApiFactory factory)
    {
        await using var db = factory.Open();
        var rows = await db.Audit.AsNoTracking().Where(a => a.Action == AuditActions.Query).OrderBy(a => a.Seq).ToListAsync(Ct);
        return [.. rows.Select(r =>
        {
            using var document = JsonDocument.Parse(r.DetailsJson);
            return document.RootElement.Clone();
        })];
    }

    [Fact]
    public async Task AMemberGetsTheStoredBytesOfTheLineExactlyAndAsAFile()
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;

        var answer = await alice.GetAsync(Url(caseId));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(Line, await answer.Content.ReadAsByteArrayAsync(Ct));

        Assert.Equal("application/octet-stream", answer.Content.Headers.ContentType?.ToString());
        Assert.Equal("attachment", answer.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"case-{caseId}-file-4-offset-900.bin", answer.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(Line.Length, answer.Content.Headers.ContentLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Line)), Assert.Single(answer.Headers.GetValues("X-Urlog-Raw-Sha256")));
        Assert.Equal("nosniff", Assert.Single(answer.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("no-store", string.Join(',', answer.Headers.GetValues("Cache-Control")), StringComparison.Ordinal);

        // One statement, for the route's case, by the key of the event.
        var call = Assert.Single(factory.Search.Calls);
        Assert.Equal(("bytes", (ulong)caseId), (call.Kind, call.CaseId));
        Assert.Contains("FROM events_v WHERE case_id = {case:UInt64} AND host = {b_host:String} AND ts = {b_ts:DateTime64(3)} AND file_id = {b_file:UInt64} AND file_offset = {b_offset:UInt64}", call.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    [InlineData("image/svg+xml")]
    [InlineData("application/json")]
    public async Task NoRequestMakesTheRouteAnswerAsAnythingButBytesToSave(string wanted)
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;

        // Asked for by the Accept header, and by every parameter a route might listen to.
        foreach (var query in new[] { "", "&format=html", "&type=" + Uri.EscapeDataString(wanted), "&content_type=" + Uri.EscapeDataString(wanted), "&inline=true", "&disposition=inline", "&download=false" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(caseId) + query);
            request.Headers.TryAddWithoutValidation("Accept", wanted);
            var answer = await alice.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal("application/octet-stream", answer.Content.Headers.ContentType?.ToString());
            Assert.Equal("attachment", answer.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", Assert.Single(answer.Headers.GetValues("X-Content-Type-Options")));
        }

        // No sibling address answers the bytes under another name.
        foreach (var path in new[] { "raw.html", "raw.txt", "raw/inline", "text", "html" })
        {
            var other = await alice.GetAsync($"/api/v1/cases/{caseId}/files/4/events/900/{path}?ts=2026-03-01T00:00:07.000Z&host=");
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("?ts=2026-03-01T00:00:07.000Z")]
    [InlineData("?host=")]
    [InlineData("?ts=yesterday&host=")]
    public async Task WithoutTheTimeAndTheHostOfTheEventTheRequestIsRefusedAndNothingIsRead(string query)
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;

        var answer = await alice.GetAsync(Url(caseId, query: query));
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains("ts and host are required", (await ApiClient.ReadAsync<JsonElement>(answer)).GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Empty(factory.Search.Calls);
    }

    [Fact]
    public async Task AnAddressThatIsNoEventIsNotFoundAndIsOnTheTrail()
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;

        // Another offset, another time, another host: each alone is no event.
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(Url(caseId, offset: 901))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(Url(caseId, query: "?ts=2026-03-01T00:00:08.000Z&host="))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(Url(caseId, query: "?ts=2026-03-01T00:00:07.000Z&host=web-01"))).StatusCode);

        var rows = await AuditAsync(factory);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(("raw", "no such event", 0), (r.GetProperty("request").GetString(), r.GetProperty("reason").GetString(), r.GetProperty("rows").GetInt64())));
    }

    [Fact]
    public async Task OnlyMembersOfTheCaseReachTheBytes()
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;
        await factory.AddUserAsync("bob", Password);
        await factory.AddUserAsync("carol", Password);
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsync($"/api/v1/cases/{caseId}/members", new { user = "bob", role = "ro" })).StatusCode);
        using var bob = factory.Client();
        await bob.LoginAsync("bob", Password);
        using var carol = factory.Client();
        await carol.LoginAsync("carol", Password);
        using var nobody = factory.Client();

        // A read-only member reads; someone the case is not shared with is told there is no such case; no session, no answer.
        Assert.Equal(Line, await (await bob.GetAsync(Url(caseId))).Content.ReadAsByteArrayAsync(Ct));
        var refused = await carol.GetAsync(Url(caseId));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.DoesNotContain("caf", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.GetAsync(Url(caseId))).StatusCode);
        Assert.Single(factory.Search.Calls); // bob's: the other two never reached the store
    }

    [Fact]
    public async Task TheAuditRowSaysWhatWasSentAndHoldsNoneOfIt()
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;

        await alice.GetAsync(Url(caseId));
        var row = Assert.Single(await AuditAsync(factory));
        Assert.Equal(("bytes", "raw", "ok", 1, Line.Length), (row.GetProperty("language").GetString(), row.GetProperty("request").GetString(), row.GetProperty("outcome").GetString(), row.GetProperty("rows").GetInt64(), row.GetProperty("bytes").GetInt32()));
        Assert.Equal((4, 900), (row.GetProperty("file_id").GetInt64(), row.GetProperty("file_offset").GetInt64()));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Line)), row.GetProperty("sent_sha256").GetString());
        Assert.Equal("file 4 offset 900", row.GetProperty("text").GetString());
        Assert.True(row.GetProperty("complete").GetBoolean());

        // Not as text, not as hex, not as base64.
        var raw = row.GetRawText();
        Assert.DoesNotContain("caf", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("script", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Line), raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(Line), raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARequestOtherThanForBytesKeepsItsAuditRowAsItWas()
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;
        await SpanAsync(factory, caseId);

        await SearchStreams.ReadAsync(await alice.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" }));
        var row = Assert.Single(await AuditAsync(factory));
        Assert.False(row.TryGetProperty("file_id", out var _));
        Assert.False(row.TryGetProperty("file_offset", out var _));
    }

    [Fact]
    public async Task APageSaysOfEveryRowWhetherItsTextIsItsBytes()
    {
        var (factory, alice, caseId) = await SceneAsync();
        await using var _ = factory;
        using var __ = alice;
        await SpanAsync(factory, caseId);

        var page = (await SearchStreams.ReadAsync(await alice.PostAsync($"/api/v1/cases/{caseId}/search", new { query = "" }))).Single(e => e.Name == "page").Data;
        var columns = page.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
        var at = columns.IndexOf(SearchPlan.RawValidColumn);
        Assert.True(at > columns.IndexOf("extras"));
        Assert.All(page.GetProperty("rows").EnumerateArray(), r => Assert.Equal(1, r[at].GetInt32()));
        Assert.Contains("extras, isValidUTF8(raw) FROM events_v", factory.Search.Calls.First(c => c.Kind == "page").Sql, StringComparison.Ordinal);
    }
}
