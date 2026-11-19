using System.Text.Json;
using Urlog.Core.Detection;
using Urlog.Core.Sources;

namespace Urlog.Core.Metadata;

/// <summary>
/// What the settings operations need to know about the registered sources: which ids exist, which read the zone
/// and the year of a file, and the key a detection record made by this build carries.
/// </summary>
public sealed class SourceRules
{
    private readonly Dictionary<string, SourceInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    public SourceRules(SourceCatalog catalog, Func<string, IReadOnlyDictionary<string, string>, string?>? checkOptions = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var ids = new List<string>();
        foreach (var registration in catalog.All)
        {
            var info = registration.Factory().Info;
            ids.Add(info.Id);
            _byName[registration.Id] = info;
            foreach (var alias in registration.Aliases)
            {
                _byName[alias] = info;
            }
        }

        RegistryKey = DetectionResult.RegistryKeyOf(FormatDetector.Version, ids);
        CheckOptions = checkOptions;
    }

    /// <summary>The key of a detection record this build would write.</summary>
    public string RegistryKey { get; }

    /// <summary>
    /// Says what is wrong with a set of options (the key is named in the text), or null. Given the option set, and
    /// called for a file's own options and for a host's defaults alike.
    /// </summary>
    public Func<string, IReadOnlyDictionary<string, string>, string?>? CheckOptions { get; }

    /// <summary>The source's own id for an id or an alias; false when no such source is registered.</summary>
    public bool TryResolve(string idOrAlias, out string id)
    {
        if (!string.IsNullOrWhiteSpace(idOrAlias) && _byName.TryGetValue(idOrAlias.Trim(), out var info))
        {
            id = info.Id;
            return true;
        }

        id = string.Empty;
        return false;
    }

    public IReadOnlyList<string> Ids => [.. _byName.Values.Select(i => i.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>True when the source reads times without a zone, so the file's zone and year change its rows. An unknown source is taken to.</summary>
    public bool ReadsZone(string? sourceId) => sourceId is null || !_byName.TryGetValue(sourceId, out var info) || info.NaiveTimestamps;

    /// <summary>The source a parse of the file runs today: the override, else what the detector chose, else the fallback.</summary>
    public static string EffectiveSource(EvidenceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var id = file.OverrideSource ?? file.DetectedSource;
        return string.IsNullOrWhiteSpace(id) ? FormatDetector.GenericSourceId : id;
    }

    /// <summary>
    /// The source an option belongs to: the text before the first dot of its key (<c>nginx.log_format</c> is
    /// read by <c>nginx</c>).
    /// </summary>
    public static string OptionOwner(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return dot <= 0 ? key : key[..dot];
    }
}

/// <summary>Source options as they are stored: a JSON object of text values with its keys in ordinal order, or null for none.</summary>
public static class SourceOptionSet
{
    public const int MaxKeys = 32;
    public const int MaxKeyLength = 64;
    public const int MaxValueLength = 4096;

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The stored text as a dictionary; empty for null. A non-text value is taken as its JSON text.</summary>
    /// <exception cref="FormatException">The text is not a JSON object.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                options[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
            }

            return options;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new FormatException("source options are not a JSON object", e);
        }
    }

    /// <summary>Null for no options, else the object with its keys in ordinal order, so equal sets are equal texts.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null || options.Count == 0)
        {
            return null;
        }

        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in options)
        {
            sorted[key] = value;
        }

        return JsonSerializer.Serialize(sorted);
    }

    /// <summary>What a parse uses: every option of the host, with the file's own value where the file has one.</summary>
    public static IReadOnlyDictionary<string, string> Effective(string? fileJson, string? hostJson)
    {
        var host = Parse(hostJson);
        var file = Parse(fileJson);
        if (host.Count == 0)
        {
            return file;
        }

        var merged = new Dictionary<string, string>(host, StringComparer.Ordinal);
        foreach (var (key, value) in file)
        {
            merged[key] = value;
        }

        return merged;
    }

    /// <summary>Refuses a set that is too large, has an empty or oversized key or value, or a control character.</summary>
    /// <exception cref="ArgumentException">Names the key.</exception>
    public static void Check(IReadOnlyDictionary<string, string>? options, SourceRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (options is null)
        {
            return;
        }

        if (options.Count > MaxKeys)
        {
            throw new ArgumentException($"options: at most {MaxKeys} keys");
        }

        foreach (var (key, value) in options)
        {
            if (key.Length is 0 or > MaxKeyLength || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            {
                throw new ArgumentException($"options: '{key}' is not an option key (letters, digits, '.', '_' and '-', at most {MaxKeyLength} characters)");
            }

            if (value is null || value.Length is 0 or > MaxValueLength || value.Any(char.IsControl))
            {
                throw new ArgumentException($"options: the value of '{key}' is empty, longer than {MaxValueLength} characters or holds a control character");
            }

            if (!rules.TryResolve(SourceRules.OptionOwner(key), out _))
            {
                throw new ArgumentException($"options: '{key}' names no registered source; known: {string.Join(", ", rules.Ids)}");
            }
        }

        if (options.Count > 0 && rules.CheckOptions is { } check)
        {
            foreach (var owner in options.Keys.Select(SourceRules.OptionOwner).Distinct(StringComparer.Ordinal))
            {
                if (rules.TryResolve(owner, out var id) && check(id, options) is { } reason)
                {
                    throw new ArgumentException("options: " + reason);
                }
            }
        }
    }
}
