using Urlog.Sources.Nginx;

namespace Urlog.Api.Endpoints;

/// <summary>
/// What each source accepts as options, for an override and for a host's defaults. An option a source does not
/// know is refused, so a misspelt key is not stored and then silently ignored by every parse.
/// </summary>
internal static class SourceOptionChecks
{
    public const string NginxLogFormat = "nginx.log_format";

    /// <summary>What is wrong with the options the source <paramref name="sourceId"/> would read, or null.</summary>
    public static string? Check(string sourceId, IReadOnlyDictionary<string, string> options)
    {
        var prefix = sourceId + ".";
        foreach (var (key, value) in options)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(sourceId, "nginx", StringComparison.OrdinalIgnoreCase))
            {
                return $"'{key}': the source '{sourceId}' takes no options";
            }

            if (!string.Equals(key, NginxLogFormat, StringComparison.Ordinal))
            {
                return $"'{key}': the source 'nginx' takes {NginxLogFormat} and nothing else";
            }

            if (NginxLayout.BuiltIn(value.Trim()) is null && !NginxLayout.TryCompile(value, out _, out var reason))
            {
                return $"{NginxLogFormat}: {reason}";
            }
        }

        return null;
    }
}
