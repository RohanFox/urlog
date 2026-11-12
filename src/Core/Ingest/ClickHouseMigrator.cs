using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClickHouse.Driver.Tcp;

namespace Urlog.Core.Ingest;

/// <summary>One numbered migration file: <c>NNNN_name.sql</c>, statements separated by <c>;</c> at a line end.</summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    /// <summary>SHA-256 of the file with line endings normalised, computed from <see cref="Sql"/> on every call so a <c>with</c> copy cannot carry a stale hash.</summary>
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));

    /// <summary>The statements of the file, comments kept (ClickHouse accepts them), empty statements dropped.</summary>
    public IReadOnlyList<string> Statements()
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        foreach (var rawLine in Sql.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            current.Append(rawLine).Append('\n');
            var code = StripComment(line);
            if (code.EndsWith(';'))
            {
                Emit();
            }
        }

        Emit();
        return statements;

        void Emit()
        {
            var text = current.ToString().Trim();
            current.Clear();
            if (text.Length == 0)
            {
                return;
            }

            // A trailing block of comments only (after the last ';') is not a statement.
            var meaningful = text.Split('\n').Any(l => StripComment(l).Length > 0);
            if (meaningful)
            {
                statements.Add(text.TrimEnd(';').TrimEnd());
            }
        }

        static string StripComment(string line)
        {
            var i = line.IndexOf("--", StringComparison.Ordinal);
            return (i >= 0 ? line[..i] : line).Trim();
        }
    }
}

/// <summary>Where migrations come from: embedded in the Core assembly (what ships) or a directory (development, tests).</summary>
public static partial class MigrationSet
{
    [GeneratedRegex(@"^(\d{4})_([A-Za-z0-9_]+)\.sql$")]
    private static partial Regex FileName();

    /// <summary>The <c>migrations/clickhouse/*.sql</c> files embedded at build time, in version order.</summary>
    public static IReadOnlyList<Migration> Embedded()
    {
        var assembly = typeof(MigrationSet).Assembly;
        var list = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            const string prefix = "migrations/clickhouse/";
            if (!resource.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException(resource);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            list.Add(Parse(resource[prefix.Length..], reader.ReadToEnd()));
        }

        return Ordered(list);
    }

    public static IReadOnlyList<Migration> FromDirectory(string directory)
    {
        var list = new List<Migration>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.sql"))
        {
            list.Add(Parse(Path.GetFileName(path), File.ReadAllText(path, Encoding.UTF8)));
        }

        return Ordered(list);
    }

    private static Migration Parse(string fileName, string sql)
    {
        var m = FileName().Match(fileName);
        if (!m.Success)
        {
            throw new InvalidOperationException($"migration file name '{fileName}' is not NNNN_name.sql");
        }

        return new Migration(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value, sql);
    }

    private static List<Migration> Ordered(List<Migration> list)
    {
        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Version == list[i - 1].Version)
            {
                throw new InvalidOperationException($"two migrations carry version {list[i].Version:D4}");
            }
        }

        return list;
    }
}

public sealed record MigrationReport(IReadOnlyList<int> Applied, IReadOnlyList<int> AlreadyApplied);

public sealed class MigrationException : Exception
{
    public MigrationException()
    {
    }

    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Applies the numbered ClickHouse migrations at backend startup over the driver's TCP client (control plane) and
/// records each in <c>schema_migrations</c> with the file's SHA-256. A recorded migration whose file now hashes
/// differently stops startup: files are additive-only and never edited after they ship (design record, "Schema
/// migrations"). Statements use <c>IF NOT EXISTS</c>, so a migration interrupted between its statements and its record
/// is simply re-run. The version table itself is created here, not by a migration.
/// </summary>
public sealed class ClickHouseMigrator(ClickHouseTcpClient client)
{
    public const string Table = "schema_migrations";

    public async Task<MigrationReport> ApplyAsync(IReadOnlyList<Migration> migrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        await client.ExecuteAsync(
            $"CREATE TABLE IF NOT EXISTS {Table} (version UInt32, name String, sha256 String, applied_at DateTime64(3, 'UTC')) " +
            "ENGINE = ReplacingMergeTree(applied_at) ORDER BY version",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var recorded = new Dictionary<int, (string Name, string Sha256)>();
        await foreach (var row in client.QueryAsync($"SELECT version, name, sha256 FROM {Table} FINAL", cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            recorded[Convert.ToInt32(row[0], CultureInfo.InvariantCulture)] = ((string)row[1]!, (string)row[2]!);
        }

        var applied = new List<int>();
        var already = new List<int>();
        foreach (var migration in migrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recorded.TryGetValue(migration.Version, out var record))
            {
                if (!string.Equals(record.Sha256, migration.Sha256, StringComparison.Ordinal))
                {
                    throw new MigrationException($"migration {migration.Version:D4}_{migration.Name} was applied with hash {record.Sha256} and now hashes {migration.Sha256}; migrations are never edited, add a new one");
                }

                already.Add(migration.Version);
                continue;
            }

            foreach (var statement in migration.Statements())
            {
                try
                {
                    await client.ExecuteAsync(statement, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (ClickHouseTcpException e)
                {
                    throw new MigrationException($"migration {migration.Version:D4}_{migration.Name} failed: {e.Message}", e);
                }
            }

            var versions = ClickHouseTcpColumn.Create("version", new[] { (uint)migration.Version });
            var names = ClickHouseTcpColumn.Create("name", new[] { migration.Name });
            var hashes = ClickHouseTcpColumn.Create("sha256", new[] { migration.Sha256 });
            var times = ClickHouseTcpColumn.Create("applied_at", new[] { UtcNowMilliseconds() });
            await client.InsertAsync($"INSERT INTO {Table} (version, name, sha256, applied_at) VALUES", [versions, names, hashes, times], cancellationToken: cancellationToken).ConfigureAwait(false);
            applied.Add(migration.Version);
        }

        return new MigrationReport(applied, already);
    }

    /// <summary>Now at millisecond precision: the driver refuses a DateTime with sub-millisecond ticks for DateTime64(3).</summary>
    internal static DateTime UtcNowMilliseconds() => new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
}
