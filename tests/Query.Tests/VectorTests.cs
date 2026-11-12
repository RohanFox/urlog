using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urlog.Query;

namespace Urlog.Query.Tests;

/// <summary>
/// Runs every vector in <c>src/Query/tests/vectors/*.json</c>: SPL → expected SQL, parameters and warnings, or the
/// expected error with its position. The same files drive the Lezer grammar in <c>web/</c>. With
/// <c>URLOG_VECTORS_UPDATE=1</c> a vector without an <c>expect</c>/<c>error</c> block is filled in from the current
/// output (then reviewed and committed); a vector with one is always compared.
/// </summary>
public sealed class VectorTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n", // the vectors are LF files on every platform (.gitattributes), so a regeneration never churns line endings
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(VectorDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(f));
        }

        return data;
    }

    public static string VectorDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "urlog.sln")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("urlog.sln"), "src", "Query", "tests", "vectors");
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryVectorMatches(string file)
    {
        var path = Path.Combine(VectorDirectory, file);
        var vectors = JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(path), Json) ?? [];
        Assert.NotEmpty(vectors);
        var update = Environment.GetEnvironmentVariable("URLOG_VECTORS_UPDATE") == "1";
        var changed = false;
        var failures = new List<string>();
        foreach (var v in vectors)
        {
            var actual = Run(v);
            if (v.Expect is null && v.Error is null)
            {
                if (!update)
                {
                    failures.Add($"{v.Name}: no expectation recorded (run with URLOG_VECTORS_UPDATE=1 to fill it in)");
                    continue;
                }

                v.Expect = actual.Expect;
                v.Error = actual.Error;
                changed = true;
                continue;
            }

            var expectedJson = JsonSerializer.Serialize(new { v.Expect, v.Error }, Json);
            var actualJson = JsonSerializer.Serialize(new { actual.Expect, actual.Error }, Json);
            if (expectedJson != actualJson)
            {
                failures.Add($"{v.Name} ({v.Spl})\n--- expected\n{expectedJson}\n--- actual\n{actualJson}");
            }
        }

        if (changed)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(vectors, Json) + "\n");
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    [Fact]
    public void AtLeastFortyVectorsIncludingEveryErrorPath()
    {
        var all = Directory.GetFiles(VectorDirectory, "*.json").SelectMany(f => JsonSerializer.Deserialize<List<Vector>>(File.ReadAllText(f), Json) ?? []).ToList();
        Assert.True(all.Count >= 40, $"{all.Count} vectors");
        Assert.True(all.Count(v => v.Error is not null) >= 15, "error vectors");
        Assert.Contains(all, v => v.Error?.Message.StartsWith("not supported in v1:", StringComparison.Ordinal) == true);
    }

    private static (Expectation? Expect, VectorError? Error) Run(Vector v)
    {
        var o = v.Options ?? new VectorOptions();
        var options = new TranspileOptions(
            o.Case,
            DateTimeOffset.Parse(o.From, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(o.To, System.Globalization.CultureInfo.InvariantCulture),
            TimeZones.Find(o.Tz),
            DateTimeOffset.Parse(o.Now, System.Globalization.CultureInfo.InvariantCulture))
        {
            AllowedCases = o.Allowed.ToHashSet(),
        };
        try
        {
            var result = Transpiler.Transpile(v.Spl, options);
            return (new Expectation
            {
                Sql = result.Sql,
                Params = result.Parameters.ToDictionary(p => p.Name, p => $"{p.Type}:{p.Value}"),
                Warnings = [.. result.Warnings.Select(w => $"{w.Code}@{w.Span.Offset}+{w.Span.Length}")],
                Fields = [.. result.Fields.Select(f => f.Name)],
                Aggregate = result.IsAggregate,
            }, null);
        }
        catch (QueryException e)
        {
            return (null, new VectorError { Offset = e.Offset, Length = e.Length, Message = e.Message });
        }
    }

    public sealed class Vector
    {
        public string Name { get; set; } = "";

        public string Spl { get; set; } = "";

        public VectorOptions? Options { get; set; }

        public Expectation? Expect { get; set; }

        public VectorError? Error { get; set; }
    }

    public sealed class VectorOptions
    {
        public ulong Case { get; set; } = 1;

        public string From { get; set; } = "2026-03-01T00:00:00Z";

        public string To { get; set; } = "2026-04-01T00:00:00Z";

        public string Tz { get; set; } = "UTC";

        public string Now { get; set; } = "2026-03-15T12:00:00Z";

        public ulong[] Allowed { get; set; } = [1, 7];
    }

    public sealed class Expectation
    {
        public string Sql { get; set; } = "";

        public Dictionary<string, string> Params { get; set; } = [];

        public string[] Warnings { get; set; } = [];

        public string[] Fields { get; set; } = [];

        public bool Aggregate { get; set; }
    }

    public sealed class VectorError
    {
        public int Offset { get; set; }

        public int Length { get; set; }

        public string Message { get; set; } = "";
    }
}
