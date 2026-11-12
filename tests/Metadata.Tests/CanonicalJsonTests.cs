using System.Text;
using Urlog.Core.Audit;

namespace Urlog.Metadata.Tests;

/// <summary>
/// Canonical JSON pinned by vectors: every rule of <see cref="CanonicalJson"/> as exact bytes, and three
/// known-answer rows whose canonical bytes and SHA-256 were computed outside .NET (Python's json and hashlib:
/// <c>CanonicalJsonKat.py</c> beside this file, run with <c>python -I</c>) so a runtime upgrade that moves any of this fails here, not in the chain.
/// </summary>
public sealed class CanonicalJsonTests
{
    private const string GenesisHash = "3349c89c4b73c1c4342dbd1a7ab217dfaadc048f59eed494202beada326d4d2b";
    private const string Row2Hash = "06ed137472cec76a4a330746958377f20e24637a4016707a2430b7fed3b6a4d2";
    private const string Row3Hash = "110b9b16670fd6189c3c28808bfca720a20afde4d57678386c763b4e4e63b724";

    private static readonly DateTime GenesisTs = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Row2Ts = new DateTime(2026, 11, 6, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234560);
    private static readonly DateTime Row3Ts = new DateTime(2026, 11, 6, 12, 34, 57, DateTimeKind.Utc).AddTicks(10);

    public static TheoryData<string, object?> Vectors => new()
    {
        // Rule 1: ordinal order, uppercase before lowercase; nulls present.
        { """{"B":3,"a":2,"b":1,"n":null}""", new Dictionary<string, object?> { ["b"] = 1, ["a"] = 2, ["B"] = 3, ["n"] = null } },
        // Rule 1: UTF-16 code-unit order, not code-point order: U+1F600 (D83D DE00) sorts before U+FB01.
        { """{"😀":2,"ﬁ":1}""", new Dictionary<string, object?> { ["ﬁ"] = 1, ["😀"] = 2 } },
        // Rule 3: the escape set, lowercase hex; slash and DEL raw.
        { "\"\\u0000\\u001f\\\"\\\\\\n\\r\\t\\b\\f/\u007f\"", "\u0000\u001f\"\\\n\r\t\b\f/\u007f" },
        // Rule 3: U+2028 and U+2029 raw.
        { "\"\u2028\u2029\"", "\u2028\u2029" },
        // Rule 4: integers of every width at their extremes.
        { "[0,-1,-9223372036854775808,9223372036854775807,18446744073709551615,4294967295,-32768,255]", new object?[] { 0, -1, long.MinValue, long.MaxValue, ulong.MaxValue, uint.MaxValue, short.MinValue, byte.MaxValue } },
        // Literals, empty containers, nesting.
        { "[true,false,null,{},[]]", new object?[] { true, false, null, new Dictionary<string, object?>(), Array.Empty<object?>() } },
        { """{"a":[],"z":{"y":[1,"two",null,{"x":false}]}}""", new Dictionary<string, object?> { ["z"] = new Dictionary<string, object?> { ["y"] = new object?[] { 1, "two", null, new Dictionary<string, object?> { ["x"] = false } } }, ["a"] = Array.Empty<object?>() } },
        // Rule 5: a DateTime is the six-digit UTC string, truncated, Local converted, Unspecified taken as UTC.
        { "\"2026-11-06T12:34:56.123456Z\"", new DateTime(2026, 11, 6, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567) },
        { "\"2026-11-06T12:00:00.000000Z\"", new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Unspecified) },
        { "\"1970-01-01T00:00:00.000000Z\"", DateTime.UnixEpoch },
        // A List<T> is an IReadOnlyList<object?> by covariance.
        { "[\"a\",\"b\"]", new List<string> { "a", "b" } },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void VectorsAreExact(string expected, object? value) => Assert.Equal(expected, CanonicalJson.SerializeToString(value));

    [Fact]
    public void NonAsciiIsRawUtf8()
    {
        Assert.Equal("22c3a9e282acf09f988022", Convert.ToHexString(CanonicalJson.Serialize("é€😀")).ToLowerInvariant());
        Assert.Equal("22e280a822", Convert.ToHexString(CanonicalJson.Serialize("\u2028")).ToLowerInvariant());
    }

