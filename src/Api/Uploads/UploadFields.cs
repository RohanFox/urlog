using System.Globalization;
using System.Text;
using Urlog.Core.Intake;
using Urlog.Sources.Nginx;

namespace Urlog.Api.Uploads;

/// <summary>
/// What an uploader says about a file in <c>Upload-Metadata</c>: its name, and optionally the host it came from, the
/// zone and the year of its timestamps and the nginx <c>log_format</c> it was written with. These become the intake
/// hints. Every value is checked before the upload exists, so a value intake would refuse costs no bytes.
/// <para>
/// Browser tus clients send keys of their own. <c>name</c> is the file's name when <c>filename</c> is absent, and
/// <c>filetype</c>, <c>type</c> and <c>relativePath</c> are accepted and not used: what a file is comes from
/// detection, and no path from an uploader is kept. Any other key is refused, so a misspelt hint is not lost silently.
/// </para>
/// </summary>
public sealed record UploadFields
{
    public const string NameKey = "filename";
    public const string HostKey = "host";
    public const string TimezoneKey = "timezone";
    public const string YearKey = "year";
    public const string LogFormatKey = "nginx_log_format";

    /// <summary>The name as some browser clients send it; used when <see cref="NameKey"/> is absent.</summary>
    public const string AlternativeNameKey = "name";

    /// <summary>Keys browser clients send that are accepted and not used.</summary>
    public static IReadOnlyList<string> IgnoredKeys { get; } = ["filetype", "type", "relativePath"];

    public const int MaxName = 255;
    public const int MaxHost = 253;
    public const int MaxTimezone = 64;
    public const int MaxLogFormat = 4096;
    public const int MinYear = 1970;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public required string Name { get; init; }

    public string? Host { get; init; }

    public string? Timezone { get; init; }

    public int? Year { get; init; }

    public string? LogFormat { get; init; }

    /// <summary>The fields, or the reason the first bad one is refused, which names its key.</summary>
    /// <param name="metadata">The decoded values by key.</param>
    /// <param name="thisYear">The current year: a file cannot be from later than the next one.</param>
    public static bool TryParse(IReadOnlyDictionary<string, byte[]> metadata, int thisYear, out UploadFields? fields, out string? error)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        fields = null;
        foreach (var key in metadata.Keys)
        {
            if (key is not (NameKey or AlternativeNameKey or HostKey or TimezoneKey or YearKey or LogFormatKey) && !IgnoredKeys.Contains(key, StringComparer.Ordinal))
            {
                error = $"Upload-Metadata {Printable(key)}: unknown key; known: {NameKey}, {HostKey}, {TimezoneKey}, {YearKey}, {LogFormatKey}";
                return false;
            }
        }

        if (!Text(metadata, NameKey, MaxName, out var name, out error))
        {
            return false;
        }

        // Our own key decides when it is there; the other one is then not looked at.
        if (name is null && !Text(metadata, AlternativeNameKey, MaxName, out name, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = $"Upload-Metadata {NameKey}: required, the file's original name";
            return false;
        }

        if (!Text(metadata, HostKey, MaxHost, out var host, out error)
            || !Text(metadata, TimezoneKey, MaxTimezone, out var timezone, out error)
            || !Text(metadata, YearKey, 4, out var yearText, out error)
            || !Text(metadata, LogFormatKey, MaxLogFormat, out var logFormat, out error))
        {
            return false;
        }

        if (host is not null && !IsHostName(host))
        {
            error = $"Upload-Metadata {HostKey}: not a host name or an address (letters, digits, '.', '-', '_' and ':' only, no dot or dash at either end)";
            return false;
        }

        if (timezone is not null && !IntakeHints.IsKnownZone(timezone))
        {
            error = $"Upload-Metadata {TimezoneKey}: unknown zone; use an IANA id, UTC, or an offset like +02:00";
            return false;
        }

        int? year = null;
        if (yearText is not null)
        {
            if (yearText.Length != 4 || !int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < MinYear || parsed > thisYear + 1)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"Upload-Metadata {YearKey}: four digits from {MinYear} to {thisYear + 1}");
                return false;
            }

            year = parsed;
        }

        if (logFormat is not null && NginxLayout.BuiltIn(logFormat.Trim()) is null && !NginxLayout.TryCompile(logFormat, out _, out var reason))
        {
            error = $"Upload-Metadata {LogFormatKey}: {reason}";
            return false;
        }

        fields = new UploadFields { Name = name, Host = host, Timezone = timezone, Year = year, LogFormat = logFormat };
        error = null;
        return true;
    }

    /// <summary>One value as text: valid UTF-8, no control character, within its length. An absent or empty value is null.</summary>
    private static bool Text(IReadOnlyDictionary<string, byte[]> metadata, string key, int maxLength, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!metadata.TryGetValue(key, out var bytes) || bytes.Length == 0)
        {
            return true;
        }

        // Four bytes are the most one character takes, so a longer value is too long whatever it decodes to.
        if (bytes.Length > maxLength * 4)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Upload-Metadata {key}: longer than {maxLength} characters");
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            error = $"Upload-Metadata {key}: not valid UTF-8";
            return false;
        }

        if (text.Length > maxLength)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Upload-Metadata {key}: longer than {maxLength} characters");
            return false;
        }

        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                error = $"Upload-Metadata {key}: control characters are not allowed";
                return false;
            }
        }

        value = text;
        return true;
    }

    private static bool IsHostName(string host)
    {
        if (host[0] is '.' or '-' || host[^1] is '.' or '-')
        {
            return false;
        }

        foreach (var c in host)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A key as it can be shown in an error: ASCII, cut to a sane length.</summary>
    private static string Printable(string key)
    {
        var shown = new StringBuilder();
        foreach (var c in key.Length > 40 ? key[..40] : key)
        {
            shown.Append(c is > ' ' and < (char)127 ? c : '?');
        }

        return shown.ToString();
    }
}
