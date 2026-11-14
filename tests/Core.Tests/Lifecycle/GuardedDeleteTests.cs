using Urlog.Core.Intake;
using Urlog.Core.Lifecycle;

namespace Urlog.Core.Tests.Lifecycle;

/// <summary>
/// The guard on every recursive removal: what it refuses, each with nothing removed, and what it does with links
/// and read-only files inside a tree it does remove.
/// </summary>
public sealed class GuardedDeleteTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("urlog-guard-");
    private readonly string _root;
    private readonly string _cache;
    private readonly string _outside;

    public GuardedDeleteTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_temp.FullName, "evidence")).FullName;
        _cache = Directory.CreateDirectory(Path.Combine(_temp.FullName, "evidence-cache")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_temp.FullName, "outside")).FullName;
        File.WriteAllText(Path.Combine(_outside, "keep.txt"), "keep");
    }

    private GuardedDelete Guard => new([_root, _cache]);

    [Fact]
    public void RemovesATreeUnderARootWithItsReadOnlyFiles()
    {
        var caseDirectory = Tree(_root, "7");
        Assert.Equal(4, Guard.RemoveTree(caseDirectory));
        Assert.False(Directory.Exists(caseDirectory));
        Assert.True(Directory.Exists(_root));
        // Again, and for a directory that never was: nothing to do is not an error.
        Assert.Equal(0, Guard.RemoveTree(caseDirectory));
        Assert.Equal(0, Guard.RemoveTree(Path.Combine(_cache, "7")));
    }

    [Fact]
    public void RefusesTheRootItself()
    {
        Tree(_root, "7");
        foreach (var path in new[] { _root, _root + Path.DirectorySeparatorChar, Path.Combine(_root, "7", ".."), _cache })
        {
            var refused = Assert.Throws<DeletionRefusedException>(() => Guard.RemoveTree(path));
            Assert.Contains("root directory itself", refused.Message, StringComparison.Ordinal);
        }

        AssertUntouched();
    }

    [Fact]
    public void RefusesAPathOutsideEveryRoot()
    {
        Tree(_root, "7");
        var sibling = Directory.CreateDirectory(_root + "-old").FullName;
        foreach (var path in new[] { _outside, _temp.FullName, Path.Combine(_root, "..", "outside"), Path.Combine(_root, "7", "..", "..", "outside"), sibling, Path.GetPathRoot(_root)! })
        {
            var refused = Assert.Throws<DeletionRefusedException>(() => Guard.RemoveTree(path));
            Assert.Contains("not under a directory this process may empty", refused.Message, StringComparison.Ordinal);
        }

        AssertUntouched();
        Assert.True(Directory.Exists(sibling));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("7")]
    [InlineData("evidence/7")]
    public void RefusesARelativeOrEmptyPath(string path)
    {
        Tree(_root, "7");
        var refused = Assert.Throws<DeletionRefusedException>(() => Guard.RemoveTree(path));
        Assert.Contains("not an absolute path", refused.Message, StringComparison.Ordinal);
        AssertUntouched();
    }

    [Fact]
    public void RefusesAFileWhereADirectoryIsExpected()
    {
        var file = Path.Combine(_root, "7");
        File.WriteAllText(file, "not a directory");
        Assert.Throws<DeletionRefusedException>(() => Guard.RemoveTree(file));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void AVolumeRootCannotBeARootForRemovals()
    {
        Assert.Throws<DeletionRefusedException>(() => new GuardedDelete([Path.GetPathRoot(_root)]));
    }

    [Fact]
    public void RefusesATargetThatIsALink()
    {
        var link = Path.Combine(_root, "7");
        if (!TryLink(link, _outside))
        {
            Assert.Skip("this account cannot create a directory link here");
        }

        var refused = Assert.Throws<DeletionRefusedException>(() => Guard.RemoveTree(link));
        Assert.Contains("is a link", refused.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_outside, "keep.txt")));
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void RefusesATargetBehindALink()
    {
        Directory.CreateDirectory(Path.Combine(_outside, "7"));
        File.WriteAllText(Path.Combine(_outside, "7", "inner.txt"), "keep");
        var link = Path.Combine(_root, "cases");
        if (!TryLink(link, _outside))
        {
            Assert.Skip("this account cannot create a directory link here");
        }

        var refused = Assert.Throws<DeletionRefusedException>(() => Guard.RemoveTree(Path.Combine(link, "7")));
        Assert.Contains("a removal never goes through one", refused.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_outside, "7", "inner.txt")));
    }

    [Fact]
    public void ALinkInsideTheTreeIsRemovedAndNeverEntered()
    {
        var caseDirectory = Tree(_root, "7");
        if (!TryLink(Path.Combine(caseDirectory, "elsewhere"), _outside))
        {
            Assert.Skip("this account cannot create a directory link here");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(caseDirectory, "web", "file-link"), Path.Combine(_outside, "keep.txt"));
        }

        Guard.RemoveTree(caseDirectory);
        Assert.False(Directory.Exists(caseDirectory));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_outside, "keep.txt")));
    }

    [Fact]
    public void TheHookSeesEveryPathBeforeItGoesAndAStopLeavesTheRest()
    {
        var caseDirectory = Tree(_root, "7");
        var seen = new List<string>();
        var guard = new GuardedDelete([_root], path =>
        {
            seen.Add(path);
            if (seen.Count == 3)
            {
                throw new IOException("stopped by the test");
            }
        });
        Assert.Throws<IOException>(() => guard.RemoveTree(caseDirectory));
        Assert.True(Directory.Exists(caseDirectory));
        // What the hook saw before the stop is gone, the path it stopped on is still there.
        Assert.All(seen.Take(2), path => Assert.False(File.Exists(path) || Directory.Exists(path)));
        Assert.True(File.Exists(seen[2]) || Directory.Exists(seen[2]));
        // A second run finishes what the first left.
        Guard.RemoveTree(caseDirectory);
        Assert.False(Directory.Exists(caseDirectory));
    }

    [Fact]
    public void OnLinuxEvidenceWithMode0440IsRemovedAndADirectoryWithoutWriteIsOpenedFirst()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("mode bits exist on Linux and macOS only");
            return;
        }

        var caseDirectory = Tree(_root, "7");
        foreach (var file in Directory.EnumerateFiles(caseDirectory, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(EvidencePermissions.Mode, File.GetUnixFileMode(file));
        }

        File.SetUnixFileMode(Path.Combine(caseDirectory, "web"), UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        Guard.RemoveTree(caseDirectory);
        Assert.False(Directory.Exists(caseDirectory));
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in _temp.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                file.Attributes = FileAttributes.Normal;
            }

            _temp.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the OS to clean.
        }
    }

    /// <summary>A case directory with four read-only files: two at the top, one in web/, one in .uploads/.</summary>
    private static string Tree(string root, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(directory, CaseFolders.Web));
        Directory.CreateDirectory(Path.Combine(directory, CaseFolders.Uploads));
        foreach (var file in new[] { "a.log", "b.log", Path.Combine(CaseFolders.Web, "c"), Path.Combine(CaseFolders.Uploads, "d") })
        {
            var path = Path.Combine(directory, file);
            File.WriteAllText(path, file);
            EvidencePermissions.MakeReadOnly(path);
        }

        return directory;
    }

    private void AssertUntouched()
    {
        Assert.Equal(4, Directory.EnumerateFiles(Path.Combine(_root, "7"), "*", SearchOption.AllDirectories).Count());
        Assert.True(File.Exists(Path.Combine(_outside, "keep.txt")));
        Assert.True(Directory.Exists(_cache));
    }

    /// <summary>A directory link: a symbolic link where the account may make one, a junction on Windows otherwise.</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
