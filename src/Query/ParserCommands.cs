using System.Collections.Immutable;
using System.Globalization;

namespace Urlog.Query;

/// <summary>
/// The verbs after <c>|</c> (design record, "Query language"): <c>where</c>, <c>stats</c>, <c>table</c>,
/// <c>rex</c>, <c>timechart</c>, <c>sort</c>, <c>head</c>. Each is parsed by its own form; which verb may follow
/// which is decided by the transpiler, where the whole pipeline is known.
/// </summary>
public sealed partial class Parser
{
    public const int MaxSpanCount = 10_000;

    private static readonly Dictionary<string, AggregateFunction> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["count"] = AggregateFunction.Count,
        ["dc"] = AggregateFunction.Dc,
        ["sum"] = AggregateFunction.Sum,
        ["avg"] = AggregateFunction.Avg,
        ["min"] = AggregateFunction.Min,
        ["max"] = AggregateFunction.Max,
        ["values"] = AggregateFunction.Values,
        ["earliest"] = AggregateFunction.Earliest,
        ["latest"] = AggregateFunction.Latest,
    };

    /// <summary>Splunk aggregates that v1 does not have, with what to write where there is something.</summary>
    private static readonly Dictionary<string, string?> OtherAggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["distinct_count"] = "use dc",
        ["c"] = "use count",
        ["mean"] = "use avg",
        ["first"] = "use earliest",
        ["last"] = "use latest",
        ["list"] = "use values",
        ["median"] = null,
        ["mode"] = null,
        ["range"] = null,
        ["stdev"] = null,
        ["var"] = null,
        ["perc95"] = null,
        ["p95"] = null,
    };

    /// <summary>Verbs v1 does not have. A value is what to write for the same result; null when v1 has nothing.</summary>
    private static readonly Dictionary<string, string?> UnsupportedVerbs = new(StringComparer.Ordinal)
    {
        ["top"] = "use stats count by <field> | sort -count | head",
        ["rare"] = "use stats count by <field> | sort count | head",
        ["fields"] = "use table <field> ...",
        ["dedup"] = "use stats count by <field>, or stats latest(raw) by <field> for one event of each",
        ["rename"] = "name an aggregate with as: stats count as <name>",
        ["search"] = "use where <expression>",
        ["regex"] = "use rex field=<field> \"(?<name>...)\" | where name=*",
        ["chart"] = "use stats ... by <field>",
        ["bin"] = "use timechart span=<N><unit> ...",
        ["bucket"] = "use timechart span=<N><unit> ...",
        ["tail"] = "use sort _time | head <N>",
        ["reverse"] = "use sort with the other sign",
        ["eval"] = null,
        ["join"] = null,
        ["transaction"] = null,
        ["lookup"] = null,
        ["append"] = null,
    };

    /// <summary>The verbs after a pipe, as <see cref="ParseCommand"/> takes them; what autocomplete offers there.</summary>
    public static ImmutableArray<string> Verbs { get; } = ["where", "stats", "sort", "head", "table", "rex", "timechart"];

    private bool _inWhere;
    private bool _afterAggregate;

    private CommandNode ParseCommand()
    {
        var verb = Advance();
        var name = verb.Text.ToLowerInvariant();
        switch (name)
        {
            case "where":
                return ParseWhere(verb);
            case "stats":
            {
                var stats = ParseStats(verb);
                _afterAggregate = true;
                return stats;
            }

            case "table":
                return ParseTable(verb);
            case "rex":
                return ParseRex(verb);
            case "timechart":
            {
                var chart = ParseTimechart(verb);
                _afterAggregate = true;
                return chart;
            }

            case "sort":
                return ParseSort(verb);
            case "head":
                return ParseHead(verb);
            default:
                throw new QueryException(verb.Span, UnsupportedVerbs.GetValueOrDefault(name) is { } rewrite ? $"not supported in v1: {name} ({rewrite})" : $"not supported in v1: {name}");
        }
    }

    private WhereNode ParseWhere(Token verb)
    {
        if (Current.Kind is TokenKind.Pipe or TokenKind.End)
        {
            throw new QueryException(verb.Span, "where: expected an expression, like where status>=500");
        }

        _inWhere = true;
        var expression = ParseOr(allowScope: false);
        _inWhere = false;
        if (Current.Kind == TokenKind.RParen)
        {
            throw new QueryException(Current.Span, "unmatched ')'");
        }

        return new WhereNode(expression, Through(verb));
    }

    private StatsNode ParseStats(Token verb)
    {
        var aggregates = ImmutableArray.CreateBuilder<AggregateCall>();
        while (true)
        {
            if (Current.Kind != TokenKind.Word || Current.IsWord("by"))
            {
                if (aggregates.Count == 0 || _tokens[_i - 1].Kind == TokenKind.Comma)
                {
                    throw new QueryException(Current.Kind == TokenKind.End ? verb.Span : Current.Span, "stats: expected an aggregate: count, dc(f), sum(f), avg(f), min(f), max(f), values(f), earliest(f), latest(f)");
                }

                break;
            }

            aggregates.Add(ParseAggregate("stats"));
            if (Current.Kind == TokenKind.Comma)
            {
                Advance();
            }
        }

        var by = ImmutableArray.CreateBuilder<(string, TextSpan)>();
        if (Current.IsWord("by"))
        {
            by.AddRange(ParseFieldList(Advance(), "expected a field name after 'by'"));
        }

        if (Current.Kind is not (TokenKind.Pipe or TokenKind.End))
        {
            throw new QueryException(Current.Span, $"stats: unexpected '{Current.Text}'");
        }

        return new StatsNode(aggregates.ToImmutable(), by.ToImmutable(), Through(verb));
    }

    /// <summary><c>count [as name]</c> or <c>fn(field) [as name]</c>.</summary>
    private AggregateCall ParseAggregate(string verb)
    {
        var fn = Advance();
        var hasArgument = Current.Kind == TokenKind.LParen;
        if (!AggregateFunctions.TryGetValue(fn.Text, out var function))
        {
            if (OtherAggregates.TryGetValue(fn.Text, out var instead))
            {
                var lower = fn.Text.ToLowerInvariant();
                throw new QueryException(fn.Span, instead is null ? $"not supported in v1: {verb} {lower}(...)" : $"not supported in v1: {verb} {lower}(...) ({instead})");
            }

            throw new QueryException(fn.Span, $"{verb}: '{fn.Text}' is not an aggregate: count, dc(f), sum(f), avg(f), min(f), max(f), values(f), earliest(f), latest(f)");
        }

        string? field = null;
        var fieldSpan = fn.Span;
        if (hasArgument)
        {
            var open = Advance();
            if (Current.Kind != TokenKind.Word)
            {
                throw new QueryException(Current.Kind == TokenKind.End ? open.Span : Current.Span, $"{verb}: expected a field name inside {fn.Text.ToLowerInvariant()}(...)");
            }

            var f = Advance();
            ValidateName(f.Text, f.Span);
            if (Current.Kind != TokenKind.RParen)
            {
                throw new QueryException(open.Span, "missing ')'");
            }

            Advance();
            field = f.Text;
            fieldSpan = f.Span;
            if (function == AggregateFunction.Count)
            {
                throw new QueryException(new TextSpan(fn.Span.Offset, PreviousEnd() - fn.Span.Offset), $"not supported in v1: {verb} count({field}) (use {field}=* | {verb} count)");
            }
        }
        else if (function != AggregateFunction.Count)
        {
            var lower = fn.Text.ToLowerInvariant();
            throw new QueryException(fn.Span, $"{verb}: {lower} needs a field: {lower}(<field>)");
        }

        string? alias = null;
        if (Current.IsWord("as"))
        {
            var @as = Advance();
            if (Current.Kind != TokenKind.Word)
            {
                throw new QueryException(Current.Kind == TokenKind.End ? @as.Span : Current.Span, "expected a name after 'as'");
            }

            var a = Advance();
            if (!Names.IsStrict(a.Text))
            {
                throw new QueryException(a.Span, $"'{a.Text}' is not a name: use letters, digits and '_', starting with a letter or '_', at most {Names.MaxLength} characters");
            }

            alias = a.Text;
        }

        return new AggregateCall(function, field, fieldSpan, alias, new TextSpan(fn.Span.Offset, PreviousEnd() - fn.Span.Offset));
    }

    /// <summary>One or more field names, separated by commas or by whitespace.</summary>
    private List<(string, TextSpan)> ParseFieldList(Token after, string expected)
    {
        var fields = new List<(string, TextSpan)>();
        while (true)
        {
            if (Current.Kind != TokenKind.Word)
            {
                throw new QueryException(Current.Kind == TokenKind.End ? after.Span : Current.Span, expected);
            }

            var f = Advance();
            ValidateName(f.Text, f.Span);
            fields.Add((f.Text, f.Span));
            if (Current.Kind == TokenKind.Comma)
            {
                after = Advance();
                continue;
            }

            if (Current.Kind != TokenKind.Word)
            {
                return fields;
            }
        }
    }

    private TableNode ParseTable(Token verb)
    {
        var fields = ParseFieldList(verb, "table: expected one or more fields, like table _time, host, message");
        foreach (var (name, span) in fields)
        {
            if (name.Contains('*', StringComparison.Ordinal))
            {
                throw new QueryException(span, "table: wildcards are not supported; name each field");
            }
        }

        if (Current.Kind == TokenKind.LParen && AggregateFunctions.ContainsKey(_tokens[_i - 1].Text))
        {
            throw NameTheAggregate(_tokens[_i - 1]);
        }

        if (Current.Kind is not (TokenKind.Pipe or TokenKind.End))
        {
            throw new QueryException(Current.Span, $"table: unexpected '{Current.Text}'");
        }

        return new TableNode([.. fields], Through(verb));
    }

    private RexNode ParseRex(Token verb)
    {
        const string Form = "rex field=<field> \"<pattern with (?<name>...) groups>\"";
        (string Name, TextSpan Span)? field = null;
        while (Current.Kind == TokenKind.Word && _i + 1 < _tokens.Count && _tokens[_i + 1].Kind == TokenKind.Operator)
        {
            var option = Advance();
            var op = Advance();
            if (op.Text != "=" || Current.Kind is not (TokenKind.Word or TokenKind.Quoted))
            {
                throw new QueryException(op.Span, $"rex: expected {Form}");
            }

            var value = Advance();
            if (!option.Text.Equals("field", StringComparison.OrdinalIgnoreCase))
            {
                throw new QueryException(new TextSpan(option.Span.Offset, PreviousEnd() - option.Span.Offset), $"not supported in v1: rex {option.Text.ToLowerInvariant()}= (only {Form})");
            }

            if (field is not null)
            {
                throw new QueryException(option.Span, "rex: field= given twice");
            }

            if (value.Kind != TokenKind.Word)
            {
                throw new QueryException(value.Span, "rex: field= takes a field name");
            }

            ValidateName(value.Text, value.Span);
            field = (value.Text, value.Span);
        }

        if (Current.Kind != TokenKind.Quoted)
        {
            throw new QueryException(Current.Kind is TokenKind.End or TokenKind.Pipe ? verb.Span : Current.Span, $"rex: expected {Form}");
        }

        var quoted = Advance();
        if (field is null)
        {
            throw new QueryException(verb.Span, "rex: field=<field> is required before the pattern (field=raw for the whole event)");
        }

        // The quoted token is the pattern with \" and \\ undone; the map says where each of its characters stands
        // in the query, so an error inside the pattern points at the construct.
        var offsets = new List<int>(quoted.Text.Length + 1);
        var end = quoted.Span.Offset + quoted.Span.Length - 1;
        for (var at = quoted.Span.Offset + 1; at < end; at++)
        {
            offsets.Add(at);
            if (_text[at] == '\\' && at + 1 < end && (_text[at + 1] == '"' || _text[at + 1] == '\\'))
            {
                at++;
            }
        }

        offsets.Add(end);
        IReadOnlyList<Re2Group> groups;
        try
        {
            groups = Re2Syntax.Check(quoted.Text);
        }
        catch (Re2SyntaxException e)
        {
            var from = offsets[Math.Min(e.Offset, offsets.Count - 1)];
            var to = offsets[Math.Min(e.Offset + Math.Max(1, e.Length), offsets.Count - 1)];
            throw new QueryException(new TextSpan(from, Math.Max(1, to - from)), "rex: " + e.Message);
        }

        var named = groups.Where(g => g.Name is not null)
            .Select(g => new RexGroup(g.Name!, g.Index, new TextSpan(offsets[g.NameOffset], offsets[g.NameOffset + g.NameLength] - offsets[g.NameOffset])))
            .ToImmutableArray();
        if (named.Length == 0)
        {
            throw new QueryException(quoted.Span, "rex: the pattern needs a named group, like (?<user>\\w+): each one becomes a field");
        }

        if (Current.Kind is not (TokenKind.Pipe or TokenKind.End))
        {
            throw new QueryException(Current.Span, $"rex: unexpected '{Current.Text}'");
        }

        return new RexNode(field.Value.Name, field.Value.Span, quoted.Text, quoted.Span, named, Through(verb));
    }

    private TimechartNode ParseTimechart(Token verb)
    {
        const string Form = "timechart span=<N><s|m|h|d|w> <aggregate> [by <field>]";
        (int Count, char Unit, TextSpan Span)? span = null;
        AggregateCall? aggregate = null;
        (string, TextSpan)? by = null;
        while (Current.Kind is not (TokenKind.Pipe or TokenKind.End))
        {
            if (Current.IsWord("span") && _i + 1 < _tokens.Count && _tokens[_i + 1].Kind == TokenKind.Operator)
            {
                var word = Advance();
                var op = Advance();
                if (op.Text != "=" || Current.Kind != TokenKind.Word)
                {
                    throw new QueryException(op.Span, $"timechart: expected {Form}");
                }

                var value = Advance();
                var whole = new TextSpan(word.Span.Offset, PreviousEnd() - word.Span.Offset);
                if (span is not null)
                {
                    throw new QueryException(whole, "timechart: span= given twice");
                }

                span = ParseSpan(value, whole);
            }
            else if (Current.IsWord("by") && aggregate is not null && by is null)
            {
                // One field, not a list: the word after it may be span=.
                var byWord = Advance();
                if (Current.Kind != TokenKind.Word)
                {
                    throw new QueryException(Current.Kind == TokenKind.End ? byWord.Span : Current.Span, "expected a field name after 'by'");
                }

                var f = Advance();
                ValidateName(f.Text, f.Span);
                by = (f.Text, f.Span);
                var second = Current.Kind == TokenKind.Comma ? (_i + 1 < _tokens.Count ? _tokens[_i + 1] : Current) : Current;
                var spanFollows = Current.IsWord("span") && _i + 1 < _tokens.Count && _tokens[_i + 1].Kind == TokenKind.Operator;
                if (Current.Kind == TokenKind.Comma || (Current.Kind == TokenKind.Word && !spanFollows))
                {
                    throw new QueryException(second.Span, "timechart: one by field only; the series are its values");
                }
            }
            else if (Current.Kind == TokenKind.Word && aggregate is null)
            {
                aggregate = ParseAggregate("timechart");
            }
            else if (Current.Kind == TokenKind.Word && by is null && (AggregateFunctions.ContainsKey(Current.Text) || Current.Kind == TokenKind.Comma))
            {
                throw new QueryException(Current.Span, "timechart: one aggregate only");
            }
            else
            {
                throw new QueryException(Current.Span, Current.Kind == TokenKind.Comma ? "timechart: one aggregate only" : $"timechart: unexpected '{Current.Text}'");
            }
        }

        if (aggregate is null)
        {
            throw new QueryException(verb.Span, $"timechart: expected {Form}");
        }

        if (span is null)
        {
            throw new QueryException(verb.Span, "timechart: span= is required, like span=1h (units s, m, h, d, w)");
        }

        return new TimechartNode(span.Value.Count, span.Value.Unit, span.Value.Span, aggregate, by, Through(verb));
    }

    private static (int Count, char Unit, TextSpan Span) ParseSpan(Token value, TextSpan whole)
    {
        var text = value.Text;
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits != text.Length - 1 || !"smhdw".Contains(text[^1], StringComparison.Ordinal))
        {
            throw new QueryException(value.Span, $"timechart: '{text}' is not a span: a whole number and a unit, like 5m, 1h or 1d (units s, m, h, d, w)");
        }

        if (!int.TryParse(text.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1 || count > MaxSpanCount)
        {
            throw new QueryException(value.Span, $"timechart: the span count must be between 1 and {MaxSpanCount:N0}");
        }

        return (count, text[^1], whole);
    }

    private SortNode ParseSort(Token verb)
    {
        var keys = ImmutableArray.CreateBuilder<(string, bool, TextSpan)>();
        while (Current.Kind is TokenKind.Word or TokenKind.Operator)
        {
            var k = Advance();
            var text = k.Text;
            var descending = false;
            var span = k.Span;
            if (k.Kind == TokenKind.Operator)
            {
                throw new QueryException(k.Span, $"sort: unexpected '{k.Text}'");
            }

            if (text.StartsWith('-') || text.StartsWith('+'))
            {
                descending = text[0] == '-';
                text = text[1..];
                if (text.Length == 0)
                {
                    throw new QueryException(k.Span, "sort: expected a field name after the sign");
                }
            }

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new QueryException(k.Span, "sort: a row limit is not supported here; use head N");
            }

            if (_afterAggregate && Current.Kind == TokenKind.LParen && AggregateFunctions.ContainsKey(text))
            {
                throw NameTheAggregate(k);
            }

            ValidateName(text, span);
            keys.Add((text, descending, span));
            if (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (Current.Kind != TokenKind.Word)
                {
                    throw new QueryException(Current.Kind == TokenKind.End ? _tokens[_i - 1].Span : Current.Span, "sort: expected a field name after ','");
                }
            }
        }

        if (keys.Count == 0)
        {
            throw new QueryException(Current.Kind == TokenKind.End ? verb.Span : Current.Span, "sort: expected one or more fields, like sort -count");
        }

        if (Current.Kind is not (TokenKind.Pipe or TokenKind.End))
        {
            throw new QueryException(Current.Span, $"sort: unexpected '{Current.Text}'");
        }

        return new SortNode(keys.ToImmutable(), Through(verb));
    }

    private HeadNode ParseHead(Token verb)
    {
        var count = DefaultHead;
        if (Current.Kind == TokenKind.Word)
        {
            var n = Advance();
            if (!int.TryParse(n.Text, NumberStyles.None, CultureInfo.InvariantCulture, out count))
            {
                throw new QueryException(n.Span, $"head: expected a number, got '{n.Text}'");
            }

            if (count < 1 || count > MaxHead)
            {
                throw new QueryException(n.Span, $"head: the count must be between 1 and {MaxHead:N0}");
            }
        }

        if (Current.Kind is not (TokenKind.Pipe or TokenKind.End))
        {
            throw new QueryException(Current.Span, $"head: unexpected '{Current.Text}'");
        }

        return new HeadNode(count, Through(verb));
    }

    /// <summary>An aggregate written as a call after the aggregation: it has no name there unless <c>as</c> gave it one.</summary>
    private static QueryException NameTheAggregate(Token function)
    {
        var fn = function.Text.TrimStart('-', '+').ToLowerInvariant();
        return new QueryException(function.Span, $"{fn}(...) has no name here: name it in stats, like stats {fn}(<field>) as n, then use n");
    }

    private TextSpan Through(Token verb) => new(verb.Span.Offset, PreviousEnd() - verb.Span.Offset);
}
