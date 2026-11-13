using System.Diagnostics;
using System.Globalization;
using Urlog.Core.Ingest;
using Urlog.Core.Pipeline;
using Urlog.Query;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog ingest --case &lt;id&gt; --host &lt;name&gt; [--host-id n] [--file-id n] [--generation n] [--source id]
/// [--format &lt;nginx layout&gt;] [--tz zone] [--year yyyy] [--parallelism n] [--writers n] [--slo] [--allow-truncated]
/// &lt;file&gt;</c>.
/// The real pipeline end to end: SHA-256 over the raw bytes before anything else (evidence rule 1), detection
/// by every registered source's <c>Sniff</c> (or <c>--source</c>), <see cref="ParsePipeline"/> into
/// <see cref="NativeInserter"/> on the <c>events</c> table, then the <c>file_generation</c> commit. A generation
/// above 1 is a re-parse: the file's rows are deleted first (design record, "Consistency"). No metadata store yet:
/// the ids are arguments, and so there is no file record to carry a flag: a compressed input that is cut short is
/// not committed here unless <c>--allow-truncated</c> says so (<c>urlog enqueue</c> records the flag). Everything runs as the ingest account (<c>urlog_ingest</c>), which
/// cannot change the schema: a missing schema is an error that names <c>urlog migrate</c>.
/// </summary>
internal static class IngestCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        if (args.Positional.Count != 1)
        {
            throw new CliException("ingest takes exactly one file");
        }

        var path = Path.GetFullPath(args.Positional[0]);
        if (!File.Exists(path))
        {
            throw new CliException($"no such file: {path}");
        }

        var caseId = args.ULong("case", 0);
        if (caseId == 0)
        {
            throw new CliException("--case <id> is required (a positive integer)");
        }

        var host = args.Required("host");
        var hostId = args.ULong("host-id", 1);
        var fileId = args.ULong("file-id", 1);
        var generation = (uint)args.ULong("generation", 1);
        var year = args.Int("year", 1970);
        var zoneHint = TimeSpan.Zero;
        if (args.Option("tz") is { } tz)
        {
            // The offset at the file's own time, as the job runner takes it: never the clock's.
            zoneHint = TimeZones.Find(tz).GetUtcOffset(File.GetLastWriteTimeUtc(path));
        }

        var parallelism = args.Int("parallelism", Math.Min(Environment.ProcessorCount, 8));
        var writers = args.Int("writers", 4);
        var quiet = args.Flag("quiet");
        var connection = new Connection(args);
        Console.WriteLine(connection.AnnounceIngest());

        // 1. Hash the evidence bytes as they are on disk.
        var total = Stopwatch.StartNew();
        var length = new FileInfo(path).Length;
        var sha256 = await FileDetection.HashAsync(path, ct).ConfigureAwait(false);

        var hashSeconds = total.Elapsed.TotalSeconds;
        Console.WriteLine($"file      {path}");
        Console.WriteLine($"bytes     {length:N0}");
        Console.WriteLine($"sha256    {sha256}  ({length / 1e6 / hashSeconds:N0} MB/s)");

        // 2. Container, encoding, format.
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Option("format") is { } format)
        {
            options[(args.Option("source") ?? "nginx") + ".log_format"] = format;
        }

        var detected = FileDetection.Run(path, args.Option("source"), FileDetection.Catalog(), options.Count == 0 ? null : options);
        var source = detected.Source
            ?? throw new CliException($"not a log ({detected.Record.Format}); nothing ingested. To parse it anyway name a parser with --source <id>");
        var compression = detected.Compression;
        var encoding = detected.Encoding;

        // 3. The schema is there (this account cannot create it), then the re-parse protocol's delete when this is not
        //    the first generation.
        await using var control = connection.Ingest();
        var store = new EventStoreAdmin(control);
        string? storeIdentity;
        try
        {
            await store.RequireSchemaAsync(ct).ConfigureAwait(false);
            storeIdentity = await store.ReadStoreIdentityAsync(ct).ConfigureAwait(false);
        }
        catch (EventStoreSchemaException e)
        {
            throw new CliException($"{e.Message} (connected as '{connection.IngestUser}': --user/--password or URLOG_CH_USER/URLOG_CH_PASSWORD)");
        }
        catch (Urlog.Core.Metadata.StoreIdentityException e)
        {
            throw new CliException(e.Message);
        }

        RequireStore(connection.Server, storeIdentity, args.Option("store-id"), caseId, fileId);

        if (generation > 1)
        {
            await store.DeleteFileRowsAsync(fileId, ct).ConfigureAwait(false);
            Console.WriteLine($"reparse   generation {generation}: rows of file {fileId} deleted (lightweight), the view hides the old generation at commit");
        }

        // 4. The pipeline into the inserter.
        var pipelineOptions = new PipelineOptions
        {
            Parallelism = parallelism,
            InFlightChunks = parallelism + 2,
            Writers = writers,
            InFlightBatches = writers,
        };
        pipelineOptions.Validate();
        var buffers = new PipelineBuffers(pipelineOptions);
        var pipeline = new ParsePipeline(buffers);
        var identity = new FileIdentity(caseId, hostId, fileId, generation, host, zoneHint, year, options.Count == 0 ? null : options, File.GetLastWriteTimeUtc(path));
        Console.WriteLine($"pipeline  {parallelism} parsers, {writers} writers, chunks {pipelineOptions.ChunkBytes >> 20} MiB, batches {pipelineOptions.BatchRows:N0} rows; case {caseId}, host {host} (#{hostId}), file #{fileId}, generation {generation}");

        PipelineResult result;
        InputEnd inputEnd;
        long inserts, wireBytes, payloadBytes;
        var parse = Stopwatch.StartNew();
        await using (var inserter = new NativeInserter(connection.Inserter(writers), pipelineOptions))
        {
            using var raw = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var decompressed = InputDecoding.OpenChecked(raw, compression);
            using var decoded = InputDecoding.Transcode(decompressed, encoding);
            result = await pipeline.RunAsync(decoded, source, identity, inserter, ct).ConfigureAwait(false);
            inputEnd = decompressed.End;
            inserts = inserter.Inserts;
            wireBytes = inserter.BytesSent;
            payloadBytes = inserter.PayloadBytes;
        }

        parse.Stop();

        if (inputEnd.Kind != InputEndKind.Complete && !(inputEnd.IsTruncated && args.Flag("allow-truncated")))
        {
            // Nothing was committed, so nothing is visible; the rows of this attempt go as well.
            await store.DeleteFileRowsAsync(fileId, ct).ConfigureAwait(false);
            throw new CliException(inputEnd.IsTruncated
                ? $"the {Urlog.Core.Metadata.Names.Of(compression)} input is truncated: it ends after {inputEnd.DecodedOffset:N0} bytes of content, at stored byte {inputEnd.StoredOffset:N0}. Nothing was committed. 'urlog enqueue' records the file with that flag; --allow-truncated commits what was read from here"
                : "the end of the input was not determined; nothing was committed");
        }

        if (inputEnd.IsTruncated)
        {
            Console.WriteLine($"note      input truncated: content ends after {inputEnd.DecodedOffset:N0} decoded bytes, at stored byte {inputEnd.StoredOffset:N0}; committed with --allow-truncated");
        }
        else if (inputEnd.TrailingBytes > 0)
        {
            Console.WriteLine($"note      {(inputEnd.TrailingBytesExact ? string.Empty : "at least ")}{inputEnd.TrailingBytes:N0} trailing bytes behind the compressed data are not content");
        }

        // 5. Commit: the generation becomes the visible one.
        await store.CommitFileGenerationAsync(fileId, generation, ct).ConfigureAwait(false);
        total.Stop();

        var seconds = parse.Elapsed.TotalSeconds;
        Console.WriteLine($"rows      {result.Rows:N0} ({result.Rejects:N0} rejected) in {result.Chunks} chunks, {inserts} INSERTs; {wireBytes / 1e6:N0} MB on the wire for {payloadBytes / 1e6:N0} MB of blocks");
        Console.WriteLine($"ingest    {seconds:F2} s: {result.Bytes / 1e6 / seconds:N1} MB/s, {result.Rows / seconds:N0} rows/s (decoded bytes, file read to ClickHouse ack)");
        Console.WriteLine($"total     {total.Elapsed.TotalSeconds:F2} s including hash ({hashSeconds:F2} s) and commit; generation {generation} of file {fileId} is visible");
        if (!quiet)
        {
            Console.WriteLine($"query     urlog query --case {caseId} 'host={host.ToLowerInvariant()} | stats count by program'");
        }

        return 0;
    }

    /// <summary>
    /// This command keeps no record, so nothing ties its ids to a metadata file. A store with no identity
    /// belongs to nobody and takes any id. A store with one belongs to a metadata file: the caller has to name the
    /// identity (<c>--store-id</c>), and the case id and the file id have to lie in the ranges the metadata file
    /// never allocates.
    /// </summary>
    internal static void RequireStore(string server, string? storeIdentity, string? told, ulong caseId, ulong fileId)
    {
        const ulong floor = (ulong)Urlog.Core.Metadata.StoreIdentity.IngestFileIdFloor;
        const ulong caseFloor = (ulong)Urlog.Core.Metadata.StoreIdentity.IngestCaseIdFloor;
        var shown = Urlog.Core.Metadata.StoreIdentity.Show(storeIdentity);
        if (storeIdentity is null)
        {
            if (told is not null)
            {
                throw new CliException($"--store-id {told} was given and the event store {server} has store identity none: they do not match and nothing was written");
            }

            return;
        }

        if (told is null)
        {
            throw new CliException(string.Create(CultureInfo.InvariantCulture,
                $"the event store {server} has store identity {shown} and no --store-id was given (none): the store belongs to a metadata file, and 'urlog ingest' keeps no file record. Nothing was written. 'urlog enqueue' and 'urlog worker' are the recorded way in; to write into this store without a record, pass --store-id with its identity, a --case from {caseFloor} up and a --file-id from {floor} up"));
        }

        if (!string.Equals(told, storeIdentity, StringComparison.Ordinal))
        {
            throw new CliException($"--store-id is {told}, the event store {server} has store identity {shown}: this is not the store that was meant and nothing was written");
        }

        if (caseId < caseFloor)
        {
            throw new CliException(string.Create(CultureInfo.InvariantCulture,
                $"--case {caseId} is below {caseFloor}: in a store that has an identity the case ids under that floor are the metadata file's, and rows written here under one of them would show in a recorded case and go with it when it is purged. Nothing was written. Pass a --case from {caseFloor} up"));
        }

        if (fileId < floor)
        {
            throw new CliException(string.Create(CultureInfo.InvariantCulture,
                $"--file-id {fileId} is below {floor}: in a store that has an identity the ids under that floor are the metadata file's, and a row written here under one of them would be hidden or would hide a recorded file. Nothing was written. Pass a --file-id from {floor} up"));
        }
    }
}
