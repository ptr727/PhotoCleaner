using CliWrap;
using Microsoft.Data.Sqlite;
using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class LivePhotoCatalogTests(TempDirectoryFixture fixture)
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

    // -- Live photo pairing through the catalog --------------------------------

    [Fact]
    public async Task ExecuteAsync_DifferingStems_DeletesVideo()
    {
        // The rclone mirror inserts an asset id into a colliding name, so the stems differ.
        await RunPairAsync(
            "IMG_1234.heic",
            "IMG_1234_QWJj.mp4",
            "uuid-differing-stems",
            "uuid-differing-stems",
            expected: ProcessTask.ProcessResult.Deleted
        );
    }

    [Fact]
    public async Task ExecuteAsync_DifferentDateFolders_DeletesVideo()
    {
        await RunPairAsync(
            Path.Combine("2024", "01", "01", "IMG_1234.heic"),
            Path.Combine("2024", "01", "02", "IMG_1234.mp4"),
            "uuid-folders",
            "uuid-folders",
            expected: ProcessTask.ProcessResult.Deleted
        );
    }

    [Fact]
    public async Task ExecuteAsync_AssetIdWithPlusAndFullwidthSlash_DeletesVideo()
    {
        // U+FF0F is the fullwidth solidus, which an asset id can carry in place of a slash.
        string videoName = $"IMG_1234_Ab+Cd{(char)0xFF0F}Ef.mp4";
        await RunPairAsync(
            "IMG_1234.heic",
            videoName,
            "uuid-special-name",
            "uuid-special-name",
            expected: ProcessTask.ProcessResult.Deleted
        );
    }

    [Fact]
    public async Task ExecuteAsync_StillRemovedFromDiskButCatalogued_DeletesVideo()
    {
        await RunPairAsync(
            "IMG_1234.heic",
            "IMG_1234_QWJj.mp4",
            "uuid-still-gone",
            "uuid-still-gone",
            expected: ProcessTask.ProcessResult.Deleted,
            deleteStillBeforeProcess: true
        );
    }

    [Fact]
    public async Task ExecuteAsync_VideoWithoutContentIdentifier_KeepsVideo()
    {
        await RunPairAsync(
            "IMG_1234.heic",
            "IMG_1234_QWJj.mp4",
            "uuid-no-video-id",
            null,
            expected: ProcessTask.ProcessResult.Success
        );
    }

    [Fact]
    public async Task ExecuteAsync_NonMatchingContentIdentifier_KeepsVideo()
    {
        await RunPairAsync(
            "IMG_1234.heic",
            "IMG_1234_QWJj.mp4",
            "uuid-still-AAA",
            "uuid-video-BBB",
            expected: ProcessTask.ProcessResult.Success
        );
    }

    [Fact]
    public async Task ExecuteAsync_NoDatabaseAndDifferingStems_KeepsVideo()
    {
        // Without a database the lookup stays the same-folder name match.
        await RunPairAsync(
            "IMG_1234.heic",
            "IMG_1234_QWJj.mp4",
            "uuid-no-db",
            "uuid-no-db",
            expected: ProcessTask.ProcessResult.Success,
            useDatabase: false
        );
    }

    [Fact]
    public async Task ExecuteAsync_NoDatabaseAndSameStem_DeletesVideo()
    {
        await RunPairAsync(
            "IMG_1234.heic",
            "IMG_1234.mp4",
            "uuid-no-db-same",
            "uuid-no-db-same",
            expected: ProcessTask.ProcessResult.Deleted,
            useDatabase: false
        );
    }

    // -- Schema ----------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_OldSchemaDatabase_UpgradesInPlaceKeepingRows()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - a database in the shape written before the catalog columns existed
            string dbPath = Path.Combine(workDir, "old.db");
            await using (SqliteConnection connection = new($"Data Source={dbPath};Pooling=False"))
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
                    );
                    INSERT INTO files VALUES ('/photos/IMG_1234.heic', 'sha256-a', 'sha1-a', 10, 20, 1)
                    """;
                _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // Act
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            FileRecord? record = await db.GetByPathAsync(
                "/photos/IMG_1234.heic",
                TestContext.Current.CancellationToken
            );

            // Assert - rows intact, metadata marked as not read yet
            record.Should().NotBeNull();
            record.Sha256.Should().Be("sha256-a");
            record.Sha1.Should().Be("sha1-a");
            record.FileSize.Should().Be(10);
            record.MtimeTicks.Should().Be(20);
            record.IsProcessed.Should().BeTrue();
            record.MetadataRead.Should().BeFalse();
            record.ContentIdentifier.Should().BeNull();
            record.ImageHash.Should().BeNull();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task InitializeAsync_OldSchemaReadOnly_StillAnswersHashLookups()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - a skip database written before the catalog columns existed
            string dbPath = Path.Combine(workDir, "old.db");
            await using (SqliteConnection connection = new($"Data Source={dbPath};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE files (
                        path TEXT NOT NULL PRIMARY KEY, sha256 TEXT NOT NULL, sha1 TEXT NOT NULL,
                        file_size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL,
                        is_processed INTEGER NOT NULL DEFAULT 0
                    );
                    INSERT INTO files VALUES ('/photos/IMG_1234.heic', 'sha256-a', 'sha1-a', 10, 20, 0)
                    """;
                _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // Act
            await using Database db = new(dbPath, readOnly: true);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            bool found = await db.Sha256ExistsAsync(
                "sha256-a",
                TestContext.Current.CancellationToken
            );

            // Assert
            found.Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- Catalog columns -------------------------------------------------------

    [Fact]
    public async Task IndexFileAsync_OldRowWithFileOnDisk_BackfillsOnceAndIsNotReread()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - catalog the file, then turn its row back into an unread old row
            string filePath = Path.Combine(workDir, "IMG_1234.heic");
            await CreateStillAsync(filePath, "uuid-backfill");
            string dbPath = Path.Combine(workDir, "photos.db");
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask task = CreateIndexTask(workDir, db);
            _ = await task.IndexFileAsync(filePath, TestContext.Current.CancellationToken);
            await ResetMetadataAsync(dbPath);

            // Act
            _ = await task.IndexFileAsync(filePath, TestContext.Current.CancellationToken);
            FileRecord? backfilled = await db.GetByPathAsync(
                filePath,
                TestContext.Current.CancellationToken
            );
            await db.SetMetadataAsync(
                filePath,
                new FileMetadata("sentinel-id", "sentinel-hash"),
                TestContext.Current.CancellationToken
            );
            _ = await task.IndexFileAsync(filePath, TestContext.Current.CancellationToken);
            FileRecord? afterSecondRun = await db.GetByPathAsync(
                filePath,
                TestContext.Current.CancellationToken
            );

            // Assert
            backfilled.Should().NotBeNull();
            backfilled.MetadataRead.Should().BeTrue();
            backfilled.ContentIdentifier.Should().Be("uuid-backfill");
            afterSecondRun.Should().NotBeNull();
            afterSecondRun.ContentIdentifier.Should().Be("sentinel-id");
            afterSecondRun.ImageHash.Should().Be("sentinel-hash");
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task IndexFileAsync_FileWithoutTags_StoresReadAndAbsent()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - not an image, so exiftool reads no hash and no ContentIdentifier
            string filePath = Path.Combine(workDir, "notes.jpg");
            await File.WriteAllTextAsync(
                filePath,
                "plain text",
                TestContext.Current.CancellationToken
            );
            await using Database db = new(Path.Combine(workDir, "photos.db"));
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            // Act
            _ = await CreateIndexTask(workDir, db)
                .IndexFileAsync(filePath, TestContext.Current.CancellationToken);
            FileRecord? record = await db.GetByPathAsync(
                filePath,
                TestContext.Current.CancellationToken
            );

            // Assert
            record.Should().NotBeNull();
            record.MetadataRead.Should().BeTrue();
            record.ContentIdentifier.Should().BeNull();
            record.ImageHash.Should().BeNull();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task IndexFileAsync_CopiesDifferingOnlyInMetadata_ShareImageHash()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange
            string first = Path.Combine(workDir, "first.jpg");
            string second = Path.Combine(workDir, "second.jpg");
            string other = Path.Combine(workDir, "other.png");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), first);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), second);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallPngFile), other);
            _ = await Cli.Wrap("exiftool")
                .WithArguments(["-overwrite_original", "-Comment=edited metadata", second])
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(TestContext.Current.CancellationToken);
            await using Database db = new(Path.Combine(workDir, "photos.db"));
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask task = CreateIndexTask(workDir, db);

            // Act
            _ = await task.IndexFileAsync(first, TestContext.Current.CancellationToken);
            _ = await task.IndexFileAsync(second, TestContext.Current.CancellationToken);
            _ = await task.IndexFileAsync(other, TestContext.Current.CancellationToken);
            FileRecord? firstRecord = await db.GetByPathAsync(
                first,
                TestContext.Current.CancellationToken
            );
            FileRecord? secondRecord = await db.GetByPathAsync(
                second,
                TestContext.Current.CancellationToken
            );
            FileRecord? otherRecord = await db.GetByPathAsync(
                other,
                TestContext.Current.CancellationToken
            );

            // Assert - the whole-file hashes differ, the image hashes do not
            firstRecord.Should().NotBeNull();
            secondRecord.Should().NotBeNull();
            otherRecord.Should().NotBeNull();
            firstRecord.Sha256.Should().NotBe(secondRecord.Sha256);
            firstRecord.ImageHash.Should().NotBeNullOrEmpty();
            firstRecord.ImageHash.Should().Be(secondRecord.ImageHash);
            otherRecord.ImageHash.Should().NotBeNullOrEmpty();
            otherRecord.ImageHash.Should().NotBe(firstRecord.ImageHash);
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void FromExifTool_ErrorReported_LeavesRowUnread()
    {
        FileMetadata
            .FromExifTool(new ExifToolJson { ExifToolError = "cannot read" })
            .Should()
            .BeNull();
        FileMetadata.FromExifTool(null).Should().BeNull();
        FileMetadata
            .FromExifTool(new ExifToolJson { ImageDataHash = "abc" })
            .Should()
            .BeEquivalentTo(new FileMetadata(null, "abc"));
    }

    [Fact]
    public async Task ExecuteAsync_VideoRenamedFromStaleStillName_DoesNotPairWithItself()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - a video once cataloged under a .heic name, since renamed to .mp4
            string stalePath = Path.Combine(workDir, "clip.heic");
            string videoPath = Path.Combine(workDir, "clip.mp4");
            await CreateMediaAsync(stalePath, "uuid-self", differentiate: false);
            CommandLine.Options options = CreateOptions(workDir, null);
            await using Database db = new(Path.Combine(workDir, "photos.db"));
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            _ = await new IndexTask(options, db, new SkippedExtensionTracker()).IndexFileAsync(
                stalePath,
                TestContext.Current.CancellationToken
            );
            File.Move(stalePath, videoPath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                options,
                new FileInfo(videoPath),
                db,
                null,
                [],
                new SkippedExtensionTracker(),
                TestContext.Current.CancellationToken
            );

            // Assert - the only matching row holds the video's own content
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // -- Helpers ---------------------------------------------------------------

    private async Task RunPairAsync(
        string stillRelativePath,
        string videoRelativePath,
        string? stillContentId,
        string? videoContentId,
        ProcessTask.ProcessResult expected,
        bool useDatabase = true,
        bool deleteStillBeforeProcess = false
    )
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange
            string stillPath = Path.Combine(workDir, stillRelativePath);
            string videoPath = Path.Combine(workDir, videoRelativePath);
            await CreateStillAsync(stillPath, stillContentId);
            await CreateVideoAsync(videoPath, videoContentId);
            CommandLine.Options options = CreateOptions(workDir, null);
            await using Database db = new(Path.Combine(workDir, "photos.db"));
            {
                if (useDatabase)
                {
                    await db.InitializeAsync(TestContext.Current.CancellationToken);

                    // Phase one of process, the still is cataloged before the video is judged
                    _ = await new IndexTask(
                        options,
                        db,
                        new SkippedExtensionTracker()
                    ).IndexFileAsync(stillPath, TestContext.Current.CancellationToken);
                    if (deleteStillBeforeProcess)
                    {
                        File.Delete(stillPath);
                    }
                }

                // Act
                ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                    options,
                    new FileInfo(videoPath),
                    useDatabase ? db : null,
                    null,
                    [],
                    new SkippedExtensionTracker(),
                    TestContext.Current.CancellationToken
                );

                // Assert
                result.Should().Be(expected);
                File.Exists(videoPath).Should().Be(expected != ProcessTask.ProcessResult.Deleted);
                File.Exists(videoPath + ".bak")
                    .Should()
                    .Be(expected == ProcessTask.ProcessResult.Deleted);
            }
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // The still is a copy of the live video renamed to .heic, as the other live photo tests do.
    // The ISOBMFF container accepts a ContentIdentifier where a plain JPEG does not.
    private Task CreateStillAsync(string path, string? contentId) =>
        CreateMediaAsync(path, contentId, differentiate: true);

    private Task CreateVideoAsync(string path, string? contentId) =>
        CreateMediaAsync(path, contentId, differentiate: false);

    private async Task CreateMediaAsync(string path, string? contentId, bool differentiate)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), path);
        // A still carrying a description differs in bytes from a video with the same ContentIdentifier.
        List<string> arguments = [];
        if (contentId is not null)
        {
            arguments.Add($"-Keys:ContentIdentifier={contentId}");
        }

        if (differentiate)
        {
            arguments.Add("-Keys:Description=still image");
        }

        if (arguments.Count > 0)
        {
            // ExifTool may exit with code 1 for warnings, so suppress validation.
            _ = await Cli.Wrap("exiftool")
                .WithArguments([.. arguments, "-overwrite_original", path])
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task ResetMetadataAsync(string dbPath)
    {
        await using SqliteConnection connection = new($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE files SET content_identifier = NULL, image_hash = NULL, metadata_read = 0";
        _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static IndexTask CreateIndexTask(string workDir, Database db) =>
        new(CreateOptions(workDir, null), db, new SkippedExtensionTracker());

    private static CommandLine.Options CreateOptions(string workDir, string? dbFile) =>
        new()
        {
            Path = new DirectoryInfo(workDir),
            Threads = 1,
            DryRun = false,
            DatePath = false,
            SkipBackup = false,
            OutPath = null,
            Format = "yyyy/MM/dd",
            DeleteEmpty = false,
            Move = false,
            TagPath = false,
            Tags = null,
            DbFile = dbFile is null ? null : new FileInfo(dbFile),
            Rehash = false,
            ShortVideoDuration = MediaUtilities.ShortVideoDuration,
            Reprocess = false,
            MarkProcessed = false,
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
}
