using Urlog.Api;
using Urlog.Api.Auth;

// Composition only: the pieces are in ApiHost so tests/Api.Tests builds the same host and the startup assertion runs there too.
var builder = WebApplication.CreateBuilder(args);
ApiHost.ConfigureServices(builder);

var app = builder.Build();
ApiHost.ConfigurePipeline(app);
ApiHost.MapEndpoints(app);
CaseAccess.AssertGuarded(app);
await ApiHost.StartAsync(app).ConfigureAwait(false);
await app.RunAsync().ConfigureAwait(false);

/// <summary>The entry point type WebApplicationFactory needs.</summary>
public partial class Program
{
}
