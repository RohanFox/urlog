using System.Collections.Frozen;

namespace Urlog.Core.Detection;

/// <summary>
/// File-name extensions of things that are not logs. Consulted only after every source has scored the sample and
/// none reached the confident band: a log by content stays a log whatever it is called. Extensions that log exports
/// use (<c>log txt out err json jsonl ndjson csv tsv xml evtx journal</c>) are never listed.
/// </summary>
internal static class ArtifactExtensions
{
    private static readonly FrozenSet<string> Artifacts = new[]
    {
        // documents
        "pdf", "doc", "docx", "docm", "xls", "xlsx", "xlsm", "ppt", "pptx", "odt", "ods", "odp", "rtf", "msg", "eml", "pst", "ost", "vsd", "vsdx", "one",
        // images
        "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "ico", "webp", "svg", "heic", "psd",
        // executables and objects
        "exe", "dll", "sys", "scr", "com", "msi", "ocx", "drv", "cpl", "so", "dylib", "o", "a", "lib", "class", "jar", "war", "ear", "apk", "dex", "pyc", "lnk",
        // scripts and markup
        "php", "phtml", "phar", "asp", "aspx", "ashx", "jsp", "jspx", "cgi", "pl", "py", "rb", "sh", "bash", "ps1", "psm1", "bat", "cmd", "vbs", "vbe",
        "js", "jse", "wsf", "hta", "html", "htm", "css",
        // databases
        "db", "sqlite", "sqlite3", "mdb", "accdb", "mdf", "ldf", "ndf", "edb", "dit", "frm", "ibd", "myd", "myi", "dbf",
        // keys and certificates
        "pem", "crt", "cer", "der", "p12", "pfx", "key", "csr", "jks", "keystore", "kdbx", "pub", "ppk",
        // media
        "mp3", "mp4", "avi", "mov", "mkv", "wav", "flac", "ogg", "webm", "wmv",
        // disk and memory images
        "iso", "img", "vmdk", "vhd", "vhdx", "qcow2", "e01", "dd", "dmp", "mem", "vmem", "hiv",
        // fonts
        "ttf", "otf", "woff", "woff2", "eot",
        // configuration
        "conf", "config", "cfg", "ini", "yaml", "yml", "toml", "properties", "plist", "reg",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Zip-based documents and packages: a zip with one of these names is an artifact, not an archive of logs.</summary>
    private static readonly FrozenSet<string> ZipDocuments = new[]
    {
        "docx", "docm", "xlsx", "xlsm", "pptx", "vsdx", "odt", "ods", "odp", "jar", "war", "ear", "apk", "epub",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] CompressionSuffixes = [".gz", ".bz2", ".xz", ".zst", ".zstd"];

    public static bool IsArtifact(string extension) => extension.Length > 0 && Artifacts.Contains(extension);

    public static bool IsZipDocument(string extension) => extension.Length > 0 && ZipDocuments.Contains(extension);

    /// <summary>
    /// The extension that says what the file is, lowercase without the dot: the last path segment (an archive path
    /// counts), compression suffixes and rotation suffixes removed (<c>access.log.3.gz</c> and
    /// <c>access.log-20240101</c> are both <c>log</c>). Empty when the name has none.
    /// </summary>
    public static string Of(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var name = fileName.AsSpan();
        var cut = name.LastIndexOfAny('/', '\\', '!');
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }

        while (true)
        {
            var stripped = false;
            foreach (var suffix in CompressionSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^suffix.Length];
                    stripped = true;
                }
            }

            var rotation = RotationSuffix(name);
            if (rotation > 0)
            {
                name = name[..^rotation];
                stripped = true;
            }

            if (!stripped)
            {
                break;
            }
        }

        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return string.Empty;
        }

        var extension = name[(dot + 1)..];
        Span<char> lower = stackalloc char[Math.Min(extension.Length, 16)];
        if (extension.Length > lower.Length)
        {
            return string.Empty; // nothing in the list is this long
        }

        extension.ToLowerInvariant(lower);
        return lower.ToString();
    }

    /// <summary>Length of a trailing <c>.3</c>, <c>-20240101</c>, <c>.2024-01-01</c> or <c>_20240101</c>, else 0.</summary>
    private static int RotationSuffix(ReadOnlySpan<char> name)
    {
        // yyyy-MM-dd after a separator.
        if (name.Length > 11 && name[^11] is '.' or '-' or '_' && IsDate(name[^10..]))
        {
            return 11;
        }

        var i = name.Length;
        while (i > 0 && char.IsAsciiDigit(name[i - 1]))
        {
            i--;
        }

        var digits = name.Length - i;
        if (digits == 0 || i == 0)
        {
            return 0;
        }

        var separator = name[i - 1];
        if (separator == '.' || ((separator == '-' || separator == '_') && digits >= 6))
        {
            return digits + 1;
        }

        return 0;

        static bool IsDate(ReadOnlySpan<char> text)
        {
            for (var k = 0; k < text.Length; k++)
            {
                var ok = k is 4 or 7 ? text[k] == '-' : char.IsAsciiDigit(text[k]);
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
