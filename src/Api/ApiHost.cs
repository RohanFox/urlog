using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urlog.Api.Auth;
using Urlog.Api.Endpoints;
using Urlog.Api.Middleware;
using Urlog.Api.Uploads;
using Urlog.Core.Intake;
using Urlog.Core.Sources;
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

    /// <summary>The setting that names the local settings file; empty turns it off.</summary>
    public const string LocalSettingsKey = "Urlog:LocalSettings";

    /// <summary>The local settings file when the setting does not name another: beside appsettings.json, never tracked.</summary>
    public const string LocalSettingsFile = "appsettings.Local.json";

    /// <summary>The name of the one description of the routes (<c>web/openapi.json</c>).</summary>
    public const string RouteDescription = "v1";

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddLocalSettings(builder.Configuration, builder.Environment.ContentRootPath, builder.Configuration[LocalSettingsKey] ?? LocalSettingsFile);
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
            && o.ClickHouse.RetryInterval > TimeSpan.Zero
            && !string.IsNullOrWhiteSpace(o.Ingest.EvidenceRoot) && o.Upload.MaxFileBytes > 0 && o.Upload.Expiry > TimeSpan.Zero && o.Upload.SweepInterval > TimeSpan.Zero
            && o.Upload.HandOverBusyTimeout > TimeSpan.Zero && o.Upload.HandOverRetry > TimeSpan.Zero,
            "Urlog: metadata path, lockout, throttle, session timeouts, the evidence root and the ingest and upload settings must be set and in range").ValidateOnStart();

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
        // The event store as the read-only query account, over HTTP: what the API reads for a member of a case.
        services.AddSingleton(sp =>
        {
            var clickHouse = sp.GetRequiredService<IOptions<UrlogOptions>>().Value.ClickHouse;
            return new Urlog.Query.ClickHouseQueryClient(clickHouse.Host, clickHouse.HttpPort, clickHouse.QueryUser, clickHouse.QueryPassword);
        });
        services.AddSingleton<Endpoints.IFieldStatsSource, Endpoints.QueryFieldStatsSource>();
        services.AddSingleton<Search.ISearchBackend, Search.QuerySearchBackend>();
        services.AddSingleton<Search.IStoredHistogram, Search.StoredHistogram>();
        services.AddSingleton<Search.QueryAudit>();
        services.AddSingleton(sp =>
        {
            var query = sp.GetRequiredService<IOptions<UrlogOptions>>().Value.Query;
            query.Validate();
            return new Search.QuerySlots(query.UserSlots, query.TotalSlots, query.SlotWait);
        });
        services.AddSingleton<StoreReadiness>();
        services.AddHostedService<StoreReadinessService>();
        if (builder.Configuration.GetValue($"{UrlogOptions.Section}:Ingest:Enabled", true))
        {
            // The ingest queue runs in this process as the ingest account; it holds no admin credential.
            services.AddHostedService<IngestService>();
        }
        services.AddSingleton<LoginThrottle>();

        // Web upload: the bytes land under the evidence root and reach the records through the intake pipeline alone.
        services.AddSingleton(sp => new UploadFolders(EvidenceRoot(sp.GetRequiredService<IOptions<UrlogOptions>>().Value, builder.Environment)));
        services.AddSingleton(_ =>
        {
            var catalog = new SourceCatalog();
            catalog.Add(Urlog.Sources.LogSourceRegistry.Registrations);
            return catalog;
        });
        // What the hosts and files routes ask about the sources: ids, who reads a zone, and whether a set of options is one a source takes.
        services.AddSingleton(sp => new SourceRules(sp.GetRequiredService<SourceCatalog>(), SourceOptionChecks.Check));
        services.AddSingleton<ICaseUsageSource, CaseUsageSource>();
        services.AddSingleton(sp =>
        {
            // The record of an upload waits for a locked metadata file as the job runner's commit does.
            var retry = new BusyRetry { Timeout = sp.GetRequiredService<IOptions<UrlogOptions>>().Value.Upload.HandOverBusyTimeout };
            return new IntakePipeline(sp.GetRequiredService<MetadataStore>(), sp.GetRequiredService<SourceCatalog>(), sp.GetRequiredService<UploadFolders>().Root, recordRetry: retry);
        });
        services.AddSingleton<IDiskProbe, DriveDiskProbe>();
        services.AddSingleton(sp =>
        {
            var mark = sp.GetRequiredService<IOptions<UrlogOptions>>().Value.Ingest.Watermark;
            return new IntakeGate(sp.GetRequiredService<IDiskProbe>(), new WatermarkOptions { MinFreeBytes = mark.MinFreeBytes, MinFreePercent = mark.MinFreePercent });
        });
        services.AddSingleton<PendingHandOvers>();
        services.AddSingleton<UploadFinaliser>();
        services.AddSingleton<UploadHandOverRetryService>();
        services.AddHostedService(sp => sp.GetRequiredService<UploadHandOverRetryService>());
        services.AddSingleton<UploadSweeper>();
        services.AddHostedService<UploadSweepService>();

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
        // The description of the routes, for the client's types. No route serves it: web/openapi.json is written from
        // it and a test compares the two, so a changed route or record fails the build until the file follows.
        services.AddOpenApi(RouteDescription);
        services.AddValidation();
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
    }

    /// <summary>
    /// Adds the settings of this one box: an optional JSON file, <c>appsettings.Local.json</c> in the content root
    /// unless <paramref name="file"/> names another, that git ignores. It is read after the tracked appsettings
    /// files and before everything that comes after them (user secrets, the environment, the command line), so it
    /// changes a tracked default and an environment variable still has the last word. A file that is not there is
    /// no error. An empty name adds nothing.
    /// </summary>
    public static void AddLocalSettings(IConfigurationBuilder configuration, string contentRoot, string? file)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(contentRoot);
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        var path = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(contentRoot, file));
        var source = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource { Path = path, Optional = true, ReloadOnChange = false };
        source.ResolveFileProvider();
        var after = -1;
        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource { Path: { } tracked } && Path.GetFileName(tracked).StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase))
            {
                after = i;
            }
        }

        if (after < 0)
        {
            // No appsettings file in front of it: the local file is the first word, and every other source still follows.
            configuration.Sources.Insert(0, source);
            return;
        }

        configuration.Sources.Insert(after + 1, source);
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

        // Evidence is reached through the API and its case ACL, never as a file of the site.
        var evidence = app.Services.GetRequiredService<UploadFolders>().Root;
        if (app.Environment.WebRootPath is { Length: > 0 } web && IsUnder(evidence, web))
        {
            throw new InvalidOperationException($"Urlog:Ingest:EvidenceRoot ({evidence}) lies inside the web root ({Path.GetFullPath(web)}), whose files are served to anyone; the host refuses to start");
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        // Before anything that reads the metadata file: until the store identity check has passed, only the health route answers.
        app.UseMiddleware<StoreGateMiddleware>();
        if (Directory.Exists(app.Environment.WebRootPath))
        {
            // web/ builds into wwwroot; until it exists there is nothing to serve and nothing to warn about.
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

        // The SPA placeholder: web/ builds into wwwroot; until then there is nothing to fall back to.
        if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html")))
        {
            // An address the interface routes itself answers with the page. One under /api names a route of the
            // API or nothing: answered with the page it would read as a success to a client that asked for data.
            app.MapFallbackToFile("index.html").AllowAnonymous();
            app.MapFallback("/api/{**rest}", () => TypedResults.Problem(Problems.NotFound("no such route")));
        }
    }

    /// <summary>The evidence root as a full path; a relative setting is under the content root.</summary>
    public static string EvidenceRoot(UrlogOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var root = options.Ingest.EvidenceRoot;
        return Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(environment.ContentRootPath, root));
    }

    private static bool IsUnder(string path, string directory)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        return child.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
