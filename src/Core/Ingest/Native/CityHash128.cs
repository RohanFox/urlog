using System.Buffers.Binary;

namespace Urlog.Core.Ingest.Native;

/// <summary>
/// CityHash128 as ClickHouse uses it for compressed-frame checksums: Google's CityHash <b>v1.0.2</b>
/// (<c>CityHash_v1_0_2::CityHash128</c>), not the later 1.1 revision, which changed the 128-bit mixing. Every
/// compressed frame on the native protocol starts with this hash over <c>method byte + sizes + compressed data</c>;
/// the server rejects a frame whose checksum does not match, so a wrong port of this fails loudly, never silently.
/// </summary>
public static class CityHash128
{
    private const ulong K0 = 0xc3a5c85c97cb3127UL;
    private const ulong K1 = 0xb492b66fbe98f273UL;
    private const ulong K2 = 0x9ae16a3b2f90404fUL;
    private const ulong K3 = 0xc949d7c7509e6557UL;
    private const ulong KMul = 0x9ddfea08eb382d69UL;

    public static (ulong Low, ulong High) Hash(ReadOnlySpan<byte> s)
    {
        var len = s.Length;
        if (len >= 16)
        {
            return HashWithSeed(s[16..], Fetch64(s) ^ K3, Fetch64(s[8..]));
        }

        if (len >= 8)
        {
            return HashWithSeed(default, Fetch64(s) ^ ((ulong)len * K0), Fetch64(s[(len - 8)..]) ^ K1);
        }

        return HashWithSeed(s, K0, K1);
    }

    private static ulong Fetch64(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt64LittleEndian(p);

    private static uint Fetch32(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt32LittleEndian(p);

    private static ulong Rotate(ulong v, int shift) => shift == 0 ? v : (v >> shift) | (v << (64 - shift));

    private static ulong RotateByAtLeast1(ulong v, int shift) => (v >> shift) | (v << (64 - shift));

    private static ulong ShiftMix(ulong v) => v ^ (v >> 47);

    private static ulong HashLen16(ulong u, ulong v)
    {
        var a = (u ^ v) * KMul;
        a ^= a >> 47;
        var b = (v ^ a) * KMul;
        b ^= b >> 47;
        b *= KMul;
        return b;
    }

    private static ulong HashLen0to16(ReadOnlySpan<byte> s)
    {
        var len = s.Length;
        if (len > 8)
        {
            var a = Fetch64(s);
            var b = Fetch64(s[(len - 8)..]);
            return HashLen16(a, RotateByAtLeast1(b + (ulong)len, len)) ^ b;
        }

        if (len >= 4)
        {
            ulong a = Fetch32(s);
            return HashLen16((ulong)len + (a << 3), Fetch32(s[(len - 4)..]));
        }

        if (len > 0)
        {
            var a = s[0];
            var b = s[len >> 1];
            var c = s[len - 1];
            var y = a + ((uint)b << 8);
            var z = (uint)len + ((uint)c << 2);
            return ShiftMix((y * K2) ^ (z * K3)) * K2;
        }

        return K2;
    }

    private static (ulong, ulong) WeakHashLen32WithSeeds(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rotate(b + a + z, 21);
        var c = a;
        a += x;
        a += y;
        b += Rotate(a, 44);
        return (a + z, b + c);
    }

    private static (ulong, ulong) WeakHashLen32WithSeeds(ReadOnlySpan<byte> s, ulong a, ulong b) =>
        WeakHashLen32WithSeeds(Fetch64(s), Fetch64(s[8..]), Fetch64(s[16..]), Fetch64(s[24..]), a, b);

    private static (ulong, ulong) CityMurmur(ReadOnlySpan<byte> s, ulong seedLow, ulong seedHigh)
    {
        var a = seedLow;
        var b = seedHigh;
        ulong c;
        ulong d;
        var len = s.Length;
        var l = len - 16;
        if (l <= 0)
        {
            a = ShiftMix(a * K1) * K1;
            c = (b * K1) + HashLen0to16(s);
            d = ShiftMix(a + (len >= 8 ? Fetch64(s) : c));
        }
        else
        {
            c = HashLen16(Fetch64(s[(len - 8)..]) + K1, a);
            d = HashLen16(b + (ulong)len, c + Fetch64(s[(len - 16)..]));
            a += d;
            var p = 0;
            do
            {
                a ^= ShiftMix(Fetch64(s[p..]) * K1) * K1;
                a *= K1;
                b ^= a;
                c ^= ShiftMix(Fetch64(s[(p + 8)..]) * K1) * K1;
                c *= K1;
                d ^= c;
                p += 16;
                l -= 16;
            }
            while (l > 0);
        }

        a = HashLen16(a, c);
        b = HashLen16(d, b);
        return (a ^ b, HashLen16(b, a));
    }

    private static (ulong, ulong) HashWithSeed(ReadOnlySpan<byte> s, ulong seedLow, ulong seedHigh)
    {
        var len = s.Length;
        if (len < 128)
        {
            return CityMurmur(s, seedLow, seedHigh);
        }

        var x = seedLow;
        var y = seedHigh;
        var z = (ulong)len * K1;
        var vf = (Rotate(y ^ K1, 49) * K1) + Fetch64(s);
        var vs = (Rotate(vf, 42) * K1) + Fetch64(s[8..]);
        var wf = (Rotate(y + z, 35) * K1) + x;
        var ws = Rotate(x + Fetch64(s[88..]), 53) * K1;
        var p = 0;
        do
        {
            x = Rotate(x + y + vf + Fetch64(s[(p + 16)..]), 37) * K1;
            y = Rotate(y + vs + Fetch64(s[(p + 48)..]), 42) * K1;
            x ^= ws;
            y ^= vf;
            z = Rotate(z ^ wf, 33);
            (vf, vs) = WeakHashLen32WithSeeds(s[p..], vs * K1, x + wf);
            (wf, ws) = WeakHashLen32WithSeeds(s[(p + 32)..], z + ws, y);
            (z, x) = (x, z);
            p += 64;
            x = Rotate(x + y + vf + Fetch64(s[(p + 16)..]), 37) * K1;
            y = Rotate(y + vs + Fetch64(s[(p + 48)..]), 42) * K1;
            x ^= ws;
            y ^= vf;
            z = Rotate(z ^ wf, 33);
            (vf, vs) = WeakHashLen32WithSeeds(s[p..], vs * K1, x + wf);
            (wf, ws) = WeakHashLen32WithSeeds(s[(p + 32)..], z + ws, y);
            (z, x) = (x, z);
            p += 64;
            len -= 128;
        }
        while (len >= 128);

        y += (Rotate(wf, 37) * K0) + z;
        x += Rotate(vf + z, 49) * K0;
        for (var tailDone = 0; tailDone < len;)
        {
            tailDone += 32;
            y = (Rotate(y - x, 42) * K0) + vs;
            wf += Fetch64(s[(p + len - tailDone + 16)..]);
            x = (Rotate(x, 49) * K0) + wf;
            wf += vf;
            (vf, vs) = WeakHashLen32WithSeeds(s[(p + len - tailDone)..], vf, vs);
        }

        x = HashLen16(x, vf);
        y = HashLen16(y, wf);
        return (HashLen16(x + vs, ws) + y, HashLen16(x + ws, y + vs));
    }
}
