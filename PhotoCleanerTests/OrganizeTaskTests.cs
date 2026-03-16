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

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"OrganizeTaskTest_{Path.GetRandomFileName()}.db");

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
                deleteEmpty: false,
                move: false,
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([txt], [], TestContext.Current.CancellationToken);

            organized.Should().Be(0);
            ignored.Should().Be(1);
            skippedSamePath.Should().Be(0);
            skippedDuplicate.Should().Be(0);
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
                deleteEmpty: false,
                move: false,
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skippedSamePath.Should().Be(0);
            skippedDuplicate.Should().Be(0);
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

    // -- Supported file with EXIF date: copied to correct date subdir (default copy) --

    [Fact]
    public async Task ExecuteOrganizeAsync_SupportedFile_CopiedToDateSubdir()
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
                deleteEmpty: false,
                move: false,
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skippedSamePath.Should().Be(0);
            skippedDuplicate.Should().Be(0);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            File.Exists(jpg).Should().BeTrue(); // source retained (copy default)
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
                deleteEmpty: false,
                move: false,
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skippedSamePath.Should().Be(0);
            skippedDuplicate.Should().Be(0);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            File.Exists(jpg).Should().BeTrue(); // source retained (copy default)
            File.Exists(Path.Combine(outDir, "0001-01", "photo.jpg")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- Same-name collision: second file overwrites the first -----------------

    [Fact]
    public async Task ExecuteOrganizeAsync_SameNameFilesInSameMonth_SecondOverwritesFirst()
    {
        string srcDir1 = TempDir();
        string srcDir2 = TempDir();
        string outDir = TempDir();
        try
        {
            // Two files named photo.jpg, both without EXIF date -> same destination
            string jpg1 = Path.Combine(srcDir1, "photo.jpg");
            string jpg2 = Path.Combine(srcDir2, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg1);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg2);
            // Make jpg2 distinguishable by size
            await File.AppendAllTextAsync(jpg2, "extra", TestContext.Current.CancellationToken);

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false,
                move: false,
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync(
                [jpg1, jpg2],
                [],
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(2);
            ignored.Should().Be(0);
            skippedSamePath.Should().Be(0);
            skippedDuplicate.Should().Be(0);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            // Only one file at the destination (second overwrites first)
            string dest = Path.Combine(outDir, "0001-01", "photo.jpg");
            File.Exists(dest).Should().BeTrue();
            File.Exists(Path.Combine(outDir, "0001-01", "photo_1.jpg")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(srcDir1, recursive: true);
            Directory.Delete(srcDir2, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- Timestamp: organized file retains original LastWriteTime ---------------

    [Fact]
    public async Task ExecuteOrganizeAsync_OrganizedFile_PreservesLastWriteTime()
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
                deleteEmpty: false,
                move: false,
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(1);
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

    // -- MoveFlag: source file removed when --move is set ----------------------

    [Fact]
    public async Task ExecuteOrganizeAsync_MoveFlag_SourceFileRemoved()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:03:10 08:00:00");

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false,
                move: true,
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skippedSamePath.Should().Be(0);
            skippedDuplicate.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(jpg).Should().BeFalse(); // source removed (move)
            File.Exists(Path.Combine(outDir, "2024-03", "photo.jpg")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- CopyDefault: source file retained when --move is not set --------------

    [Fact]
    public async Task ExecuteOrganizeAsync_CopyDefault_SourceFileRetained()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:03:10 08:00:00");

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false,
                move: false,
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(1);
            File.Exists(jpg).Should().BeTrue(); // source retained (copy)
            File.Exists(Path.Combine(outDir, "2024-03", "photo.jpg")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- DeleteEmpty: empty subdirectories are removed after move -------------

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
                deleteEmpty: true,
                move: true, // move so source dirs become empty
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync(
                [jpg],
                [new DirectoryInfo(srcDir)],
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
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

    // -- Database: duplicate hash skipped -------------------------------------

    [Fact]
    public async Task ExecuteOrganizeAsync_WithDatabase_DuplicateHash_Skipped()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            // Pre-record the file's hash so it appears already organized
            string hash = await Database.ComputeHashAsync(jpg);
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            await db.InsertAsync(
                new OrganizedFileRecord(
                    hash,
                    jpg,
                    "photo.jpg",
                    null,
                    null,
                    new FileInfo(jpg).Length,
                    null,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    "/already/organized/photo.jpg"
                )
            );

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false,
                move: false,
                database: db
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(0);
            ignored.Should().Be(0);
            skippedSamePath.Should().Be(1);
            skippedDuplicate.Should().Be(0);
            failed.Should().Be(0);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    // -- Database: different source path with same hash counted as skippedDuplicate --

    [Fact]
    public async Task ExecuteOrganizeAsync_WithDatabase_DifferentSourcePath_CountedSeparately()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            // Pre-record the same hash but from a different source path
            string hash = await Database.ComputeHashAsync(jpg);
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            await db.InsertAsync(
                new OrganizedFileRecord(
                    hash,
                    "/other/dir/photo.jpg",
                    "photo.jpg",
                    null,
                    null,
                    new FileInfo(jpg).Length,
                    null,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    "/already/organized/photo.jpg"
                )
            );

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false,
                move: false,
                database: db
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(0);
            ignored.Should().Be(0);
            skippedSamePath.Should().Be(0);
            skippedDuplicate.Should().Be(1);
            failed.Should().Be(0);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    // -- SubdirectoryFormat: nested directories created from yyyy/MM format ---

    [Fact]
    public async Task ExecuteOrganizeAsync_SubdirectoryFormat_CreatesNestedDirs()
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
                "yyyy/MM",
                threads: 1,
                deleteEmpty: false,
                move: false,
                database: null
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skippedSamePath.Should().Be(0);
            skippedDuplicate.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(Path.Combine(outDir, "2024", "06", "photo.jpg")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- Database: new file organized and recorded ----------------------------

    [Fact]
    public async Task ExecuteOrganizeAsync_WithDatabase_NewFile_RecordedInDb()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            string hash = await Database.ComputeHashAsync(jpg);
            await using Database db = new(dbPath);
            await db.InitializeAsync();

            OrganizeTask task = new(
                dryRun: false,
                new DirectoryInfo(outDir),
                "yyyy-MM",
                threads: 1,
                deleteEmpty: false,
                move: false,
                database: db
            );
            (
                int organized,
                int ignored,
                int skippedSamePath,
                int skippedDuplicate,
                int failed,
                int deletedDirs
            ) = await task.ExecuteOrganizeAsync([jpg], [], TestContext.Current.CancellationToken);

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skippedSamePath.Should().Be(0);
            skippedDuplicate.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(Path.Combine(outDir, "2024-06", "photo.jpg")).Should().BeTrue();
            bool recorded = await db.ExistsAsync(hash);
            recorded.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }
}
