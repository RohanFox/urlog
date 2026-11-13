using Urlog.Core.Ingest;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Api;

/// <summary>What <c>GET /api/v1/health</c> reports about the pairing of the metadata file and the event store.</summary>
/// <param name="Status"><c>ok</c>, <c>waiting_for_event_store</c> or <c>store_identity_mismatch</c>.</param>
/// <param name="Reason">Why the backend does not serve yet; null when it does.</param>
/// <param name="MetadataStoreId">On a mismatch, what the metadata file holds (<c>none</c> for none).</param>
/// <param name="EventStoreId">On a mismatch, what the event store holds (<c>none</c> for none).</param>
public sealed record StoreState(string Status, string? Reason = null, string? MetadataStoreId = null, string? EventStoreId = null)
{
    public const string Ok = "ok";
    public const string Waiting = "waiting_for_event_store";
    public const string Mismatch = "store_identity_mismatch";

    public bool IsReady => Status == Ok;
}

/// <summary>
/// The backend's store identity check (design record, "Consistency", Store identity). Nothing in this process
/// writes to the metadata file or to the event store before <see cref="TryAsync"/> has compared the two
/// identities and found them equal: not the metadata migrations, not a login, not the job runner. Until then the
/// host answers the health route alone, with the reason; the comparison is tried again every few seconds, so a
/// store that comes up later, or a pair joined with <c>urlog pair</c>, is picked up without a restart.
/// </summary>
public sealed class StoreReadiness(MetadataStore store, IStoreIdentitySource events, ILogger<StoreReadiness> logger)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile StoreState _state = new(StoreState.Waiting, "the store identity has not been compared yet");

    public StoreState State => _state;

    /// <summary>Completes when the comparison has passed and the metadata file is migrated.</summary>
    public Task WhenReady => _ready.Task;

    /// <summary>One comparison. True when the backend may serve.</summary>
    public async Task<bool> TryAsync(CancellationToken cancellationToken)
    {
        if (_state.IsReady)
        {
            return true;
        }

        StoreState next;
        try
        {
            var metadataId = await StoreIdentity.ReadAsync(store.Path, cancellationToken).ConfigureAwait(false);
            var storeId = await events.ReadStoreIdentityAsync(cancellationToken).ConfigureAwait(false);
            StoreIdentityGuard.Compare(store.Path, metadataId, events.Endpoint, storeId);

            await store.MigrateAsync(cancellationToken).ConfigureAwait(false);
            await using var db = store.CreateDbContext();
            var users = await db.Users.CountAsync(cancellationToken).ConfigureAwait(false);
            Log.Migrated(logger, store.Path, users);
            if (users == 0)
            {
                Log.NoUsers(logger, store.Path);
            }

            _state = new StoreState(StoreState.Ok);
            _ready.TrySetResult();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (StoreIdentityException e)
        {
            // The log names the file and the server; the health route, which anyone can read, names the two values only.
            next = new StoreState(StoreState.Mismatch,
                "the metadata file and the event store do not carry the same store identity; nothing is served and nothing was written",
                StoreIdentity.Show(e.MetadataId), StoreIdentity.Show(e.StoreId));
            Change(next, () => Log.StoreMismatch(logger, e.Message));
        }
        catch (EventStoreSchemaException e)
        {
            next = new StoreState(StoreState.Waiting, e.Message);
            Change(next, () => Log.StoreWaiting(logger, e.Message));
        }
        catch (Exception e)
        {
            next = new StoreState(StoreState.Waiting, "the event store does not answer the ingest account");
            Change(next, () => Log.StoreWaiting(logger, e.Message));
        }

        return false;
    }

    /// <summary>The hosted runner found the store replaced under it: the backend stops serving again.</summary>
    public void Lost(StoreIdentityException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _state = new StoreState(StoreState.Mismatch,
            "the event store was replaced while the backend was up: its store identity is no longer the metadata file's; nothing is served",
            StoreIdentity.Show(e.MetadataId), StoreIdentity.Show(e.StoreId));
        Log.StoreMismatch(logger, e.Message);
    }

    /// <summary>Logs when the state or its reason changes, not on every retry.</summary>
    private void Change(StoreState next, Action log)
    {
        if (_state != next)
        {
            _state = next;
            log();
        }
    }
}

/// <summary>Repeats the comparison while it has not passed.</summary>
public sealed class StoreReadinessService(StoreReadiness readiness, Microsoft.Extensions.Options.IOptions<UrlogOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.ClickHouse.RetryInterval;
        try
        {
            while (!await readiness.TryAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Until the store identity comparison has passed, every route but the health route answers 503: no handler runs,
/// so nothing reads or writes the metadata file on behalf of a request.
/// </summary>
public sealed class StoreGateMiddleware(RequestDelegate next, StoreReadiness readiness)
{
    public const string HealthRoute = "/api/v1/health";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (readiness.State.IsReady || context.Request.Path.Equals(HealthRoute, StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }

        context.Response.Headers.RetryAfter = "5";
        return Problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "the service is not ready", "https://urlog/problems/not-ready", "see " + HealthRoute);
    }
}
