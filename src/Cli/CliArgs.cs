using System.Globalization;

namespace Urlog.Cli;

/// <summary>
/// Minimal argument parsing: <c>--name value</c> or <c>--name=value</c> options, <c>--flag</c> switches, the rest
/// positional. Values come from the command line or, for the connection, from the <c>URLOG_CH_*</c> environment
/// (the same variables the benches read), with the compose.dev.yml defaults last.
/// </summary>
internal sealed class CliArgs
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "slo", "cold", "sql", "explain", "help", "quiet", "json", "head", "admin", "password-stdin", "once", "small-box", "allow-truncated", "two-person", "no-sweep", "no-stats" };

    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public CliArgs(IReadOnlyList<string> args, int start)
    {
        for (var i = start; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                Positional.Add(a);
                continue;
            }

            var name = a[2..];
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                _options[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            if (Flags.Contains(name))
            {
                _flags.Add(name);
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new CliException($"--{name} needs a value");
            }

            _options[name] = args[++i];
        }
    }

    public List<string> Positional { get; } = [];

    public bool Flag(string name) => _flags.Contains(name);

    public string? Option(string name) => _options.TryGetValue(name, out var v) ? v : null;

    public string Required(string name) => Option(name) ?? throw new CliException($"--{name} is required");

    public ulong ULong(string name, ulong fallback)
    {
        var v = Option(name);
        if (v is null)
        {
            return fallback;
        }

        return ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new CliException($"--{name} must be an unsigned integer, got '{v}'");
    }

    public int Int(string name, int fallback)
    {
        var v = Option(name);
        if (v is null)
        {
            return fallback;
        }

        return int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : throw new CliException($"--{name} must be an integer, got '{v}'");
    }

    public string Env(string name, string variable, string fallback) => Option(name) ?? Environment.GetEnvironmentVariable(variable) ?? fallback;
}

internal sealed class CliException(string message) : Exception(message);
