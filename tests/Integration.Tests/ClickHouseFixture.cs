using DotNet.Testcontainers.Builders;
using Urlog.Core;
using Testcontainers.ClickHouse;
using Urlog.Tests.Shared;

namespace Urlog.Integration.Tests;

/// <summary>
/// One ClickHouse container per test class, the same image tag as compose.dev.yml and the same
/// users/config overrides from deploy/clickhouse, so what the tests prove is what the dev stack runs.
/// </summary>
public sealed class ClickHouseFixture : IAsyncLifetime
{
    /// <summary>Must match <c>compose.dev.yml</c>; the integration test asserts the major.minor against <see cref="CoreInfo.ClickHouseLts"/>.</summary>
    public const string Image = "clickhouse/clickhouse-server:26.8.36.3";

    public const string NativePort = "9000";
    public const string AdminUser = "default";
    public const string AdminPassword = "clickhouse";
    public const string QueryUser = "urlog_query";
    public const string QueryPassword = "urlog_query";

    private readonly ClickHouseContainer _container;

    public ClickHouseFixture()
    {
        var deploy = FindDeployDir();
        _container = new ClickHouseBuilder(Image)
            .WithUsername(AdminUser)
            .WithPassword(AdminPassword)
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithResourceMapping(new FileInfo(Path.Combine(deploy, "config.d", "urlog.xml")), "/etc/clickhouse-server/config.d/")
            .WithResourceMapping(new FileInfo(Path.Combine(deploy, "users.d", "urlog.xml")), "/etc/clickhouse-server/users.d/")
            // A ceiling of its own, so the server's memory limit does not move with what else the box holds.
            .WithResourceMapping(TestServerMemory.ConfigBytes, TestServerMemory.ContainerPath)
            .Build();
    }

    public string Host => _container.Hostname;

    public int NativeHostPort => _container.GetMappedPublicPort(9000);

    public string NativeConnectionString(string user, string password) =>
        $"Host={Host};Port={NativeHostPort};Username={user};Password={password};Database=default;Compression=lz4";

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static string FindDeployDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("urlog.sln not found above the test binary; cannot locate deploy/clickhouse");
        }

        return Path.Combine(dir.FullName, "deploy", "clickhouse");
    }
}
