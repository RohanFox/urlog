using System.Text.RegularExpressions;

namespace Urlog.Api.Tests;

/// <summary>
/// What accessibility cannot stop: the API project could still write a case's state past the lifecycle operations
/// with a bulk update or delete on the cases set, or with SQL of its own. This reads the project's source and
/// fails when any of it does.
/// </summary>
public sealed partial class LifecycleSourceTests
{
    /// <summary>Each pattern with what it is there to catch.</summary>
    private static readonly (string What, Regex Pattern)[] Forbidden =
    [
        ("a bulk update or delete on the cases set", BulkOnCases()),
        ("the cases set reached through Set<CaseRecord>()", SetOfCases()),
        ("a tracked case removed, attached or marked as changed through the set", TrackedWrite()),
        ("the state of a case written through a property entry", PropertyEntry()),
        ("SQL that writes the cases table", RawSql()),
    ];

    [Fact]
    public void NoSourceOfTheApiProjectWritesCaseStatePastTheLifecycleOperations()
    {
        var project = Path.Combine(RepositoryRoot(), "src", "Api");
        var files = Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 20, $"only {files.Count} source files found under {project}: the check is not looking at the API project");

        var found = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var (what, pattern) in Forbidden)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                    found.Add($"{Path.GetRelativePath(project, file)}:{line}: {what}");
                }
            }
        }

        Assert.True(found.Count == 0, "the API project changes cases outside LifecycleOperations and CaseOperations:\n" + string.Join("\n", found));
    }

    [Theory]
    [InlineData("await db.Cases.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, CaseState.Closed), ct);")]
    [InlineData("db.Cases\n    .Where(c => c.Id == id)\n    .ExecuteDeleteAsync(ct);")]
    [InlineData("var n = db.Cases.ExecuteUpdate(s => s.SetProperty(c => c.Name, name));")]
    [InlineData("await db.Set<CaseRecord>().ExecuteDeleteAsync(ct);")]
    [InlineData("db.Cases.Remove(@case);")]
    [InlineData("db.Cases.RemoveRange(cases);")]
    [InlineData("db.Cases.Update(@case);")]
    [InlineData("db.Entry(@case).Property(\"State\").CurrentValue = CaseState.Purging;")]
    [InlineData("db.Entry(@case).Property(nameof(CaseRecord.State)).CurrentValue = state;")]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"UPDATE cases SET state = 'closed' WHERE id = 1\");")]
    [InlineData("await db.Database.ExecuteSqlAsync($\"delete from cases where id = {id}\");")]
    public void TheCheckCatchesEveryWayItKnows(string source)
    {
        Assert.Contains(Forbidden, f => f.Pattern.IsMatch(source));
    }

    [Theory]
    [InlineData("var @case = await db.Cases.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);")]
    [InlineData("var open = await db.Cases.Where(c => c.State == CaseState.Active).ToListAsync(ct); await db.Jobs.Where(j => j.CaseId == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Message, m), ct);")]
    [InlineData("await CaseOperations.CreateAsync(db, audit, actor, name, ownerId, now, ct);")]
    [InlineData("await LifecycleOperations.CloseAsync(db, audit, actor, caseId, now, ct);")]
    [InlineData("return TypedResults.Ok(new CaseResponse(@case.Id, @case.Name, Names.Of(@case.State)));")]
    [InlineData("// update cases through the lifecycle operations only")]
    public void TheCheckLeavesReadsAndTheOperationsAlone(string source)
    {
        Assert.DoesNotContain(Forbidden, f => f.Pattern.IsMatch(source));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Api")) && Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"no repository root above {AppContext.BaseDirectory}");
    }

    // From the set to the call inside one statement: no semicolon in between.
    [GeneratedRegex(@"\bCases\b[^;]*?\.\s*Execute(Update|Delete)(Async)?\s*\(", RegexOptions.Singleline)]
    private static partial Regex BulkOnCases();

    [GeneratedRegex(@"\bSet\s*<\s*CaseRecord\s*>")]
    private static partial Regex SetOfCases();

    [GeneratedRegex(@"\bCases\s*\.\s*(Remove|RemoveRange|Update|UpdateRange|Attach|AttachRange)\s*\(")]
    private static partial Regex TrackedWrite();

    [GeneratedRegex(@"\.\s*Property\s*\(\s*(""(State|PriorState|DeletedAt|PurgingAt)""|nameof\s*\(\s*CaseRecord\s*\.)")]
    private static partial Regex PropertyEntry();

    // In a string, not in a comment: the statement starts inside a quote.
    [GeneratedRegex(@"""[^""\n]*\b(update\s+""?cases\b|delete\s+from\s+""?cases\b|insert\s+into\s+""?cases\b)", RegexOptions.IgnoreCase)]
    private static partial Regex RawSql();
}
