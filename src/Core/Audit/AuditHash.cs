using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Urlog.Core.Metadata;

namespace Urlog.Core.Audit;

/// <summary>
/// The link formula of the audit chain (design record, "Audit log"):
/// <c>hash = SHA-256( prev_hash || canonical_json(row without hash) )</c>, both parts UTF-8. <c>prev_hash</c> is the
/// previous row's 64-character lowercase hex as ASCII (<see cref="ZeroHash"/> before the genesis row); the row object has
/// exactly the ten keys <c>action, actor, actor_id, details_json, prev_hash, schema_version, seq, target_id, target_type,
/// ts_utc</c>, every one present (null as <c>null</c>), with <c>details_json</c> embedded as the stored text, a string, never
/// re-parsed: any byte of it that changes breaks the link, and a verifier needs no JSON parser. Hex is lowercase everywhere.
/// The ten keys are written in that (sorted) order directly, which is what <see cref="CanonicalJson"/> would produce for
/// the same dictionary; the known-answer tests pin the bytes against an independent implementation.
/// </summary>
public static class AuditHash
{
    /// <summary>The <c>prev_hash</c> of the genesis row.</summary>
    public const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// The audit format carried by the genesis row's <c>schema_version</c>: the canonical rules of <see cref="CanonicalJson"/>
    /// and this formula. A change gets a new number and a new verifier branch; rows already written are never rehashed.
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>The canonical JSON of the row without its hash: what is hashed after <paramref name="prevHash"/>.</summary>
    public static byte[] CanonicalRow(long seq, DateTime tsUtc, string actor, long? actorId, string action, string? targetType, long? targetId, string detailsJson, string prevHash, int? schemaVersion)
    {
        var output = new ArrayBufferWriter<byte>(512);
        WriteRow(output, seq, tsUtc, actor, actorId, action, targetType, targetId, detailsJson, prevHash, schemaVersion);
        return output.WrittenSpan.ToArray();
    }

    public static string Compute(long seq, DateTime tsUtc, string actor, long? actorId, string action, string? targetType, long? targetId, string detailsJson, string prevHash, int? schemaVersion)
    {
        Span<byte> hash = stackalloc byte[32];
        Digest(hash, seq, tsUtc, actor, actorId, action, targetType, targetId, detailsJson, prevHash, schemaVersion, new ArrayBufferWriter<byte>(512));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>The hash the row should carry, from its stored fields.</summary>
    public static string Compute(AuditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Compute(row.Seq, row.TsUtc, row.Actor, row.ActorId, row.Action, row.TargetType, row.TargetId, row.DetailsJson, row.PrevHash, row.SchemaVersion);
    }

    /// <summary>
    /// True when the row's fields hash to <paramref name="hash"/>; the verifier's inner loop, allocation-free given a
    /// <paramref name="scratch"/> buffer it reuses.
    /// </summary>
    public static bool Matches(string hash, long seq, DateTime tsUtc, string actor, long? actorId, string action, string? targetType, long? targetId, string detailsJson, string prevHash, int? schemaVersion, ArrayBufferWriter<byte> scratch)
    {
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(scratch);
        Span<byte> digest = stackalloc byte[32];
        Digest(digest, seq, tsUtc, actor, actorId, action, targetType, targetId, detailsJson, prevHash, schemaVersion, scratch);
        Span<char> hex = stackalloc char[64];
        return Convert.TryToHexStringLower(digest, hex, out var written) && written == 64 && hex.SequenceEqual(hash);
    }

    /// <summary>64 characters of <c>0-9a-f</c>: the only form a hash column may hold.</summary>
    public static bool IsHex64(ReadOnlySpan<char> text)
    {
        if (text.Length != 64)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static void Digest(Span<byte> digest, long seq, DateTime tsUtc, string actor, long? actorId, string action, string? targetType, long? targetId, string detailsJson, string prevHash, int? schemaVersion, ArrayBufferWriter<byte> scratch)
    {
        ArgumentNullException.ThrowIfNull(prevHash);
        if (!IsHex64(prevHash))
        {
            throw new ArgumentException("prev_hash must be 64 lowercase hex characters", nameof(prevHash));
        }

        scratch.ResetWrittenCount();
        var prefix = scratch.GetSpan(64);
        Encoding.ASCII.GetBytes(prevHash, prefix);
        scratch.Advance(64);
        WriteRow(scratch, seq, tsUtc, actor, actorId, action, targetType, targetId, detailsJson, prevHash, schemaVersion);
        SHA256.HashData(scratch.WrittenSpan, digest);
    }

    private static void WriteRow(IBufferWriter<byte> output, long seq, DateTime tsUtc, string actor, long? actorId, string action, string? targetType, long? targetId, string detailsJson, string prevHash, int? schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(detailsJson);
        output.Write("{\"action\":"u8);
        CanonicalJson.Write(action, output);
        output.Write(",\"actor\":"u8);
        CanonicalJson.Write(actor, output);
        output.Write(",\"actor_id\":"u8);
        CanonicalJson.Write(actorId, output);
        output.Write(",\"details_json\":"u8);
        CanonicalJson.Write(detailsJson, output);
        output.Write(",\"prev_hash\":"u8);
        CanonicalJson.Write(prevHash, output);
        output.Write(",\"schema_version\":"u8);
        CanonicalJson.Write(schemaVersion.HasValue ? (long?)schemaVersion.Value : null, output);
        output.Write(",\"seq\":"u8);
        CanonicalJson.Write(seq, output);
        output.Write(",\"target_id\":"u8);
        CanonicalJson.Write(targetId, output);
        output.Write(",\"target_type\":"u8);
        CanonicalJson.Write(targetType, output);
        output.Write(",\"ts_utc\":"u8);
        CanonicalJson.Write(tsUtc, output);
        output.Write("}"u8);
    }
}
