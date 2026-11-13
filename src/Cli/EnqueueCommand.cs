using System.Globalization;
using Urlog.Core.Detection;
using Urlog.Core.Intake;
using Urlog.Core.Metadata;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog enqueue --db &lt;metadata.db&gt; --case &lt;id&gt; [--host name] [--source id] [--format layout] [--tz zone]
/// [--year yyyy] [--evidence-root dir] &lt;file&gt;</c>: one file on this box through the intake pipeline. SHA-256 over
/// the raw bytes before anything else, then detection, then one transaction with the <c>files</c> row, its
/// <c>file_intake</c> audit row and its job: a parse for a log, an expansion for an archive (zip, tar, a compressed
/// tar, an archive inside an archive). A file that is not a log (a document, a binary journal) gets its row in
/// state <c>artifact</c> and no job. A packet capture is refused with an audit row and no record.
/// <para>
/// Without <c>--host</c> the file's name, and for an archive each member's path, proposes one; a file no pattern
/// fits is recorded unassigned and parsed all the same. The file stays where it is and <c>stored_path</c> is its
/// absolute path, unless it lies under <c>--evidence-root</c>, where it is made read-only and recorded relative to
/// its case. The same bytes in the same case are a duplicate and are not queued again. <c>urlog worker</c> or the
/// backend runs the job.
/// </para>
/// </summary>
internal static class EnqueueCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        if (args.Positional.Count != 1)
        {
            throw new CliException("enqueue takes exactly one file");
        }

        var path = Path.GetFullPath(args.Positional[0]);
        var caseId = (long)args.ULong("case", 0);
        if (caseId == 0)
        {
            throw new CliException("--case <id> is required (a positive integer)");
        }

        var wanted = args.Option("source");
        Dictionary<string, string>? sourceOptions = null;
        if (args.Option("format") is { } format)
        {
            sourceOptions = new Dictionary<string, string>(StringComparer.Ordinal) { [(wanted ?? "nginx") + ".log_format"] = format };
        }

        var store = new MetadataStore(VerifyAuditCommand.MetadataPath(args, mustExist: true));
        await store.MigrateAsync(ct).ConfigureAwait(false);
        var intake = new IntakePipeline(store, FileDetection.Catalog(), args.Option("evidence-root"));
        IntakeOutcome outcome;
        try
        {
            outcome = await intake.AcceptAsync(new IntakeRequest
            {
                CaseId = caseId,
                Path = path,
                Source = FileSource.Cli,
                Uploader = Environment.UserName,
                Host = args.Option("host"),
                Timezone = args.Option("tz"),
                Year = args.Option("year") is null ? null : args.Int("year", 1970),
                OverrideSource = wanted,
                Options = sourceOptions,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IntakeRefusedException or MetadataException)
        {
            throw new CliException(e.Message);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException e)
        {
            throw new CliException($"the metadata store refused the record (does case {caseId} exist?): {e.InnerException?.Message ?? e.Message}");
        }

        var record = outcome.Detection;
        Console.WriteLine($"file      {path}");
        Console.WriteLine($"bytes     {outcome.Size:N0}");
        Console.WriteLine($"sha256    {outcome.Sha256}");
        if (!record.Scores.IsEmpty)
        {
            Console.WriteLine($"detect    {string.Join(", ", record.Scores.OrderByDescending(s => s.Score).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.SourceId} {s.Score:F2}")))}");
        }

        var chain = record.Containers.IsDefaultOrEmpty ? string.Empty : $" [{string.Join(" > ", record.Containers)}]";
        Console.WriteLine($"kind      {Names.Of(record.Kind)}{chain}{(record.SourceId is null ? string.Empty : ", source " + record.SourceId)}{(record.Format is null ? string.Empty : ", " + record.Format)}");
        if (record.Message is not null)
        {
            Console.WriteLine($"note      {record.Message}");
        }

        if (outcome.Rejected)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"refused   nothing of the file is recorded; the refusal is in the audit trail of case {caseId}"));
        }
        else if (outcome.Duplicate)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"duplicate file {outcome.File!.Id} of case {caseId} already holds these bytes ('{outcome.File.OriginalName}'); nothing queued"));
        }
        else
        {
            var file = outcome.File!;
            var host = outcome.Host is null ? "no host (unassigned)" : $"host {outcome.Host}{(outcome.HostInferred ? " (inferred from the name)" : string.Empty)}";
            Console.WriteLine(outcome.Job is { } job
                ? string.Create(CultureInfo.InvariantCulture, $"queued    job {job.Id}: {Names.Of(job.Kind)} of file {file.Id}, case {caseId}, {host}")
                : string.Create(CultureInfo.InvariantCulture, $"recorded  file {file.Id} of case {caseId} as {Names.Of(record.Kind)} ({record.Format}); nothing queued"));
        }

        await using var db = store.CreateDbContext();
        await UserCommand.WriteHeadAsync(db, ct).ConfigureAwait(false);
        return outcome.Rejected ? 2 : 0;
    }
}
