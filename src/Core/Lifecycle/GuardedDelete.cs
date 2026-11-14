namespace Urlog.Core.Lifecycle;

/// <summary>A recursive removal was refused: the path is not strictly inside a directory this process may empty.</summary>
public sealed class DeletionRefusedException : Exception
{
    public DeletionRefusedException()
    {
    }

    public DeletionRefusedException(string message)
        : base(message)
    {
    }

    public DeletionRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The one place a directory tree is removed (purge, and the archive after it is verified). A removal is carried
/// out only for a path that, once made absolute and normalised, lies strictly under one of the roots given at
/// construction and is not a root itself, and that reaches its target without passing through a link: neither the
/// target nor any directory between the root and the target may be a symbolic link or another reparse point.
/// Inside the tree a link is removed as a link and never entered.
/// </summary>
public sealed class GuardedDelete
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly string[] _roots;
    private readonly Action<string>? _before;

    /// <param name="roots">The directories whose contents may be removed: the evidence root, the cache root. Empty or null entries are left out.</param>
    /// <param name="before">Called with each path just before it is removed; tests stop a removal part-way with it.</param>
    public GuardedDelete(IEnumerable<string?> roots, Action<string>? before = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = [.. roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r!)))];
        foreach (var root in _roots)
        {
            if (string.Equals(Path.GetPathRoot(root), root, PathComparison) || root.Length == 0)
            {
                throw new DeletionRefusedException($"'{root}' is the root of a volume and cannot be a root for removals");
            }
        }

        _before = before;
    }

    /// <summary>
    /// The absolute form of <paramref name="path"/> when a removal of it would be carried out.
    /// </summary>
    /// <exception cref="DeletionRefusedException">The path is relative, outside every root, a root itself, a link, or behind a link.</exception>
    public string Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new DeletionRefusedException($"'{path}' is not an absolute path; nothing was removed");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var root in _roots)
        {
            if (string.Equals(full, root, PathComparison))
            {
                throw new DeletionRefusedException($"'{full}' is a root directory itself; nothing was removed");
            }

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            {
                continue;
            }

            // Every step from the root down to the target has to be a real directory.
            var current = root;
            foreach (var segment in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (IsLinkAt(current))
                {
                    throw new DeletionRefusedException($"'{full}' is not removed: '{current}' is a link, and a removal never goes through one");
                }
            }

            return full;
        }

        throw new DeletionRefusedException($"'{full}' is not under a directory this process may empty ({string.Join(", ", _roots)}); nothing was removed");
    }

    /// <summary>
    /// Removes the directory at <paramref name="path"/> with everything in it. A directory that is not there is
    /// fine. Returns the files and links removed.
    /// </summary>
    /// <exception cref="DeletionRefusedException">See <see cref="Check"/>. Nothing was removed.</exception>
    public int RemoveTree(string path)
    {
        var full = Check(path);
        var directory = new DirectoryInfo(full);
        if (!directory.Exists)
        {
            if (File.Exists(full))
            {
                throw new DeletionRefusedException($"'{full}' is a file where a directory was expected; nothing was removed");
            }

            return 0;
        }

        return Remove(directory);
    }

    /// <summary>True for a symbolic link, a junction or any other reparse point: an entry whose content is somewhere else.</summary>
    public static bool IsLink(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Refresh();
        if (entry.LinkTarget is not null)
        {
            return true;
        }

        // A path that is not there reads as -1, which has every flag set.
        var attributes = entry.Attributes;
        return attributes != (FileAttributes)(-1) && attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool IsLinkAt(string path) => IsLink(Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path));

    private int Remove(DirectoryInfo directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Entries are removed through the directory's own permissions, whatever mode the files have.
            directory.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }

        var removed = 0;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (IsLink(entry))
            {
                _before?.Invoke(entry.FullName);
                entry.Delete();
                removed++;
            }
            else if (entry is DirectoryInfo sub)
            {
                removed += Remove(sub);
            }
            else
            {
                _before?.Invoke(entry.FullName);
                if (OperatingSystem.IsWindows() && entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    // Stored evidence carries the read-only attribute here, which refuses a delete.
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }

                entry.Delete();
                removed++;
            }
        }

        _before?.Invoke(directory.FullName);
        directory.Delete();
        return removed;
    }
}
