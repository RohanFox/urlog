using System.Net;
using System.Text.Json;
using Urlog.Core.Metadata;
using Urlog.Query;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Api.Tests;

/// <summary>
/// <c>GET /api/v1/cases/{id}/fields</c>: the catalog with the case's statistics, read for the route's case and for
/// nobody who is not a member of it. The event store is a stand-in that records which case it was asked for.
/// </summary>
public sealed class FieldEndpointTests
{
    private const string Password = "correct horse battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CaseFieldStats Stats(ulong caseId) => FieldStats.Read(caseId,
    [
        [caseId.ToString(System.Globalization.CultureInfo.InvariantCulture), "", "", "1000", "0", "2026-11-18 10:02:11.482"],
        [caseId.ToString(System.Globalization.CultureInfo.InvariantCulture), "http.response.status_code", "", "990", "2", "2026-11-18 10:02:11.482"],
        [caseId.ToString(System.Globalization.CultureInfo.InvariantCulture), "http.response.status_code", "200", "900", "0", "2026-11-18 10:02:11.482"],
        [caseId.ToString(System.Globalization.CultureInfo.InvariantCulture), "http.response.status_code", "404", "90", "0", "2026-11-18 10:02:11.482"],
        [caseId.ToString(System.Globalization.CultureInfo.InvariantCulture), "source.ip", "", "1000", "250000", "2026-11-18 10:02:11.482"],
        [caseId.ToString(System.Globalization.CultureInfo.InvariantCulture), "user.name", "", "12", "0", "2026-11-18 10:02:11.482"],
        [caseId.ToString(System.Globalization.CultureInfo.InvariantCulture), "x_tenant", "", "1000", "4", "2026-11-18 10:02:11.482"],
    ]);

    private static JsonElement Field(JsonElement body, string name) =>
        body.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);

    private static async Task<(ApiClient Owner, long CaseId)> CaseAsync(ApiFactory factory)
    {
        await factory.AddUserAsync("alice", Password);
        var owner = factory.Client();
        await owner.LoginAsync("alice", Password);
        var created = await ApiClient.ReadAsync<JsonElement>(await owner.PostAsync("/api/v1/cases", new { name = "incident" }));
        return (owner, created.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task AReadOnlyMemberGetsTheCatalogWithTheCasesStatistics()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory);
        using var _ = owner;
        await factory.AddUserAsync("bob", Password);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/v1/cases/{caseId}/members", new { user = "bob", role = "ro" })).StatusCode);
        factory.FieldStats.Sets[(ulong)caseId] = Stats((ulong)caseId);
        var first = new DateTime(2026, 3, 1, 0, 0, 4, DateTimeKind.Utc);
        await using (var db = factory.Open())
        {
            await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(
                s => s.SetProperty(c => c.TsMin, first).SetProperty(c => c.TsMax, first.AddDays(6)).SetProperty(c => c.UndatedRows, 212L).SetProperty(c => c.FutureRows, 3L), Ct);
        }

        using var bob = factory.Client();
        await bob.LoginAsync("bob", Password);
        var response = await bob.GetAsync($"/api/v1/cases/{caseId}/fields");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiClient.ReadAsync<JsonElement>(response);

        Assert.Equal(caseId, body.GetProperty("case_id").GetInt64());
        Assert.Equal([(ulong)caseId], factory.FieldStats.Asked);
        var span = body.GetProperty("span");
        Assert.Equal(new DateTimeOffset(first), span.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(first.AddDays(6)), span.GetProperty("to").GetDateTimeOffset());
        Assert.Equal((212, 3), (span.GetProperty("undated_events").GetInt64(), span.GetProperty("future_events").GetInt64()));

        var statistics = body.GetProperty("statistics");
        Assert.Equal(("ok", false, 1000), (statistics.GetProperty("state").GetString(), statistics.GetProperty("pending").GetBoolean(), statistics.GetProperty("events").GetInt64()));
        Assert.Equal(JsonValueKind.Null, statistics.GetProperty("reason").ValueKind);

        // Every catalog field is listed, with or without events in this case.
        Assert.Equal(FieldCatalog.Embedded.Fields.Select(f => f.Name), body.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));

        var status = Field(body, "http.response.status_code");
        Assert.Equal(("int", false, 990, 2), (status.GetProperty("type").GetString(), status.GetProperty("sensitive").GetBoolean(), status.GetProperty("events").GetInt64(), status.GetProperty("distinct_estimate").GetInt64()));
        Assert.Contains("status", status.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal([("200", 900L), ("404", 90L)], status.GetProperty("top").EnumerateArray().Select(t => (t.GetProperty("value").GetString()!, t.GetProperty("count").GetInt64())));

        // The catalog marks the fields whose 0 is "no value"; a reader of an event needs to know which.
        Assert.True(status.GetProperty("zero_is_absent").GetBoolean());
        Assert.Equal(
            ["event.code", "http.response.status_code", "process.pid"],
            body.GetProperty("fields").EnumerateArray().Where(f => f.GetProperty("zero_is_absent").GetBoolean()).Select(f => f.GetProperty("name").GetString()).Order(StringComparer.Ordinal));

        // An address field: sensitive, counted, and here with more distinct values than top values are kept for.
        var source = Field(body, "source.ip");
        Assert.True(source.GetProperty("sensitive").GetBoolean());
        Assert.Equal(250000, source.GetProperty("distinct_estimate").GetInt64());
        Assert.Equal(0, source.GetProperty("top").GetArrayLength());

        // A string field never has top values; a request target has none by the catalog's rule; text is not counted.
        var user = Field(body, "user.name");
        Assert.Equal((12, JsonValueKind.Null, JsonValueKind.Null), (user.GetProperty("events").GetInt64(), user.GetProperty("top").ValueKind, user.GetProperty("distinct_estimate").ValueKind));
        Assert.Equal(JsonValueKind.Null, Field(body, "url.original").GetProperty("top").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(body, "message").GetProperty("events").ValueKind);
        // A field no event of the case has.
        Assert.Equal(0, Field(body, "process.name").GetProperty("events").GetInt64());

        var keys = body.GetProperty("extras_keys").EnumerateArray().Select(k => (k.GetProperty("key").GetString(), k.GetProperty("events").GetInt64())).ToList();
        Assert.Equal([("x_tenant", 1000L)], keys);
        Assert.Equal(Parser.Verbs, body.GetProperty("verbs").EnumerateArray().Select(v => v.GetString()!));
    }

    [Fact]
    public async Task NobodyOutsideTheCaseReadsItsStatisticsAndTheStoreIsNotAsked()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory);
        using var _ = owner;
        factory.FieldStats.Sets[(ulong)caseId] = Stats((ulong)caseId);
        await factory.AddUserAsync("mallory", Password);
        await factory.AddUserAsync("root", Password, UserRole.Admin);

        using var mallory = factory.Client();
        await mallory.LoginAsync("mallory", Password);
        var refused = await mallory.GetAsync($"/api/v1/cases/{caseId}/fields");
        var unknown = await mallory.GetAsync("/api/v1/cases/4242/fields");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(Ct), await refused.Content.ReadAsStringAsync(Ct));

        // An administrator has no implicit access to a case, and the admin routes carry no statistics.
        using var root = factory.Client();
        await root.LoginAsync("root", Password);
        Assert.Equal(HttpStatusCode.NotFound, (await root.GetAsync($"/api/v1/cases/{caseId}/fields")).StatusCode);
        Assert.DoesNotContain("x_tenant", await (await root.GetAsync("/api/v1/admin/cases")).Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var anonymous = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/cases/{caseId}/fields")).StatusCode);
        Assert.Empty(factory.FieldStats.Asked);
    }

    [Fact]
    public async Task WithoutASetTheCatalogIsServedAndTheStateSaysWhy()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory);
        using var _ = owner;

        // Nothing computed yet.
        var body = await ApiClient.ReadAsync<JsonElement>(await owner.GetAsync($"/api/v1/cases/{caseId}/fields"));
        Assert.Equal("none", body.GetProperty("statistics").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("span").GetProperty("from").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(body, "host.name").GetProperty("events").ValueKind);
        Assert.Equal(0, Field(body, "host.name").GetProperty("top").GetArrayLength());
        Assert.Equal(0, body.GetProperty("extras_keys").GetArrayLength());

        // The case is larger than the statistics are computed for, and a parse was committed since.
        await using (var db = factory.Open())
        {
            db.CaseStats.Add(new CaseStats { CaseId = caseId, State = CaseStatsState.Skipped, Message = "the case holds 412,000,000 events, more than the 250,000,000 the field statistics are computed for", DirtySince = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        body = await ApiClient.ReadAsync<JsonElement>(await owner.GetAsync($"/api/v1/cases/{caseId}/fields"));
        var statistics = body.GetProperty("statistics");
        Assert.Equal(("skipped", true), (statistics.GetProperty("state").GetString(), statistics.GetProperty("pending").GetBoolean()));
        Assert.Contains("412,000,000", statistics.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(FieldCatalog.Embedded.Fields.Count, body.GetProperty("fields").GetArrayLength());

        // The event store does not answer: the catalog half still does.
        factory.FieldStats.Failure = new HttpRequestException("connection refused");
        body = await ApiClient.ReadAsync<JsonElement>(await owner.GetAsync($"/api/v1/cases/{caseId}/fields"));
        Assert.Equal("unavailable", body.GetProperty("statistics").GetProperty("state").GetString());
        Assert.Equal(FieldCatalog.Embedded.Fields.Count, body.GetProperty("fields").GetArrayLength());
    }

    [Fact]
    public async Task TheCaseResponseCarriesTheSpan()
    {
        await using var factory = new ApiFactory();
        var (owner, caseId) = await CaseAsync(factory);
        using var _ = owner;
        var first = new DateTime(2026, 3, 1, 0, 0, 4, DateTimeKind.Utc);
        await using (var db = factory.Open())
        {
            await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.TsMin, first).SetProperty(c => c.TsMax, first.AddHours(1)).SetProperty(c => c.UndatedRows, 5L), Ct);
        }

        var span = (await ApiClient.ReadAsync<JsonElement>(await owner.GetAsync($"/api/v1/cases/{caseId}"))).GetProperty("span");
        Assert.Equal(new DateTimeOffset(first), span.GetProperty("from").GetDateTimeOffset());
        Assert.Equal((5, 0), (span.GetProperty("undated_events").GetInt64(), span.GetProperty("future_events").GetInt64()));
    }
}
