namespace Urlog.Core.Intake;

/// <summary>
/// Evidence is read-only from the moment it lands (design record, "Intake"). On Linux and macOS that is mode
/// 0440: read for the service user and the case's group, nothing for others, write for nobody. Windows has no
/// modes, so the read-only attribute stands in for it there. The service user already owns what it wrote or moved
/// into place, so nothing is chowned.
/// </summary>
public static class EvidencePermissions
{
    /// <summary>The mode of a stored evidence file where modes exist.</summary>
    public const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.GroupRead;

    public static void MakeReadOnly(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(path, Mode);
        }
    }

    /// <summary>True when the file cannot be written through its permissions: mode 0440 exactly, or the read-only attribute.</summary>
    public static bool IsReadOnly(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return OperatingSystem.IsWindows()
            ? File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)
            : File.GetUnixFileMode(path) == Mode;
    }
}
