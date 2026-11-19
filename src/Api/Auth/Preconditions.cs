using System.Globalization;
using System.Text.Json;
using Urlog.Core.Metadata;

namespace Urlog.Api.Auth;

/// <summary>
/// The version a change names (design record, "Cases, hosts, files and jobs API"). A host and a file carry a
/// version that every change a person makes raises; a change sends the version it was made against in
/// <c>If-Match</c>, and one that names an older version is refused (412), so of two people who edit the same record
/// from the same view one is told. A bulk change names the digest of its selection the same way. A change that
/// names nothing is refused too (428): a client cannot overwrite unseen by leaving the header out.
/// </summary>
internal static class Preconditions
{
    /// <summary>The <c>ETag</c> text of a version.</summary>
    public static string Tag(int version) => string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");

    /// <summary>The header's value without quotes and weakness mark; null when the header is absent or <c>*</c>.</summary>
    public static string? IfMatch(HttpContext http)
    {
        var raw = http.Request.Headers.IfMatch.ToString().Trim();
        if (raw.Length == 0 || raw == "*")
        {
            return null;
        }

        if (raw.StartsWith("W/", StringComparison.Ordinal))
        {
            raw = raw[2..];
        }

        return raw.Trim('"');
    }

    /// <summary>The version in <c>If-Match</c>.</summary>
    /// <exception cref="PreconditionRequiredException">No header.</exception>
    /// <exception cref="StaleWriteException">The header is not a version number: it names no version this record ever had.</exception>
    public static int Version(HttpContext http, string what)
    {
        var text = IfMatch(http) ?? throw new PreconditionRequiredException($"send the version of {what} you are changing");
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : throw new StaleWriteException(what, text, "a number");
    }

    /// <summary>The digest in <c>If-Match</c> of a confirmed bulk change.</summary>
    /// <exception cref="PreconditionRequiredException">No header.</exception>
    public static string Digest(HttpContext http) =>
        IfMatch(http) ?? throw new PreconditionRequiredException("send the digest the unconfirmed call answered with");

    /// <summary>True for <c>?confirm=true</c>: the caller has seen what the change costs.</summary>
    public static bool Confirmed(HttpContext http) =>
        http.Request.Query.TryGetValue("confirm", out var value) && string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A change to a versioned record arrived without <c>If-Match</c>.</summary>
public sealed class PreconditionRequiredException : Exception
{
    public PreconditionRequiredException()
    {
    }

    public PreconditionRequiredException(string message)
        : base(message)
    {
    }

    public PreconditionRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads the members of a JSON body that may be absent, null or set, which a bound record cannot tell apart.</summary>
internal static class Body
{
    public static JsonElement Object(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object ? body : throw new ArgumentException("the body must be a JSON object");

    public static bool Has(JsonElement body, string name) => body.TryGetProperty(name, out _);

    /// <summary>A text member: absent is not set; null is set to null; anything but text is refused.</summary>
    public static Change<string?> Text(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value))
        {
            return default;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => Change.To<string?>(null),
            JsonValueKind.String => Change.To(value.GetString()),
            _ => throw new ArgumentException($"{name}: text or null"),
        };
    }

    public static string? OptionalText(JsonElement body, string name) => Text(body, name).Value;

    public static string RequiredText(JsonElement body, string name) =>
        Text(body, name) is { IsSet: true, Value: { Length: > 0 } text } ? text : throw new ArgumentException($"{name}: required");

    public static Change<int?> Number(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value))
        {
            return default;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => Change.To<int?>(null),
            JsonValueKind.Number when value.TryGetInt32(out var number) => Change.To<int?>(number),
            _ => throw new ArgumentException($"{name}: a whole number or null"),
        };
    }

    public static long? OptionalLong(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw new ArgumentException($"{name}: a whole number");
    }

    public static bool Flag(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            _ => throw new ArgumentException($"{name}: true or false"),
        };
    }

    /// <summary>A member that is an object of text values (source options): absent is not set, null is set to none.</summary>
    public static Change<IReadOnlyDictionary<string, string>?> Options(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value))
        {
            return default;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return Change.To<IReadOnlyDictionary<string, string>?>(null);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{name}: an object of option names and text values, or null");
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            options[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()!
                : throw new ArgumentException($"{name}: the value of '{property.Name}' must be text");
        }

        return Change.To<IReadOnlyDictionary<string, string>?>(options);
    }

    public static IReadOnlyList<long>? Ids(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"{name}: a list of ids");
        }

        var ids = new List<long>();
        foreach (var item in value.EnumerateArray())
        {
            ids.Add(item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var id) ? id : throw new ArgumentException($"{name}: a list of ids"));
        }

        return ids;
    }
}
