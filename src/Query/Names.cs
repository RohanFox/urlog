namespace Urlog.Query;

/// <summary>
/// The rule for a name the analyst invents (an alias after <c>as</c>): an ASCII letter or underscore, then ASCII
/// letters, digits and underscores, at most <see cref="MaxLength"/> characters. Such a name labels a result column;
/// it is never written into the SQL text, where every output column that is not a catalog field carries a
/// generated alias (<see cref="Generated"/>).
/// </summary>
public static class Names
{
    public const int MaxLength = 64;

    public static bool IsStrict(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Length > MaxLength || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The alias of the n-th generated output column: <c>c0</c>, <c>c1</c>, ...</summary>
    public static string Generated(int index) => "c" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True for a name of the generated form, which the catalog must never use.</summary>
    public static bool IsGenerated(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length > 1 && name[0] == 'c' && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>
    /// The rule for a catalog field name, the only other text that is written inside backquotes: the strict rule
    /// plus dots, and never the generated form.
    /// </summary>
    public static bool IsCatalogName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length <= MaxLength && !IsGenerated(name) && IsStrict(name.Replace(".", "_", StringComparison.Ordinal)) && name[0] != '.';
    }
}
