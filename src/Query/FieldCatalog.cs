using System.Collections.Immutable;
using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Urlog.Query;

/// <summary>How a field compares and transpiles (<c>src/Query/field-catalog.yaml</c>, header comment).</summary>
public enum FieldType
{
    /// <summary><c>ts</c>: instants; <c>earliest=</c>/<c>latest=</c> and ordering operators.</summary>
    Time,

    /// <summary>A LowCardinality column, lowercased at ingest: equality on the lowercased value.</summary>
    Keyword,

    /// <summary>A String column as logged (YAML type <c>string</c>): exact, case-sensitive equality; wildcards through LIKE/ILIKE.</summary>
    Verbatim,

    /// <summary>An unsigned integer column (YAML type <c>int</c>).</summary>
    Number,

    /// <summary>An IPv6 column (IPv4 mapped): equality and CIDR.</summary>
    Ip,

    /// <summary><c>message</c> and <c>raw</c>: bare terms go to the text index on <c>message</c>; equality is a substring compare.</summary>
    Text,

    /// <summary>An extras key: <c>extras[key]</c>, string compare, unindexed.</summary>
    Extra,

    /// <summary>A Float64 the query computes (an average, a sum over an extras key). Never a catalog type.</summary>
    Real,
}

/// <summary>One catalog entry. <see cref="Column"/> is the <c>events_v</c> column, or null for an extras-backed field (<see cref="ExtraKey"/>).</summary>
public sealed record FieldDef(string Name, string? Column, string? ExtraKey, FieldType Type, string Description, ImmutableArray<string> Aliases)
{
    /// <summary>True for a field the catalog does not list: it resolves to <c>extras[name]</c>.</summary>
    public bool IsUnknown { get; init; }

    /// <summary>
    /// For an integer column: 0 is what a source that does not supply the field stores, so 0 reads as "no value"
    /// (YAML <c>zero_is_absent: true</c>). Without it every stored number is a value, 0 included.
    /// </summary>
    public bool ZeroIsAbsent { get; init; }

    /// <summary>
    /// The values of the field can name a person or carry a credential (YAML <c>sensitive: true</c>). The field
    /// listing passes the mark on, for an interface to act on; it changes nothing in a query.
    /// </summary>
    public bool Sensitive { get; init; }

    /// <summary>
    /// Whether the per-case statistics store the field's most frequent values (YAML <c>top_values: false</c> turns
    /// it off: a request target can carry a token). Only keyword, int, ip and extras fields have them at all.
    /// </summary>
    public bool TopValues { get; init; } = true;

    /// <summary>
    /// The test for "this row has a value in the field", or null when every row has one. A string is a value
    /// unless empty, an address unless <c>::</c>, a number unless it is 0 in a column the catalog marks
    /// (<see cref="ZeroIsAbsent"/>); the columns are not nullable, so a source without the field stores exactly
    /// those. One rule for the query verbs and for the field statistics.
    /// </summary>
    /// <param name="unspecifiedAddress">The SQL of the string <c>::</c>: a bound parameter in a query, a literal in the statistics.</param>
    public static string? HasValue(FieldType type, bool zeroIsAbsent, string expression, string unspecifiedAddress) => type switch
    {
        FieldType.Keyword or FieldType.Verbatim or FieldType.Text or FieldType.Extra => $"notEmpty({expression})",
        FieldType.Ip => $"{expression} != toIPv6({unspecifiedAddress})",
        FieldType.Number when zeroIsAbsent => $"{expression} != 0",
        _ => null,
    };

    /// <summary>The SQL expression that reads the field from <c>events_v</c> (an extras lookup takes a parameter name for the key).</summary>
    public string Expression(string? keyParameter = null) =>
        Column ?? (keyParameter is null ? throw new ArgumentNullException(nameof(keyParameter)) : $"extras[{{{keyParameter}:String}}]");
}

/// <summary>
/// The field catalog: canonical names, aliases and types, loaded once from the embedded YAML. Resolution is
/// case-insensitive on names and aliases; an unlisted name resolves to an extras key of that name (the SPL contract:
/// "unknown field -> extras[name]"), flagged so the transpiler can warn.
/// </summary>
public sealed class FieldCatalog
{
    private static readonly Lazy<FieldCatalog> EmbeddedLazy = new(LoadEmbedded);

    private readonly Dictionary<string, FieldDef> _byName;

