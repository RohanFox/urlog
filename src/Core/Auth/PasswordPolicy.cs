namespace Urlog.Core.Auth;

/// <summary>
/// What a password must look like (NIST SP 800-63B): a length floor and ceiling, no composition rules, nothing trimmed.
/// One place, used by the API's user endpoints and by <c>urlog user</c>.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;

    /// <summary>A ceiling so a request cannot make the hasher pre-hash megabytes; well above any password a person types.</summary>
    public const int MaxLength = 256;

    /// <summary>Null when <paramref name="password"/> is acceptable, otherwise the reason in the words the API and the CLI show.</summary>
    public static string? Check(string? password)
    {
        if (password is null || password.Length < MinLength)
        {
            return $"the password must be at least {MinLength} characters";
        }

        if (password.Length > MaxLength)
        {
            return $"the password must be at most {MaxLength} characters";
        }

        return null;
    }

    /// <exception cref="ArgumentException">The password fails <see cref="Check"/>.</exception>
    public static void Require(string? password, string paramName = "password")
    {
        if (Check(password) is { } reason)
        {
            throw new ArgumentException(reason, paramName);
        }
    }
}
