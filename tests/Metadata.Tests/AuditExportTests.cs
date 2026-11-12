using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Urlog.Core.Audit;
using Urlog.Core.Metadata;

namespace Urlog.Metadata.Tests;

/// <summary>The per-case JSONL (consumed by the archive manifest): the right rows, one canonical line each, every line self-verifying.</summary>
public sealed class AuditExportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExportsTheCaseTrailAsSelfVerifyingLines()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var one = await TempDb.AddCaseAsync(db, alice, "incident 1");
        var two = await TempDb.AddCaseAsync(db, alice, "incident 2");
        var file = (await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(one.Id, "a\n"), Ct)).File;
        var other = (await FileOperations.AddAsync(db, AuditChain.Default, TempDb.Alice, TempDb.NewFile(two.Id, "b\n"), Ct)).File;

        // Create and intake wrote their own rows; the query row is the search endpoint's, appended here as it will be.
        await Append(db, temp, new AuditEntry("alice", alice, AuditActions.Query, AuditTargets.Query, one.Id, new Dictionary<string, object?> { ["case_name"] = one.Name, ["spl"] = "host=web-01", ["duration_ms"] = 12 }));
        await CaseOperations.TransitionAsync(db, AuditChain.Default, TempDb.Alice, two.Id, CaseState.Closed, DateTime.UtcNow, Ct);
        await CaseOperations.TransitionAsync(db, AuditChain.Default, TempDb.Alice, one.Id, CaseState.Closed, DateTime.UtcNow, Ct);
        Assert.NotEqual(file.Id, other.Id);

        using var stream = new MemoryStream();
        var count = await AuditExport.WriteCaseAsync(db, one.Id, stream, Ct);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, count);
        Assert.Equal(["case_create", "file_intake", "query", "case_close"], lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString()).ToArray());

        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.Equal(["action", "actor", "actor_id", "details_json", "hash", "prev_hash", "schema_version", "seq", "target_id", "target_type", "ts_utc"], root.EnumerateObject().Select(p => p.Name).ToArray());
            // The verification rule, from the line alone: drop hash, canonical JSON, prepend prev_hash, SHA-256.
            var without = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name == "hash")
                {
                    continue;
                }

                without[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.Number => p.Value.GetInt64(),
                    _ => p.Value.GetString(),
                };
            }

            var input = Encoding.ASCII.GetBytes(root.GetProperty("prev_hash").GetString()!).Concat(CanonicalJson.Serialize(without)).ToArray();
            Assert.Equal(root.GetProperty("hash").GetString(), Convert.ToHexStringLower(SHA256.HashData(input)));
            Assert.Equal(one.Name, JsonDocument.Parse(root.GetProperty("details_json").GetString()!).RootElement.GetProperty("case_name").GetString());
        }
    }

    [Fact]
    public async Task AReusedIdStartsANewTrail()
    {
        await using var temp = await TempDb.CreateAsync();
        await using var db = temp.Open();
        var alice = await TempDb.AddUserAsync(db);
        var @case = await TempDb.AddCaseAsync(db, alice, "old");
        await Append(db, temp, AuditEntry.ForCase("alice", alice, AuditActions.CasePurgeComplete, @case));
        // The same id again, as SQLite hands out after the newest row is deleted.
        var reused = new CaseRecord { Id = @case.Id, Name = "new", OwnerId = alice, CreatedAt = DateTime.UtcNow };
        await Append(db, temp, AuditEntry.ForCase("alice", alice, AuditActions.CaseCreate, reused));
        await Append(db, temp, AuditEntry.ForCase("alice", alice, AuditActions.CaseClose, reused));

        using var stream = new MemoryStream();
        Assert.Equal(2, await AuditExport.WriteCaseAsync(db, @case.Id, stream, Ct));
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("old", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static async Task Append(MetadataDb db, TempDb temp, AuditEntry entry)
    {
        await using var tx = await AuditChain.BeginTransactionAsync(db, Ct);
        await temp.Store.Audit.AppendAsync(db, entry, Ct);
        await tx.CommitAsync(Ct);
    }
}
