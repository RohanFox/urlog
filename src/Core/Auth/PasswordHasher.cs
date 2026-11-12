using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Urlog.Core.Auth;

/// <summary>The three cost parameters of an Argon2id hash (RFC 9106): memory in KiB, passes, lanes.</summary>
public readonly record struct Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism);

/// <summary>A parsed PHC string: the parameters, the salt and the tag.</summary>
public sealed record Argon2Hash(Argon2Parameters Parameters, byte[] Salt, byte[] Tag);

/// <summary>
/// Argon2id password hashing for the local accounts, shared by the API and <c>urlog user</c>. The parameters are
/// constants, not configuration: lowering them is a code change with a test to pass. Stored form is the PHC string the
/// reference <c>argon2</c> CLI and libsodium write, <c>$argon2id$v=19$m=65536,t=3,p=1$&lt;salt&gt;$&lt;tag&gt;</c> (base64
/// without padding), so a stored hash can be checked with the reference tool and the parameters can be raised later:
/// <see cref="Verify"/> reads them from the string and <see cref="NeedsRehash"/> says when they are below the pinned ones.
/// The implementation is Konscious.Security.Cryptography.Argon2 1.3.1 (managed, no native artifact), pinned by the
/// RFC 9106 section 5.3 vector and a string from the reference implementation's test.c in tests/Core.Tests.
/// </summary>
public static class PasswordHasher
{
    /// <summary>64 MiB. OWASP's floor is 19 MiB at two passes; libsodium's interactive profile is 64 MiB at two.</summary>
    public const int MemoryKiB = 65536;

    public const int Iterations = 3;

    /// <summary>One lane: one thread per login, 1-3 analysts.</summary>
    public const int Parallelism = 1;

    public const int SaltBytes = 16;

    public const int TagBytes = 32;

    /// <summary>Argon2 version 1.3, written as <c>v=19</c>.</summary>
    public const int Version = 19;

    public static Argon2Parameters Pinned { get; } = new(MemoryKiB, Iterations, Parallelism);

    private static readonly Lazy<string> DummyLazy = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A hash of a random password at the pinned parameters, computed once per process. A login for a name that does not
    /// exist verifies the submitted password against this, so the response time does not say whether the name exists.
    /// </summary>
    public static string DummyHash => DummyLazy.Value;

    /// <summary>Hashes <paramref name="password"/> with a fresh random salt at the pinned parameters.</summary>
    public static string Hash(string password) => Hash(password, RandomNumberGenerator.GetBytes(SaltBytes), Pinned);

    /// <inheritdoc cref="Hash(string)"/>
    public static Task<string> HashAsync(string password) => HashAsync(password, RandomNumberGenerator.GetBytes(SaltBytes), Pinned);

    /// <summary>Hashes with a given salt and parameters (tests pin vectors through this; production uses <see cref="Hash(string)"/>).</summary>
    public static string Hash(string password, byte[] salt, Argon2Parameters parameters) => HashAsync(password, salt, parameters).GetAwaiter().GetResult();

    /// <inheritdoc cref="Hash(string, byte[], Argon2Parameters)"/>
    public static async Task<string> HashAsync(string password, byte[] salt, Argon2Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        var tag = await DeriveAsync(PasswordBytes(password), salt, parameters, TagBytes, secret: null, associatedData: null).ConfigureAwait(false);
        return Encode(parameters, salt, tag);
    }

    /// <summary>True when <paramref name="password"/> is the one behind <paramref name="encoded"/>; false for a wrong password or a malformed string.</summary>
    public static bool Verify(string encoded, string password) => VerifyAsync(encoded, password).GetAwaiter().GetResult();

    /// <inheritdoc cref="Verify"/>
    public static async Task<bool> VerifyAsync(string encoded, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryParse(encoded, out var parsed))
        {
            return false;
        }

        var tag = await DeriveAsync(PasswordBytes(password), parsed.Salt, parsed.Parameters, parsed.Tag.Length, secret: null, associatedData: null).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(tag, parsed.Tag);
    }

    /// <summary>True when the stored string is not at the pinned parameters (or cannot be read): the next successful login re-hashes.</summary>
    public static bool NeedsRehash(string encoded) =>
        !TryParse(encoded, out var parsed) || parsed.Parameters != Pinned || parsed.Salt.Length != SaltBytes || parsed.Tag.Length != TagBytes;

    /// <summary>Reads a PHC string. False for anything that is not an <c>argon2id</c> version 19 string with the five fields.</summary>
    public static bool TryParse(string? encoded, out Argon2Hash parsed)
    {
        parsed = null!;
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        // $argon2id$v=19$m=65536,t=3,p=1$<salt>$<tag>
        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id" || parts[2] != "v=" + Version.ToString(CultureInfo.InvariantCulture))
        {
            return false;
        }

        var costs = parts[3].Split(',');
        if (costs.Length != 3 || !TryCost(costs[0], "m=", out var m) || !TryCost(costs[1], "t=", out var t) || !TryCost(costs[2], "p=", out var p))
        {
            return false;
        }

        if (!TryBase64(parts[4], out var salt) || !TryBase64(parts[5], out var tag) || salt.Length < 8 || tag.Length < 4)
        {
            return false;
        }

        parsed = new Argon2Hash(new Argon2Parameters(m, t, p), salt, tag);
        return true;

        static bool TryCost(string field, string prefix, out int value)
        {
            value = 0;
            return field.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(field.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value > 0;
        }
    }

    /// <summary>The raw function, parameters and optional secret and associated data included, for the RFC 9106 vector.</summary>
    internal static async Task<byte[]> DeriveAsync(byte[] password, byte[] salt, Argon2Parameters parameters, int tagBytes, byte[]? secret, byte[]? associatedData)
    {
        if (parameters.MemoryKiB < 8 * parameters.Parallelism || parameters.Iterations < 1 || parameters.Parallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), parameters, "Argon2id needs m ≥ 8p, t ≥ 1, p ≥ 1");
        }

        using var argon2 = new Argon2id(password)
        {
            Salt = salt,
            MemorySize = parameters.MemoryKiB,
            Iterations = parameters.Iterations,
            DegreeOfParallelism = parameters.Parallelism,
            KnownSecret = secret,
            AssociatedData = associatedData,
        };
        return await argon2.GetBytesAsync(tagBytes).ConfigureAwait(false);
    }

    internal static string Encode(Argon2Parameters parameters, byte[] salt, byte[] tag) =>
        string.Create(CultureInfo.InvariantCulture, $"$argon2id$v={Version}$m={parameters.MemoryKiB},t={parameters.Iterations},p={parameters.Parallelism}${Base64(salt)}${Base64(tag)}");

    /// <summary>UTF-8 as typed. The library refuses an empty password; one zero byte keeps the cost uniform and can match nothing the policy allows.</summary>
    private static byte[] PasswordBytes(string password) => password.Length == 0 ? [0] : Encoding.UTF8.GetBytes(password);

    /// <summary>Standard base64 alphabet without padding, as the reference implementation writes it.</summary>
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryBase64(string field, out byte[] bytes)
    {
        bytes = [];
        if (field.Length == 0 || field.Contains('=', StringComparison.Ordinal))
        {
            return false;
        }

        var padded = field.Length % 4 == 0 ? field : field + new string('=', 4 - field.Length % 4);
        var buffer = new byte[padded.Length / 4 * 3];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }
}
