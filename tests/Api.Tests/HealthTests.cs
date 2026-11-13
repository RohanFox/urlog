using System.Net;
using System.Text.Json;
using Urlog.Core.Metadata;
using Urlog.Tests.Shared;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Urlog.Api.Tests;

/// <summary>
/// The backend and the store identity: until the metadata file's identity has been compared with the event
/// store's and found equal, the host answers the health route alone, 503 with the reason, and writes nothing to
/// the metadata file. Both reasons are here (the event store is not answering; the two identities differ, with
/// both values), and so is what follows when the reason goes away.
/// </summary>
public sealed class HealthTests
{
    private const string Health = "/api/v1/health";
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APairThatBelongsTogetherIsServedAndHealthSaysOkToAnyone()
    {
        await using var factory = new ApiFactory();
        await factory.AddUserAsync("alice", Password);
        using var client = factory.Client();

        var health = await client.GetAsync(Health); // no session
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync(Ct));
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        // Anyone may ask: the answer does not hand out the identity of a healthy pair.
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("metadata_store_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("event_store_id").ValueKind);
        Assert.DoesNotContain(factory.EventStore.StoreId!, body.RootElement.GetRawText(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("alice", Password)).StatusCode);
        Assert.Equal(1, factory.EventStore.Reads); // compared once, before the host listened
    }

