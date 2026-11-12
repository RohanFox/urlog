namespace Urlog.Core.Detection;

/// <summary>A file recognised by its first bytes: the format name and, where the analyst must do something, why.</summary>
internal readonly record struct MagicHit(string Format, string? Message);

/// <summary>
/// Byte signatures of files that are not line logs (design record, "Format detection"). A signature that is plain
/// ASCII and belongs to a binary format (<c>MZ</c>, <c>regf</c>, <c>OggS</c>) counts only when the first 64 bytes also
/// hold a control byte, so a text line that happens to start with those letters stays text. A two-byte signature
/// too short to trust counts only with one of its extensions.
/// </summary>
internal static class MagicTable
{
    public const string ArchiveMessage = "archive format not expanded: extract and upload the contents";

    private static readonly Entry[] Entries =
    [
        // Documents.
        new(0, "%PDF-"u8.ToArray(), "pdf"),
        new(0, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], "ole2"),
        new(0, "{\\rtf"u8.ToArray(), "rtf"),
        new(0, "%!PS"u8.ToArray(), "postscript"),

        // Images.
        new(0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "png"),
        new(0, [0xFF, 0xD8, 0xFF], "jpeg"),
        new(0, "GIF87a"u8.ToArray(), "gif"),
        new(0, "GIF89a"u8.ToArray(), "gif"),
        new(0, [0x49, 0x49, 0x2A, 0x00], "tiff"),
        new(0, [0x4D, 0x4D, 0x00, 0x2A], "tiff"),
        new(0, "8BPS"u8.ToArray(), "psd", NeedsControlByte: true),

        // Executables and objects.
        new(0, "MZ"u8.ToArray(), "pe", NeedsControlByte: true),
        new(0, [0x7F, 0x45, 0x4C, 0x46], "elf"),
        new(0, [0xFE, 0xED, 0xFA, 0xCE], "macho"),
        new(0, [0xFE, 0xED, 0xFA, 0xCF], "macho"),
        new(0, [0xCE, 0xFA, 0xED, 0xFE], "macho"),
        new(0, [0xCF, 0xFA, 0xED, 0xFE], "macho"),
        new(0, [0xCA, 0xFE, 0xBA, 0xBE], "java-class-or-macho"),
        new(0, "dex\n"u8.ToArray(), "dex", NeedsControlByte: true),
        new(0, [0x00, 0x61, 0x73, 0x6D], "wasm"),
        new(0, [0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00], "lnk"),

        // Databases and system stores.
        new(0, "SQLite format 3\0"u8.ToArray(), "sqlite"),
        new(4, [0xEF, 0xCD, 0xAB, 0x89], "ese"),
        new(0, "regf"u8.ToArray(), "registry-hive", NeedsControlByte: true),
        new(0, "MDMP"u8.ToArray(), "minidump", NeedsControlByte: true),
        new(4, "SCCA"u8.ToArray(), "prefetch", NeedsControlByte: true),
        new(0, [0x4D, 0x41, 0x4D, 0x04], "prefetch"),

        // Keys and key stores.
        new(0, "-----BEGIN "u8.ToArray(), "pem"),
        new(0, [0x03, 0xD9, 0xA2, 0x9A], "keepass"),
        new(0, [0xFE, 0xED, 0xFE, 0xED], "jks"),

        // Media.
        new(4, "ftyp"u8.ToArray(), "mp4", NeedsControlByte: true),
        new(0, "OggS"u8.ToArray(), "ogg", NeedsControlByte: true),
        new(0, "fLaC"u8.ToArray(), "flac", NeedsControlByte: true),
        new(0, [0x1A, 0x45, 0xDF, 0xA3], "matroska"),
        new(0, "ID3"u8.ToArray(), "mp3", NeedsControlByte: true),

