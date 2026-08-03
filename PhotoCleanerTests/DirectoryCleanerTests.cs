using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class DirectoryCleanerTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            $"DirectoryCleanerTest_{Path.GetRandomFileName()}"
        );
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void DeleteEmptyDirectories_RemovesEmptyChild()
    {
        string root = TempDir();
        try
        {
            string emptyChild = Path.Combine(root, "empty");
            Directory.CreateDirectory(emptyChild);

            int deleted = DirectoryCleaner.DeleteEmptyDirectories(
                new DirectoryInfo(root),
                dryRun: false
            );

            deleted.Should().Be(1);
            Directory.Exists(emptyChild).Should().BeFalse();
            Directory.Exists(root).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteEmptyDirectories_KeepsRootEvenWhenEmpty()
    {
        string root = TempDir();
        try
        {
            int deleted = DirectoryCleaner.DeleteEmptyDirectories(
                new DirectoryInfo(root),
                dryRun: false
            );

            deleted.Should().Be(0);
            Directory.Exists(root).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteEmptyDirectories_KeepsDirectoryWithFile()
    {
        string root = TempDir();
        try
        {
            string keep = Path.Combine(root, "keep");
            Directory.CreateDirectory(keep);
            File.WriteAllBytes(Path.Combine(keep, "file.bin"), []);

            int deleted = DirectoryCleaner.DeleteEmptyDirectories(
                new DirectoryInfo(root),
                dryRun: false
            );

            deleted.Should().Be(0);
            Directory.Exists(keep).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteEmptyDirectories_RemovesDeepestFirst()
    {
        string root = TempDir();
        try
        {
            string a = Path.Combine(root, "a");
            string b = Path.Combine(a, "b");
            string c = Path.Combine(b, "c");
            Directory.CreateDirectory(c);

            int deleted = DirectoryCleaner.DeleteEmptyDirectories(
                new DirectoryInfo(root),
                dryRun: false
            );

            deleted.Should().Be(3);
            Directory.Exists(a).Should().BeFalse();
            Directory.Exists(b).Should().BeFalse();
            Directory.Exists(c).Should().BeFalse();
            Directory.Exists(root).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteEmptyDirectories_KeepsAncestorWithSibling()
    {
        string root = TempDir();
        try
        {
            // root/keep contains a file (so root/keep stays, and so does root)
            // root/empty has no files (should be deleted)
            string keep = Path.Combine(root, "keep");
            string empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(keep);
            Directory.CreateDirectory(empty);
            File.WriteAllBytes(Path.Combine(keep, "f.bin"), []);

            int deleted = DirectoryCleaner.DeleteEmptyDirectories(
                new DirectoryInfo(root),
                dryRun: false
            );

            deleted.Should().Be(1);
            Directory.Exists(empty).Should().BeFalse();
            Directory.Exists(keep).Should().BeTrue();
            Directory.Exists(root).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteEmptyDirectories_DryRun_DoesNotDeleteFilesystem()
    {
        string root = TempDir();
        try
        {
            string emptyA = Path.Combine(root, "empty-a");
            string emptyB = Path.Combine(root, "empty-b");
            Directory.CreateDirectory(emptyA);
            Directory.CreateDirectory(emptyB);

            int deleted = DirectoryCleaner.DeleteEmptyDirectories(
                new DirectoryInfo(root),
                dryRun: true
            );

            // Dry-run counts each directory empty at scan time but deletes nothing.
            // Cascading deletes are not simulated, so only the leaves are reported.
            deleted.Should().Be(2);
            Directory.Exists(emptyA).Should().BeTrue();
            Directory.Exists(emptyB).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
