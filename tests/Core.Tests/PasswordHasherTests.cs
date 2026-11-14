using System.Diagnostics;
using System.Text;
using Urlog.Core.Auth;

namespace Urlog.Core.Tests;

/// <summary>
/// The hasher is pinned by two outside answers (RFC 9106 §5.3 and the reference implementation's test.c), so a library
/// bump that moves the output fails here, not at a login. The timing floor: a verify at the pinned
/// parameters must cost at least 50 ms on the dev box.
/// </summary>
public sealed class PasswordHasherTests
{
    [Fact]
    public async Task Rfc9106Vector()
    {
        // RFC 9106 §5.3: Argon2id v19, m = 32 KiB, t = 3, p = 4, 32-byte tag, password 32 × 0x01, salt 16 × 0x02,
        // secret 8 × 0x03, associated data 12 × 0x04.
        var password = Enumerable.Repeat((byte)1, 32).ToArray();
        var salt = Enumerable.Repeat((byte)2, 16).ToArray();
        var secret = Enumerable.Repeat((byte)3, 8).ToArray();
        var ad = Enumerable.Repeat((byte)4, 12).ToArray();
        var tag = await PasswordHasher.DeriveAsync(password, salt, new Argon2Parameters(32, 3, 4), 32, secret, ad);
        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659", Convert.ToHexStringLower(tag));
    }

    [Fact]
    public void ReferenceImplementationString()
    {
        // phc-winner-argon2 src/test.c: hashtest(version 0x13, t = 2, m = 2^16, p = 1, "password", "somesalt", ...).
        const string expected = "$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc";
        var encoded = PasswordHasher.Hash("password", Encoding.ASCII.GetBytes("somesalt"), new Argon2Parameters(65536, 2, 1));
        Assert.Equal(expected, encoded);
        Assert.True(PasswordHasher.Verify(expected, "password"));
        Assert.False(PasswordHasher.Verify(expected, "Password"));
        // Two passes is below the pinned three: the next successful login re-hashes.
        Assert.True(PasswordHasher.NeedsRehash(expected));
    }

    [Fact]
    public void RoundTripAtThePinnedParameters()
    {
        const string password = "correct horse battery staple";
        var a = PasswordHasher.Hash(password);
        var b = PasswordHasher.Hash(password);
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify(a, password));
        Assert.True(PasswordHasher.Verify(b, password));
        Assert.False(PasswordHasher.Verify(a, "correct horse battery staples"));
        Assert.False(PasswordHasher.NeedsRehash(a));
        Assert.True(PasswordHasher.TryParse(a, out var parsed));
        Assert.Equal(PasswordHasher.Pinned, parsed.Parameters);
        Assert.Equal(16, parsed.Salt.Length);
        Assert.Equal(32, parsed.Tag.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("argon2id$placeholder")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    [InlineData("$argon2id$v=16$m=65536,t=3,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    [InlineData("$argon2id$v=19$m=65536,t=3$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    [InlineData("$argon2id$v=19$m=0,t=3,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$c29tZXNhbHQ=$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$c29tZXNhbHQ$not*base64")]
    public void MalformedStringsNeverVerifyAndAlwaysNeedARehash(string encoded)
    {
        Assert.False(PasswordHasher.TryParse(encoded, out _));
        Assert.False(PasswordHasher.Verify(encoded, "password"));
        Assert.True(PasswordHasher.NeedsRehash(encoded));
    }

    [Fact]
    public void ATamperedTagOrParameterDoesNotVerify()
    {
        const string good = "$argon2id$v=19$m=65536,t=2,p=1$c29tZXNhbHQ$CTFhFdXPJO1aFaMaO6Mm5c8y7cJHAph8ArZWb2GRPPc";
        // A middle character of the tag: the last one's low bits are padding and decode to the same bytes.
        Assert.False(PasswordHasher.Verify(good.Replace("CTFhFd", "CTFhFe", StringComparison.Ordinal), "password"));
        Assert.False(PasswordHasher.Verify(good.Replace("t=2", "t=3", StringComparison.Ordinal), "password"));
        Assert.False(PasswordHasher.Verify(good.Replace("m=65536", "m=32768", StringComparison.Ordinal), "password"));
        Assert.False(PasswordHasher.Verify(good, ""));
    }

    [Fact]
    public void NeedsRehashBelowThePinnedParameters()
    {
        // OWASP's floor, 19 MiB at two passes: valid, verifiable, and due for a re-hash.
        var low = PasswordHasher.Hash("correct horse battery staple", Encoding.ASCII.GetBytes("sixteen byte salt"), new Argon2Parameters(19456, 2, 1));
        Assert.True(PasswordHasher.Verify(low, "correct horse battery staple"));
        Assert.True(PasswordHasher.NeedsRehash(low));
    }

    [Fact]
    public void TheDummyHashIsAtThePinnedParametersAndMatchesNothing()
    {
        var dummy = PasswordHasher.DummyHash;
        Assert.Same(dummy, PasswordHasher.DummyHash);
        Assert.False(PasswordHasher.NeedsRehash(dummy));
        Assert.False(PasswordHasher.Verify(dummy, ""));
        Assert.False(PasswordHasher.Verify(dummy, "password"));
    }

    [Fact]
    public void AVerifyCostsAtLeast50MsAndAtMostASecond()
    {
        var encoded = PasswordHasher.Hash("correct horse battery staple");
        var samples = new List<double>(5);
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            Assert.True(PasswordHasher.Verify(encoded, "correct horse battery staple"));
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[2];
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "hasher.timing"), $"verify at 64 MiB / t=3 / p=1: median {median:F0} ms of 5 (min {samples[0]:F0}, max {samples[4]:F0})" + Environment.NewLine);
        Assert.InRange(median, 50, 1000);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("elevenchars", false)]
    [InlineData("twelve chars", true)]
    [InlineData("äöü ñ 日本語 😀 ok", true)]
    public void PolicyIsALengthFloorAndCeiling(string? password, bool ok)
    {
        Assert.Equal(ok, PasswordPolicy.Check(password) is null);
        Assert.Null(PasswordPolicy.Check(new string('x', 256)));
        Assert.NotNull(PasswordPolicy.Check(new string('x', 257)));
    }
}