        // Disk and memory images.
        new(0, "KDMV"u8.ToArray(), "vmdk", NeedsControlByte: true),
        new(0, "conectix"u8.ToArray(), "vhd", NeedsControlByte: true),
        new(0, "vhdxfile"u8.ToArray(), "vhdx", NeedsControlByte: true),
        new(0, [0x51, 0x46, 0x49, 0xFB], "qcow2"),
        new(32769, "CD001"u8.ToArray(), "iso"),
        new(0, [0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00], "e01"),

        // Archives the intake does not expand.
        new(0, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07], "rar", Message: ArchiveMessage),
        new(0, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], "7z", Message: ArchiveMessage),
        new(0, "MSCF"u8.ToArray(), "cab", NeedsControlByte: true, Message: ArchiveMessage),
        new(0, "!<arch>\n"u8.ToArray(), "ar", Message: ArchiveMessage),
        new(0, [0xED, 0xAB, 0xEE, 0xDB], "rpm", Message: ArchiveMessage),
        new(0, "xar!"u8.ToArray(), "xar", NeedsControlByte: true, Message: ArchiveMessage),

        // Too short to trust without the extension.
        new(0, "BM"u8.ToArray(), "bmp", Extensions: ["bmp"]),
        new(0, [0x30, 0x82], "der", Extensions: ["cer", "crt", "der", "p12", "pfx"]),
        new(0, [0x00, 0x01, 0x00, 0x00], "truetype", Extensions: ["ttf"]),
        new(0, [0x00, 0x00, 0x01, 0x00], "ico", Extensions: ["ico"]),
    ];

    private static ReadOnlySpan<byte> Riff => "RIFF"u8;

    /// <summary>PCAP in either byte order and resolution, or PCAPNG; null for anything else.</summary>
    public static string? PacketCapture(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4)
        {
            return null;
        }

        var first = (uint)((head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3]);
        if (first is 0xA1B2C3D4 or 0xD4C3B2A1 or 0xA1B23C4D or 0x4D3CB2A1)
        {
            return "pcap";
        }

        if (first == 0x0A0D0D0A && head.Length >= 12)
        {
            var order = (uint)((head[8] << 24) | (head[9] << 16) | (head[10] << 8) | head[11]);
            if (order is 0x1A2B3C4D or 0x4D3C2B1A)
            {
                return "pcapng";
            }
        }

        return null;
    }

    /// <summary>The first signature that fits <paramref name="head"/>; <paramref name="extension"/> is lowercase without the dot.</summary>
    public static MagicHit? Match(ReadOnlySpan<byte> head, string extension)
    {
        if (head.StartsWith(Riff) && head.Length >= 12)
        {
            var form = head.Slice(8, 4);
            if (form.SequenceEqual("WEBP"u8))
            {
                return new MagicHit("webp", null);
            }

            if (form.SequenceEqual("AVI "u8))
            {
                return new MagicHit("avi", null);
            }

            if (form.SequenceEqual("WAVE"u8))
            {
                return new MagicHit("wav", null);
            }
        }

        var controlByte = -1; // -1 not looked yet, 0 none, 1 present
        foreach (var entry in Entries)
        {
            if (head.Length < entry.Offset + entry.Bytes.Length || !head.Slice(entry.Offset, entry.Bytes.Length).SequenceEqual(entry.Bytes))
            {
                continue;
            }

            if (entry.Extensions is not null && Array.IndexOf(entry.Extensions, extension) < 0)
            {
                continue;
            }

            if (entry.NeedsControlByte)
            {
                if (controlByte < 0)
                {
                    controlByte = TextProbe.HasControlByte(head[..Math.Min(head.Length, 64)]) ? 1 : 0;
                }

                if (controlByte == 0)
                {
                    continue;
                }
            }

            return new MagicHit(entry.Format, entry.Message);
        }

        return null;
    }

    private sealed record Entry(int Offset, byte[] Bytes, string Format, bool NeedsControlByte = false, string[]? Extensions = null, string? Message = null);
}
