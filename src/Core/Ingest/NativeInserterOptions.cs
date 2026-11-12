namespace Urlog.Core.Ingest;

/// <summary>
/// Where and how the <see cref="NativeInserter"/> writes. Defaults are the insert measurement's choice for the 8-core
/// reference box (design record, "Throughput SLO and benchmark harness"): 4 writers, up to 500k rows per INSERT, LZ4 on the wire. The
/// reference box may pick its own point on the curve recorded there; the SLO gate runs with these defaults.
/// </summary>
public sealed record NativeInserterOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 9000;

    public string Database { get; init; } = "default";

    public string User { get; init; } = "default";

    public string Password { get; init; } = "";

    /// <summary>Target table. Only <c>Core/Ingest</c> writes <c>events</c>; benches point this at their own table.</summary>
    public string Table { get; init; } = "events";

    /// <summary>Writer threads, one connection each. Measured: 4 give 0.99-1.13 M rows/s on the dev box, 8 give 1.13-1.21 M.</summary>
    public int Writers { get; init; } = 4;

    /// <summary>
    /// A writer streams batches into one open INSERT until this many rows are in it, then ends it; the server squashes
    /// the packets of one INSERT into one part (<c>min_insert_block_size_rows</c>), so 100k batches still make 500k parts.
    /// </summary>
    public int MaxRowsPerInsert { get; init; } = 500_000;

    /// <summary>An open INSERT with no new batch for this long is ended, so the tail of a file is not held back.</summary>
    public TimeSpan InsertIdleTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>LZ4 on the wire. Measured as the difference between 0.3 and 1.2 M rows/s on the dev box; off only for diagnostics.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>
    /// Sort a block by <c>(case_id, host, ts)</c> on the client when it is not already sorted (a gather at
    /// serialisation, the batch is untouched). Default decided by the insert measurement on syslog-shaped blocks:
    /// kept only if it beats the server's own sort by more than 10 % end to end.
    /// </summary>
    public bool SortBlocks { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Socket read and write timeout. An INSERT of <see cref="MaxRowsPerInsert"/> rows is acknowledged in a few seconds; a stalled server shows up as a typed error, not a hang.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        if (Writers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Writers), Writers, "must be positive");
        }

        if (MaxRowsPerInsert < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRowsPerInsert), MaxRowsPerInsert, "must be positive");
        }

        if (InsertIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(InsertIdleTimeout), InsertIdleTimeout, "must be positive");
        }

        if (string.IsNullOrEmpty(Table) || Table.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.')))
        {
            throw new ArgumentException("table must be an identifier", nameof(Table));
        }
    }
}
