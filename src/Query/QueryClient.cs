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
public sealed record QueryTable(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, TimeSpan Elapsed)
{
    /// <summary>
    /// Rows where a <c>values(f)</c> cell reached the cap: the group has more values than the cell shows. A result
    /// with any such row carries the <c>values_capped</c> warning; one without does not.
    /// </summary>
    public IReadOnlyList<int> CappedRows { get; init; } = [];

    public bool ValuesCapped => CappedRows.Count > 0;
}

/// <summary>Rows of a statement with their cells as the driver decoded them, and whether reading stopped at the caller's row limit.</summary>
public sealed record QueryRows(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated, TimeSpan Elapsed);

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
        var table = await RunGatedAsync(query.Sql, query.Parameters, cases, cancellationToken, query.ValuesColumns).ConfigureAwait(false);

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
    private async Task<QueryTable> RunGatedAsync(string sql, IReadOnlyList<QueryParameter> parameters, string allowedCases, CancellationToken cancellationToken, IReadOnlyList<int>? valuesColumns = null)
    {
        try
        {
            return await ExecuteAsync(sql, parameters, allowedCases, cancellationToken, valuesColumns).ConfigureAwait(false);
        }
        catch (ClickHouseServerException e)
        {
            throw Failed(e, valuesColumns is { Count: > 0 });
        }
    }

    /// <summary>The server's code for a query that passed its memory limit.</summary>
    public const int MemoryLimitExceeded = 241;

    /// <summary>
    /// A server refusal as the caller's query error: the server's code and the first line of its text. The one
    /// refusal that is explained is a <c>values(f)</c> query that passed the memory limit, because the cause is the
    /// aggregate (it holds every distinct value of a group) and the analyst can act on it.
    /// </summary>
    private static QueryFailedException Failed(ClickHouseServerException e, bool holdsValues) =>
        e.ErrorCode == MemoryLimitExceeded && holdsValues
            ? new QueryFailedException(e.ErrorCode, ValuesOutOfMemory, e)
            : new QueryFailedException(e.ErrorCode, e.Message.Split((char)10, 2)[0], e);

    public const string ValuesOutOfMemory =
        "values(f) passed the memory limit of a query: it keeps every distinct value of a group while it reads, and this search has too many. "
        + "Count them with dc(f), or narrow the search or the time range.";

    /// <summary>
    /// The span of a case through <c>events_v</c>, for a caller without a metadata file: null when the case has no
    /// visible event with a time. Times below <see cref="Urlog.Core.EventTime.Floor"/> are left out, as in the
    /// recorded span (rejected lines and timestamps parsed without a year sit at the epoch and in 1970). The other
    /// bound of the recorded span, the arrival of each file, is not known here.
    /// </summary>
    public async Task<(DateTimeOffset From, DateTimeOffset To)?> CaseSpanAsync(ulong caseId, CancellationToken cancellationToken = default)
    {
        var table = await ExecuteAsync(
            "SELECT toString(min(ts)), toString(max(ts)), count() FROM events_v WHERE case_id = {case:UInt64} AND ts >= {floor:DateTime64(3)}",
            [
                new QueryParameter("case", "UInt64", caseId.ToString(CultureInfo.InvariantCulture)),
                new QueryParameter("floor", "DateTime64(3)", Urlog.Core.EventTime.Floor.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
            ],
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

    /// <summary>
    /// The field statistics of one case through <c>field_stats_v</c>, with the allowed-case list set to that case
    /// alone: <see cref="FieldStats.Sql"/>, this class's own constant text.
    /// </summary>
    public async Task<CaseFieldStats> FieldStatsAsync(ulong caseId, CancellationToken cancellationToken = default)
    {
        var table = await ExecuteAsync(
            FieldStats.Sql,
            [new QueryParameter("case", "UInt64", caseId.ToString(CultureInfo.InvariantCulture))],
            AllowedCases([caseId]),
            cancellationToken).ConfigureAwait(false);
        return FieldStats.Read(caseId, table.Rows);
    }

    /// <summary>
    /// One statement of a search, gated, with its cells as the driver decoded them. Reading stops after
    /// <paramref name="maxRows"/> rows; <see cref="QueryRows.Truncated"/> says whether there were more.
    /// </summary>
    public async Task<QueryRows> RunAsync(SearchStatement statement, IReadOnlyCollection<ulong> allowedCases, int maxRows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(allowedCases);
        var cases = AllowedCases(allowedCases);
        await EnsureAllowedAsync(statement.Sql, statement.Parameters, cases, cancellationToken).ConfigureAwait(false);
        return await CollectAsync(statement.Sql, statement.Parameters, cases, maxRows, cancellationToken, !statement.ValuesColumns.IsDefaultOrEmpty).ConfigureAwait(false);
    }

    /// <summary>The raw-SQL tab's text, gated, as <see cref="RunAsync(SearchStatement, IReadOnlyCollection{ulong}, int, CancellationToken)"/> returns a statement.</summary>
    public async Task<QueryRows> RunSqlAsync(string sql, IReadOnlyCollection<ulong> allowedCases, int maxRows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(allowedCases);
        var text = RawSqlGate.Normalise(sql);
        var cases = AllowedCases(allowedCases);
        await EnsureAllowedAsync(text, [], cases, cancellationToken).ConfigureAwait(false);
        return await CollectAsync(text, [], cases, maxRows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A statement read row by row, gated: <paramref name="onColumns"/> once with the column names, then
    /// <paramref name="onRow"/> for each row as it is decoded, until it returns false or the rows end. Nothing is
    /// kept here, so the caller's memory is its own row. Returns the rows handed over.
    /// </summary>
    public async Task<long> StreamAsync(SearchStatement statement, IReadOnlyCollection<ulong> allowedCases, Action<IReadOnlyList<string>> onColumns, Func<object?[], ValueTask<bool>> onRow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(allowedCases);
        var cases = AllowedCases(allowedCases);
        await EnsureAllowedAsync(statement.Sql, statement.Parameters, cases, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(statement.Sql, statement.Parameters, cases, onColumns, onRow, cancellationToken, !statement.ValuesColumns.IsDefaultOrEmpty).ConfigureAwait(false);
    }

    /// <summary>The raw-SQL tab's text read row by row, gated.</summary>
    public async Task<long> StreamSqlAsync(string sql, IReadOnlyCollection<ulong> allowedCases, Action<IReadOnlyList<string>> onColumns, Func<object?[], ValueTask<bool>> onRow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(allowedCases);
        var text = RawSqlGate.Normalise(sql);
        var cases = AllowedCases(allowedCases);
        await EnsureAllowedAsync(text, [], cases, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(text, [], cases, onColumns, onRow, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QueryRows> CollectAsync(string sql, IReadOnlyList<QueryParameter> parameters, string allowedCases, int maxRows, CancellationToken cancellationToken, bool holdsValues = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRows);
        var sw = Stopwatch.StartNew();
        IReadOnlyList<string> columns = [];
        var rows = new List<object?[]>();
        var truncated = false;
        await ReadAsync(
            sql,
            parameters,
            allowedCases,
            names => columns = names,
            row =>
            {
                if (rows.Count == maxRows)
                {
                    truncated = true;
                    return ValueTask.FromResult(false);
                }

                rows.Add(row);
                return ValueTask.FromResult(true);
            },
            cancellationToken,
            holdsValues).ConfigureAwait(false);
        return new QueryRows(columns, rows, truncated, sw.Elapsed);
    }

    /// <summary>A gated text, run and handed over a row at a time; a server refusal is the caller's query error.</summary>
    private async Task<long> ReadAsync(string sql, IReadOnlyList<QueryParameter> parameters, string allowedCases, Action<IReadOnlyList<string>> onColumns, Func<object?[], ValueTask<bool>> onRow, CancellationToken cancellationToken, bool holdsValues = false)
    {
        ArgumentNullException.ThrowIfNull(onColumns);
        ArgumentNullException.ThrowIfNull(onRow);
        try
        {
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
            var columns = new string[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = reader.GetName(i);
            }

            onColumns(columns);
            long count = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new object?[columns.Length];
                for (var i = 0; i < row.Length; i++)
                {
                    var value = reader.GetValue(i);
                    row[i] = value is DBNull ? null : value;
                }

                if (!await onRow(row).ConfigureAwait(false))
                {
                    break;
                }

                count++;
            }

            return count;
        }
        catch (ClickHouseServerException e)
        {
            throw Failed(e, holdsValues);
        }
    }

    private static string AllowedCases(IReadOnlyCollection<ulong> cases) => "[" + string.Join(",", cases.Select(c => c.ToString(CultureInfo.InvariantCulture))) + "]";

    private async Task<QueryTable> ExecuteAsync(string sql, IReadOnlyList<QueryParameter> parameters, string allowedCases, CancellationToken cancellationToken, IReadOnlyList<int>? valuesColumns = null)
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
        var capped = new List<int>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new string[reader.FieldCount];
            var rowCapped = false;
            for (var i = 0; i < row.Length; i++)
            {
                var value = reader.GetValue(i);

                // values(f) asked for one value more than the cap: a cell that holds it is cut back and its row marked.
                if (valuesColumns?.Contains(i) == true && value is Array { Length: > Transpiler.ValuesCap } list)
                {
                    var kept = Array.CreateInstance(list.GetType().GetElementType()!, Transpiler.ValuesCap);
                    Array.Copy(list, kept, Transpiler.ValuesCap);
                    row[i] = Text(kept)[..^1] + ", ...]";
                    rowCapped = true;
                    continue;
                }

                row[i] = Text(value);
            }

            if (rowCapped)
            {
                capped.Add(rows.Count);
            }

            rows.Add(row);
        }

        return new QueryTable(columns, rows, sw.Elapsed) { CappedRows = capped };
    }

    /// <summary>The driver formats a request parameter from a typed value: instants as UTC DateTime, integers as integers, the rest as text.</summary>
    private static object Typed(QueryParameter p) => p.Type switch
    {
        "DateTime64(3)" => DateTime.SpecifyKind(DateTime.ParseExact(p.Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), DateTimeKind.Utc),
        "UInt64" => ulong.Parse(p.Value, CultureInfo.InvariantCulture),
        "UInt32" => uint.Parse(p.Value, CultureInfo.InvariantCulture),
        "Float64" => double.Parse(p.Value, CultureInfo.InvariantCulture),
        "Int64" => long.Parse(p.Value, CultureInfo.InvariantCulture),
        _ => p.Value,
    };

    /// <summary>A cell as the CLI prints it: instants in UTC with milliseconds, IPv4-mapped addresses as IPv4, lists as <c>[a, b]</c>, maps as <c>{k:v, ...}</c>.</summary>
    public static string Text(object? value) => value switch
    {
        null or DBNull => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        IPAddress ip => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString(),
        string s => s,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        Array list => "[" + string.Join(", ", list.Cast<object?>().Select(Text)) + "]",
        IDictionary<string, string> map => "{" + string.Join(", ", map.Select(kv => kv.Key + ":" + kv.Value)) + "}",
        System.Collections.IDictionary map => "{" + string.Join(", ", map.Keys.Cast<object>().Select(k => k + ":" + map[k])) + "}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
