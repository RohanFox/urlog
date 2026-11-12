using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Urlog.Core.Audit;

/// <summary>
/// The result of a chain walk. <see cref="HeadSeq"/> and <see cref="HeadHash"/> are the last good link, which is what an
/// analyst writes down: a truncated tail is invisible to the chain alone, so the head recorded externally (a notebook, a
/// ticket) or a backup (<see cref="AuditVerifier.VerifyExtendsAsync"/>) is the only thing that catches it.
/// </summary>
public sealed record AuditVerification(bool Ok, long Rows, long HeadSeq, string? HeadHash, long? FirstBrokenSeq, string? Reason, string? Detail, TimeSpan Elapsed)
{
    /// <summary>One line for the notebook: <c>audit head seq 10000 hash 3f9c...</c> (the full 64 characters).</summary>
    public string HeadLine => HeadHash is null ? "audit head: none (no rows)" : $"audit head seq {HeadSeq.ToString(CultureInfo.InvariantCulture)} hash {HeadHash}";

    /// <summary>What <c>urlog verify-audit</c> prints: the verdict, then the head line (the last good link when broken).</summary>
    public string ToText()
    {
        var inv = CultureInfo.InvariantCulture;
        if (Ok)
        {
            return string.Create(inv, $"audit chain OK: {Rows:N0} rows, seq 1..{HeadSeq}, {Elapsed.TotalSeconds:F2} s") + Environment.NewLine + HeadLine;
        }

        var first = string.Create(inv, $"audit chain BROKEN at seq {FirstBrokenSeq}: {Reason}, {Detail}");
        var last = HeadHash is null ? "chain verifies through: nothing" : string.Create(inv, $"chain verifies through: seq {HeadSeq} hash {HeadHash}");
        return first + Environment.NewLine + last;
    }

    /// <summary><c>--json</c>: one line, canonical form.</summary>
    public string ToJson() => CanonicalJson.SerializeToString(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["ok"] = Ok,
        ["rows"] = Rows,
        ["head_seq"] = HeadHash is null ? null : HeadSeq,
        ["head_hash"] = HeadHash,
        ["first_broken_seq"] = FirstBrokenSeq,
        ["reason"] = Reason,
        ["detail"] = Detail,
        ["elapsed_ms"] = (long)Elapsed.TotalMilliseconds,
    });
}

/// <summary>
/// <c>urlog verify-audit</c> as a component: walks the <c>audit</c> table in <c>seq</c> order through a plain
/// read-only SQLite connection (no EF, nothing written, so a third party can reimplement it from this description),
/// recomputing every link with <see cref="AuditHash"/>. Keeps <c>expected_seq = 1</c> and <c>expected_prev</c> = the zero
/// hash; for each row: <c>seq != expected_seq</c> -> <see cref="Gap"/>; row 1 must be <c>genesis</c> with a known
/// <c>schema_version</c> and every later row's must be null -> <see cref="Genesis"/>; a <c>hash</c> that is not 64 lowercase
/// hex, or a timestamp below microsecond precision -> <see cref="Malformed"/>; <c>prev_hash != expected_prev</c> ->
/// <see cref="PrevHash"/>; the recomputed hash != the stored one -> <see cref="Hash"/>. The first failure stops the walk and
/// names its <c>seq</c>: a deleted row 5000 is reported at 5001 (<see cref="Gap"/>), a changed byte in row 7 at 7
/// (<see cref="Hash"/>). An empty table is broken at seq 1 (<see cref="NoGenesis"/>).
/// </summary>
public static class AuditVerifier
{
    public const string Gap = "gap";
    public const string Genesis = "genesis";
    public const string NoGenesis = "no_genesis";
    public const string PrevHash = "prev_hash";
    public const string Hash = "hash";
    public const string Malformed = "malformed";

    /// <summary>How EF Core's SQLite provider writes a <see cref="DateTime"/>: a space, no zone, up to seven fractional digits with trailing zeros trimmed.</summary>
    public static readonly string[] StoredTimestampFormats = ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss"];

    private const string Select = "SELECT seq, ts_utc, actor, actor_id, action, target_type, target_id, details_json, prev_hash, hash, schema_version FROM audit ORDER BY seq";

    /// <summary>Opens <paramref name="path"/> read-only and walks the chain.</summary>
    public static async Task<AuditVerification> VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = OpenReadOnly(path);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await VerifyAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Walks the chain over an open connection (read-only or not; nothing is written).</summary>
    public static async Task<AuditVerification> VerifyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var clock = Stopwatch.StartNew();
        var expectedSeq = 1L;
        var expectedPrev = AuditHash.ZeroHash;
        var rows = 0L;
        string? headHash = null;
        var scratch = new ArrayBufferWriter<byte>(1024);

