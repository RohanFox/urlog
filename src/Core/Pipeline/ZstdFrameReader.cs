using System.Buffers;
using System.Buffers.Binary;
using ZstdSharp;

namespace Urlog.Core.Pipeline;

/// <summary>
/// Reads a zstd file one frame at a time and knows where each frame ends. The decoder cannot be left to find
/// that out: given a frame with other bytes behind it in the same call, it finishes the frame, runs into the
/// bytes and reports an error for the whole call. A zstd frame states its own extent (RFC 8878: a header whose
/// length follows from its first byte, then blocks that each carry their size and a last-block flag, then an
/// optional checksum), so this reader walks that structure and hands the decoder the bytes of one frame only.
/// <list type="bullet">
/// <item>The stored bytes end between frames: complete.</item>
/// <item>They end inside a walked frame, or after the first bytes of a frame magic: truncated.</item>
/// <item>Behind a finished frame come bytes that are no frame magic: trailing bytes, counted and not decoded.</item>
/// <item>The decoder rejects the bytes of a frame, or is not finished when the frame's structure is: damage, an error.</item>
/// </list>
/// A cut exactly between two frames leaves a valid shorter file and cannot be seen.
/// </summary>
internal sealed class ZstdFrameReader(RawMeter raw) : IDisposable
{
    private const uint FrameMagic = 0xFD2FB528;
    private const uint SkippableMagic = 0x184D2A50;

    private readonly Decompressor _decompressor = new();
    private readonly byte[] _input = new byte[128 << 10];
    private readonly byte[] _header = new byte[16];
    private int _start;
    private int _walk;
    private int _end;
    private bool _inputEnded;
    private bool _anyFrame;
    private bool _done;

    private bool _frameOpen;
    private bool _frameWalked;
    private bool _decoderDone;
    private Stage _stage;
    private int _headerHave;
    private int _headerNeed;
    private long _bodyLeft;
    private bool _lastBlock;
    private bool _checksum;

    private enum Stage
    {
        Descriptor,
        HeaderRest,
        BlockHeader,
        BlockBody,
        Checksum,
        SkippableSize,
        SkippableBody,
    }

    /// <summary>True once the input ended inside a frame.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Stored bytes behind the last frame that start no frame.</summary>
    public long TrailingBytes { get; private set; }

    public int Read(Span<byte> buffer)
    {
        while (!_done)
        {
            if (!_frameOpen && !StartFrame())
            {
                _done = true;
                break;
            }

            Walk();
            OperationStatus status;
            int written;
            if (_start < _walk)
            {
                status = _decompressor.UnwrapStream(_input.AsSpan(_start, _walk - _start), buffer, out var consumed, out written);
                _start += consumed;
                _decoderDone |= status == OperationStatus.Done;
                if (written > 0)
                {
                    return written;
                }

                if (status == OperationStatus.InvalidData || consumed == 0)
                {
                    throw new InvalidDataException($"the zstd decoder rejected the frame at stored offset {StoredOffsetOf(_start)} ({status})");
                }

                continue;
            }

            // The decoder has every byte walked so far. What it still holds comes out on a call without input.
            status = _decompressor.UnwrapStream([], buffer, out _, out written);
            _decoderDone |= status == OperationStatus.Done;
            if (written > 0)
            {
                return written;
            }

            if (_frameWalked)
            {
                if (!_decoderDone)
                {
                    throw new InvalidDataException($"the zstd frame's structure ends at stored offset {StoredOffsetOf(_start)} and the decoder is not finished with it ({status})");
                }

                _frameOpen = false;
                continue;
            }

            if (_walk < _end)
            {
                continue;
            }

            if (_inputEnded)
            {
                Truncated = true;
                _done = true;
                break;
            }

            Fill();
        }

        return 0;
    }

    public void Dispose() => _decompressor.Dispose();

    private long StoredOffsetOf(int index) => raw.Bytes - (_end - index);

    /// <summary>
    /// Between frames: true when another frame starts here. False at the end of the input, at trailing bytes
    /// (counted) and at the first bytes of a frame magic with nothing after them (truncated).
    /// </summary>
    private bool StartFrame()
    {
        while (_end - _start < 4 && !_inputEnded)
        {
            Fill();
        }

        var head = _input.AsSpan(_start, Math.Min(4, _end - _start));
        if (head.IsEmpty)
        {
            return false;
        }

        if (head.Length == 4)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
            var skippable = (magic & 0xFFFFFFF0) == SkippableMagic;
            if (magic == FrameMagic || skippable)
            {
                _anyFrame = true;
                _frameOpen = true;
                _frameWalked = false;
                _decoderDone = false;
                _walk = _start + 4;
                _stage = skippable ? Stage.SkippableSize : Stage.Descriptor;
                _headerHave = 0;
                _headerNeed = skippable ? 4 : 1;
                _bodyLeft = 0;
                return true;
            }
        }
        else if (CouldBecomeAMagic(head))
        {
            Truncated = true;
            return false;
        }

