using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urlog.Api.Auth;
using Urlog.Api.Endpoints;
using Urlog.Api.Middleware;
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
            && o.Ingest.Watermark.MinFreeBytes >= 0 && o.Ingest.Watermark.MinFreePercent is >= 0 and <= 100,
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
        if (builder.Configuration.GetValue($"{UrlogOptions.Section}:Ingest:Enabled", true))
        {
            // The ingest queue runs in this process as the ingest account; it holds no admin credential.
            services.AddHostedService<IngestService>();
        }
        services.AddSingleton<LoginThrottle>();

        services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder()
            // Every endpoint needs a session unless it says AllowAnonymous (login only; CaseAccess.AssertGuarded checks).
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

    /// <summary>Migrates the metadata store (creates the file on first run, writes the genesis row) and warns when nobody can log in yet.</summary>
    public static async Task StartAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var store = app.Services.GetRequiredService<MetadataStore>();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        await store.MigrateAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
        await using var db = store.CreateDbContext();
        var users = await db.Users.CountAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
        Log.Migrated(app.Logger, store.Path, users);
        if (users == 0)
        {
            Log.NoUsers(app.Logger, store.Path);
        }
    }
}
