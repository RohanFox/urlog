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
internal sealed class ApiFactory(IDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public const string ClientAddressHeader = "X-Test-Client-Address";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("urlog-api-");

    public FakeClock Clock { get; } = new();

    public CaptureLoggerProvider Logs { get; } = new();

    public string MetadataPath => Path.Combine(_directory.FullName, "metadata.db");

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

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(text, ApiFactory.Json) ?? throw new InvalidOperationException("empty body");
    }

    public void Dispose() => Http.Dispose();
}
