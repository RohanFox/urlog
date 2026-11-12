using ClickHouse.Driver.Tcp;
using Urlog.Core.Ingest;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog migrate</c>: applies the embedded ClickHouse migrations as the <b>admin</b> account
/// (<c>--admin-user</c>/<c>--admin-password</c> or <c>URLOG_CH_ADMIN_USER</c>/<c>URLOG_CH_ADMIN_PASSWORD</c>).
/// The only command that changes the schema: <c>urlog ingest</c> and the backend run as <c>urlog_ingest</c>,
/// which holds no DDL grant and cannot read <c>schema_migrations</c>. The account that runs this is the
/// definer of <c>events_v</c> (ADR 0001): never drop or rename it afterwards.
/// </summary>
internal static class MigrateCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        if (args.Positional.Count != 0)
        {
            throw new CliException("migrate takes no argument");
        }

        var connection = new Connection(args);
        Console.WriteLine(connection.AnnounceAdmin());
        await using var admin = connection.Admin();
        MigrationReport report;
        try
        {
            report = await new ClickHouseMigrator(admin).ApplyAsync(MigrationSet.Embedded(), ct).ConfigureAwait(false);
        }
        catch (ClickHouseTcpServerException e) when (e.RawCode == 497)
        {
            throw new CliException($"the account '{connection.AdminUser}' may not change the schema (server code 497); migrate runs as the admin account: --admin-user/--admin-password or URLOG_CH_ADMIN_USER/URLOG_CH_ADMIN_PASSWORD");
        }

        Console.WriteLine(report.Applied.Count > 0
            ? $"schema    applied migrations {string.Join(", ", report.Applied)}; already applied: {report.AlreadyApplied.Count}"
            : $"schema    up to date ({report.AlreadyApplied.Count} migrations)");
        return 0;
    }
}
