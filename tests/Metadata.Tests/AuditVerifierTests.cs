using System.Diagnostics;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;

namespace Urlog.Metadata.Tests;

/// <summary>Tamper detection and verify-audit over 1 M rows in ≤ 10 s: the walk, every reason, the head.</summary>
public sealed class AuditVerifierTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFreshStoreVerifies()
    {
        await using var temp = await TempDb.CreateAsync();
        var result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal((1L, 1L), (result.Rows, result.HeadSeq));
        Assert.True(AuditHash.IsHex64(result.HeadHash!));
        Assert.Equal($"audit head seq 1 hash {result.HeadHash}", result.HeadLine);
        Assert.Null(result.FirstBrokenSeq);
    }

    [Fact]
    public async Task ADeletedRowIsReportedAtTheNextSeq()
    {
        await using var temp = await TempDb.CreateAsync();
        await AuditRows.AppendAsync(temp.Path, 6_000);
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);

        await AuditRows.TamperAsync(temp.Path, "DELETE FROM audit WHERE seq = 5000");
        var result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.False(result.Ok);
        Assert.Equal((5001L, AuditVerifier.Gap), (result.FirstBrokenSeq, result.Reason));
        Assert.Equal(4999, result.HeadSeq);
        Assert.Contains("row 5000 is missing", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedByteIsReportedAtItsRow()
    {
        await using var temp = await TempDb.CreateAsync();
        await AuditRows.AppendAsync(temp.Path, 20);

        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET details_json = replace(details_json, 'incident 1', 'incident 2') WHERE seq = 7");
        var result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.Equal((false, 7L, AuditVerifier.Hash, 6L), (result.Ok, result.FirstBrokenSeq, result.Reason, result.HeadSeq));

        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET details_json = replace(details_json, 'incident 2', 'incident 1'), actor = 'mallory' WHERE seq = 7");
        result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.Equal((7L, AuditVerifier.Hash), (result.FirstBrokenSeq, result.Reason));

        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET actor = 'writer-1', ts_utc = '2026-11-06 12:00:00.0000061' WHERE seq = 7");
        result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.Equal((7L, AuditVerifier.Malformed), (result.FirstBrokenSeq, result.Reason));

        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET ts_utc = '2026-11-06 12:00:00.0000060' WHERE seq = 7");
        Assert.True((await AuditVerifier.VerifyAsync(temp.Path, Ct)).Ok);
    }

    [Fact]
    public async Task ABrokenLinkIsReportedAsPrevHash()
    {
        await using var temp = await TempDb.CreateAsync();
        await AuditRows.AppendAsync(temp.Path, 10);
        await AuditRows.TamperAsync(temp.Path, $"UPDATE audit SET prev_hash = '{AuditHash.ZeroHash}' WHERE seq = 9");
        var result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.Equal((9L, AuditVerifier.PrevHash), (result.FirstBrokenSeq, result.Reason));
    }

    [Fact]
    public async Task MalformedAndGenesisFaultsAreNamed()
    {
        await using var temp = await TempDb.CreateAsync();
        await AuditRows.AppendAsync(temp.Path, 3);

        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET hash = upper(hash) WHERE seq = 3");
        var result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.Equal((3L, AuditVerifier.Malformed), (result.FirstBrokenSeq, result.Reason));
        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET hash = lower(hash) WHERE seq = 3");

        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET schema_version = 1 WHERE seq = 2");
        result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.Equal((2L, AuditVerifier.Genesis), (result.FirstBrokenSeq, result.Reason));
        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET schema_version = NULL WHERE seq = 2");

        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET schema_version = 99 WHERE seq = 1");
        result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.Equal((1L, AuditVerifier.Genesis), (result.FirstBrokenSeq, result.Reason));
        Assert.Contains("99", result.Detail, StringComparison.Ordinal);
        await AuditRows.TamperAsync(temp.Path, "UPDATE audit SET schema_version = 1 WHERE seq = 1");

        // The genesis row gone: the first row found is 2, whose link to row 1 is what is broken.
        await AuditRows.TamperAsync(temp.Path, "DELETE FROM audit WHERE seq = 1");
        result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.Equal((2L, AuditVerifier.Gap, 0L), (result.FirstBrokenSeq, result.Reason, result.HeadSeq));

        await AuditRows.TamperAsync(temp.Path, "DELETE FROM audit");
        result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.Equal((1L, AuditVerifier.NoGenesis, 0L), (result.FirstBrokenSeq, result.Reason, result.Rows));
    }

    [Fact]
    public async Task ATruncatedTailVerifiesWithAShorterHead()
    {
        // The limit of the chain alone: the head must be recorded elsewhere (the notebook line, or a backup) to see this.
        await using var temp = await TempDb.CreateAsync();
        await AuditRows.AppendAsync(temp.Path, 10);
        var before = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        await AuditRows.TamperAsync(temp.Path, "DELETE FROM audit WHERE seq > 8");
        var after = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(after.Ok);
        Assert.Equal((11L, 8L), (before.HeadSeq, after.HeadSeq));
        Assert.NotEqual(before.HeadHash, after.HeadHash);
    }

    [Fact]
    public async Task VerifyIsReadOnly()
    {
        await using var temp = await TempDb.CreateAsync();
        await AuditRows.AppendAsync(temp.Path, 5);
        var first = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        var second = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(first.Ok);
        Assert.Equal((first.Rows, first.HeadSeq, first.HeadHash), (second.Rows, second.HeadSeq, second.HeadHash));
        // The connection the verifier opens refuses a write outright (a byte comparison of the file is not a test of
        // this: another connection closing can checkpoint the WAL into the main file at any time).
        await using var connection = AuditVerifier.OpenReadOnly(temp.Path);
        await connection.OpenAsync(Ct);
        Assert.Contains("Mode=ReadOnly", connection.ConnectionString, StringComparison.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER audit_no_delete";
        var e = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQueryAsync(Ct));
        Assert.Equal(8, e.SqliteErrorCode); // SQLITE_READONLY
        command.CommandText = "INSERT INTO audit (seq) VALUES (99)";
        Assert.Equal(8, (await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQueryAsync(Ct))).SqliteErrorCode);
    }

    [Fact]
    public async Task TheReportNamesTheHeadAndTheFirstBrokenLink()
    {
        await using var temp = await TempDb.CreateAsync();
        await AuditRows.AppendAsync(temp.Path, 9);
        var ok = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        var text = ok.ToText().ReplaceLineEndings("\n").Split('\n');
        Assert.StartsWith("audit chain OK: 10 rows, seq 1..10, ", text[0], StringComparison.Ordinal);
        Assert.Equal($"audit head seq 10 hash {ok.HeadHash}", text[1]);
        Assert.Matches("^audit head seq [0-9]+ hash [0-9a-f]{64}$", ok.HeadLine);
        Assert.StartsWith($$"""{"detail":null,"elapsed_ms":""", ok.ToJson(), StringComparison.Ordinal);
        Assert.EndsWith($$""","first_broken_seq":null,"head_hash":"{{ok.HeadHash}}","head_seq":10,"ok":true,"reason":null,"rows":10}""", ok.ToJson(), StringComparison.Ordinal);

        // A head written down earlier is checked against the live chain.
        Assert.Null(await AuditVerifier.CheckHeadAsync(temp.Path, ok.HeadSeq, ok.HeadHash!, "the recorded head", Ct));
        await AuditRows.TamperAsync(temp.Path, "DELETE FROM audit WHERE seq > 6");
        var shorter = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(shorter.Ok);
        var check = await AuditVerifier.CheckHeadAsync(temp.Path, ok.HeadSeq, ok.HeadHash!, "the recorded head", Ct);
        Assert.Equal(AuditVerifier.Gap, check?.Reason);
        Assert.Contains("truncated below the recorded head", check?.Detail, StringComparison.Ordinal);
        Assert.Equal(AuditVerifier.Hash, (await AuditVerifier.CheckHeadAsync(temp.Path, 3, ok.HeadHash!, "the recorded head", Ct))?.Reason);

        await AuditRows.TamperAsync(temp.Path, "DELETE FROM audit WHERE seq = 4");
        var broken = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        text = broken.ToText().ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("audit chain BROKEN at seq 5: gap, row 4 is missing (the next row is 5)", text[0]);
        Assert.Equal($"chain verifies through: seq 3 hash {broken.HeadHash}", text[1]);
        Assert.Contains(""","first_broken_seq":5,""", broken.ToJson(), StringComparison.Ordinal);
        Assert.EndsWith(""","ok":false,"reason":"gap","rows":3}""", broken.ToJson(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The budget for that walk. A duration measures the machine as much as the code, so the ceiling is explicit
    /// and runs alone from tools/timing-tests.ps1; the default run walks the same million rows and asserts the result.
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Timing")]
    public async Task OneMillionRowsVerifyInUnderTenSeconds()
    {
        await using var temp = await TempDb.CreateAsync();
        await AuditRows.AppendAsync(temp.Path, 1_000_000, withTriggers: false);
        var result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(result.Ok, result.Detail);
        output.WriteLine($"verify-audit over {result.Rows:N0} rows: {result.Elapsed.TotalSeconds:F2} s");
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(10), $"verify took {result.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task OneMillionRowsVerify()
    {
        await using var temp = await TempDb.CreateAsync();
        var fill = Stopwatch.StartNew();
        await AuditRows.AppendAsync(temp.Path, 1_000_000, withTriggers: false);
        fill.Stop();
        var result = await AuditVerifier.VerifyAsync(temp.Path, Ct);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(1_000_001, result.Rows);
        await Timing.RecordAsync(output, $"verify-audit over {result.Rows:N0} rows: {result.Elapsed.TotalSeconds:F2} s ({result.Rows / result.Elapsed.TotalSeconds:N0} rows/s); filling them through one prepared INSERT with the triggers off: {fill.Elapsed.TotalSeconds:F2} s");
    }
}