        await using var command = connection.CreateCommand();
        command.CommandText = Select;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var seq = reader.GetInt64(0);
            if (seq != expectedSeq)
            {
                return Broken(seq, Gap, seq < expectedSeq ? $"seq {seq} is out of order or repeated" : $"row {expectedSeq} is missing (the next row is {seq})");
            }

            var hash = reader.GetString(9);
            var prevHash = reader.GetString(8);
            if (!AuditHash.IsHex64(hash) || !AuditHash.IsHex64(prevHash))
            {
                return Broken(seq, Malformed, "hash or prev_hash is not 64 lowercase hex characters");
            }

            // EF stores the column as this exact text; anything else is not a row the writer produced.
            var tsText = reader.GetString(1);
            if (!DateTime.TryParseExact(tsText, StoredTimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
            {
                return Broken(seq, Malformed, $"ts_utc '{tsText}' is not a timestamp of the stored form");
            }

            if (ts.Ticks % 10 != 0)
            {
                return Broken(seq, Malformed, "ts_utc has sub-microsecond digits the writer never produces");
            }

            var action = reader.GetString(4);
            var schemaVersion = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10);
            if (seq == 1)
            {
                if (action != AuditActions.Genesis || schemaVersion != AuditHash.FormatVersion)
                {
                    return Broken(seq, Genesis, schemaVersion is null or AuditHash.FormatVersion
                        ? $"row 1 is '{action}', not the genesis row"
                        : $"genesis schema_version {schemaVersion} is unknown to this verifier (knows {AuditHash.FormatVersion})");
                }
            }
            else if (schemaVersion is not null || action == AuditActions.Genesis)
            {
                return Broken(seq, Genesis, "a second genesis row (schema_version is set, or the action is genesis)");
            }

            if (prevHash != expectedPrev)
            {
                return Broken(seq, PrevHash, $"prev_hash does not match the hash of seq {seq - 1}");
            }

            var matches = AuditHash.Matches(
                hash,
                seq,
                ts,
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                action,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7),
                prevHash,
                schemaVersion,
                scratch);
            if (!matches)
            {
                return Broken(seq, Hash, "the row's content does not hash to its stored hash");
            }

            rows++;
            headHash = hash;
            expectedPrev = hash;
            expectedSeq++;
        }

        if (rows == 0)
        {
            return Broken(1, NoGenesis, "the audit table is empty");
        }

        return new AuditVerification(true, rows, rows, headHash, null, null, null, clock.Elapsed);

        AuditVerification Broken(long at, string reason, string detail) =>
            new(false, rows, rows, headHash, at, reason, detail, clock.Elapsed);
    }

    /// <summary>
    /// Verifies <paramref name="backupPath"/> and then checks that the live chain at <paramref name="livePath"/> extends it:
    /// the live row at the backup's head <c>seq</c> must carry the backup's head <c>hash</c>. Catches a live chain that was
    /// truncated below the backup's head, or rewritten from some point before it.
    /// </summary>
    public static async Task<AuditVerification> VerifyExtendsAsync(string livePath, string backupPath, CancellationToken cancellationToken = default)
    {
        var backup = await VerifyAsync(backupPath, cancellationToken).ConfigureAwait(false);
        if (!backup.Ok)
        {
            return backup with { Detail = "in the backup: " + backup.Detail };
        }

        var check = await CheckHeadAsync(livePath, backup.HeadSeq, backup.HeadHash!, "the backup's head", cancellationToken).ConfigureAwait(false);
        return check is null ? backup : backup with { Ok = false, FirstBrokenSeq = backup.HeadSeq, Reason = check.Value.Reason, Detail = check.Value.Detail };
    }

    /// <summary>
    /// Checks a head recorded earlier (the notebook line, or a backup's head) against the live chain: the live row at
    /// <paramref name="seq"/> must carry <paramref name="hash"/>. Null when it does; otherwise <see cref="Gap"/> (no such
    /// row: the chain was truncated below the recorded head) or <see cref="Hash"/> (a different row: rewritten before it).
    /// The chain itself is verified separately; this only compares one row.
    /// </summary>
    public static async Task<(string Reason, string Detail)?> CheckHeadAsync(string livePath, long seq, string hash, string what, CancellationToken cancellationToken = default)
    {
        await using var live = OpenReadOnly(livePath);
        await live.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = live.CreateCommand();
        command.CommandText = "SELECT hash FROM audit WHERE seq = $seq";
        command.Parameters.AddWithValue("$seq", seq);
        var liveHash = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (liveHash is null)
        {
            return (Gap, $"the live chain has no row {seq}: it was truncated below {what}");
        }

        if (!string.Equals(liveHash, hash, StringComparison.Ordinal))
        {
            return (Hash, $"the live row {seq} differs from {what}: the chain was rewritten before it");
        }

        return null;
    }

    /// <summary>A read-only connection: nothing this class does writes, and a backup file is never touched.</summary>
    public static SqliteConnection OpenReadOnly(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString());
}
