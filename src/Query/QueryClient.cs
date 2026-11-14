using System.Diagnostics;
using System.Globalization;
using System.Net;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.ADO.Parameters;

namespace Urlog.Query;

/// <summary>
/// A query the gate let through and the server then refused while running it (an unknown table, a pattern it cannot
/// compile, a limit of the profile): a query error for the caller to show, with the server's code and first line.
/// </summary>
public sealed class QueryFailedException : Exception
{
    public QueryFailedException()
    {
    }

    public QueryFailedException(string message)
        : base(message)
    {
    }

    public QueryFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public QueryFailedException(int errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The ClickHouse error code (60 UNKNOWN_TABLE, 159 TIMEOUT_EXCEEDED, ...); 0 when there is none.</summary>
    public int ErrorCode { get; }
}

/// <summary>Rows of a query as text, column names from the result, and the client-side elapsed time.</summary>
public sealed record QueryTable(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, TimeSpan Elapsed);

/// <summary>
/// The query path's transport (ADR 0003): the driver's HTTP client as the read-only user, with the allowed-case
/// list sent as the per-request custom setting <c>SQL_allowed_cases</c> and every value as a <c>param_*</c> request
/// parameter. The SQL text is never touched here; nothing is appended as SETTINGS (the raw-SQL gate rejects
/// SETTINGS in the text). One request per query, no session state, so nothing can leak between users.
/// Every query text, transpiled or raw, passes <see cref="RawSqlGate"/> first (ADR 0001): one <c>EXPLAIN AST</c>
/// round trip as the same user, and a <see cref="RawSqlRejectedException"/> when the tree is not a plain SELECT
/// over the allowed relations. The only ungated SQL here is this class's own constant text.
/// </summary>
public sealed class ClickHouseQueryClient
{
    private readonly string _connectionString;

    public ClickHouseQueryClient(string host, int httpPort, string user, string password, string database = "default")
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(user);
        _connectionString = $"Host={host};Port={httpPort.ToString(CultureInfo.InvariantCulture)};Username={user};Password={password};Database={database}";
    }

    /// <summary>Runs a transpiled query, gated, with <c>SQL_allowed_cases</c> set to the cases in <paramref name="allowedCases"/> (the result's case when null).</summary>
    public async Task<QueryTable> RunAsync(TranspileResult query, IReadOnlyCollection<ulong>? allowedCases = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cases = AllowedCases(allowedCases ?? [query.CaseId]);
        await EnsureAllowedAsync(query.Sql, query.Parameters, cases, cancellationToken).ConfigureAwait(false);
        var table = await RunGatedAsync(query.Sql, query.Parameters, cases, cancellationToken).ConfigureAwait(false);

        // The SQL names a column the analyst named by a generated alias; the headers are the transpiler's.
        return query.Columns.Length == table.Columns.Count ? table with { Columns = query.Columns } : table;
    }

    /// <summary>
    /// Runs the raw-SQL tab's text, gated, as the cases in <paramref name="allowedCases"/>. The text is sent exactly
    /// as given after <see cref="RawSqlGate.Normalise"/> (surrounding whitespace, one trailing semicolon); it has no
    /// parameters.
    /// </summary>
    public async Task<QueryTable> RunSqlAsync(string sql, IReadOnlyCollection<ulong> allowedCases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(allowedCases);
        var text = RawSqlGate.Normalise(sql);
        var cases = AllowedCases(allowedCases);
        await EnsureAllowedAsync(text, [], cases, cancellationToken).ConfigureAwait(false);
        return await RunGatedAsync(text, [], cases, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>EXPLAIN indexes = 1</c> of a transpiled query (gated first), one line per row of the plan.</summary>
    public async Task<IReadOnlyList<string>> ExplainAsync(TranspileResult query, IReadOnlyCollection<ulong>? allowedCases = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cases = AllowedCases(allowedCases ?? [query.CaseId]);
        await EnsureAllowedAsync(query.Sql, query.Parameters, cases, cancellationToken).ConfigureAwait(false);
        var table = await RunGatedAsync("EXPLAIN indexes = 1 " + query.Sql, query.Parameters, cases, cancellationToken).ConfigureAwait(false);
        return table.Rows.Select(r => r[0]).ToList();
    }

    /// <summary><c>EXPLAIN indexes = 1</c> of the raw-SQL tab's text (gated first), one line per row of the plan.</summary>
    public async Task<IReadOnlyList<string>> ExplainSqlAsync(string sql, IReadOnlyCollection<ulong> allowedCases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(allowedCases);
        var text = RawSqlGate.Normalise(sql);
        var cases = AllowedCases(allowedCases);
        await EnsureAllowedAsync(text, [], cases, cancellationToken).ConfigureAwait(false);
        var table = await RunGatedAsync("EXPLAIN indexes = 1 " + text, [], cases, cancellationToken).ConfigureAwait(false);
        return table.Rows.Select(r => r[0]).ToList();
    }

    /// <summary>
    /// <c>EXPLAIN AST</c> of a query text as the read-only user, one dumped node per line: the server's own parse,
    /// nothing executed. A text the server cannot parse (or refuses: a constraint violation in its SETTINGS clause,
    /// a multi-statement) surfaces as the driver's server exception.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExplainAstAsync(string sql, IReadOnlyList<QueryParameter> parameters, IReadOnlyCollection<ulong> allowedCases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(allowedCases);
        var table = await ExecuteAsync("EXPLAIN AST " + sql, parameters, AllowedCases(allowedCases), cancellationToken).ConfigureAwait(false);
        return table.Rows.Select(r => r[0]).ToList();
    }

    /// <summary>The gate: the text-only checks, then the server's parse, then the walk; throws <see cref="RawSqlRejectedException"/>.</summary>
    private async Task EnsureAllowedAsync(string sql, IReadOnlyList<QueryParameter> parameters, string allowedCases, CancellationToken cancellationToken)
    {
        if (RawSqlGate.PreCheck(sql) is { } early)
        {
            throw new RawSqlRejectedException(early);
        }

        IReadOnlyList<string> ast;
        try
        {
            var table = await ExecuteAsync("EXPLAIN AST " + sql, parameters, allowedCases, cancellationToken).ConfigureAwait(false);
            ast = table.Rows.Select(r => r[0]).ToList();
        }
        catch (ClickHouseServerException e)
        {
            // Code 62 SYNTAX_ERROR for text the server cannot parse; anything else the server refused before
            // parsing finished (a SETTINGS clause that violates a constraint, INTO OUTFILE over HTTP, ...).
            var first = e.Message.Split('\n', 2)[0];
            throw new RawSqlRejectedException($"the server did not accept the query: {first}", e);
        }

        if (RawSqlGate.Inspect(ast) is { } reason)
        {
            throw new RawSqlRejectedException(reason);
        }
    }

    /// <summary>A query that passed the gate, run: what the server refuses at this point is the caller's query error, with the server's code.</summary>
    private async Task<QueryTable> RunGatedAsync(string sql, IReadOnlyList<QueryParameter> parameters, string allowedCases, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(sql, parameters, allowedCases, cancellationToken).ConfigureAwait(false);
        }
        catch (ClickHouseServerException e)
        {
            throw new QueryFailedException(e.ErrorCode, e.Message.Split((char)10, 2)[0], e);
        }
    }

