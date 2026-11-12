using System.Globalization;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog enqueue --db &lt;metadata.db&gt; --case &lt;id&gt; --host &lt;name&gt; [--source id] [--format layout] [--tz zone]
/// [--year yyyy] &lt;file&gt;</c>: the intake record and a queued parse for one file on this box. SHA-256 over the raw
/// bytes before anything else, then detection, then the <c>files</c> row with its <c>file_intake</c> audit row, then
/// the job. A file that is not a log (a document, a binary journal) gets its row in state <c>artifact</c> and no
/// job. The file stays where it is (<c>stored_path</c> is its absolute path, <c>source = cli</c>); the same bytes in
/// the same case are a duplicate and are not queued again. <c>urlog worker</c> or the backend runs the job.
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
        if (!File.Exists(path))
        {
            throw new CliException($"no such file: {path}");
        }

        var caseId = (long)args.ULong("case", 0);
        if (caseId == 0)
        {
            throw new CliException("--case <id> is required (a positive integer)");
        }

        var hostName = args.Required("host");
        var length = new FileInfo(path).Length;
        var sha256 = await FileDetection.HashAsync(path, ct).ConfigureAwait(false);
        Console.WriteLine($"file      {path}");
        Console.WriteLine($"bytes     {length:N0}");
        Console.WriteLine($"sha256    {sha256}");

        var catalog = FileDetection.Catalog();
        var wanted = args.Option("source");
        Dictionary<string, string>? sourceOptions = null;
        if (args.Option("format") is { } format)
        {
            sourceOptions = new Dictionary<string, string>(StringComparer.Ordinal) { [(wanted ?? "nginx") + ".log_format"] = format };
        }

        var detected = FileDetection.Run(path, wanted, catalog, sourceOptions);
        var record = detected.Record;
        var options = sourceOptions is null ? null : JsonSerializer.Serialize(sourceOptions);

        var store = new MetadataStore(VerifyAuditCommand.MetadataPath(args, mustExist: true));
        await store.MigrateAsync(ct).ConfigureAwait(false);
        await using var db = store.CreateDbContext();
        var now = store.Clock.GetUtcNow().UtcDateTime;
        try
        {
            var host = await HostOperations.GetOrCreateAsync(db, caseId, hostName, now, ct).ConfigureAwait(false);
            var file = new EvidenceFile
            {
                CaseId = caseId,
                HostId = host.Id,
                Sha256 = sha256,
                Size = length,
                OriginalName = Path.GetFileName(path),
                StoredPath = path,
                Source = FileSource.Cli,
                Uploader = Environment.UserName,
                ReceivedAt = now,
                Timezone = args.Option("tz"),
                YearHint = args.Option("year") is null ? null : args.Int("year", 1970),
                Encoding = Names.Of(detected.Encoding),
                Compression = detected.Compression == Urlog.Core.Pipeline.CompressionKind.None ? null : Names.Of(detected.Compression),
                DetectedKind = record.Kind,
                DetectedSource = record.SourceId,
                DetectedConfidence = record.Confidence,
                DetectedLayout = record.Layout,
                Detection = record.ToJson(),
                FileTime = File.GetLastWriteTimeUtc(path),
                OverrideSource = detected.Overridden ? detected.Source!.Info.Id : null,
                OverrideOptions = options,
                // Not a log and no parser named: hashed and recorded, never queued.
                State = detected.Source is null ? FileState.Artifact : FileState.Received,
            };
            var intake = await FileOperations.AddAsync(db, store.Audit, new AuditActor(Environment.UserName), file, ct).ConfigureAwait(false);
            if (intake.Duplicate)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"duplicate file {intake.File.Id} of case {caseId} already holds these bytes ('{intake.File.OriginalName}'); nothing queued"));
            }
            else if (detected.Source is null)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"recorded  file {intake.File.Id} of case {caseId} as {Names.Of(record.Kind)} ({record.Format}); nothing queued"));
            }
            else
            {
                var job = await JobOperations.EnqueueAsync(db, intake.File.Id, now, ct).ConfigureAwait(false);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"queued    job {job.Id}: {Names.Of(job.Kind)} of file {intake.File.Id}, case {caseId}, host {host.Name} (#{host.Id})"));
            }
        }
        catch (MetadataException e)
        {
            throw new CliException(e.Message);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException e)
        {
            throw new CliException($"the metadata store refused the record (does case {caseId} exist?): {e.InnerException?.Message ?? e.Message}");
        }

        await UserCommand.WriteHeadAsync(db, ct).ConfigureAwait(false);
        return 0;
    }
}
