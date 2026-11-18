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

    [Fact]
    public async Task ExecuteAsync_StillCatalogedAsHeifThenRenamedToHeic_DeletesVideo()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string heifPath = Path.Combine(workDir, "IMG_1234.heif");
            string heicPath = Path.Combine(workDir, "IMG_1234.heic");
            string videoPath = Path.Combine(workDir, "IMG_1234_QWJj.mp4");
            await CreateStillAsync(heifPath, "uuid-heif-renamed");
            await CreateVideoAsync(videoPath, "uuid-heif-renamed");
            CommandLine.Options options = CreateOptions(workDir, null);
            await using Database db = new(Path.Combine(workDir, "photos.db"));
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            _ = await new IndexTask(options, db, new SkippedExtensionTracker()).IndexFileAsync(
                heifPath,
                TestContext.Current.CancellationToken
            );
            File.Move(heifPath, heicPath);

            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                options,
                new FileInfo(videoPath),
                db,
                null,
                [],
                new SkippedExtensionTracker(),
                TestContext.Current.CancellationToken
            );

            result.Should().Be(ProcessTask.ProcessResult.Deleted);
            File.Exists(videoPath + ".bak").Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NoDatabaseAndHeifCompanion_DeletesVideo()
    {
        await RunPairAsync(
            "IMG_1234.heif",
            "IMG_1234.mp4",
            "uuid-no-db-heif",
            "uuid-no-db-heif",
            expected: ProcessTask.ProcessResult.Deleted,
            useDatabase: false
        );
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
            // Leave the stale row without an image hash, so only the sha256 exclusion can apply
            await db.SetMetadataAsync(
                stalePath,
                new FileMetadata("uuid-self", null),
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

    [Fact]
    public async Task ExecuteAsync_StillTrashedAndDeletedFirst_StillDeletesVideo()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - the still is processed first and removed because its SHA-1 is in the trash database
            string stillPath = Path.Combine(workDir, "IMG_1234.heic");
            string videoPath = Path.Combine(workDir, "IMG_1234_QWJj.mp4");
            await CreateStillAsync(stillPath, "uuid-trashed-still");
            await CreateVideoAsync(videoPath, "uuid-trashed-still");
            (string _, string stillSha1) = await Database.ComputeHashesAsync(
                stillPath,
                TestContext.Current.CancellationToken
            );
            CommandLine.Options options = CreateOptions(workDir, null);
            await using Database db = new(Path.Combine(workDir, "photos.db"));
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await using TrashDatabase trashDb = new(Path.Combine(workDir, "trash.db"));
            await trashDb.InitializeAsync(TestContext.Current.CancellationToken);
            await trashDb.InsertHashAsync(
                stillSha1,
                cancellationToken: TestContext.Current.CancellationToken
            );

            // Act
            ProcessTask.ProcessResult stillResult = await ProcessTask.ExecuteAsync(
                options,
                new FileInfo(stillPath),
                db,
                trashDb,
                [],
                new SkippedExtensionTracker(),
                TestContext.Current.CancellationToken
            );
            ProcessTask.ProcessResult videoResult = await ProcessTask.ExecuteAsync(
                options,
                new FileInfo(videoPath),
                db,
                trashDb,
                [],
                new SkippedExtensionTracker(),
                TestContext.Current.CancellationToken
            );

            // Assert - the deleted still's row still pairs the video
            stillResult.Should().Be(ProcessTask.ProcessResult.Deleted);
            File.Exists(stillPath).Should().BeFalse();
            videoResult.Should().Be(ProcessTask.ProcessResult.Deleted);
            File.Exists(videoPath + ".bak").Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_VideoListedFirst_PairsOnlyWhenStillIsCataloged()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - the video name sorts before the still name and is listed first
            string videoPath = Path.Combine(workDir, "a_video.mp4");
            string stillPath = Path.Combine(workDir, "z_still.heic");
            await CreateVideoAsync(videoPath, "uuid-order");
            await CreateStillAsync(stillPath, "uuid-order");
            CommandLine.Options options = CreateOptions(workDir, null);
            await using Database uncataloged = new(Path.Combine(workDir, "before.db"));
            await uncataloged.InitializeAsync(TestContext.Current.CancellationToken);
            await using Database cataloged = new(Path.Combine(workDir, "after.db"));
            await cataloged.InitializeAsync(TestContext.Current.CancellationToken);

            // Act - judged with no still row, then after the catalog batch phase one runs
            ProcessTask.ProcessResult before = await ProcessTask.ExecuteAsync(
                options,
                new FileInfo(videoPath),
                uncataloged,
                null,
                [],
                new SkippedExtensionTracker(),
                TestContext.Current.CancellationToken
            );
            _ = await new IndexTask(options, cataloged, new SkippedExtensionTracker()).ExecuteAsync(
                [videoPath, stillPath],
                TestContext.Current.CancellationToken
            );
            ProcessTask.ProcessResult after = await ProcessTask.ExecuteAsync(
                options,
                new FileInfo(videoPath),
                cataloged,
                null,
                [],
                new SkippedExtensionTracker(),
                TestContext.Current.CancellationToken
            );

            // Assert
            before.Should().Be(ProcessTask.ProcessResult.Success);
            after.Should().Be(ProcessTask.ProcessResult.Deleted);
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CopyOfSameVideoUnderImageExtension_KeepsVideo()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - two copies of one video that differ only in metadata, one named as an image
            string copyPath = Path.Combine(workDir, "IMG_1234.heic");
            string videoPath = Path.Combine(workDir, "IMG_1234_QWJj.mp4");
            await CreateMediaAsync(copyPath, "uuid-copy", differentiate: true);
            await CreateMediaAsync(videoPath, "uuid-copy", differentiate: false);
            CommandLine.Options options = CreateOptions(workDir, null);
            await using Database db = new(Path.Combine(workDir, "photos.db"));
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            _ = await new IndexTask(options, db, new SkippedExtensionTracker()).IndexFileAsync(
                copyPath,
                TestContext.Current.CancellationToken
            );

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

            // Assert - the copy holds the same media data, so it is no still
            FileRecord? copyRow = await db.GetByPathAsync(
                copyPath,
                TestContext.Current.CancellationToken
            );
            copyRow.Should().NotBeNull();
            copyRow
                .Sha256.Should()
                .NotBe(
                    (
                        await Database.ComputeHashesAsync(
                            videoPath,
                            TestContext.Current.CancellationToken
                        )
                    ).sha256
                );
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ProcessCommand_VideoListedBeforeStill_DeletesVideoOfCatalogedPair()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - neither file is indexed, and the still has a different name and folder
            string videoPath = Path.Combine(workDir, "a-folder", "IMG_1234_QWJj.mp4");
            string stillPath = Path.Combine(workDir, "z-folder", "IMG_1234.heic");
            await CreateVideoAsync(videoPath, "uuid-command");
            await CreateStillAsync(stillPath, "uuid-command");
            CommandLine.Options options = CreateOptions(
                workDir,
                Path.Combine(workDir, "db", "process.db")
            );

            // Act
            int exitCode = await new ProcessCommand(
                options,
                TestContext.Current.CancellationToken
            ).ExecuteAsync();

            // Assert
            exitCode.Should().Be(0);
            File.Exists(videoPath).Should().BeFalse();
            File.Exists(videoPath + ".bak").Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task IndexFileAsync_MetadataReadingOff_LeavesRowUnread()
    {
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Arrange - verify indexes without exiftool, so its rows carry no catalog metadata
            string filePath = Path.Combine(workDir, "IMG_1234.heic");
            await CreateStillAsync(filePath, "uuid-verify");
            await using Database db = new(Path.Combine(workDir, "verify.db"));
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            // Act
            _ = await new IndexTask(
                CreateOptions(workDir, null),
                db,
                new SkippedExtensionTracker(),
                readMetadata: false
            ).IndexFileAsync(filePath, TestContext.Current.CancellationToken);
            FileRecord? record = await db.GetByPathAsync(
                filePath,
                TestContext.Current.CancellationToken
            );

            // Assert
            record.Should().NotBeNull();
            record.MetadataRead.Should().BeFalse();
            record.ContentIdentifier.Should().BeNull();
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

    // The still is a different video renamed to .heic, since only a video container accepts a ContentIdentifier here.
    // Its media data differs from the live video, so the image hash exclusion does not apply to it.
    private Task CreateStillAsync(string path, string? contentId) =>
        CreateMediaAsync(path, contentId, differentiate: true, TempDirectoryFixture.LongVideoFile);

    private Task CreateVideoAsync(string path, string? contentId) =>
        CreateMediaAsync(path, contentId, differentiate: false);

    private async Task CreateMediaAsync(
        string path,
        string? contentId,
        bool differentiate,
        string sourceFile = TempDirectoryFixture.LiveVideoFile
    )
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(fixture.SourceFile(sourceFile), path);
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
