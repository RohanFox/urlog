using System.Collections.Concurrent;
using System.Net;
using Urlog.Api.Contracts;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;
using Microsoft.Extensions.Options;

namespace Urlog.Api.Auth;

/// <summary>What the throttle says about one attempt: let it through, or refuse it (and whether this refusal is the window's first).</summary>
public readonly record struct ThrottleDecision(bool Allowed, int RetryAfterSeconds, bool FirstRefusal);

/// <summary>
/// The per-address fixed window on <c>POST /api/v1/auth/login</c>: more than <see cref="LoginThrottleOptions.Attempts"/>
/// in <see cref="LoginThrottleOptions.Window"/> from one client address is 429 with <c>Retry-After</c>, before the user row is
/// read or any hash computed. <b>Storage: process memory, reset on every restart.</b> The design is one process; a restart
/// empties every window, which is harmless because the durable control (the lockout counter) is in SQLite and the throttle
/// only protects it. The address is <c>RemoteIpAddress</c> as the trusted proxy reported it (<see cref="ApiHost"/> adds the
/// forwarded-headers middleware only for configured proxies). Stale windows are swept on insert.
/// </summary>
public sealed class LoginThrottle(IOptions<UrlogOptions> options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<IPAddress, Entry> _windows = new();
    private readonly LoginThrottleOptions _options = options.Value.Auth.LoginThrottle;
    private int _sinceSweep;

    public int Attempts => _options.Attempts;

    public TimeSpan Window => _options.Window;

    public ThrottleDecision Check(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var now = clock.GetUtcNow();
        if (Interlocked.Increment(ref _sinceSweep) >= 1024)
        {
            Interlocked.Exchange(ref _sinceSweep, 0);
            Sweep(now);
        }

        var entry = _windows.GetOrAdd(address, _ => new Entry(now));
        lock (entry)
        {
            if (now >= entry.Start + _options.Window)
            {
                entry.Start = now;
                entry.Count = 0;
                entry.Tripped = false;
            }

            entry.Count++;
            if (entry.Count <= _options.Attempts)
            {
                return new ThrottleDecision(Allowed: true, 0, FirstRefusal: false);
            }

            var first = !entry.Tripped;
            entry.Tripped = true;
            var retryAfter = (int)Math.Ceiling((entry.Start + _options.Window - now).TotalSeconds);
            return new ThrottleDecision(Allowed: false, Math.Max(1, retryAfter), first);
        }
    }

    /// <summary>Windows currently tracked (tests).</summary>
    public int TrackedAddresses => _windows.Count;

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (address, entry) in _windows)
        {
            bool stale;
            lock (entry)
            {
                stale = now >= entry.Start + _options.Window;
            }

            if (stale)
            {
                _windows.TryRemove(address, out _);
            }
        }
    }

    private sealed class Entry(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; set; } = start;

        public int Count { get; set; }

        public bool Tripped { get; set; }
    }
}

/// <summary>
/// The filter on the login endpoint: asks <see cref="LoginThrottle"/> first; a refusal is 429 with <c>Retry-After</c>, and
/// the first refusal of a window writes one <c>login_throttled</c> row (address, the name as submitted, the limits). Later
/// refusals in the same window write nothing, so the chain grows by at most one row per address per window from here.
/// </summary>
public sealed class LoginThrottleFilter(LoginThrottle throttle, ILogger<LoginThrottleFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var address = http.Connection.RemoteIpAddress ?? IPAddress.None;
        var decision = throttle.Check(address);
        if (decision.Allowed)
        {
            return await next(context).ConfigureAwait(false);
        }

        Log.Throttled(logger, address.ToString(), throttle.Attempts, throttle.Window, decision.RetryAfterSeconds);
        if (decision.FirstRefusal)
        {
            // A login names the account in its body; a change of password is made by the session's user.
            var submitted = context.Arguments.OfType<LoginRequest>().FirstOrDefault()?.Name
                ?? http.Features.Get<SessionContext>()?.User.Name
                ?? string.Empty;
            await WriteRowAsync(http, address.ToString(), submitted).ConfigureAwait(false);
        }

        http.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var problem = Problems.Throttled(decision.RetryAfterSeconds);
        return TypedResults.Problem(problem);
    }

    private async Task WriteRowAsync(HttpContext http, string address, string submitted)
    {
        var db = http.RequestServices.GetRequiredService<MetadataDb>();
        var store = http.RequestServices.GetRequiredService<MetadataStore>();
        var name = submitted.Length <= LoginOperations.NameDetailMaxLength ? submitted : submitted[..LoginOperations.NameDetailMaxLength];
        var details = new Dictionary<string, object?>
        {
            ["address"] = address,
            ["name"] = name,
            ["attempts"] = throttle.Attempts,
            ["window_seconds"] = (long)throttle.Window.TotalSeconds,
        };
        await using var transaction = await AuditChain.BeginTransactionAsync(db, http.RequestAborted).ConfigureAwait(false);
        await store.Audit.AppendAsync(db, new AuditEntry(name.Length == 0 ? "anonymous" : name, null, AuditActions.LoginThrottled, null, null, details), http.RequestAborted).ConfigureAwait(false);
        await transaction.CommitAsync(http.RequestAborted).ConfigureAwait(false);
    }
}
