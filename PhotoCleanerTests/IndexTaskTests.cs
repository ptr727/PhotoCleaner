using System.Text;
using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class IndexTaskTests
{
    private static CommandLine.Options CreateOptions(bool rehash = false) =>
        new()
        {
            Path = new DirectoryInfo(Path.GetTempPath()),
            Threads = 1,
            DryRun = false,
            DatePath = false,
            SkipBackup = false,
            OutPath = null,
            Format = "yyyy/MM",
            DeleteEmpty = false,
            Move = false,
            TagPath = false,
            Tags = null,
            DbFile = null,
            Rehash = rehash,
            ShortVideoDuration = MediaUtilities.ShortVideoDuration,
            Reprocess = false,
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
            await db.InitializeAsync();
            IndexTask task = new(CreateOptions(), db);

            (IndexStatus status, string hash, bool wasProcessed) = await task.IndexFileAsync(
                filePath
            );

            status.Should().Be(IndexStatus.Inserted);
            hash.Should().NotBeNullOrEmpty();
            wasProcessed.Should().BeFalse();

            bool exists = await db.HashExistsAsync(hash);
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
            await db.InitializeAsync();
            IndexTask task = new(CreateOptions(), db);

            // First call inserts
            await task.IndexFileAsync(filePath);

            // Second call with same file should return Unchanged
            (IndexStatus status, string hash, bool wasProcessed) = await task.IndexFileAsync(
                filePath
            );

            status.Should().Be(IndexStatus.Unchanged);
            hash.Should().NotBeNullOrEmpty();
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
            await db.InitializeAsync();
            IndexTask task = new(CreateOptions(), db);

            // Insert and mark as processed
            await task.IndexFileAsync(filePath);
            await db.SetProcessedAsync(filePath);

            FileRecord? before = await db.GetByPathAsync(filePath);
            before!.IsProcessed.Should().BeTrue();

            // Overwrite file with new content and touch mtime
            await File.WriteAllBytesAsync(filePath, Encoding.UTF8.GetBytes("completely different"));
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(1));

            (IndexStatus status, _, bool wasProcessed) = await task.IndexFileAsync(filePath);

            status.Should().Be(IndexStatus.Updated);
            wasProcessed.Should().BeFalse();

            FileRecord? after = await db.GetByPathAsync(filePath);
            after!.IsProcessed.Should().BeFalse(); // reset by UpdateHashAsync
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
            await db.InitializeAsync();
            IndexTask task = new(CreateOptions(), db);

            await task.IndexFileAsync(filePath);
            await db.SetProcessedAsync(filePath);

            (IndexStatus status, _, bool wasProcessed) = await task.IndexFileAsync(filePath);

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
            await db.InitializeAsync();

            // Insert with no-rehash first
            IndexTask noRehashTask = new(CreateOptions(), db);
            (_, string firstHash, _) = await noRehashTask.IndexFileAsync(filePath);

            // Rehash task forces recomputation - result should be same hash (file unchanged)
            // but the code path goes through ComputeHashAsync
            IndexTask rehashTask = new(CreateOptions(rehash: true), db);
            (IndexStatus status, string rehashResult, _) = await rehashTask.IndexFileAsync(
                filePath
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
            await db.InitializeAsync();
            IndexTask task = new(CreateOptions(), db);

            (int inserted, int updated, int unchanged, int ignored, int failed) =
                await task.ExecuteAsync([mediaFile, nonMediaFile]);

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
    public async Task ExecuteAsync_MultipleFiles_CountsAllStatuses()
    {
        string dbPath = TempDb();
        string newFile = TempFile("new content");
        string changedFile = TempFile("original");
        string unchangedFile = TempFile("stable");
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            IndexTask task = new(CreateOptions(), db);

            // Pre-insert changedFile and unchangedFile
            await task.IndexFileAsync(changedFile);
            await task.IndexFileAsync(unchangedFile);

            // Modify changedFile so hash changes
            await File.WriteAllBytesAsync(changedFile, Encoding.UTF8.GetBytes("modified"));
            File.SetLastWriteTimeUtc(changedFile, DateTime.UtcNow.AddSeconds(1));

            (int inserted, int updated, int unchanged, int ignored, int failed) =
                await task.ExecuteAsync([newFile, changedFile, unchangedFile]);

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