    [Fact]
    public async Task WhileTheEventStoreDoesNotAnswerOnlyHealthAnswersAndNothingIsWritten()
    {
        await using var factory = new ApiFactory();
        factory.EventStore.Failure = new IOException("connection refused by 10.20.30.40:9000");
        using var client = factory.Client();

        var health = await client.GetAsync(Health);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        var text = await health.Content.ReadAsStringAsync(Ct);
        using (var body = JsonDocument.Parse(text))
        {
            Assert.Equal("waiting_for_event_store", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("the event store does not answer the ingest account", body.RootElement.GetProperty("reason").GetString());
        }

        // The address is for the log, not for whoever asks the health route.
        Assert.DoesNotContain("10.20.30.40", text, StringComparison.Ordinal);
        Assert.Contains(factory.Logs.Lines, l => l.Contains("not serving yet", StringComparison.Ordinal) && l.Contains("10.20.30.40", StringComparison.Ordinal));

        await AssertEveryOtherRouteWaitsAsync(factory, client);
        SqliteConnection.ClearAllPools();
        Assert.Equal(factory.MetadataAtStart, await MetadataDump.TakeAsync(factory.MetadataPath, Ct));

        // The event store comes up: the comparison is tried again and the host serves, without a restart.
        factory.EventStore.Failure = null;
        await EventuallyOkAsync(client);
        await factory.AddUserAsync("alice", Password);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("alice", Password)).StatusCode);
    }

    [Fact]
    public async Task AnEventStoreWithoutItsSchemaIsWaitedForWithTheCommandThatFixesIt()
    {
        await using var factory = new ApiFactory();
        factory.EventStore.Failure = new Urlog.Core.Ingest.EventStoreSchemaException("the event store's schema has no store_identity table (server code 60); run 'urlog migrate' as the admin account first");
        using var client = factory.Client();

        var health = await client.GetAsync(Health);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        using var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync(Ct));
        Assert.Equal("waiting_for_event_store", body.RootElement.GetProperty("status").GetString());
        Assert.Contains("urlog migrate", body.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMismatchIsReportedWithBothValuesAndNothingIsServedOrWritten()
    {
        await using var factory = new ApiFactory();
        var mine = factory.StoreId;
        var theirs = StoreIdentity.NewId();
        factory.EventStore.StoreId = theirs; // the event store of another deployment
        using var client = factory.Client();

        var health = await client.GetAsync(Health);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        var text = await health.Content.ReadAsStringAsync(Ct);
        using (var body = JsonDocument.Parse(text))
        {
            Assert.Equal("store_identity_mismatch", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(mine, body.RootElement.GetProperty("metadata_store_id").GetString());
            Assert.Equal(theirs, body.RootElement.GetProperty("event_store_id").GetString());
            Assert.Contains("nothing was written", body.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
        }

        // The file's path and the server stay in the log, with both values and the command.
        Assert.DoesNotContain(factory.MetadataPath, text, StringComparison.Ordinal);
        Assert.Contains(factory.Logs.Lines, l => l.Contains(factory.MetadataPath, StringComparison.Ordinal) && l.Contains(mine, StringComparison.Ordinal) && l.Contains(theirs, StringComparison.Ordinal) && l.Contains("urlog pair", StringComparison.Ordinal));

        await AssertEveryOtherRouteWaitsAsync(factory, client);
        SqliteConnection.ClearAllPools();
        Assert.Equal(factory.MetadataAtStart, await MetadataDump.TakeAsync(factory.MetadataPath, Ct));

        // Still refused a few comparisons later: nothing makes a mismatch go away by itself.
        var reads = factory.EventStore.Reads;
        await EventuallyAsync(() => Task.FromResult(factory.EventStore.Reads >= reads + 3), "the comparison tried again");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(Health)).StatusCode);

        // The operator points the backend at its own store again (or pairs the two by hand): it serves.
        factory.EventStore.StoreId = mine;
        await EventuallyOkAsync(client);
    }

    [Fact]
    public async Task AMissingOrUnpairedMetadataFileIsAMismatchAndTheHostCreatesNothing()
    {
        // No metadata file: the host does not create one.
        await using (var factory = new ApiFactory(paired: false))
        {
            using var client = factory.Client();
            var health = await client.GetAsync(Health);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
            using var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync(Ct));
            Assert.Equal("store_identity_mismatch", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("none", body.RootElement.GetProperty("metadata_store_id").GetString());
            Assert.Equal(factory.EventStore.StoreId, body.RootElement.GetProperty("event_store_id").GetString());
            Assert.False(File.Exists(factory.MetadataPath));
        }

        // A metadata file from before the identity existed: reported, and not even migrated.
        await using (var factory = new ApiFactory(paired: false))
        {
            await using (var db = new MetadataStore(factory.MetadataPath).CreateDbContext())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261113061516_IntakeMembers", Ct);
            }

            SqliteConnection.ClearAllPools();
            using var client = factory.Client();
            var health = await client.GetAsync(Health);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
            using var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync(Ct));
            Assert.Equal("none", body.RootElement.GetProperty("metadata_store_id").GetString());
            await AssertEveryOtherRouteWaitsAsync(factory, client);
            SqliteConnection.ClearAllPools();
            Assert.Equal(factory.MetadataAtStart, await MetadataDump.TakeAsync(factory.MetadataPath, Ct));
            Assert.DoesNotContain("store_identity", factory.MetadataAtStart, StringComparison.Ordinal);
        }

        // Neither side has an identity.
        await using (var factory = new ApiFactory(paired: false))
        {
            factory.EventStore.StoreId = null;
            using var client = factory.Client();
            using var body = JsonDocument.Parse(await (await client.GetAsync(Health)).Content.ReadAsStringAsync(Ct));
            Assert.Equal(("none", "none"), (body.RootElement.GetProperty("metadata_store_id").GetString(), body.RootElement.GetProperty("event_store_id").GetString()));
            Assert.Contains(factory.Logs.Lines, l => l.Contains("urlog migrate --db", StringComparison.Ordinal));
        }
    }

    /// <summary>Every route of the live table but health answers 503 and names the health route; no handler ran.</summary>
    private static async Task AssertEveryOtherRouteWaitsAsync(ApiFactory factory, ApiClient client)
    {
        var routes = 0;
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var text = (endpoint.RoutePattern.RawText ?? string.Empty).TrimEnd('/');
            if (text == Health)
            {
                continue;
            }

            var path = text.Replace("{caseId:long}", "1", StringComparison.Ordinal).Replace("{id:long}", "1", StringComparison.Ordinal).Replace("{userId:long}", "1", StringComparison.Ordinal);
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                var http = new HttpMethod(method);
                var response = await client.SendAsync(http, path, http == HttpMethod.Get || http == HttpMethod.Delete ? null : new { name = "alice", password = Password });
                Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{method} {path} answered {(int)response.StatusCode}");
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                Assert.Contains(Health, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
                Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
                routes++;
            }
        }

        Assert.True(routes >= 10, $"only {routes} routes were tried");
        // A path with no route is held as well.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/anything")).StatusCode);
    }

    private static Task EventuallyOkAsync(ApiClient client) =>
        EventuallyAsync(async () => (await client.GetAsync(Health)).StatusCode == HttpStatusCode.OK, "health answers 200");

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(20, Ct);
        }
    }
}
