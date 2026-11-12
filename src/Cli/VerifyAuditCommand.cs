using System.Globalization;
using Urlog.Core.Audit;

namespace Urlog.Cli;

/// <summary>
/// <c>urlog verify-audit --db &lt;metadata.db&gt; [--head] [--expect &lt;seq&gt;:&lt;hash&gt;] [--backup &lt;file&gt;] [--json]</c>.
/// Walks the hash chain read-only (<see cref="AuditVerifier"/>) and reports the first broken link, then prints the head:
/// <c>audit head seq N hash H</c>. The chain proves that nothing before the head changed; it cannot show that rows were
/// cut off the end. That is caught only by a head recorded outside the machine: copy the head line into the case
/// notebook or the ticket, and pass it back later as <c>--expect N:H</c>; or pass <c>--backup</c> with a backup file,
/// whose head is checked the same way. <c>--head</c> prints only the head line (after verifying).
/// Exit 0 OK, 1 broken (or the recorded head does not match), 2 usage or the file cannot be opened.
/// </summary>
internal static class VerifyAuditCommand
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var path = MetadataPath(args);
        var result = await AuditVerifier.VerifyAsync(path, ct).ConfigureAwait(false);

        if (result.Ok && args.Option("expect") is { } expect)
        {
            var (seq, hash) = ParseHead(expect);
            if (await AuditVerifier.CheckHeadAsync(path, seq, hash, "the recorded head", ct).ConfigureAwait(false) is { } mismatch)
            {
                result = result with { Ok = false, FirstBrokenSeq = seq, Reason = mismatch.Reason, Detail = mismatch.Detail };
            }
        }

        if (result.Ok && args.Option("backup") is { } backup)
        {
            var backupPath = Path.GetFullPath(backup);
            if (!File.Exists(backupPath))
            {
                throw new CliException($"no such backup: {backupPath}");
            }

            var extends = await AuditVerifier.VerifyExtendsAsync(path, backupPath, ct).ConfigureAwait(false);
            if (!extends.Ok)
            {
                // The live head stays in the report; the failure is the backup's.
                result = result with { Ok = false, FirstBrokenSeq = extends.FirstBrokenSeq, Reason = extends.Reason, Detail = extends.Detail };
            }
            else if (!args.Flag("json") && !args.Flag("head"))
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"backup OK: {extends.Rows:N0} rows, the live chain extends its head (seq {extends.HeadSeq})"));
            }
        }

        if (args.Flag("json"))
        {
            Console.WriteLine(result.ToJson());
        }
        else if (args.Flag("head") && result.Ok)
        {
            Console.WriteLine(result.HeadLine);
        }
        else
        {
            Console.WriteLine(result.ToText());
        }

        return result.Ok ? 0 : 1;
    }

    /// <summary><c>--db</c>, else <c>URLOG_DB</c>; the file must exist (nothing here creates one).</summary>
    public static string MetadataPath(CliArgs args, bool mustExist = true)
    {
        var value = args.Option("db") ?? Environment.GetEnvironmentVariable("URLOG_DB") ?? throw new CliException("--db <metadata file> is required (or URLOG_DB)");
        var path = Path.GetFullPath(value);
        return !mustExist || File.Exists(path) ? path : throw new CliException($"no such metadata file: {path}");
    }

    /// <summary><c>10000:3f9c...</c>, or the head line itself pasted back (<c>audit head seq 10000 hash 3f9c...</c>).</summary>
    private static (long Seq, string Hash) ParseHead(string text)
    {
        var parts = text.Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries);
        string? seqText = null, hash = null;
        if (parts.Length == 2)
        {
            (seqText, hash) = (parts[0], parts[1]);
        }
        else if (parts is ["audit", "head", "seq", var s, "hash", var h])
        {
            (seqText, hash) = (s, h);
        }

        if (seqText is null || hash is null || !long.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) || seq < 1 || !AuditHash.IsHex64(hash))
        {
            throw new CliException("--expect takes <seq>:<hash> (64 lowercase hex), or the head line as verify-audit printed it");
        }

        return (seq, hash);
    }
}
