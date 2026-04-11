using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class OrganizeTaskTests(TempDirectoryFixture fixture)
    : IClassFixture<TempDirectoryFixture>,
        IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        if (!fixture.IsAvailable)
        {
            Assert.Skip(fixture.SkipReason ?? "Required tools not available");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CommandLine.Options CreateOptions(
        string outPath,
        bool dryRun = false,
        string format = "yyyy-MM",
        bool deleteEmpty = false,
        bool move = false,
        bool rehash = false,
        bool tagPath = false,
        bool datePath = false
    ) =>
        new()
        {
            Path = new DirectoryInfo(Path.GetTempPath()),
            Threads = 1,
            DryRun = dryRun,
            DatePath = datePath,
            SkipBackup = false,
            OutPath = new DirectoryInfo(outPath),
            Format = format,
            DeleteEmpty = deleteEmpty,
            Move = move,
            TagPath = tagPath,
            Tags = null,
            DbFile = null,
            OutDbFile = null,
            Rehash = rehash,
            ShortVideoDuration = MediaUtilities.ShortVideoDuration,
            Reprocess = false,
            ImmichUrl = null,
            ImmichApiKey = null,
            TrashDbFile = null,
            SkipDbFile = null,
            LogOptions = new LoggerFactory.Options
            {
                Level = LogEventLevel.Information,
                File = null,
                FileClear = false,
            },
        };

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
    public async Task ExecuteAsync_UnsupportedFile_Ignored()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string txt = Path.Combine(srcDir, "notes.txt");
            Touch(txt);

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [txt],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            ignored.Should().Be(1);
            skipped.Should().Be(0);
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
    public async Task ExecuteAsync_DryRun_ReportsCountButLeavesFiles()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            OrganizeTask task = new(
                CreateOptions(outDir, dryRun: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skipped.Should().Be(0);
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
    public async Task ExecuteAsync_SupportedFile_CopiedToDateSubdir()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skipped.Should().Be(0);
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
    public async Task ExecuteAsync_NoExifDate_FallsBackToMinValue()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            // No EXIF date set -> falls back to DateTime.MinValue

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skipped.Should().Be(0);
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

    // -- Same-name collision: second file gets a unique name ------------------

    [Fact]
    public async Task ExecuteAsync_SameNameFilesInSameMonth_SecondGetsUniqueName()
    {
        string srcDir1 = TempDir();
        string srcDir2 = TempDir();
        string outDir = TempDir();
        try
        {
            // Two files named photo.jpg, both without EXIF date -> same destination bucket
            string jpg1 = Path.Combine(srcDir1, "photo.jpg");
            string jpg2 = Path.Combine(srcDir2, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg1);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg2);
            // Make jpg2 distinguishable by size
            await File.AppendAllTextAsync(jpg2, "extra", TestContext.Current.CancellationToken);
            long jpg2Size = new FileInfo(jpg2).Length;

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg1, jpg2],
                new DirectoryInfo(srcDir1),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(2);
            ignored.Should().Be(0);
            skipped.Should().Be(0);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            // Both files exist: first at photo.jpg, second renamed to photo_1.jpg
            File.Exists(Path.Combine(outDir, "0001-01", "photo.jpg")).Should().BeTrue();
            string renamed = Path.Combine(outDir, "0001-01", "photo_1.jpg");
            File.Exists(renamed).Should().BeTrue();
            new FileInfo(renamed).Length.Should().Be(jpg2Size);
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
    public async Task ExecuteAsync_OrganizedFile_PreservesLastWriteTime()
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
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

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
    public async Task ExecuteAsync_MoveFlag_SourceFileRemoved()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:03:10 08:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir, move: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skipped.Should().Be(0);
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
    public async Task ExecuteAsync_CopyDefault_SourceFileRetained()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:03:10 08:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

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
    public async Task ExecuteAsync_DeleteEmpty_RemovesEmptySubdirectories()
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
                CreateOptions(outDir, deleteEmpty: true, move: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
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
    public async Task ExecuteAsync_WithDatabase_DuplicateHash_Skipped()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            // Pre-record the file's hash so it appears already organized
            (string sha256, string sha1) = await Database.ComputeHashesAsync(jpg);
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            FileInfo info = new(jpg);
            await db.InsertAsync(
                new FileRecord(
                    "/already/organized/photo.jpg",
                    sha256,
                    sha1,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    false
                )
            );

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            ignored.Should().Be(0);
            skipped.Should().Be(1);
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

    // -- SubdirectoryFormat: nested directories created from custom format ---

    [Fact]
    public async Task ExecuteAsync_SubdirectoryFormat_CreatesNestedDirs()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir, format: "yyyy/MM"),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skipped.Should().Be(0);
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
    public async Task ExecuteAsync_WithDatabase_NewFile_RecordedInDb()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            (string sha256, string _) = await Database.ComputeHashesAsync(jpg);
            await using Database db = new(dbPath);
            await db.InitializeAsync();

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            ignored.Should().Be(0);
            skipped.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(Path.Combine(outDir, "2024-06", "photo.jpg")).Should().BeTrue();
            bool recorded = await db.Sha256ExistsAsync(sha256);
            recorded.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    // -- Helpers --------------------------------------------------------------

    private static async Task<string[]> GetXmpSubjectAsync(string filePath)
    {
        BufferedCommandResult result = await Cli.Wrap("exiftool")
            .WithArguments(["-groupNames", "-json", filePath])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        ExifToolJson? meta = JsonSerializer.Deserialize(
            result.StandardOutput.AsSpan().Trim([' ', '\n', '\r', '[', ']']),
            ExifToolJsonContext.Default.ExifToolJson
        );
        return meta?.XMPSubject ?? [];
    }

    private static Task SetXmpSubjectAsync(string filePath, string tag) =>
        Cli.Wrap("exiftool")
            .WithArguments(["-overwrite_original", $"-XMP:Subject={tag}", filePath])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

    // -- tagPath: sub-path tokens applied as XMP:Subject ----------------------

    [Fact]
    public async Task ExecuteAsync_TagPath_SubPath_AddsXmpSubjectTags()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string subDir = Path.Combine(srcDir, "vacation", "beach");
            Directory.CreateDirectory(subDir);
            string jpg = Path.Combine(subDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir, tagPath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            string dest = Path.Combine(outDir, "2024-06", "photo.jpg");
            File.Exists(dest).Should().BeTrue();
            string[] tags = await GetXmpSubjectAsync(dest);
            tags.Should().Contain("vacation");
            tags.Should().Contain("beach");
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagPath_RootLevelFile_NoTagsAdded()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir, tagPath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            string dest = Path.Combine(outDir, "2024-06", "photo.jpg");
            File.Exists(dest).Should().BeTrue();
            string[] tags = await GetXmpSubjectAsync(dest);
            tags.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagPath_NoDuplicateTags()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string subDir = Path.Combine(srcDir, "trip");
            Directory.CreateDirectory(subDir);
            string jpg = Path.Combine(subDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetXmpSubjectAsync(jpg, "trip");
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir, tagPath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            string dest = Path.Combine(outDir, "2024-06", "photo.jpg");
            string[] tags = await GetXmpSubjectAsync(dest);
            tags.Where(t => t == "trip").Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagPath_PreservesExistingTags()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string subDir = Path.Combine(srcDir, "newdir");
            Directory.CreateDirectory(subDir);
            string jpg = Path.Combine(subDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetXmpSubjectAsync(jpg, "existingtag");
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir, tagPath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            string dest = Path.Combine(outDir, "2024-06", "photo.jpg");
            string[] tags = await GetXmpSubjectAsync(dest);
            tags.Should().Contain("existingtag");
            tags.Should().Contain("newdir");
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagPath_False_DoesNotAddTags()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string subDir = Path.Combine(srcDir, "vacation");
            Directory.CreateDirectory(subDir);
            string jpg = Path.Combine(subDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            string dest = Path.Combine(outDir, "2024-06", "photo.jpg");
            string[] tags = await GetXmpSubjectAsync(dest);
            tags.Should().NotContain("vacation");
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagPath_DryRun_NoTagsApplied()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string subDir = Path.Combine(srcDir, "vacation");
            Directory.CreateDirectory(subDir);
            string jpg = Path.Combine(subDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir, dryRun: true, tagPath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- datePath: missing EXIF date inferred from path -----------------------

    [Fact]
    public async Task ExecuteAsync_DatePath_DateInferredFromPath_SetsExifDateAndOrganizesCorrectly()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string datedDir = Path.Combine(srcDir, "2021", "05", "02");
            Directory.CreateDirectory(datedDir);
            string jpg = Path.Combine(datedDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            OrganizeTask task = new(
                CreateOptions(outDir, datePath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            failed.Should().Be(0);
            string dest = Path.Combine(outDir, "2021-05", "photo.jpg");
            File.Exists(dest).Should().BeTrue();
            ExifToolJson? meta = await MediaUtilities.GetExifToolJsonAsync(dest);
            meta!.IsDateSet().Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DatePath_FileAlreadyHasDate_DateNotOverwritten()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string datedDir = Path.Combine(srcDir, "2021", "05", "02");
            Directory.CreateDirectory(datedDir);
            string jpg = Path.Combine(datedDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            OrganizeTask task = new(
                CreateOptions(outDir, datePath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            // EXIF date wins: file organized to 2024-06, not 2021-05
            string dest = Path.Combine(outDir, "2024-06", "photo.jpg");
            File.Exists(dest).Should().BeTrue();
            File.Exists(Path.Combine(outDir, "2021-05", "photo.jpg")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DatePath_False_DateNotSetFromPath()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string datedDir = Path.Combine(srcDir, "2021", "05", "02");
            Directory.CreateDirectory(datedDir);
            string jpg = Path.Combine(datedDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            // No date inference: falls back to DateTime.MinValue bucket
            File.Exists(Path.Combine(outDir, "0001-01", "photo.jpg")).Should().BeTrue();
            File.Exists(Path.Combine(outDir, "2021-05", "photo.jpg")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DatePath_DryRun_NoDateWritten()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string datedDir = Path.Combine(srcDir, "2021", "05", "02");
            Directory.CreateDirectory(datedDir);
            string jpg = Path.Combine(datedDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            OrganizeTask task = new(
                CreateOptions(outDir, dryRun: true, datePath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- UnknownExtension: non-media extensions tracked in returned dictionary --

    [Fact]
    public async Task ExecuteAsync_NonMediaFile_TracksExtension()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string txt = Path.Combine(srcDir, "notes.txt");
            string jpg = Path.Combine(srcDir, "photo.jpg");
            Touch(txt);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            SkippedExtensionTracker skippedExtensions = new();
            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                skippedExtensions
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [txt, jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            ignored.Should().Be(1);
            skipped.Should().Be(0);
            failed.Should().Be(0);
            skippedExtensions.Extensions.Should().Contain(".txt");
            skippedExtensions.Extensions.Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // -- TrashDb: file whose SHA-1 is in trash DB is skipped -------------------

    [Fact]
    public async Task ExecuteAsync_TrashDb_MatchingFileSkipped()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string trashDbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            // Compute SHA-1 and seed the trash DB
            (string _, string sha1) = await Database.ComputeHashesAsync(jpg);
            await using TrashDatabase trashDb = new(trashDbPath);
            await trashDb.InitializeAsync();
            await trashDb.InsertHashAsync(sha1);

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: trashDb,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            trashSkipped.Should().Be(1);
            skipDbSkipped.Should().Be(0);
            skipped.Should().Be(0);
            failed.Should().Be(0);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(trashDbPath);
        }
    }

    // -- TrashDb: file NOT in trash DB is organized normally --------------------

    [Fact]
    public async Task ExecuteAsync_TrashDb_NonMatchingFileOrganized()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string trashDbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            // Seed trash DB with a different hash
            await using TrashDatabase trashDb = new(trashDbPath);
            await trashDb.InitializeAsync();
            await trashDb.InsertHashAsync("0000000000000000000000000000000000000000");

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: null,
                trashDatabase: trashDb,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            trashSkipped.Should().Be(0);
            failed.Should().Be(0);
            Directory.GetFiles(outDir, "*.jpg", SearchOption.AllDirectories).Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(trashDbPath);
        }
    }

    // -- SkipDb: file whose SHA-256 is in skip DB is skipped -------------------

    [Fact]
    public async Task ExecuteAsync_SkipDb_MatchingFileSkipped()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string skipDbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            // Compute SHA-256 and seed the skip DB
            (string sha256, string sha1) = await Database.ComputeHashesAsync(jpg);
            await using Database skipDb = new(skipDbPath);
            await skipDb.InitializeAsync();
            FileInfo info = new(jpg);
            await skipDb.InsertAsync(
                new FileRecord(
                    "/dummy/path.jpg",
                    sha256,
                    sha1,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    false
                )
            );

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: skipDb,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            skipDbSkipped.Should().Be(1);
            trashSkipped.Should().Be(0);
            skipped.Should().Be(0);
            failed.Should().Be(0);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(skipDbPath);
        }
    }

    // -- SkipDb without Db: organize works with only skipdb --------------------

    [Fact]
    public async Task ExecuteAsync_SkipDbWithoutDb_NonMatchingFileOrganized()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string skipDbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            // Seed skip DB with a different hash so the file is not skipped
            await using Database skipDb = new(skipDbPath);
            await skipDb.InitializeAsync();
            FileInfo info = new(jpg);
            await skipDb.InsertAsync(
                new FileRecord(
                    "/dummy/other.jpg",
                    "0000000000000000000000000000000000000000000000000000000000000000",
                    null,
                    1,
                    0,
                    false
                )
            );

            OrganizeTask task = new(
                CreateOptions(outDir),
                database: null,
                skipDatabase: skipDb,
                trashDatabase: null,
                new()
            );
            (
                int organized,
                int ignored,
                int skipped,
                int skipDbSkipped,
                int trashSkipped,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            skipDbSkipped.Should().Be(0);
            failed.Should().Be(0);
            Directory.GetFiles(outDir, "*.jpg", SearchOption.AllDirectories).Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(skipDbPath);
        }
    }
}