        if (!_anyFrame)
        {
            throw new InvalidDataException("the stored bytes do not start with a zstd frame");
        }

        TrailingBytes = _end - _start;
        _start = _walk = _end;
        while (!_inputEnded)
        {
            Fill();
            TrailingBytes += _end - _start;
            _start = _walk = _end;
        }

        return false;
    }

    /// <summary>Moves <see cref="_walk"/> over the bytes in hand that belong to the current frame, up to the frame's end.</summary>
    private void Walk()
    {
        while (_walk < _end && !_frameWalked)
        {
            if (_bodyLeft > 0)
            {
                var take = (int)Math.Min(_bodyLeft, _end - _walk);
                _walk += take;
                _bodyLeft -= take;
                if (_bodyLeft == 0)
                {
                    AfterBody();
                }

                continue;
            }

            _header[_headerHave++] = _input[_walk++];
            if (_headerHave < _headerNeed)
            {
                continue;
            }

            _headerHave = 0;
            switch (_stage)
            {
                case Stage.Descriptor:
                    var descriptor = _header[0];
                    var singleSegment = (descriptor & 0x20) != 0;
                    _checksum = (descriptor & 0x04) != 0;
                    var dictionaryId = (descriptor & 0x03) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
                    var contentSize = (descriptor >> 6) switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
                    var rest = (singleSegment ? 0 : 1) + dictionaryId + contentSize;
                    Expect(rest == 0 ? Stage.BlockHeader : Stage.HeaderRest, rest == 0 ? 3 : rest);
                    break;
                case Stage.HeaderRest:
                    Expect(Stage.BlockHeader, 3);
                    break;
                case Stage.BlockHeader:
                    var block = _header[0] | (_header[1] << 8) | (_header[2] << 16);
                    _lastBlock = (block & 1) != 0;
                    var type = (block >> 1) & 3;
                    if (type == 3)
                    {
                        throw new InvalidDataException($"zstd block of the reserved type at stored offset {StoredOffsetOf(_walk - 3)}");
                    }

                    // A run-length block stores one byte whatever size it states.
                    _bodyLeft = type == 1 ? 1 : block >> 3;
                    _stage = Stage.BlockBody;
                    if (_bodyLeft == 0)
                    {
                        AfterBody();
                    }

                    break;
                case Stage.Checksum:
                    _frameWalked = true;
                    break;
                case Stage.SkippableSize:
                    _bodyLeft = BinaryPrimitives.ReadUInt32LittleEndian(_header);
                    _stage = Stage.SkippableBody;
                    if (_bodyLeft == 0)
                    {
                        AfterBody();
                    }

                    break;
                default:
                    throw new InvalidOperationException($"no header is read in stage {_stage}");
            }
        }
    }

    private void AfterBody()
    {
        if (_stage == Stage.SkippableBody || (_lastBlock && !_checksum))
        {
            _frameWalked = true;
        }
        else if (_lastBlock)
        {
            Expect(Stage.Checksum, 4);
        }
        else
        {
            Expect(Stage.BlockHeader, 3);
        }
    }

    private void Expect(Stage stage, int headerBytes)
    {
        _stage = stage;
        _headerNeed = headerBytes;
    }

    private static bool CouldBecomeAMagic(ReadOnlySpan<byte> head)
    {
        Span<byte> frame = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, FrameMagic);
        if (frame.StartsWith(head))
        {
            return true;
        }

        // A skippable frame's first byte is 0x50 to 0x5F, then 2A 4D 18.
        ReadOnlySpan<byte> skippable = [0x50, 0x2A, 0x4D, 0x18];
        return (head[0] & 0xF0) == 0x50 && skippable[1..head.Length].SequenceEqual(head[1..]);
    }

    private void Fill()
    {
        if (_start > 0)
        {
            _input.AsSpan(_start, _end - _start).CopyTo(_input);
            _walk -= _start;
            _end -= _start;
            _start = 0;
        }

        var n = raw.Read(_input.AsSpan(_end));
        _inputEnded = n == 0;
        _end += n;
    }
}