    /// <summary>The span of a case through <c>events_v</c>: null when the case has no visible events.</summary>
    public async Task<(DateTimeOffset From, DateTimeOffset To)?> CaseSpanAsync(ulong caseId, CancellationToken cancellationToken = default)
    {
        var table = await ExecuteAsync(
            "SELECT toString(min(ts)), toString(max(ts)), count() FROM events_v WHERE case_id = {case:UInt64}",
            [new QueryParameter("case", "UInt64", caseId.ToString(CultureInfo.InvariantCulture))],
            AllowedCases([caseId]),
            cancellationToken).ConfigureAwait(false);
        var row = table.Rows[0];
        if (row[2] == "0")
        {
            return null;
        }

        var from = DateTimeOffset.ParseExact(row[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var to = DateTimeOffset.ParseExact(row[1], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return (from, to.AddMilliseconds(1)); // the range is half-open
    }

    private static string AllowedCases(IReadOnlyCollection<ulong> cases) => "[" + string.Join(",", cases.Select(c => c.ToString(CultureInfo.InvariantCulture))) + "]";

    private async Task<QueryTable> ExecuteAsync(string sql, IReadOnlyList<QueryParameter> parameters, string allowedCases, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        await using var connection = new ClickHouseConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CustomSettings["SQL_allowed_cases"] = allowedCases;
        foreach (var p in parameters)
        {
            var parameter = (ClickHouseDbParameter)command.CreateParameter();
            parameter.ParameterName = p.Name;
            parameter.ClickHouseType = p.Type;
            parameter.Value = Typed(p);
            command.Parameters.Add(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        var rows = new List<string[]>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = Text(reader.GetValue(i));
            }

            rows.Add(row);
        }

        return new QueryTable(columns, rows, sw.Elapsed);
    }

    /// <summary>The driver formats a request parameter from a typed value: instants as UTC DateTime, integers as integers, the rest as text.</summary>
    private static object Typed(QueryParameter p) => p.Type switch
    {
        "DateTime64(3)" => DateTime.SpecifyKind(DateTime.ParseExact(p.Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), DateTimeKind.Utc),
        "UInt64" => ulong.Parse(p.Value, CultureInfo.InvariantCulture),
        "Int64" => long.Parse(p.Value, CultureInfo.InvariantCulture),
        _ => p.Value,
    };

    /// <summary>A cell as the CLI prints it: instants in UTC with milliseconds, IPv4-mapped addresses as IPv4, maps as <c>{k:v, ...}</c>.</summary>
    public static string Text(object? value) => value switch
    {
        null or DBNull => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        IPAddress ip => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString(),
        string s => s,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        IDictionary<string, string> map => "{" + string.Join(", ", map.Select(kv => kv.Key + ":" + kv.Value)) + "}",
        System.Collections.IDictionary map => "{" + string.Join(", ", map.Keys.Cast<object>().Select(k => k + ":" + map[k])) + "}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