    private FieldCatalog(IReadOnlyList<FieldDef> fields)
    {
        Fields = fields;
        _byName = new Dictionary<string, FieldDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
        {
            Add(f.Name, f);
            foreach (var alias in f.Aliases)
            {
                Add(alias, f);
            }
        }

        void Add(string key, FieldDef f)
        {
            if (!_byName.TryAdd(key, f))
            {
                throw new InvalidDataException($"field catalog: '{key}' names both {_byName[key].Name} and {f.Name}");
            }
        }
    }

    public static FieldCatalog Embedded => EmbeddedLazy.Value;

    /// <summary>Every listed field, in file order.</summary>
    public IReadOnlyList<FieldDef> Fields { get; }

    /// <summary>Parses a catalog from YAML text (the embedded file, or a test's).</summary>
    public static FieldCatalog Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var file = deserializer.Deserialize<CatalogFile>(yaml) ?? throw new InvalidDataException("field catalog: empty");
        if (file.Version != 1)
        {
            throw new InvalidDataException($"field catalog: version {file.Version} is not 1");
        }

        var fields = new List<FieldDef>();
        foreach (var e in file.Fields ?? [])
        {
            if (string.IsNullOrWhiteSpace(e.Name))
            {
                throw new InvalidDataException("field catalog: a field without a name");
            }

            if (!Names.IsCatalogName(e.Name))
            {
                // A catalog name is written into the SQL as an identifier; nothing else from outside the code is.
                throw new InvalidDataException($"field catalog: '{e.Name}' is not a field name (letters, digits, '_' and '.', and not of the form c<number>)");
            }

            var type = (e.Type ?? "").ToLowerInvariant() switch
            {
                "time" => FieldType.Time,
                "keyword" => FieldType.Keyword,
                "string" => FieldType.Verbatim,
                "int" => FieldType.Number,
                "ip" => FieldType.Ip,
                "text" => FieldType.Text,
                "extra" => FieldType.Extra,
                _ => throw new InvalidDataException($"field catalog: {e.Name} has unknown type '{e.Type}'"),
            };

            var hasColumn = !string.IsNullOrWhiteSpace(e.Column);
            var hasExtra = !string.IsNullOrWhiteSpace(e.Extra);
            if (hasColumn == hasExtra)
            {
                throw new InvalidDataException($"field catalog: {e.Name} needs exactly one of column or extra");
            }

            if (hasExtra != (type == FieldType.Extra))
            {
                throw new InvalidDataException($"field catalog: {e.Name}: extras-backed fields have type extra and column-backed fields do not");
            }

            if (e.ZeroIsAbsent && type != FieldType.Number)
            {
                throw new InvalidDataException($"field catalog: {e.Name}: zero_is_absent applies to int fields only");
            }

            fields.Add(new FieldDef(e.Name, hasColumn ? e.Column : null, hasExtra ? e.Extra : null, type, e.Description ?? "", [.. e.Aliases ?? []]) { ZeroIsAbsent = e.ZeroIsAbsent, Sensitive = e.Sensitive, TopValues = e.TopValues ?? true });
        }

        return new FieldCatalog(fields);
    }

    /// <summary>The field a name or alias denotes, or an unknown extras-backed field of that name.</summary>
    public FieldDef Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _byName.TryGetValue(name, out var f) ? f : new FieldDef(name, null, name, FieldType.Extra, "", []) { IsUnknown = true };
    }

    /// <summary>True when the catalog lists <paramref name="name"/> as a field or alias.</summary>
    public bool Contains(string name) => _byName.ContainsKey(name);

    private static FieldCatalog LoadEmbedded()
    {
        using var stream = typeof(FieldCatalog).Assembly.GetManifestResourceStream("field-catalog.yaml")
            ?? throw new InvalidOperationException("field-catalog.yaml is not embedded");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private sealed class CatalogFile
    {
        public int Version { get; set; }

        public List<CatalogEntry>? Fields { get; set; }
    }

    private sealed class CatalogEntry
    {
        public string? Name { get; set; }

        public string? Column { get; set; }

        public string? Extra { get; set; }

        public string? Type { get; set; }

        public string? Description { get; set; }

        public bool ZeroIsAbsent { get; set; }

        public bool Sensitive { get; set; }

        public bool? TopValues { get; set; }

        public List<string>? Aliases { get; set; }
    }
}
