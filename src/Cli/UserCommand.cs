using System.Globalization;
using System.Text;
using Urlog.Core.Audit;
using Urlog.Core.Auth;
using Urlog.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog user create|unlock|passwd</c>: the accounts the API cannot create or recover by itself. <c>create
/// --admin</c> bootstraps the first admin; <c>unlock</c> and <c>passwd</c> are the break-glass for a locked or forgotten
/// sole admin. OS access on the box is the authority: the actor is the OS user name, <c>actor_id</c> null. The password
/// comes from <c>--password-stdin</c> (one line on stdin), <c>URLOG_PASSWORD</c>, or a no-echo prompt on a terminal,
/// never from an argument (argv is visible to every process).
/// </summary>
internal static class UserCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var verb = args.Positional.Count > 0 ? args.Positional[0].ToLowerInvariant() : "";
        var path = VerifyAuditCommand.MetadataPath(args, mustExist: false);
        var name = args.Required("name");
        var store = new MetadataStore(path);
        await store.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = store.CreateDbContext();
        var actor = new AuditActor(Environment.UserName);
        var now = store.Clock.GetUtcNow().UtcDateTime;
        try
        {
            switch (verb)
            {
                case "create":
                {
                    var role = args.Flag("admin") ? UserRole.Admin : UserRole.Analyst;
                    var hash = PasswordHasher.Hash(await ReadPasswordAsync(args, confirm: true, ct).ConfigureAwait(false));
                    var user = await UserOperations.CreateAsync(db, store.Audit, actor, name, role, hash, now, ct).ConfigureAwait(false);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"user {user.Name} (id {user.Id}, {Names.Of(user.Role)}) created"));
                    break;
                }

                case "unlock":
                {
                    var user = await FindAsync(db, name, ct).ConfigureAwait(false);
                    var wasLocked = LoginOperations.IsLocked(user, LockoutPolicy.Default, now);
                    await UserOperations.UnlockAsync(db, store.Audit, actor, user.Id, now, ct).ConfigureAwait(false);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"user {user.Name} (id {user.Id}) unlocked (was {(wasLocked ? "locked" : "not locked")}, {user.FailedLogins} failed logins cleared)"));
                    break;
                }

                case "passwd":
                {
                    var user = await FindAsync(db, name, ct).ConfigureAwait(false);
                    var hash = PasswordHasher.Hash(await ReadPasswordAsync(args, confirm: true, ct).ConfigureAwait(false));
                    await UserOperations.ResetPasswordAsync(db, store.Audit, actor, user.Id, hash, now, ct).ConfigureAwait(false);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"user {user.Name} (id {user.Id}) password set; every session of the user ended"));
                    break;
                }

                default:
                    throw new CliException("urlog user create|unlock|passwd --db <metadata.db> --name <name> [--admin] [--password-stdin]");
            }
        }
        catch (MetadataException e)
        {
            throw new CliException(e.Message);
        }
        catch (ArgumentException e)
        {
            throw new CliException(e.Message);
        }

        await WriteHeadAsync(db, ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The head line after the change, to be recorded like <c>verify-audit</c> prints it.</summary>
    internal static async Task WriteHeadAsync(MetadataDb db, CancellationToken ct)
    {
        var head = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).FirstAsync(ct).ConfigureAwait(false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"audit head seq {head.Seq} hash {head.Hash}"));
    }

    private static async Task<User> FindAsync(MetadataDb db, string name, CancellationToken ct) =>
        await UserOperations.FindByNameAsync(db, UserOperations.NormalizeName(name), ct).ConfigureAwait(false) ?? throw new CliException($"no user named '{name}'");

    /// <summary>The password, checked against <see cref="PasswordPolicy"/>; never echoed, never an argument.</summary>
    internal static async Task<string> ReadPasswordAsync(CliArgs args, bool confirm, CancellationToken ct)
    {
        string? password;
        if (args.Flag("password-stdin"))
        {
            password = (await Console.In.ReadLineAsync(ct).ConfigureAwait(false))?.TrimEnd('\r', '\n');
        }
        else if (Environment.GetEnvironmentVariable("URLOG_PASSWORD") is { Length: > 0 } fromEnv)
        {
            password = fromEnv;
        }
        else if (!Console.IsInputRedirected)
        {
            password = Prompt("password: ");
            if (confirm && !string.Equals(password, Prompt("again: "), StringComparison.Ordinal))
            {
                throw new CliException("the two passwords differ");
            }
        }
        else
        {
            throw new CliException("no password: pass --password-stdin with the password on stdin, set URLOG_PASSWORD, or run on a terminal to be prompted");
        }

        if (PasswordPolicy.Check(password) is { } reason)
        {
            throw new CliException(reason);
        }

        return password!;
    }

    private static string Prompt(string label)
    {
        Console.Error.Write(label);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
            }
            else if (key.KeyChar != '\0')
            {
                sb.Append(key.KeyChar);
            }
        }
    }
}
