using System.Text;
using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class IndexTaskTests
{
    private static CommandLine.Options CreateOptions(
        bool rehash = false,
        bool markProcessed = false
    ) =>
        new()
        {
            Path = new DirectoryInfo(Path.GetTempPath()),
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
            DbFile = null,
            Rehash = rehash,
            ShortVideoDuration = MediaUtilities.ShortVideoDuration,
            Reprocess = false,
            MarkProcessed = markProcessed,
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

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"db_{Path.GetRandomFileName()}.db");

    private static string TempFile(string content = "hello photo", string ext = ".jpg")
    {
        string path = Path.Combine(Path.GetTempPath(), $"idx_{Path.GetRandomFileName()}{ext}");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    [Fact]
    public async Task IndexFileAsync_NewFile_ReturnsInserted()
    {
        string dbPath = TempDb();
        string filePath = TempFile();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask task = new(CreateOptions(), db, new SkippedExtensionTracker());

            (IndexStatus status, string sha256, string? sha1, bool wasProcessed) =
                await task.IndexFileAsync(
                    filePath,
                    cancellationToken: TestContext.Current.CancellationToken
                );

            status.Should().Be(IndexStatus.Inserted);
            sha256.Should().NotBeNullOrEmpty();
            sha1.Should().NotBeNullOrEmpty();
            wasProcessed.Should().BeFalse();

            bool exists = await db.Sha256ExistsAsync(
                sha256,
                cancellationToken: TestContext.Current.CancellationToken
            );
            exists.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task IndexFileAsync_ExistingUnchangedFile_ReturnsUnchanged()
    {
        string dbPath = TempDb();
        string filePath = TempFile();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask task = new(CreateOptions(), db, new SkippedExtensionTracker());

            // First call inserts
            await task.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            // Second call with same file should return Unchanged
            (IndexStatus status, string sha256, string? sha1, bool wasProcessed) =
                await task.IndexFileAsync(
                    filePath,
                    cancellationToken: TestContext.Current.CancellationToken
                );

            status.Should().Be(IndexStatus.Unchanged);
            sha256.Should().NotBeNullOrEmpty();
            sha1.Should().NotBeNullOrEmpty();
            wasProcessed.Should().BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task IndexFileAsync_ChangedFile_ReturnsUpdatedAndResetsIsProcessed()
    {
        string dbPath = TempDb();
        string filePath = TempFile("original content");
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask task = new(CreateOptions(), db, new SkippedExtensionTracker());

            // Insert and mark as processed
            await task.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await db.SetProcessedAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            FileRecord? before = await db.GetByPathAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );
            before!.IsProcessed.Should().BeTrue();

            // Overwrite file with new content and touch mtime
            await File.WriteAllBytesAsync(
                filePath,
                Encoding.UTF8.GetBytes("completely different"),
                cancellationToken: TestContext.Current.CancellationToken
            );
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(1));

            (IndexStatus status, _, _, bool wasProcessed) = await task.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            status.Should().Be(IndexStatus.Updated);
            wasProcessed.Should().BeFalse();

            FileRecord? after = await db.GetByPathAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );
            after!.IsProcessed.Should().BeFalse(); // reset by UpdateHashesAsync
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task IndexFileAsync_UnchangedProcessedFile_ReturnsWasProcessedTrue()
    {
        string dbPath = TempDb();
        string filePath = TempFile();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask task = new(CreateOptions(), db, new SkippedExtensionTracker());

            await task.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await db.SetProcessedAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            (IndexStatus status, _, _, bool wasProcessed) = await task.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            status.Should().Be(IndexStatus.Unchanged);
            wasProcessed.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task IndexFileAsync_Rehash_AlwaysRecomputesHash()
    {
        string dbPath = TempDb();
        string filePath = TempFile();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            // Insert with no-rehash first
            IndexTask noRehashTask = new(CreateOptions(), db, new SkippedExtensionTracker());
            (_, string firstHash, _, _) = await noRehashTask.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            // The file is unchanged so the hash matches, but the path runs through ComputeHashesAsync.
            IndexTask rehashTask = new(
                CreateOptions(rehash: true),
                db,
                new SkippedExtensionTracker()
            );
            (IndexStatus status, string rehashResult, _, _) = await rehashTask.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            status.Should().Be(IndexStatus.Unchanged); // hash same, so Unchanged
            rehashResult.Should().Be(firstHash);
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonMediaFile_CountedAsIgnored()
    {
        string dbPath = TempDb();
        string mediaFile = TempFile(ext: ".jpg");
        string nonMediaFile = TempFile(ext: ".txt");
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask task = new(CreateOptions(), db, new SkippedExtensionTracker());

            (int inserted, int updated, int unchanged, int ignored, int failed) =
                await task.ExecuteAsync(
                    [mediaFile, nonMediaFile],
                    cancellationToken: TestContext.Current.CancellationToken
                );

            inserted.Should().Be(1);
            updated.Should().Be(0);
            unchanged.Should().Be(0);
            ignored.Should().Be(1);
            failed.Should().Be(0);
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(mediaFile);
            File.Delete(nonMediaFile);
        }
    }

    [Fact]
    public async Task IndexFileAsync_MarkProcessed_InsertsWithIsProcessedTrue()
    {
        string dbPath = TempDb();
        string filePath = TempFile();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask task = new(
                CreateOptions(markProcessed: true),
                db,
                new SkippedExtensionTracker()
            );

            (IndexStatus status, _, _, bool wasProcessed) = await task.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            status.Should().Be(IndexStatus.Inserted);
            wasProcessed.Should().BeTrue();
            FileRecord? row = await db.GetByPathAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );
            row.Should().NotBeNull();
            row.IsProcessed.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task IndexFileAsync_MarkProcessed_PreservesExistingFlagOnUpdate()
    {
        // --processed affects only rows being inserted, and existing rows keep their flag.
        // UpdateHashesAsync clears that flag on a hash change, but --processed never alters it.
        string dbPath = TempDb();
        string filePath = TempFile("original");
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            // Insert WITHOUT --processed (is_processed = 0).
            IndexTask noFlag = new(CreateOptions(), db, new SkippedExtensionTracker());
            await noFlag.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            // The file is unchanged, so the re-run reports Unchanged.
            // The existing row keeps is_processed at 0.
            IndexTask withFlag = new(
                CreateOptions(markProcessed: true),
                db,
                new SkippedExtensionTracker()
            );
            (IndexStatus status, _, _, bool wasProcessed) = await withFlag.IndexFileAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );

            status.Should().Be(IndexStatus.Unchanged);
            wasProcessed.Should().BeFalse();
            FileRecord? row = await db.GetByPathAsync(
                filePath,
                cancellationToken: TestContext.Current.CancellationToken
            );
            row.Should().NotBeNull();
            row.IsProcessed.Should().BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MultipleFiles_CountsAllStatuses()
    {
        string dbPath = TempDb();
        string newFile = TempFile("new content");
        string changedFile = TempFile("original");
        string unchangedFile = TempFile("stable");
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            IndexTask task = new(CreateOptions(), db, new SkippedExtensionTracker());

            // Pre-insert changedFile and unchangedFile
            await task.IndexFileAsync(
                changedFile,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await task.IndexFileAsync(
                unchangedFile,
                cancellationToken: TestContext.Current.CancellationToken
            );

            // Modify changedFile so hash changes
            await File.WriteAllBytesAsync(
                changedFile,
                Encoding.UTF8.GetBytes("modified"),
                cancellationToken: TestContext.Current.CancellationToken
            );
            File.SetLastWriteTimeUtc(changedFile, DateTime.UtcNow.AddSeconds(1));

            (int inserted, int updated, int unchanged, int ignored, int failed) =
                await task.ExecuteAsync(
                    [newFile, changedFile, unchangedFile],
                    cancellationToken: TestContext.Current.CancellationToken
                );

            inserted.Should().Be(1); // newFile
            updated.Should().Be(1); // changedFile
            unchanged.Should().Be(1); // unchangedFile
            ignored.Should().Be(0);
            failed.Should().Be(0);
        }
        finally
        {
            File.Delete(dbPath);
            File.Delete(newFile);
            File.Delete(changedFile);
            File.Delete(unchangedFile);
        }
    }
}
