using System.Globalization;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog case create --db &lt;metadata.db&gt; --name &lt;case name&gt; --owner &lt;user name&gt;</c>: a case with its
/// owner member row and the <c>case_create</c> audit row, from the box. The actor is the OS user name.
/// </summary>
internal static class CaseCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var verb = args.Positional.Count > 0 ? args.Positional[0].ToLowerInvariant() : "";
        if (verb != "create")
        {
            throw new CliException("urlog case create --db <metadata.db> --name <case name> --owner <user name>");
        }

        var path = VerifyAuditCommand.MetadataPath(args, mustExist: false);
        var name = args.Required("name");
        var ownerName = args.Required("owner");
        var store = new MetadataStore(path);
        await store.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = store.CreateDbContext();
        var now = store.Clock.GetUtcNow().UtcDateTime;
        try
        {
            var owner = await UserOperations.FindByNameAsync(db, UserOperations.NormalizeName(ownerName), ct).ConfigureAwait(false)
                ?? throw new CliException($"no user named '{ownerName}'");
            var @case = await CaseOperations.CreateAsync(db, store.Audit, new AuditActor(Environment.UserName), name, owner.Id, now, ct).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"case {@case.Id} '{@case.Name}' created, owner {owner.Name} (id {owner.Id})"));
        }
        catch (MetadataException e)
        {
            throw new CliException(e.Message);
        }

        await UserCommand.WriteHeadAsync(db, ct).ConfigureAwait(false);
        return 0;
    }
}
