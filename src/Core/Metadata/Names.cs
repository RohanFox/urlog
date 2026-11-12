using System.Text;

namespace Urlog.Core.Metadata;

/// <summary>
/// Every identifier the store creates is lowercase snake_case: it needs no quoting on SQLite or Postgres, and the
/// enum values written as text are the same strings the API will show.
/// </summary>
public static class Names
{
    /// <summary><c>SoftDeleted</c> -> <c>soft_deleted</c>, <c>TsUtc</c> -> <c>ts_utc</c>, <c>Sha256</c> -> <c>sha256</c>.</summary>
    public static string Snake(string pascal)
    {
        ArgumentNullException.ThrowIfNull(pascal);
        var sb = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(pascal[i - 1]) || char.IsDigit(pascal[i - 1]) || (i + 1 < pascal.Length && char.IsLower(pascal[i + 1]))))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>The stored text of an enum value.</summary>
    public static string Of<TEnum>(TEnum value) where TEnum : struct, Enum => Snake(value.ToString());

    /// <summary>The enum value of a stored text; throws on an unknown string (the CHECK constraint makes that a corrupt row).</summary>
    public static TEnum Parse<TEnum>(string text) where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (Of(value) == text)
            {
                return value;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(text), text, $"not a {typeof(TEnum).Name}");
    }

    /// <summary>The SQL of a CHECK constraint over every value of the enum: <c>col IN ('a', 'b')</c>.</summary>
    public static string EnumCheck<TEnum>(string column) where TEnum : struct, Enum =>
        column + " IN (" + string.Join(", ", Enum.GetValues<TEnum>().Select(v => "'" + Of(v) + "'")) + ")";
}
