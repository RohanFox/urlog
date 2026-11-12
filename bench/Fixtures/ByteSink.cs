using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Urlog.Bench.Fixtures;

/// <summary>
/// Buffered byte writer with a running SHA-256. Generators write ASCII/UTF-8 bytes directly; no strings per line.
/// </summary>
public sealed class ByteSink : IDisposable
{
    private readonly Stream _out;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _buf;
    private int _pos;
    private bool _finished;

    public ByteSink(Stream output, int bufferSize = 1 << 20)
    {
        _out = output;
        _buf = ArrayPool<byte>.Shared.Rent(bufferSize);
    }

    /// <summary>Bytes handed to the sink so far (buffered or flushed).</summary>
    public long Written { get; private set; }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _buf.Length - _pos)
        {
            Flush();
            if (bytes.Length > _buf.Length)
            {
                _hash.AppendData(bytes);
                _out.Write(bytes);
                Written += bytes.Length;
                return;
            }
        }

        bytes.CopyTo(_buf.AsSpan(_pos));
        _pos += bytes.Length;
        Written += bytes.Length;
    }

    public void WriteByte(byte b)
    {
        if (_pos == _buf.Length)
        {
            Flush();
        }

        _buf[_pos++] = b;
        Written++;
    }

    public void WriteUInt(ulong value)
    {
        Span<byte> tmp = stackalloc byte[20];
        Utf8Formatter.TryFormat(value, tmp, out var n);
        Write(tmp[..n]);
    }

    public void WriteInt(long value)
    {
        Span<byte> tmp = stackalloc byte[21];
        Utf8Formatter.TryFormat(value, tmp, out var n);
        Write(tmp[..n]);
    }

    /// <summary>Writes <paramref name="value"/> zero-padded to <paramref name="width"/> digits.</summary>
    public void WritePadded(int value, int width)
    {
        Span<byte> tmp = stackalloc byte[10];
        var n = 0;
        var v = value;
        do
        {
            tmp[n++] = (byte)('0' + (v % 10));
            v /= 10;
        }
        while (v > 0);
        for (var i = n; i < width; i++)
        {
            WriteByte((byte)'0');
        }

        for (var i = n - 1; i >= 0; i--)
        {
            WriteByte(tmp[i]);
        }
    }

    /// <summary>Repeats <paramref name="bytes"/> until at least <paramref name="total"/> bytes were written.</summary>
    public void WriteRepeated(ReadOnlySpan<byte> bytes, int total)
    {
        var done = 0;
        while (done < total)
        {
            var take = Math.Min(bytes.Length, total - done);
            Write(bytes[..take]);
            done += take;
        }
    }

    public void Flush()
    {
        if (_pos > 0)
        {
            _hash.AppendData(_buf, 0, _pos);
            _out.Write(_buf, 0, _pos);
            _pos = 0;
        }
    }

    /// <summary>Flushes and returns the lowercase hex SHA-256 of everything written.</summary>
    public string Finish()
    {
        Flush();
        _out.Flush();
        _finished = true;
        return Convert.ToHexStringLower(_hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (!_finished)
        {
            Flush();
        }

        ArrayPool<byte>.Shared.Return(_buf);
        _hash.Dispose();
    }
}
