using System.Collections.Concurrent;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Data.Sqlite;
using PhotoCleaner;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class ImportTaskTests(TempDirectoryFixture fixture)
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

    internal static CommandLine.Options CreateOptions(
        string outPath,
        bool dryRun = false,
        string format = "yyyy-MM",
        bool deleteEmpty = false,
        bool move = false,
        bool rehash = false,
        bool tagPath = false,
        bool datePath = false,
        bool imageHash = false,
        string? tags = null,
        int threads = 1,
        string? path = null,
        FileInfo? dbFile = null
    ) =>
        new()
        {
            Path = new DirectoryInfo(path ?? Path.GetTempPath()),
            Threads = threads,
            DryRun = dryRun,
            DatePath = datePath,
            SkipBackup = false,
            OutPath = new DirectoryInfo(outPath),
            Format = format,
            DeleteEmpty = deleteEmpty,
            Move = move,
            TagPath = tagPath,
            Tags = tags,
            DbFile = dbFile,
            Rehash = rehash,
            ShortVideoDuration = MediaUtilities.ShortVideoDuration,
            Reprocess = false,
            MarkProcessed = false,
            ImmichUrl = null,
            ImmichApiKey = null,
            TrashDbFile = null,
            SkipDbFile = null,
            ImageHash = imageHash,
            LogOptions = new LoggerFactory.Options
            {
                Level = LogEventLevel.Information,
                File = null,
                FileClear = false,
            },
        };

    internal static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ImportTaskTest_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"ImportTaskTest_{Path.GetRandomFileName()}.db");

    private static void Touch(string path) => File.WriteAllBytes(path, []);

    private static async Task SetExifDateAsync(string filePath, string date) =>
        await Cli.Wrap("exiftool")
            .WithArguments(["-overwrite_original", $"-EXIF:DateTimeOriginal={date}", filePath])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

    // -- Invalid: exiftool reports an error, file is skipped -----------------

    [Fact]
    public async Task ExecuteAsync_ExifToolErrorFile_IsInvalidAndNotImported()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            // A file whose bytes are not a real image makes exiftool report a format error.
            string bad = Path.Combine(srcDir, "garbage.jpg");
            await File.WriteAllBytesAsync(
                bad,
                [.. Enumerable.Range(0, 500).Select(i => (byte)(i % 251))],
                TestContext.Current.CancellationToken
            );

            ImportTask task = new(
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
                int invalid,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [bad],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            // Invalid is its own outcome, distinct from failed, and nothing is copied out.
            invalid.Should().Be(1);
            failed.Should().Be(0);
            organized.Should().Be(0);
            ignored.Should().Be(0);
            skipped.Should().Be(0);
            skipDbSkipped.Should().Be(0);
            trashSkipped.Should().Be(0);
            deletedDirs.Should().Be(0);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
            File.Exists(bad).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, true);
            Directory.Delete(outDir, true);
        }
    }

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

            ImportTask task = new(
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
                int invalid,
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

            ImportTask task = new(
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
                int invalid,
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

            ImportTask task = new(
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
                int invalid,
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

            ImportTask task = new(
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
                int invalid,
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

            ImportTask task = new(
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
                int invalid,
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

            ImportTask task = new(
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
                int invalid,
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
            DateTime sourceTime = new(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(jpg, sourceTime);

            ImportTask task = new(
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
                int invalid,
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
            string dest = Path.Combine(outDir, "2024-03", "photo.jpg");
            File.Exists(dest).Should().BeTrue();
            File.GetLastWriteTimeUtc(dest).Should().Be(sourceTime);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MoveWithDatabase_RecordsSource()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:03:10 08:00:00");
            long length = new FileInfo(jpg).Length;

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                CreateOptions(outDir, move: true),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            failed.Should().Be(0);
            FileRecord? row = await db.GetByPathAsync(jpg, TestContext.Current.CancellationToken);
            row.Should().NotBeNull();
            row!.FileSize.Should().Be(length);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
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

            ImportTask task = new(
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
                int invalid,
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

    // -- DeleteEmpty: empty target subdirectories are removed -----------------

    [Fact]
    public async Task ExecuteAsync_DeleteEmpty_RemovesEmptyTargetSubdirectories()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            // Pre-create empty subdirectories in the target so we can confirm they get cleaned.
            string emptyA = Path.Combine(outDir, "empty-a");
            string emptyNested = Path.Combine(outDir, "empty-b", "nested");
            Directory.CreateDirectory(emptyA);
            Directory.CreateDirectory(emptyNested);

            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            ImportTask task = new(
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
                int invalid,
                int failed,
                int deletedDirs
            ) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            failed.Should().Be(0);
            deletedDirs.Should().Be(3); // empty-a, empty-b/nested, empty-b
            Directory.Exists(emptyA).Should().BeFalse();
            Directory.Exists(emptyNested).Should().BeFalse();
            Directory.Exists(Path.Combine(outDir, "empty-b")).Should().BeFalse();
            Directory.Exists(outDir).Should().BeTrue(); // target root itself is not deleted
            Directory.Exists(srcDir).Should().BeTrue(); // source root is not touched
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
            (string sha256, string sha1) = await Database.ComputeHashesAsync(
                jpg,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            FileInfo info = new(jpg);
            await db.InsertAsync(
                new FileRecord(
                    "/already/organized/photo.jpg",
                    sha256,
                    sha1,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    false
                ),
                cancellationToken: TestContext.Current.CancellationToken
            );

            ImportTask task = new(
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
                int invalid,
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

            ImportTask task = new(
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
                int invalid,
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

            (string sha256, string _) = await Database.ComputeHashesAsync(
                jpg,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            ImportTask task = new(
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
                int invalid,
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
            bool recorded = await db.Sha256ExistsAsync(
                sha256,
                cancellationToken: TestContext.Current.CancellationToken
            );
            recorded.Should().BeTrue();

            // The row identifies the source file, not the destination.
            // Dedup therefore survives later mutation of the destination.
            FileRecord? row = await db.GetByPathAsync(
                jpg,
                cancellationToken: TestContext.Current.CancellationToken
            );
            row.Should().NotBeNull();
            row.Sha256.Should().Be(sha256);
            row.FileSize.Should().Be(new FileInfo(jpg).Length);
            row.MtimeTicks.Should().Be(new FileInfo(jpg).LastWriteTimeUtc.Ticks);
            FileRecord? destRow = await db.GetByPathAsync(
                Path.Combine(outDir, "2024-06", "photo.jpg"),
                cancellationToken: TestContext.Current.CancellationToken
            );
            destRow.Should().BeNull();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    // -- Database: tag-mutated dest does not affect the source-keyed row ------

    [Fact]
    public async Task ExecuteAsync_TagPathMutatesDest_SourceRowIsUntouched()
    {
        // Import keys its row by source path even though tag injection mutates the destination.
        // No command writes that row in normal use, so dedup stays stable.
        string srcDir = TempDir();
        // The file needs a sub-directory for --tagpath to have any tokens to apply.
        // ComputePathTags returns nothing for files at the root of --path.
        string srcSubDir = Path.Combine(srcDir, "vacation");
        Directory.CreateDirectory(srcSubDir);
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcSubDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");
            (string srcSha256, string _) = await Database.ComputeHashesAsync(
                jpg,
                cancellationToken: TestContext.Current.CancellationToken
            );

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            ImportTask task = new(
                CreateOptions(outDir, tagPath: true),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, _, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );
            organized.Should().Be(1);

            // Writing XMP tags changed the destination, so it no longer hashes to the source.
            string dest = Path.Combine(outDir, "2024-06", "photo.jpg");
            File.Exists(dest).Should().BeTrue();
            (string destSha256, _) = await Database.ComputeHashesAsync(
                dest,
                cancellationToken: TestContext.Current.CancellationToken
            );
            destSha256.Should().NotBe(srcSha256);

            // The DB row is keyed by the SOURCE path and stores the SOURCE hash.
            FileRecord? srcRow = await db.GetByPathAsync(
                jpg,
                cancellationToken: TestContext.Current.CancellationToken
            );
            srcRow.Should().NotBeNull();
            srcRow.Sha256.Should().Be(srcSha256);

            // No row is inserted for the destination path.
            // A separate Process.db tracks destination state, and import never writes there.
            FileRecord? destRow = await db.GetByPathAsync(
                dest,
                cancellationToken: TestContext.Current.CancellationToken
            );
            destRow.Should().BeNull();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    // -- Database: import twice with same source -> no duplicate --------------

    [Fact]
    public async Task ExecuteAsync_ImportTwice_SecondRunSkipsAndCreatesNoDuplicate()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            ImportTask first = new(
                CreateOptions(outDir),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized1, _, int skipped1, _, _, _, int failed1, _) = await first.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );
            organized1.Should().Be(1);
            skipped1.Should().Be(0);
            failed1.Should().Be(0);

            ImportTask second = new(
                CreateOptions(outDir),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized2, _, int skipped2, _, _, _, int failed2, _) = await second.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized2.Should().Be(0);
            skipped2.Should().Be(1);
            failed2.Should().Be(0);
            File.Exists(Path.Combine(outDir, "2024-06", "photo_1.jpg")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReplacedSource_UpdatesRecordAndNextRunSkips()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            await SetExifDateAsync(jpg, "2024:06:15 10:00:00");

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                CreateOptions(outDir),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );

            (int organized1, _, int skipped1, _, _, _, int failed1, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );
            organized1.Should().Be(1);
            skipped1.Should().Be(0);
            failed1.Should().Be(0);

            DateTime firstMtime = File.GetLastWriteTimeUtc(jpg);
            await SetExifDateAsync(jpg, "2024:07:15 10:00:00");
            File.SetLastWriteTimeUtc(jpg, firstMtime.AddSeconds(2));
            FileInfo replacedInfo = new(jpg);
            (string replacedSha256, string replacedSha1) = await Database.ComputeHashesAsync(
                jpg,
                cancellationToken: TestContext.Current.CancellationToken
            );

            (int organized2, _, int skipped2, _, _, _, int failed2, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );
            organized2.Should().Be(1);
            skipped2.Should().Be(0);
            failed2.Should().Be(0);

            FileRecord? row = await db.GetByPathAsync(
                jpg,
                cancellationToken: TestContext.Current.CancellationToken
            );
            row.Should().NotBeNull();
            row.Sha256.Should().Be(replacedSha256);
            row.Sha1.Should().Be(replacedSha1);
            row.FileSize.Should().Be(replacedInfo.Length);
            row.MtimeTicks.Should().Be(replacedInfo.LastWriteTimeUtc.Ticks);

            (int organized3, _, int skipped3, _, _, _, int failed3, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );
            organized3.Should().Be(0);
            skipped3.Should().Be(1);
            failed3.Should().Be(0);
            File.Exists(Path.Combine(outDir, "2024-07", "photo_1.jpg")).Should().BeFalse();
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

            ImportTask task = new(
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

            ImportTask task = new(
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

            ImportTask task = new(
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

            ImportTask task = new(
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

            ImportTask task = new(
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

            ImportTask task = new(
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
                int invalid,
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

    private static string ExifToolStub(string dir, string output, int exitCode) =>
        TempDirectoryFixture.WriteExifToolStub(
            dir,
            $"for last in \"$@\"; do :; done\ntouch \"$last\"\necho '{output}' >&2\nexit {exitCode}\n"
        );

    internal static async Task<(string jpg, string dest)> CreateTaggedSourceAsync(
        TempDirectoryFixture media,
        string srcDir
    )
    {
        string subDir = Path.Combine(srcDir, "album");
        Directory.CreateDirectory(subDir);
        string jpg = Path.Combine(subDir, "photo.jpg");
        File.Copy(media.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
        await SetExifDateAsync(jpg, "2024:06:15 10:00:00");
        return (jpg, Path.Combine("2024-06", "photo.jpg"));
    }

    [Theory]
    [InlineData(1, "Error: stub write failure")]
    [InlineData(0, "Error: stub write failure")]
    public async Task ExecuteAsync_TagWriteFails_CountsFailedAndRemovesCopy(
        int exitCode,
        string output
    )
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string stub = ExifToolStub(srcDir, output, exitCode);
            (string jpg, string dest) = await CreateTaggedSourceAsync(fixture, srcDir);

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                CreateOptions(outDir, tagPath: true, tags: "extra"),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            failed.Should().Be(1);
            File.Exists(jpg).Should().BeTrue();
            File.Exists(Path.Combine(outDir, dest)).Should().BeFalse();
            (await db.GetByPathAsync(jpg, TestContext.Current.CancellationToken)).Should().BeNull();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagWriteSucceeds_CountsImported()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            (string jpg, string dest) = await CreateTaggedSourceAsync(fixture, srcDir);

            ImportTask task = new(
                CreateOptions(outDir, tagPath: true, tags: "extra"),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            failed.Should().Be(0);
            string[] tags = await GetXmpSubjectAsync(Path.Combine(outDir, dest));
            tags.Should().BeEquivalentTo(["album", "extra"]);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagWriteFailsOnMove_KeepsMovedFileWithSourceTime()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string stub = ExifToolStub(srcDir, "Error: stub write failure", 1);
            (string jpg, string dest) = await CreateTaggedSourceAsync(fixture, srcDir);
            DateTime sourceTime = new(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(jpg, sourceTime);

            ImportTask task = new(
                CreateOptions(outDir, move: true, tagPath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            failed.Should().Be(1);
            File.Exists(jpg).Should().BeFalse();
            File.GetLastWriteTimeUtc(Path.Combine(outDir, dest)).Should().Be(sourceTime);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagWriteFailsOnMoveWithDatabase_RecordsKeptFileSoACopySkips()
    {
        string srcDir = TempDir();
        string otherDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string stub = ExifToolStub(otherDir, "Error: stub write failure", 1);
            (string jpg, string dest) = await CreateTaggedSourceAsync(fixture, srcDir);
            string copy = Path.Combine(otherDir, "photo.jpg");
            File.Copy(jpg, copy);
            (string sha256, _) = await Database.ComputeHashesAsync(
                jpg,
                TestContext.Current.CancellationToken
            );

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask moveTask = new(
                CreateOptions(outDir, move: true, tags: "extra"),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            (int moved, _, _, _, _, _, int moveFailed, _) = await moveTask.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            moved.Should().Be(0);
            moveFailed.Should().Be(1);
            File.Exists(Path.Combine(outDir, dest)).Should().BeTrue();
            FileRecord? row = await db.GetByPathAsync(jpg, TestContext.Current.CancellationToken);
            row.Should().NotBeNull();
            row!.Sha256.Should().Be(sha256);

            ImportTask copyTask = new(
                CreateOptions(outDir),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int copied, _, int skipped, _, _, _, int copyFailed, _) = await copyTask.ExecuteAsync(
                [copy],
                new DirectoryInfo(otherDir),
                TestContext.Current.CancellationToken
            );

            copied.Should().Be(0);
            skipped.Should().Be(1);
            copyFailed.Should().Be(0);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(otherDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagWriteFailsOnMoveWithDatabase_SkipsAMatchWaitingInTheRun()
    {
        string srcDir = TempDir();
        string otherDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            // The slow failing write keeps the claim held while the matching file waits on it
            string stub = TempDirectoryFixture.WriteExifToolStub(
                otherDir,
                "sleep 2\necho 'Error: stub write failure' >&2\nexit 1\n"
            );
            (string jpg, _) = await CreateTaggedSourceAsync(fixture, srcDir);
            string copy = Path.Combine(otherDir, "photo.jpg");
            File.Copy(jpg, copy);

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                CreateOptions(outDir, move: true, tags: "extra", threads: 2),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            (int organized, _, int skipped, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg, copy],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            failed.Should().Be(1);
            skipped.Should().Be(1);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(otherDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RowWriteFailsOnMove_SkipsAMatchWaitingInTheRun()
    {
        string srcDir = TempDir();
        string otherDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string stub = TempDirectoryFixture.WriteExifToolStub(otherDir, "sleep 2\nexit 0\n");
            (string jpg, _) = await CreateTaggedSourceAsync(fixture, srcDir);
            string copy = Path.Combine(otherDir, "photo.jpg");
            File.Copy(jpg, copy);
            await using (Database writable = new(dbPath))
            {
                await writable.InitializeAsync(TestContext.Current.CancellationToken);
            }

            await using Database db = new(dbPath, readOnly: true);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                CreateOptions(outDir, move: true, tags: "extra", threads: 2),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            (int organized, _, int skipped, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg, copy],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            failed.Should().Be(1);
            skipped.Should().Be(1);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(otherDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RowWriteFailsOnCopy_RemovesCopy(bool cachedRow)
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            (string jpg, string dest) = await CreateTaggedSourceAsync(fixture, srcDir);
            await using (Database writable = new(dbPath))
            {
                await writable.InitializeAsync(TestContext.Current.CancellationToken);
                if (cachedRow)
                {
                    await writable.InsertAsync(
                        new FileRecord(jpg, "stale", "stale", 1, 1, false),
                        TestContext.Current.CancellationToken
                    );
                }
            }

            // A read-only open makes the row write throw a SqliteException, not an IOException
            await using Database db = new(dbPath, readOnly: true);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                CreateOptions(outDir),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            failed.Should().Be(1);
            File.Exists(jpg).Should().BeTrue();
            File.Exists(Path.Combine(outDir, dest)).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Theory]
    [InlineData(0, "    1 image files updated", "", false)]
    [InlineData(0, "    1 image files updated", "Warning: [minor] Ignored empty value", false)]
    [InlineData(1, "    0 image files updated", "", true)]
    [InlineData(0, "", "Error: File not found - photo.jpg", true)]
    [InlineData(0, "Error: Not a valid JPEG", "", true)]
    public void IsWriteFailure_ReadsExitCodeAndErrorLines(
        int exitCode,
        string standardOutput,
        string standardError,
        bool expected
    ) =>
        MediaUtilities
            .IsWriteFailure(exitCode, standardOutput, standardError)
            .Should()
            .Be(expected);

    // -- datePath: missing EXIF date inferred from path -----------------------

    [Fact]
    public async Task ExecuteAsync_DateWriteFails_CountsFailedAndRemovesCopy()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string stub = ExifToolStub(srcDir, "Error: stub write failure", 1);
            string datedDir = Path.Combine(srcDir, "2021", "05", "02");
            Directory.CreateDirectory(datedDir);
            string jpg = Path.Combine(datedDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);

            ImportTask task = new(
                CreateOptions(outDir, datePath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            failed.Should().Be(1);
            File.Exists(jpg).Should().BeTrue();
            File.Exists(Path.Combine(outDir, "2021-05", "photo.jpg")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

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

            ImportTask task = new(
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
                int invalid,
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
            ExifToolJson? meta = await MediaUtilities.GetExifToolJsonAsync(
                dest,
                cancellationToken: TestContext.Current.CancellationToken
            );
            meta!.IsDateSet().Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DatePath_ThreeGp_SetsQuickTimeDate()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string datedDir = Path.Combine(srcDir, "2021", "05", "02");
            Directory.CreateDirectory(datedDir);
            string clip = Path.Combine(datedDir, "clip.3gp");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.ThreeGpFile), clip);
            ExifToolJson? source = await MediaUtilities.GetExifToolJsonAsync(
                clip,
                cancellationToken: TestContext.Current.CancellationToken
            );
            source!.IsDateSet().Should().BeFalse();

            ImportTask task = new(
                CreateOptions(outDir, datePath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [clip],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            failed.Should().Be(0);
            string dest = Path.Combine(outDir, "2021-05", "clip.3gp");
            ExifToolJson? meta = await MediaUtilities.GetExifToolJsonAsync(
                dest,
                cancellationToken: TestContext.Current.CancellationToken
            );
            meta!.QuickTimeCreateDate.Should().Be("2021:05:02 00:00:00");
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("2020:01:02 03:04:05+02:00", false)]
    [InlineData("2020:01:02 03:04:05.25-08:00", false)]
    [InlineData("2020:01:02 03:04:05+02:00", true)]
    public async Task ExecuteAsync_DateWithOffsetOrSubSeconds_FilesUnderRecordedDate(
        string xmpDate,
        bool datePath
    )
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string datedDir = Path.Combine(srcDir, "2021", "05", "02");
            Directory.CreateDirectory(datedDir);
            string jpg = Path.Combine(datedDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            _ = await Cli.Wrap("exiftool")
                .WithArguments(["-overwrite_original", $"-XMP:CreateDate={xmpDate}", jpg])
                .ExecuteBufferedAsync(TestContext.Current.CancellationToken);

            ImportTask task = new(
                CreateOptions(outDir, datePath: datePath),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            failed.Should().Be(0);
            File.Exists(Path.Combine(outDir, "2020-01", "photo.jpg")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DatePath_Gif_SetsXmpDate()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        try
        {
            string datedDir = Path.Combine(srcDir, "2021", "05", "02");
            Directory.CreateDirectory(datedDir);
            string gif = Path.Combine(datedDir, "anim.gif");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.GifFile), gif);
            ExifToolJson? source = await MediaUtilities.GetExifToolJsonAsync(
                gif,
                cancellationToken: TestContext.Current.CancellationToken
            );
            source!.IsDateSet().Should().BeFalse();

            ImportTask task = new(
                CreateOptions(outDir, datePath: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [gif],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            failed.Should().Be(0);
            string dest = Path.Combine(outDir, "2021-05", "anim.gif");
            ExifToolJson? meta = await MediaUtilities.GetExifToolJsonAsync(
                dest,
                cancellationToken: TestContext.Current.CancellationToken
            );
            meta!.XMPCreateDate.Should().Be("2021:05:02 00:00:00");
            BufferedCommandResult original = await Cli.Wrap("exiftool")
                .WithArguments(["-s3", "-XMP:DateTimeOriginal", dest])
                .ExecuteBufferedAsync(TestContext.Current.CancellationToken);
            original.StandardOutput.Trim().Should().Be("2021:05:02 00:00:00");
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

            ImportTask task = new(
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

            ImportTask task = new(
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

            ImportTask task = new(
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
                int invalid,
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
            ImportTask task = new(
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
                int invalid,
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
            (string _, string sha1) = await Database.ComputeHashesAsync(
                jpg,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await using TrashDatabase trashDb = new(trashDbPath);
            await trashDb.InitializeAsync(TestContext.Current.CancellationToken);
            await trashDb.InsertHashAsync(
                sha1,
                cancellationToken: TestContext.Current.CancellationToken
            );

            ImportTask task = new(
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
                int invalid,
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
            await trashDb.InitializeAsync(TestContext.Current.CancellationToken);
            await trashDb.InsertHashAsync(
                "0000000000000000000000000000000000000000",
                cancellationToken: TestContext.Current.CancellationToken
            );

            ImportTask task = new(
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
                int invalid,
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
            (string sha256, string sha1) = await Database.ComputeHashesAsync(
                jpg,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await using Database skipDb = new(skipDbPath);
            await skipDb.InitializeAsync(TestContext.Current.CancellationToken);
            FileInfo info = new(jpg);
            await skipDb.InsertAsync(
                new FileRecord(
                    "/dummy/path.jpg",
                    sha256,
                    sha1,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    false
                ),
                cancellationToken: TestContext.Current.CancellationToken
            );

            ImportTask task = new(
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
                int invalid,
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
            await skipDb.InitializeAsync(TestContext.Current.CancellationToken);
            FileInfo info = new(jpg);
            await skipDb.InsertAsync(
                new FileRecord(
                    "/dummy/other.jpg",
                    "0000000000000000000000000000000000000000000000000000000000000000",
                    "0000000000000000000000000000000000000000",
                    1,
                    0,
                    false
                ),
                cancellationToken: TestContext.Current.CancellationToken
            );

            ImportTask task = new(
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
                int invalid,
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

    // -- ImageHash: a metadata-only copy matches by image data hash, opt-in only --

    private static async Task<string?> GetImageHashAsync(string filePath) =>
        (
            await MediaUtilities.GetCatalogExifToolJsonAsync(
                filePath,
                TestContext.Current.CancellationToken
            )
        )?.ImageDataHash;

    private async Task<(string original, string copy)> CreateMetadataOnlyCopyAsync(string srcDir)
    {
        string original = Path.Combine(srcDir, "original", "IMG_1234.jpg");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), original);
        string copy = Path.Combine(srcDir, "IMG_1234.jpg");
        File.Copy(original, copy);
        await SetXmpSubjectAsync(copy, "extra");
        return (original, copy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_ImageHash_MetadataOnlyCopyOfImportedFile_SkippedOnlyWhenEnabled(
        bool imageHash
    )
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            (string original, string copy) = await CreateMetadataOnlyCopyAsync(srcDir);
            (string sha256, string sha1) = await Database.ComputeHashesAsync(
                original,
                TestContext.Current.CancellationToken
            );
            (string copySha256, _) = await Database.ComputeHashesAsync(
                copy,
                TestContext.Current.CancellationToken
            );
            copySha256.Should().NotBe(sha256);
            string? originalImageHash = await GetImageHashAsync(original);
            originalImageHash.Should().NotBeNullOrEmpty();
            (await GetImageHashAsync(copy)).Should().Be(originalImageHash);

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertAsync(
                new FileRecord(
                    "/already/imported/IMG_1234.jpg",
                    sha256,
                    sha1,
                    1L,
                    0L,
                    false,
                    ImageHash: originalImageHash,
                    MetadataRead: true
                ),
                TestContext.Current.CancellationToken
            );

            ImportTask task = new(
                CreateOptions(outDir, imageHash: imageHash),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, int skipped, _, int trashSkipped, _, int failed, _) =
                await task.ExecuteAsync(
                    [copy],
                    new DirectoryInfo(srcDir),
                    TestContext.Current.CancellationToken
                );

            organized.Should().Be(imageHash ? 0 : 1);
            skipped.Should().Be(imageHash ? 1 : 0);
            trashSkipped.Should().Be(0);
            failed.Should().Be(0);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_ImageHash_MetadataOnlyCopyOfTrashedFile_SkippedOnlyWhenEnabled(
        bool imageHash
    )
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string trashDbPath = TempDb();
        try
        {
            (string original, string copy) = await CreateMetadataOnlyCopyAsync(srcDir);
            (_, string sha1) = await Database.ComputeHashesAsync(
                original,
                TestContext.Current.CancellationToken
            );
            string? originalImageHash = await GetImageHashAsync(original);
            originalImageHash.Should().NotBeNullOrEmpty();

            await using TrashDatabase trashDb = new(trashDbPath);
            await trashDb.InitializeAsync(TestContext.Current.CancellationToken);
            await trashDb.InsertHashAsync(
                sha1,
                "IMG_1234.jpg",
                originalImageHash,
                TestContext.Current.CancellationToken
            );

            ImportTask task = new(
                CreateOptions(outDir, imageHash: imageHash),
                database: null,
                skipDatabase: null,
                trashDatabase: trashDb,
                new()
            );
            (int organized, _, int skipped, _, int trashSkipped, _, int failed, _) =
                await task.ExecuteAsync(
                    [copy],
                    new DirectoryInfo(srcDir),
                    TestContext.Current.CancellationToken
                );

            organized.Should().Be(imageHash ? 0 : 1);
            trashSkipped.Should().Be(imageHash ? 1 : 0);
            skipped.Should().Be(0);
            failed.Should().Be(0);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(trashDbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ImageHash_FileWithoutImageHash_NeverMatchesRowsWithoutOne()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string trashDbPath = TempDb();
        try
        {
            string gif = Path.Combine(srcDir, "clip.gif");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.GifFile), gif);
            (await GetImageHashAsync(gif)).Should().BeNull();

            // Rows without an image hash, as a format exiftool cannot hash leaves them
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertAsync(
                new FileRecord(
                    "/already/imported/other.gif",
                    "sha256-other",
                    "sha1-other",
                    1L,
                    0L,
                    false,
                    MetadataRead: true
                ),
                TestContext.Current.CancellationToken
            );
            await using TrashDatabase trashDb = new(trashDbPath);
            await trashDb.InitializeAsync(TestContext.Current.CancellationToken);
            await trashDb.InsertHashAsync(
                "0000000000000000000000000000000000000000",
                "other.gif",
                cancellationToken: TestContext.Current.CancellationToken
            );

            ImportTask task = new(
                CreateOptions(outDir, imageHash: true),
                database: db,
                skipDatabase: null,
                trashDatabase: trashDb,
                new()
            );
            (int organized, _, int skipped, _, int trashSkipped, _, int failed, _) =
                await task.ExecuteAsync(
                    [gif],
                    new DirectoryInfo(srcDir),
                    TestContext.Current.CancellationToken
                );

            organized.Should().Be(1);
            skipped.Should().Be(0);
            trashSkipped.Should().Be(0);
            failed.Should().Be(0);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(trashDbPath);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportCommand_DryRunWithDatabase_LeavesTheDatabaseUntouched(bool dbExists)
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbDir = TempDir();
        try
        {
            File.Copy(
                fixture.SourceFile(TempDirectoryFixture.SmallJpegFile),
                Path.Combine(srcDir, "photo.jpg")
            );
            string dbPath = Path.Combine(dbDir, "new", "Import.db");
            byte[]? before = null;
            if (dbExists)
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
                await using (
                    SqliteConnection connection = new($"Data Source={dbPath};Pooling=False")
                )
                {
                    await connection.OpenAsync(TestContext.Current.CancellationToken);
                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = """
                        CREATE TABLE files (
                            path         TEXT NOT NULL PRIMARY KEY,
                            sha256       TEXT NOT NULL,
                            sha1         TEXT NOT NULL,
                            file_size    INTEGER NOT NULL,
                            mtime_ticks  INTEGER NOT NULL,
                            is_processed INTEGER NOT NULL DEFAULT 0
                        )
                        """;
                    _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }
                before = await File.ReadAllBytesAsync(
                    dbPath,
                    TestContext.Current.CancellationToken
                );
            }

            int exitCode = await new ImportCommand(
                CreateOptions(outDir, dryRun: true, path: srcDir, dbFile: new FileInfo(dbPath)),
                TestContext.Current.CancellationToken
            ).ExecuteAsync();

            exitCode.Should().Be(ExitCode.Success);
            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().BeEmpty();
            if (before is null)
            {
                Directory.Exists(Path.GetDirectoryName(dbPath)).Should().BeFalse();
            }
            else
            {
                Directory
                    .GetFiles(Path.GetDirectoryName(dbPath)!)
                    .Select(Path.GetFileName)
                    .Should()
                    .Equal("Import.db");
                (await File.ReadAllBytesAsync(dbPath, TestContext.Current.CancellationToken))
                    .Should()
                    .Equal(before);
            }
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            Directory.Delete(dbDir, recursive: true);
        }
    }

    // -- Claims: files of one run that match each other import once ------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_DryRunWithDatabase_MatchingFilesInOneRun_ImportOnce(
        bool imageHash
    )
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            // A dry run inserts no rows, so only the in-run claim can catch the second file
            (string original, string copy) = await CreateMetadataOnlyCopyAsync(srcDir);
            string second = imageHash ? copy : Path.Combine(srcDir, "IMG_1234 (1).jpg");
            if (!imageHash)
            {
                File.Copy(original, second);
            }

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            ImportTask task = new(
                CreateOptions(outDir, dryRun: true, imageHash: imageHash),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, int skipped, _, _, _, int failed, _) = await task.ExecuteAsync(
                [original, second],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            skipped.Should().Be(1);
            failed.Should().Be(0);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ClaimingFileFailsToImport_LaterMatchStillImports()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            // Two metadata variants with different dates, so each lands in its own folder
            (string original, string copy) = await CreateMetadataOnlyCopyAsync(srcDir);
            await SetExifDateAsync(original, "2020:01:15 10:00:00");
            await SetExifDateAsync(copy, "2021:01:15 10:00:00");
            string? originalImageHash = await GetImageHashAsync(original);
            originalImageHash.Should().NotBeNullOrEmpty();
            (await GetImageHashAsync(copy)).Should().Be(originalImageHash);

            // A directory at the first file's destination makes its copy fail
            _ = Directory.CreateDirectory(Path.Combine(outDir, "2020-01", "IMG_1234.jpg"));

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            ImportTask task = new(
                CreateOptions(outDir, imageHash: true),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, int skipped, _, _, _, int failed, _) = await task.ExecuteAsync(
                [original, copy],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            failed.Should().Be(1);
            organized.Should().Be(1);
            skipped.Should().Be(0);
            File.Exists(Path.Combine(outDir, "2021-01", "IMG_1234.jpg")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ClaimedByAsync_WaiterOnRunningOwner_ClaimsWhenReleasedAndSkipsWhenImported()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        ImportTask task = new(CreateOptions(Path.GetTempPath()), null, null, null, new());

        List<(string Key, ImportTask.Claim Claim)> ownerClaims = [];
        (await task.ClaimedByAsync("sha256:a", "/src/IMG_1234.HEIC", ownerClaims, ct))
            .Should()
            .BeNull();

        // The owner is still running, so the second file waits rather than skipping
        List<(string Key, ImportTask.Claim Claim)> secondClaims = [];
        Task<string?> second = task.ClaimedByAsync(
            "sha256:a",
            "/src/IMG_1234 (1).HEIC",
            secondClaims,
            ct
        );
        second.IsCompleted.Should().BeFalse();

        // The owner does not import, so the waiting file takes the claim
        task.ReleaseClaims(ownerClaims, imported: false);
        (await second).Should().BeNull();
        secondClaims.Should().ContainSingle();

        // A third file waits on the new owner, which imports, so the third skips
        Task<string?> third = task.ClaimedByAsync("sha256:a", "/src/IMG_1234 (2).HEIC", [], ct);
        third.IsCompleted.Should().BeFalse();
        task.ReleaseClaims(secondClaims, imported: true);
        (await third).Should().Be("/src/IMG_1234 (1).HEIC");
    }

    [Fact]
    public async Task ClaimedByAsync_WaiterCancelled_ThrowsWithoutClaiming()
    {
        ImportTask task = new(CreateOptions(Path.GetTempPath()), null, null, null, new());
        List<(string Key, ImportTask.Claim Claim)> ownerClaims = [];
        (
            await task.ClaimedByAsync(
                "image:a",
                "/src/IMG_1234.HEIC",
                ownerClaims,
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeNull();

        using CancellationTokenSource cts = new();
        List<(string Key, ImportTask.Claim Claim)> waiterClaims = [];
        Task<string?> waiter = task.ClaimedByAsync(
            "image:a",
            "/src/IMG_1234 (1).HEIC",
            waiterClaims,
            cts.Token
        );
        await cts.CancelAsync();

        Func<Task> act = () => waiter;
        await act.Should().ThrowAsync<OperationCanceledException>();
        waiterClaims.Should().BeEmpty();
    }

    [Fact]
    public void ReserveDestination_SameNameBeforeEitherLands_TakesNextNameUntilFreed()
    {
        string dateDir = Path.Combine(
            Path.GetTempPath(),
            $"ImportTaskTest_{Path.GetRandomFileName()}",
            "0001-01"
        );
        string requested = Path.Combine(dateDir, "IMG_0001.jpg");
        ImportTask task = new(CreateOptions(Path.GetTempPath()), null, null, null, new());

        List<(string Key, ImportTask.Claim Claim)> first = [];
        task.ReserveDestination(requested, "/src/a/IMG_0001.jpg", first).Should().Be(requested);
        List<(string Key, ImportTask.Claim Claim)> second = [];
        task.ReserveDestination(requested, "/src/b/IMG_0001.jpg", second)
            .Should()
            .Be(Path.Combine(dateDir, "IMG_0001_1.jpg"));

        task.ReleaseClaims(first, imported: false);
        task.ReserveDestination(requested, "/src/c/IMG_0001.jpg", []).Should().Be(requested);

        task.ReleaseClaims(second, imported: true);
        task.ReserveDestination(requested, "/src/d/IMG_0001.jpg", [])
            .Should()
            .Be(Path.Combine(dateDir, "IMG_0001_2.jpg"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_DeleteEmptyIntoMissingOutPath_DoesNothingAndReturnsCounters(
        bool dryRun
    )
    {
        string srcDir = TempDir();
        string outDir = Path.Combine(
            Path.GetTempPath(),
            $"ImportTaskTest_{Path.GetRandomFileName()}"
        );
        try
        {
            string file = Path.Combine(srcDir, dryRun ? "photo.jpg" : "notes.txt");
            if (dryRun)
            {
                File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), file);
            }
            else
            {
                Touch(file);
            }

            ImportTask task = new(
                CreateOptions(outDir, dryRun: dryRun, deleteEmpty: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, int ignored, _, _, _, _, int failed, int deletedDirs) =
                await task.ExecuteAsync(
                    [file],
                    new DirectoryInfo(srcDir),
                    TestContext.Current.CancellationToken
                );

            organized.Should().Be(dryRun ? 1 : 0);
            ignored.Should().Be(dryRun ? 0 : 1);
            failed.Should().Be(0);
            deletedDirs.Should().Be(0);
            Directory.Exists(outDir).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteEmptyWithUnreadableSubdirectory_KeepsItAndReturnsCounters()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no Unix file modes");
            return;
        }

        string srcDir = TempDir();
        string outDir = TempDir();
        string locked = Path.Combine(outDir, "locked");
        string empty = Path.Combine(outDir, "empty");
        try
        {
            Directory.CreateDirectory(Path.Combine(locked, "inner"));
            Directory.CreateDirectory(empty);
            File.SetUnixFileMode(locked, UnixFileMode.None);
            try
            {
                _ = Directory.EnumerateFileSystemEntries(locked).Any();
                Assert.Skip(
                    "Running with permission to read anything, so no directory is unreadable"
                );
            }
            catch (UnauthorizedAccessException) { }

            string jpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
            ImportTask task = new(
                CreateOptions(outDir, deleteEmpty: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, int deletedDirs) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            failed.Should().Be(0);
            deletedDirs.Should().Be(1);
            Directory.Exists(empty).Should().BeFalse();
            Directory.Exists(locked).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(locked))
            {
                File.SetUnixFileMode(
                    locked,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }

            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    // A privileged user removes a file from a read-only folder, so no copy would stay.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static void SkipUnlessReadOnlyFolderKeepsFiles()
    {
        string dir = TempDir();
        try
        {
            string probe = Path.Combine(dir, "probe");
            File.WriteAllText(probe, string.Empty);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                File.Delete(probe);
                Assert.Skip("Running with permission to remove any file, so no copy stays");
            }
            catch (UnauthorizedAccessException) { }
        }
        finally
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
            Directory.Delete(dir, recursive: true);
        }
    }

    // The stub makes the date folder read-only, so the copy in it cannot be removed.
    internal static string ReadOnlyFolderStub(string dir, string tail) =>
        TempDirectoryFixture.WriteExifToolStub(
            dir,
            $"for last in \"$@\"; do :; done\nchmod 555 \"$(dirname \"$last\")\"\n{tail}"
        );

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static void RestoreFolder(string dir)
    {
        if (Directory.Exists(dir))
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
    }

    [Fact]
    public async Task ExecuteAsync_TagWriteFailsAndCopyStays_RecordsCopySoAMatchSkips()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no Unix file modes");
            return;
        }

        SkipUnlessReadOnlyFolderKeepsFiles();
        string srcDir = TempDir();
        string otherDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string stub = ReadOnlyFolderStub(
                srcDir,
                "touch \"$last\"\necho 'Error: stub write failure' >&2\nexit 1\n"
            );
            (string jpg, string dest) = await CreateTaggedSourceAsync(fixture, srcDir);
            DateTime sourceTime = new(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(jpg, sourceTime);
            string copy = Path.Combine(otherDir, "photo.jpg");
            File.Copy(jpg, copy);
            (string sha256, _) = await Database.ComputeHashesAsync(
                jpg,
                TestContext.Current.CancellationToken
            );

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                CreateOptions(outDir, tags: "extra"),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            (int organized, _, int skipped, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg, copy],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            skipped.Should().Be(1);
            failed.Should().Be(1);
            File.GetLastWriteTimeUtc(Path.Combine(outDir, dest)).Should().Be(sourceTime);
            FileRecord? row = await db.GetByPathAsync(jpg, TestContext.Current.CancellationToken);
            row.Should().NotBeNull();
            row!.Sha256.Should().Be(sha256);
        }
        finally
        {
            RestoreFolder(Path.Combine(outDir, "2024-06"));
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(otherDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancelAndCopyStays_RecordsCopyAndRethrows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no Unix file modes");
            return;
        }

        SkipUnlessReadOnlyFolderKeepsFiles();
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        try
        {
            string marker = Path.Combine(srcDir, "write-started");
            string stub = ReadOnlyFolderStub(srcDir, $"touch '{marker}'\nexec sleep 30\n");
            (string jpg, string dest) = await CreateTaggedSourceAsync(fixture, srcDir);

            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                CreateOptions(outDir, tags: "extra"),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            Task run = task.ExecuteAsync([jpg], new DirectoryInfo(srcDir), cts.Token);
            using (CancellationTokenSource bound = new(TimeSpan.FromSeconds(30)))
            {
                while (!File.Exists(marker))
                {
                    await Task.Delay(50, bound.Token);
                }
            }

            await cts.CancelAsync();
            Func<Task> act = () => run;
            _ = await act.Should().ThrowAsync<OperationCanceledException>();

            File.Exists(Path.Combine(outDir, dest)).Should().BeTrue();
            (await db.GetByPathAsync(jpg, TestContext.Current.CancellationToken))
                .Should()
                .NotBeNull();
        }
        finally
        {
            await cts.CancelAsync();
            RestoreFolder(Path.Combine(outDir, "2024-06"));
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }
}

// Swapping the global logger is safe only while no other test class runs.
[Collection(GlobalLoggerDefinition.Name)]
public sealed class ImportTaskLogTests(TempDirectoryFixture media)
    : IClassFixture<TempDirectoryFixture>,
        IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        if (!media.IsAvailable)
        {
            Assert.Skip(media.SkipReason ?? "Required tools not available");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task ExecuteAsync_SourceTimeRestoreFailsOnCopy_TakesTheCopyRollback()
    {
        string srcDir = ImportTaskTests.TempDir();
        string outDir = ImportTaskTests.TempDir();
        CapturingSink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Sink(sink)
                .CreateLogger();

            // .NET sets the time on a link itself, so only a missing destination makes the restore throw
            string stub = TempDirectoryFixture.WriteExifToolStub(
                srcDir,
                "for last in \"$@\"; do :; done\nrm \"$last\"\nexit 0\n"
            );
            (string jpg, string dest) = await ImportTaskTests.CreateTaggedSourceAsync(
                media,
                srcDir
            );
            string destPath = Path.Combine(outDir, dest);

            ImportTask task = new(
                ImportTaskTests.CreateOptions(outDir, tags: "extra"),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            failed.Should().Be(1);
            File.Exists(jpg).Should().BeTrue();
            sink.Events.Should()
                .Contain(e =>
                    e.Level == LogEventLevel.Error
                    && e.RenderMessage().Contains("modified time", StringComparison.Ordinal)
                    && e.RenderMessage().Contains(destPath, StringComparison.Ordinal)
                );
            sink.Events.Should()
                .Contain(e =>
                    e.Level == LogEventLevel.Warning
                    && e.RenderMessage().Contains("Removed", StringComparison.Ordinal)
                    && e.RenderMessage().Contains(destPath, StringComparison.Ordinal)
                );
        }
        finally
        {
            Log.Logger = savedLogger;
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RowWriteFailsOnMove_KeepsFileAndLogsDestination()
    {
        string srcDir = ImportTaskTests.TempDir();
        string outDir = ImportTaskTests.TempDir();
        string dbPath = ImportTaskTests.TempDb();
        CapturingSink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Sink(sink)
                .CreateLogger();
            (string jpg, string dest) = await ImportTaskTests.CreateTaggedSourceAsync(
                media,
                srcDir
            );
            string destPath = Path.Combine(outDir, dest);
            await using (Database writable = new(dbPath))
            {
                await writable.InitializeAsync(TestContext.Current.CancellationToken);
            }

            await using Database db = new(dbPath, readOnly: true);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                ImportTaskTests.CreateOptions(outDir, move: true),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            failed.Should().Be(1);
            File.Exists(jpg).Should().BeFalse();
            File.Exists(destPath).Should().BeTrue();
            sink.Events.Should()
                .ContainSingle(e => e.Level == LogEventLevel.Error)
                .Which.RenderMessage()
                .Should()
                .Contain(destPath);
        }
        finally
        {
            Log.Logger = savedLogger;
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RowWriteFailsAndCopyStays_KeepsClaimsAndLogsDestination()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no Unix file modes");
            return;
        }

        ImportTaskTests.SkipUnlessReadOnlyFolderKeepsFiles();
        string srcDir = ImportTaskTests.TempDir();
        string otherDir = ImportTaskTests.TempDir();
        string outDir = ImportTaskTests.TempDir();
        string dbPath = ImportTaskTests.TempDb();
        CapturingSink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Sink(sink)
                .CreateLogger();
            string stub = ImportTaskTests.ReadOnlyFolderStub(srcDir, "exit 0\n");
            (string jpg, string dest) = await ImportTaskTests.CreateTaggedSourceAsync(
                media,
                srcDir
            );
            string destPath = Path.Combine(outDir, dest);
            string copy = Path.Combine(otherDir, "photo.jpg");
            File.Copy(jpg, copy);
            await using (Database writable = new(dbPath))
            {
                await writable.InitializeAsync(TestContext.Current.CancellationToken);
            }

            await using Database db = new(dbPath, readOnly: true);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            ImportTask task = new(
                ImportTaskTests.CreateOptions(outDir, tags: "extra"),
                database: db,
                skipDatabase: null,
                trashDatabase: null,
                new()
            )
            {
                ExifToolCommand = stub,
            };
            (int organized, _, int skipped, _, _, _, int failed, _) = await task.ExecuteAsync(
                [jpg, copy],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(0);
            skipped.Should().Be(1);
            failed.Should().Be(1);
            File.Exists(destPath).Should().BeTrue();
            sink.Events.Should()
                .Contain(e =>
                    e.Level == LogEventLevel.Error
                    && e.RenderMessage().Contains("no database row", StringComparison.Ordinal)
                    && e.RenderMessage().Contains(destPath, StringComparison.Ordinal)
                );
        }
        finally
        {
            Log.Logger = savedLogger;
            ImportTaskTests.RestoreFolder(Path.Combine(outDir, "2024-06"));
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(otherDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TypeExifToolCannotWrite_WarnsSkippedTagsAndDate()
    {
        string srcDir = ImportTaskTests.TempDir();
        string outDir = ImportTaskTests.TempDir();
        CapturingSink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Sink(sink)
                .CreateLogger();
            string datedDir = Path.Combine(srcDir, "2021", "05", "02");
            Directory.CreateDirectory(datedDir);
            string avi = Path.Combine(datedDir, "clip.avi");
            File.Copy(media.SourceFile(TempDirectoryFixture.AviFile), avi);

            ImportTask task = new(
                ImportTaskTests.CreateOptions(outDir, datePath: true, tags: "trip"),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                [avi],
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(1);
            failed.Should().Be(0);
            sink.Events.Should()
                .Contain(e =>
                    e.Level == LogEventLevel.Warning
                    && e.RenderMessage().Contains(avi, StringComparison.Ordinal)
                    && e.RenderMessage().Contains("tags [trip]", StringComparison.Ordinal)
                    && e.RenderMessage().Contains("created date", StringComparison.Ordinal)
                );
        }
        finally
        {
            Log.Logger = savedLogger;
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DryRunSameNameAndDate_SecondReportsNextName()
    {
        string srcDir = ImportTaskTests.TempDir();
        string outDir = ImportTaskTests.TempDir();
        CapturingSink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Sink(sink)
                .CreateLogger();
            List<string> files = [];
            foreach (string sub in new[] { "a", "b" })
            {
                string dir = Path.Combine(srcDir, sub);
                Directory.CreateDirectory(dir);
                string jpg = Path.Combine(dir, "IMG_0001.jpg");
                File.Copy(media.SourceFile(TempDirectoryFixture.SmallJpegFile), jpg);
                files.Add(jpg);
            }

            ImportTask task = new(
                ImportTaskTests.CreateOptions(outDir, dryRun: true),
                database: null,
                skipDatabase: null,
                trashDatabase: null,
                new()
            );
            (int organized, _, _, _, _, _, int failed, _) = await task.ExecuteAsync(
                files,
                new DirectoryInfo(srcDir),
                TestContext.Current.CancellationToken
            );

            organized.Should().Be(2);
            failed.Should().Be(0);
            string[] destinations =
            [
                .. sink
                    .Events.Where(e =>
                        e.MessageTemplate.Text.StartsWith("Copying", StringComparison.Ordinal)
                    )
                    .Select(e => e.Properties["DestinationPath"].ToString().Trim('"')),
            ];
            destinations
                .Should()
                .BeEquivalentTo(
                    Path.Combine(outDir, "0001-01", "IMG_0001.jpg"),
                    Path.Combine(outDir, "0001-01", "IMG_0001_1.jpg")
                );
            sink.Events.Should()
                .ContainSingle(e => e.Level == LogEventLevel.Warning)
                .Which.RenderMessage()
                .Should()
                .Contain(Path.Combine(outDir, "0001-01", "IMG_0001_1.jpg"));
        }
        finally
        {
            Log.Logger = savedLogger;
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalLoggerDefinition
{
    internal const string Name = "GlobalLogger";
}

// Collects every event a test logs, from any thread.
internal sealed class CapturingSink : ILogEventSink
{
    internal ConcurrentQueue<LogEvent> Events { get; } = new();

    public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
}
