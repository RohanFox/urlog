using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urlog.Api.Auth;
using Urlog.Core.Auth;
using Urlog.Core.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Urlog.Api.Tests;

/// <summary>
/// The production host (Program → ApiHost) on a fresh SQLite file per factory, a settable clock, a log capture, and a
/// test-only startup filter that sets the socket address from the <c>X-Test-Client-Address</c> header (TestServer has no
/// socket). Settings overrides go through <paramref name="settings"/> (<c>Urlog:Auth:LoginThrottle:Attempts</c>, ...).
/// </summary>
/// <param name="paired">
/// True: the metadata file exists and carries the identity the fake event store answers with, as after
/// <c>urlog migrate --db</c>. False: no metadata file, so the host finds no identity on its side.
/// </param>
internal sealed class ApiFactory(IDictionary<string, string?>? settings = null, bool paired = true) : WebApplicationFactory<Program>
{
    public const string ClientAddressHeader = "X-Test-Client-Address";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("urlog-api-");
    private FakeIdentitySource? _eventStore;

    public FakeClock Clock { get; } = new();

    public CaptureLoggerProvider Logs { get; } = new();

    /// <summary>The event store's side of the store identity check; set before the first request to change what the host finds.</summary>
    public FakeIdentitySource EventStore => _eventStore ??= new FakeIdentitySource { StoreId = StoreId };

    /// <summary>The identity a paired factory writes into its metadata file, whatever a test makes the event store answer later.</summary>
    public string StoreId { get; } = StoreIdentity.NewId();

    public string MetadataPath => Path.Combine(_directory.FullName, "metadata.db");

    /// <summary>Where this host keeps evidence: a folder of its own, removed with the factory.</summary>
    public string EvidenceRoot => Path.Combine(_directory.FullName, "intake");

    /// <summary>The intake volume as the upload gate sees it; roomy unless a test says otherwise.</summary>
    public FakeDiskProbe Disk { get; } = new();

    /// <summary>Every row of the metadata file as it was when the host was built, for tests that assert the host wrote nothing.</summary>
    public string MetadataAtStart { get; private set; } = string.Empty;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Urlog:Metadata:Path", MetadataPath);
        builder.UseSetting("Urlog:Ingest:Enabled", "false"); // no event store behind these tests; the runner has its own
        builder.UseSetting("Urlog:ClickHouse:RetryInterval", "00:00:00.050");
        builder.UseSetting("Urlog:Ingest:EvidenceRoot", EvidenceRoot);
        if (paired && !File.Exists(MetadataPath))
        {
            var id = StoreId;
            Task.Run(async () =>
            {
                var store = new MetadataStore(MetadataPath);
                await store.MigrateAsync();
                await using var db = store.CreateDbContext();
                db.StoreIdentity.Add(new StoreIdentityRow { StoreId = id, CreatedAt = DateTime.UtcNow, Origin = StoreIdentityOrigin.Created });
                await db.SaveChangesAsync();
            }).GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
        }

