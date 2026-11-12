using System.Numerics;

namespace Urlog.Bench.Fixtures;

/// <summary>
/// xoshiro256** seeded through splitmix64. Implemented here rather than using <see cref="Random"/> so a
/// seed produces the same byte stream on every runtime version and platform: same seed = same fixture forever.
/// </summary>
public sealed class Xoshiro256
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256(ulong seed)
    {
        var x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    public ulong NextUInt64()
    {
        var result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform integer in [0, maxExclusive). Fixture quality, not cryptographic.</summary>
    public int Next(int maxExclusive) => (int)((NextUInt64() >> 33) % (ulong)maxExclusive);

    /// <summary>Uniform integer in [min, maxExclusive).</summary>
    public int Next(int min, int maxExclusive) => min + Next(maxExclusive - min);

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public bool Chance(double probability) => NextDouble() < probability;

    /// <summary>Index into a pool, heavily skewed towards the low indices (a few hot values, a long tail).</summary>
    public int NextSkewed(int poolSize)
    {
        var r = NextDouble();
        return (int)(poolSize * r * r * r);
    }

    /// <summary>Picks an index by integer weights.</summary>
    public int NextWeighted(ReadOnlySpan<int> weights)
    {
        var total = 0;
        foreach (var w in weights)
        {
            total += w;
        }

        var pick = Next(total);
        for (var i = 0; i < weights.Length; i++)
        {
            pick -= weights[i];
            if (pick < 0)
            {
                return i;
            }
        }

        return weights.Length - 1;
    }

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        var z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
