using System.Globalization;
using Urlog.Core.Audit;
using Urlog.Core.Intake.Archives;
using Urlog.Core.Metadata;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog expand --db &lt;metadata.db&gt; --file &lt;id&gt; [--max-depth n] [--max-ratio n] [--max-total-gb n]
/// [--max-members n]</c>: queues the expansion of one archive again, with limits given here, after its expansion
/// failed at a cap. An administrator's act on the box: the limits apply to this one job, and the request with
/// every limit is written to the audit chain in the same transaction as the job. A limit that is not given keeps
/// its default. <c>urlog worker</c> or the backend runs the job.
/// </summary>
internal static class ExpandCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var fileId = (long)args.ULong("file", 0);
        if (fileId == 0)
        {
            throw new CliException("--file <id> is required: the id of the archive's file record");
        }

        var limits = new ArchiveLimits
        {
            MaxDepth = args.Int("max-depth", ArchiveLimits.Default.MaxDepth),
            MaxRatio = args.Int("max-ratio", ArchiveLimits.Default.MaxRatio),
            MaxTotalBytes = args.Option("max-total-gb") is null ? ArchiveLimits.Default.MaxTotalBytes : (long)args.Int("max-total-gb", 250) << 30,
            MaxMembers = args.Int("max-members", ArchiveLimits.Default.MaxMembers),
        };
        try
        {
            limits.Validate();
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new CliException(e.Message);
        }

        var store = new MetadataStore(VerifyAuditCommand.MetadataPath(args, mustExist: true));
        await StoreCheck.FullAsync(store.Path, new Connection(args), ct).ConfigureAwait(false);
        await store.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = store.CreateDbContext();
        try
        {
            var job = await JobOperations.RequestExpandRerunAsync(db, store.Audit, new AuditActor(Environment.UserName), fileId, limits.ToJson(), limits.ToDetails(), store.Clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"queued    job {job.Id}: expand of file {fileId} with depth {limits.MaxDepth}, ratio {limits.MaxRatio}:1, total {limits.MaxTotalBytes:N0} bytes, {limits.MaxMembers:N0} members"));
        }
        catch (MetadataException e)
        {
            throw new CliException(e.Message);
        }

        await UserCommand.WriteHeadAsync(db, ct).ConfigureAwait(false);
        return 0;
    }
}