    [Fact]
    public void LocalTimeIsConvertedToUtc()
    {
        var utc = new DateTime(2026, 11, 6, 12, 34, 56, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        Assert.Equal(CanonicalJson.FormatTimestamp(utc), CanonicalJson.FormatTimestamp(local));
        Assert.Equal(DateTimeKind.Utc, CanonicalJson.TruncateToMicroseconds(local).Kind);
    }

    public static TheoryData<object?> Refused => new()
    {
        1.5,
        1.0f,
        1.0m,
        double.NaN,
        "\ud83d",
        "\ude00x",
        new object(),
        new Dictionary<string, string> { ["a"] = "b" },
        DuplicateKeys(),
    };

    /// <summary>The same key twice, which an ordinary dictionary cannot hold (rule 1's refusal).</summary>
    private static Dictionary<string, object?> DuplicateKeys()
    {
        var d = new Dictionary<string, object?>(new NeverEqual());
        d.Add("a", 1);
        d.Add("a", 2);
        return d;
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void RefusedValuesThrow(object? value) => Assert.Throws<ArgumentException>(() => CanonicalJson.Serialize(value));

    [Fact]
    public void GenesisKnownAnswer()
    {
        var details = CanonicalJson.SerializeToString(new Dictionary<string, object?> { ["tool"] = "urlog 1.0.0.0", ["migration"] = "20261106171503_Initial" });
        Assert.Equal("""{"migration":"20261106171503_Initial","tool":"urlog 1.0.0.0"}""", details);
        var canonical = Encoding.UTF8.GetString(AuditHash.CanonicalRow(1, GenesisTs, "system", null, "genesis", null, null, details, AuditHash.ZeroHash, 1));
        Assert.Equal(
            """{"action":"genesis","actor":"system","actor_id":null,"details_json":"{\"migration\":\"20261106171503_Initial\",\"tool\":\"urlog 1.0.0.0\"}","prev_hash":"0000000000000000000000000000000000000000000000000000000000000000","schema_version":1,"seq":1,"target_id":null,"target_type":null,"ts_utc":"2026-11-06T12:00:00.000000Z"}""",
            canonical);
        Assert.Equal(GenesisHash, AuditHash.Compute(1, GenesisTs, "system", null, "genesis", null, null, details, AuditHash.ZeroHash, 1));
    }

    [Fact]
    public void SecondRowKnownAnswer()
    {
        var details = CanonicalJson.SerializeToString(new Dictionary<string, object?> { ["case_name"] = "incident 1" });
        var canonical = Encoding.UTF8.GetString(AuditHash.CanonicalRow(2, Row2Ts, "alice", 1, "case_create", "case", 1, details, GenesisHash, null));
        Assert.Equal(
            """{"action":"case_create","actor":"alice","actor_id":1,"details_json":"{\"case_name\":\"incident 1\"}","prev_hash":"3349c89c4b73c1c4342dbd1a7ab217dfaadc048f59eed494202beada326d4d2b","schema_version":null,"seq":2,"target_id":1,"target_type":"case","ts_utc":"2026-11-06T12:34:56.123456Z"}""",
            canonical);
        Assert.Equal(Row2Hash, AuditHash.Compute(2, Row2Ts, "alice", 1, "case_create", "case", 1, details, GenesisHash, null));
    }

    [Fact]
    public void UnicodeAndEscapesKnownAnswer()
    {
        // Every escape, raw non-ASCII, raw DEL and U+2028, both integer extremes, inside details; then the whole row.
        var details = CanonicalJson.SerializeToString(new Dictionary<string, object?>
        {
            ["name"] = "é€😀 \u0000\u001f\"\\\n\r\t\b\f/\u007f\u2028",
            ["n"] = long.MinValue,
            ["u"] = ulong.MaxValue,
            ["ok"] = true,
            ["none"] = null,
        });
        Assert.Equal(
            "7b226e223a2d393232333337323033363835343737353830382c226e616d65223a22c3a9e282acf09f9880205c75303030305c75303031665c225c5c5c6e5c725c745c625c662f7fe280a8222c226e6f6e65223a6e756c6c2c226f6b223a747275652c2275223a31383434363734343037333730393535313631357d",
            Convert.ToHexStringLower(Encoding.UTF8.GetBytes(details)));
        var canonical = AuditHash.CanonicalRow(3, Row3Ts, "bob", 2, "file_intake", "file", 7, details, Row2Hash, null);
        Assert.Equal(
            "7b22616374696f6e223a2266696c655f696e74616b65222c226163746f72223a22626f62222c226163746f725f6964223a322c2264657461696c735f6a736f6e223a227b5c226e5c223a2d393232333337323033363835343737353830382c5c226e616d655c223a5c22c3a9e282acf09f9880205c5c75303030305c5c75303031665c5c5c225c5c5c5c5c5c6e5c5c725c5c745c5c625c5c662f7fe280a85c222c5c226e6f6e655c223a6e756c6c2c5c226f6b5c223a747275652c5c22755c223a31383434363734343037333730393535313631357d222c22707265765f68617368223a2230366564313337343732636563373661346133333037343639353833373766323065323436333761343031363730376132343330623766656433623661346432222c22736368656d615f76657273696f6e223a6e756c6c2c22736571223a332c227461726765745f6964223a372c227461726765745f74797065223a2266696c65222c2274735f757463223a22323032362d31312d30365431323a33343a35372e3030303030315a227d",
            Convert.ToHexStringLower(canonical));
        Assert.Equal(Row3Hash, AuditHash.Compute(3, Row3Ts, "bob", 2, "file_intake", "file", 7, details, Row2Hash, null));
    }

    [Fact]
    public void HashNeedsAWellFormedPrevHash()
    {
        Assert.Throws<ArgumentException>(() => AuditHash.Compute(1, GenesisTs, "system", null, "genesis", null, null, "{}", "abc", 1));
        Assert.Throws<ArgumentException>(() => AuditHash.Compute(1, GenesisTs, "system", null, "genesis", null, null, "{}", GenesisHash.ToUpperInvariant(), 1));
        Assert.True(AuditHash.IsHex64(AuditHash.ZeroHash));
        Assert.False(AuditHash.IsHex64(AuditHash.ZeroHash + "0"));
    }

    /// <summary>A comparer under which no two keys are equal, so a dictionary can hold the same key twice (rule 1's refusal).</summary>
    private sealed class NeverEqual : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => false;

        public int GetHashCode(string obj) => obj.Length;
    }
}