        // What the file held before the host saw it (a test may have put a file of its own there).
        MetadataAtStart = Urlog.Tests.Shared.MetadataDump.TakeAsync(MetadataPath).GetAwaiter().GetResult();

        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<ILoggerProvider>(Logs);
            services.AddSingleton<Urlog.Core.Jobs.IStoreIdentitySource>(EventStore);
            services.AddSingleton<Urlog.Core.Jobs.IDiskProbe>(Disk);
            services.AddTransient<IStartupFilter, TestClientAddressFilter>();
        });
    }

    /// <summary>A client that does not follow redirects and handles cookies by hand (<see cref="ApiClient"/>), on an https origin so the Secure cookie is honoured.</summary>
    public ApiClient Client(string address = "203.0.113.10") =>
        new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost") }), address);

    public MetadataStore Store => Services.GetRequiredService<MetadataStore>();

    public MetadataDb Open() => Store.CreateDbContext();

    /// <summary>An account straight into the store (the CLI's path), audited as <c>cli</c>.</summary>
    public async Task<User> AddUserAsync(string name, string password, UserRole role = UserRole.Analyst)
    {
        await using var db = Open();
        return await UserOperations.CreateAsync(db, Store.Audit, new Core.Audit.AuditActor("cli"), name, role, PasswordHasher.Hash(password), Clock.GetUtcNow().UtcDateTime);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                // Evidence is read-only on disk, which on Windows also stops a delete.
                foreach (var file in _directory.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    file.Attributes = FileAttributes.Normal;
                }

                _directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A straggling handle on Windows: the OS cleans the temp directory eventually.
            }
        }
    }

    private sealed class TestClientAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(ClientAddressHeader, out var header) && IPAddress.TryParse(header.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>Free and total bytes of every volume, as a test sets them.</summary>
internal sealed class FakeDiskProbe : Urlog.Core.Jobs.IDiskProbe
{
    public long FreeBytes { get; set; } = 500L << 30;

    public long TotalBytes { get; set; } = 1000L << 30;

    public Urlog.Core.Ingest.DiskSpace Measure(string path) => new(path, FreeBytes, TotalBytes);
}

/// <summary>The event store as the host's store identity check sees it: an identity, none, or no answer.</summary>
internal sealed class FakeIdentitySource : Urlog.Core.Jobs.IStoreIdentitySource
{
    private int _reads;

    public string Endpoint => "fake-store:9000";

    public string? StoreId { get; set; }

    /// <summary>Thrown in place of an answer: the store is away.</summary>
    public Exception? Failure { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public Task<string?> ReadStoreIdentityAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        return Failure is { } failure ? Task.FromException<string?>(failure) : Task.FromResult(StoreId);
    }
}

internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(DateTime.UtcNow.Ticks - (DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;
}

/// <summary>Every log line the host writes, formatted, so a test can assert a secret is in none of them.</summary>
internal sealed class CaptureLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = $"{logLevel} {category} {formatter(state, exception)}";
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                text += " " + string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"));
            }

            if (exception is not null)
            {
                text += " " + exception;
            }

            lines.Enqueue(text);
        }
    }
}

/// <summary>An HTTP client that carries the session cookie, the CSRF token and the test client address.</summary>
internal sealed class ApiClient(HttpClient http, string address) : IDisposable
{
    public HttpClient Http { get; } = http;

    public string Address { get; set; } = address;

    public string? Cookie { get; private set; }

    public string? Csrf { get; set; }

    public async Task<HttpResponseMessage> LoginAsync(string name, string password)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { name, password }, csrf: false);
        if (response.IsSuccessStatusCode)
        {
            var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SessionAuthenticationHandler.CookieName + "=", StringComparison.Ordinal));
            Cookie = setCookie.Split(';')[0];
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Csrf = doc.RootElement.GetProperty("csrf_token").GetString();
        }

        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, body: null);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body);

    public Task<HttpResponseMessage> PutAsync(string path, object? body = null) => SendAsync(HttpMethod.Put, path, body);

    public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, body: null);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool csrf = true, bool cookie = true)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(ApiFactory.ClientAddressHeader, Address);
        if (cookie && Cookie is not null)
        {
            request.Headers.Add("Cookie", Cookie);
        }

        if (csrf && Csrf is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, Csrf);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: ApiFactory.Json);
        }

        return await Http.SendAsync(request);
    }

    /// <summary>A request built by the test (tus headers, a binary body), with the session's cookie, CSRF token and address added.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool csrf = true)
    {
        request.Headers.Add(ApiFactory.ClientAddressHeader, Address);
        if (Cookie is not null)
        {
            request.Headers.Add("Cookie", Cookie);
        }

        if (csrf && Csrf is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, Csrf);
        }

        return await Http.SendAsync(request);
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(text, ApiFactory.Json) ?? throw new InvalidOperationException("empty body");
    }

    public void Dispose() => Http.Dispose();
}
