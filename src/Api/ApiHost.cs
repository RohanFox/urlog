using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urlog.Api.Auth;
using Urlog.Api.Endpoints;
using Urlog.Api.Middleware;
using Urlog.Core.Jobs;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Urlog.Api;

/// <summary>
/// The host's composition, in one place so tests/Api.Tests builds exactly the production host: services, the
/// pipeline order, the route table and the startup assertion. Program.cs calls the four methods in order.
/// </summary>
public static class ApiHost
{
    public const string AdminPolicy = "admin";

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ((IConfigurationBuilder)builder.Configuration).Add(new UrlogEnvironment());
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole();
        }

        var services = builder.Services;
        services.AddOptions<UrlogOptions>().Bind(builder.Configuration.GetSection(UrlogOptions.Section)).Validate(o =>
            !string.IsNullOrWhiteSpace(o.Metadata.Path) && o.Auth.LockoutThreshold > 0 && o.Auth.LoginThrottle.Attempts > 0 && o.Auth.LoginThrottle.Window > TimeSpan.Zero
            && o.Auth.IdleTimeout > TimeSpan.Zero && o.Auth.AbsoluteTimeout > TimeSpan.Zero && o.Auth.LockoutDuration > TimeSpan.Zero
            && o.Ingest.MaxConcurrentJobs > 0 && o.Ingest.Writers > 0 && o.Ingest.Parallelism >= 0 && o.Ingest.ProgressInterval > TimeSpan.Zero
            && o.Ingest.Watermark.MinFreeBytes >= 0 && o.Ingest.Watermark.MinFreePercent is >= 0 and <= 100
            && o.ClickHouse.RetryInterval > TimeSpan.Zero,
            "Urlog: metadata path, lockout, throttle, session timeouts and the ingest settings must be set and in range").ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<UrlogOptions>>().Value;
            var path = Path.IsPathRooted(options.Metadata.Path) ? options.Metadata.Path : Path.Combine(builder.Environment.ContentRootPath, options.Metadata.Path);
            return new MetadataStore(path, sp.GetRequiredService<TimeProvider>());
        });
        services.AddScoped(sp => sp.GetRequiredService<MetadataStore>().CreateDbContext());
        services.AddSingleton<PasswordService>();
        // The event store as the ingest account: the store identity check of this host, and the job runner's store.
        services.AddSingleton<IJobEventStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<UrlogOptions>>().Value;
            return ClickHouseJobEventStore.Create(options.ClickHouse.Host, options.ClickHouse.NativePort, options.ClickHouse.User, options.ClickHouse.Password, options.Ingest.Writers);
        });
        services.AddSingleton<IStoreIdentitySource>(sp => sp.GetRequiredService<IJobEventStore>());
        services.AddSingleton<StoreReadiness>();
        services.AddHostedService<StoreReadinessService>();
        if (builder.Configuration.GetValue($"{UrlogOptions.Section}:Ingest:Enabled", true))
        {
            // The ingest queue runs in this process as the ingest account; it holds no admin credential.
            services.AddHostedService<IngestService>();
        }
        services.AddSingleton<LoginThrottle>();

        services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder()
            // Every endpoint needs a session unless it says AllowAnonymous (login and health only; CaseAccess.AssertGuarded checks).
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(Names.Of(UserRole.Admin)));

        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });
        services.AddValidation();
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var http = app.Services.GetRequiredService<IOptions<UrlogOptions>>().Value.Http;
        if (http.TrustedProxies.Count > 0)
        {
            // Only a listed proxy's X-Forwarded-For is believed; with none listed the middleware is absent and the socket
            // address is the client address, so nobody can choose their throttle key.
            var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var proxy in http.TrustedProxies)
            {
                if (System.Net.IPNetwork.TryParse(proxy, out var network))
                {
                    forwarded.KnownIPNetworks.Add(network);
                }
                else if (IPAddress.TryParse(proxy, out var address))
                {
                    forwarded.KnownProxies.Add(address);
                }
                else
                {
                    throw new InvalidOperationException($"Urlog:Http:TrustedProxies: '{proxy}' is neither an address nor a CIDR network");
                }
            }

            app.UseForwardedHeaders(forwarded);
            var listed = string.Join(", ", http.TrustedProxies);
            Log.TrustedProxies(app.Logger, listed);
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        // Before anything that reads the metadata file: until the store identity check has passed, only the health route answers.
        app.UseMiddleware<StoreGateMiddleware>();
        if (Directory.Exists(app.Environment.WebRootPath))
        {
            // web/ builds into wwwroot (milestone 2); until it exists there is nothing to serve and nothing to warn about.
            app.UseStaticFiles();
        }

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<CsrfMiddleware>();
        app.UseMiddleware<CaseAccessMiddleware>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var api = app.MapGroup("/api/v1");
        HealthEndpoints.Map(api);
        AuthEndpoints.Map(api);
        UserEndpoints.Map(api);
        CaseEndpoints.Map(api);
        AdminCaseEndpoints.Map(api);

        // The SPA placeholder: web/ builds into wwwroot (milestone 2); until then there is nothing to fall back to.
        if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html")))
        {
            app.MapFallbackToFile("index.html").AllowAnonymous();
        }
    }

    /// <summary>
    /// The first comparison of the metadata file's store identity with the event store's, before the host listens.
    /// When it passes, the metadata file is migrated and the host serves from its first request. When it does not
    /// (the event store is away, or the two do not belong together) the host starts all the same and answers the
    /// health route alone, with the reason, while <see cref="StoreReadinessService"/> tries again. The host never
    /// creates a metadata file: <c>urlog migrate --db</c> does.
    /// </summary>
    public static async Task StartAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        await app.Services.GetRequiredService<StoreReadiness>().TryAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
    }
}
