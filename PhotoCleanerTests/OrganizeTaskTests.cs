using CliWrap;
using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class OrganizeTaskTests(TempDirectoryFixture fixture)
    : IClassFixture<TempDirectoryFixture>
{
    private static string TempDir()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            $"OrganizeTaskTest_{Path.GetRandomFileName()}"
        );
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Touch(string path) => File.WriteAllBytes(path, []);

    private static async Task SetExifDateAsync(string filePath, string date) =>
        await Cli.Wrap("exiftool")
            .WithArguments(["-overwrite_original", $"-EXIF:DateTimeOriginal={date}", filePath])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

    // -- UnsupportedFile: ignored, count incremented -------------------------

    [Fact]
    public async Task ExecuteOrganizeAsync_UnsupportedFile_Ignored()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string txt = Path.Combine(srcDir, "notes.txt");
            Touch(txt);

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false
            );
            (int moved, int ignored, int failed, int deletedDirs) = await task.ExecuteOrganizeAsync(
                [txt],
                [],
                TestContext.Current.CancellationToken
            );

            moved.Should().Be(0);
            ignored.Should().Be(1);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            File.Exists(txt).Should().BeTrue(); // not moved
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- DryRun: count incremented, source file stays -------------------------

    [Fact]
    public async Task ExecuteOrganizeAsync_DryRun_ReportsCountButLeavesFiles()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            OrganizeTask task = new(
                dryRun: true,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false
            );
            (int moved, int ignored, int failed, int deletedDirs) = await task.ExecuteOrganizeAsync(
                [jpg],
                [],
                TestContext.Current.CancellationToken
            );

            moved.Should().Be(1);
            ignored.Should().Be(0);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            File.Exists(jpg).Should().BeTrue(); // not moved in dry-run
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- Supported file with EXIF date: moved to correct date subdir ----------

    [Fact]
    public async Task ExecuteOrganizeAsync_SupportedFile_MovedToDateSubdir()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false
            );
            (int moved, int ignored, int failed, int deletedDirs) = await task.ExecuteOrganizeAsync(
                [jpg],
                [],
                TestContext.Current.CancellationToken
            );

            moved.Should().Be(1);
            ignored.Should().Be(0);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            File.Exists(jpg).Should().BeFalse();
            File.Exists(Path.Combine(outDir, "2024-06", "photo.jpg")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- No EXIF date: file lands in DateTime.MinValue bucket -----------------

    [Fact]
    public async Task ExecuteOrganizeAsync_NoExifDate_FallsBackToMinValue()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            // No EXIF date set -> falls back to DateTime.MinValue

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false
            );
            (int moved, int ignored, int failed, int deletedDirs) = await task.ExecuteOrganizeAsync(
                [jpg],
                [],
                TestContext.Current.CancellationToken
            );

            moved.Should().Be(1);
            ignored.Should().Be(0);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            File.Exists(jpg).Should().BeFalse();
            File.Exists(Path.Combine(outDir, "0001-01", "photo.jpg")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- Same-name collision: second file gets uniquified name -----------------

    [Fact]
    public async Task ExecuteOrganizeAsync_SameNameFilesInSameMonth_BothMovedWithUniquifiedNames()
    {
        string srcDir1 = TempDir();
        string srcDir2 = TempDir();
        string outDir = TempDir();
        try
        {
            // Two files named photo.jpg, both without EXIF date -> both land in 0001-01/
            string jpg1 = Path.Combine(srcDir1, "photo.jpg");
            string jpg2 = Path.Combine(srcDir2, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg1);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg2);

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false
            );
            (int moved, int ignored, int failed, int deletedDirs) = await task.ExecuteOrganizeAsync(
                [jpg1, jpg2],
                [],
                TestContext.Current.CancellationToken
            );

            moved.Should().Be(2);
            ignored.Should().Be(0);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            File.Exists(Path.Combine(outDir, "0001-01", "photo.jpg")).Should().BeTrue();
            File.Exists(Path.Combine(outDir, "0001-01", "photo_1.jpg")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir1, recursive: true);
            Directory.Delete(srcDir2, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- Timestamp: moved file retains original LastWriteTime -------------------

    [Fact]
    public async Task ExecuteOrganizeAsync_MovedFile_PreservesLastWriteTime()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            DateTime originalMtime = new(2020, 6, 15, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(jpg, originalMtime);

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false
            );
            (int moved, int ignored, int failed, int deletedDirs) = await task.ExecuteOrganizeAsync(
                [jpg],
                [],
                TestContext.Current.CancellationToken
            );

            moved.Should().Be(1);
            string dest = Path.Combine(outDir, "0001-01", "photo.jpg");
            File.Exists(dest).Should().BeTrue();
            File.GetLastWriteTimeUtc(dest)
                .Should()
                .BeCloseTo(originalMtime, TimeSpan.FromSeconds(1));
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- DeleteEmpty: empty subdirectories are removed after organize ----------

    [Fact]
    public async Task ExecuteOrganizeAsync_DeleteEmpty_RemovesEmptySubdirectories()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            // Create nested structure: srcDir/sub/nested/ with one photo in sub/
            string subDir = Path.Combine(srcDir, "sub");
            string nestedDir = Path.Combine(subDir, "nested");
            Directory.CreateDirectory(nestedDir);

            string jpg = Path.Combine(subDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: true
            );
            (int moved, int ignored, int failed, int deletedDirs) = await task.ExecuteOrganizeAsync(
                [jpg],
                [new DirectoryInfo(srcDir)],
                TestContext.Current.CancellationToken
            );

            moved.Should().Be(1);
            failed.Should().Be(0);
            deletedDirs.Should().Be(2); // nested/ then sub/
            Directory.Exists(nestedDir).Should().BeFalse();
            Directory.Exists(subDir).Should().BeFalse();
            Directory.Exists(srcDir).Should().BeTrue(); // root itself is not deleted
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }
}
