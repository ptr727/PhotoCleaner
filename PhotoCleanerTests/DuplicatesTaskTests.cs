using PhotoCleaner;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class DuplicatesTaskTests(TempDirectoryFixture fixture)
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

    private static CommandLine.Options CreateOptions(bool dryRun = false) =>
        new()
        {
            Path = new DirectoryInfo(Path.GetTempPath()),
            Threads = 1,
            DryRun = dryRun,
            DatePath = false,
            SkipBackup = false,
            OutPath = null,
            Format = "yyyy/MM/dd",
            DeleteEmpty = false,
            Move = false,
            TagPath = false,
            Tags = null,
            DbFile = null,
            OutDbFile = null,
            Rehash = false,
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
            $"DuplicatesTaskTest_{Path.GetRandomFileName()}"
        );
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"DuplicatesTaskTest_{Path.GetRandomFileName()}.db");

    private static async Task<Database> OpenDbAsync(string dbPath)
    {
        Database db = new(dbPath);
        await db.InitializeAsync();
        return db;
    }

    // -- UnsupportedExtension: ignored in both phases -------------------------

    [Fact]
    public async Task ExecuteAsync_UnsupportedExtension_Ignored()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        try
        {
            string srcTxt = Path.Combine(srcDir, "notes.txt");
            string outTxt = Path.Combine(outDir, "notes.txt");
            await File.WriteAllTextAsync(srcTxt, "hello");
            File.Copy(srcTxt, outTxt);

            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(
                CreateOptions(),
                db,
                outDb,
                null,
                new SkippedExtensionTracker()
            );
            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [srcTxt],
                [outTxt],
                TestContext.Current.CancellationToken
            );

            indexed.Should().Be(0);
            ignored.Should().Be(2); // .txt not a supported media extension (source + target)
            deleted.Should().Be(0);
            kept.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(outTxt).Should().BeTrue(); // unsupported file not touched
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
        }
    }

    // -- NoDuplicates: unique outpath files are kept --------------------------

    [Fact]
    public async Task ExecuteAsync_NoDuplicates_NoFilesDeleted()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "source.jpg");
            string outJpg = Path.Combine(outDir, "other.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallPngFile), outJpg);

            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(
                CreateOptions(),
                db,
                outDb,
                null,
                new SkippedExtensionTracker()
            );
            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [srcJpg],
                [outJpg],
                TestContext.Current.CancellationToken
            );

            indexed.Should().Be(1);
            ignored.Should().Be(0);
            deleted.Should().Be(0);
            kept.Should().Be(1);
            failed.Should().Be(0);
            File.Exists(outJpg).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
        }
    }

    // -- DuplicateFound: matching outpath file is deleted ---------------------

    [Fact]
    public async Task ExecuteAsync_DuplicateFound_FileDeleted()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "photo.jpg");
            string outJpg = Path.Combine(outDir, "photo_copy.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(srcJpg, outJpg); // identical content

            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(
                CreateOptions(),
                db,
                outDb,
                null,
                new SkippedExtensionTracker()
            );
            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [srcJpg],
                [outJpg],
                TestContext.Current.CancellationToken
            );

            indexed.Should().Be(1);
            ignored.Should().Be(0);
            deleted.Should().Be(1);
            kept.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(outJpg).Should().BeFalse();
            File.Exists(srcJpg).Should().BeTrue(); // source is never touched
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
        }
    }

    // -- DryRun: duplicate reported but not deleted ---------------------------

    [Fact]
    public async Task ExecuteAsync_DryRun_ReportsCountButLeavesFiles()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "photo.jpg");
            string outJpg = Path.Combine(outDir, "photo_copy.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(srcJpg, outJpg);

            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(
                CreateOptions(dryRun: true),
                db,
                outDb,
                null,
                new SkippedExtensionTracker()
            );
            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [srcJpg],
                [outJpg],
                TestContext.Current.CancellationToken
            );

            indexed.Should().Be(1);
            ignored.Should().Be(0);
            deleted.Should().Be(1); // reported as deleted
            kept.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(outJpg).Should().BeTrue(); // not actually deleted in dry run
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
        }
    }

    // -- MultipleSourceFiles: all indexed, only duplicates deleted -------------

    [Fact]
    public async Task ExecuteAsync_MultipleSourceFiles_AllIndexed()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "a.jpg");
            string srcPng = Path.Combine(srcDir, "b.png");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallPngFile), srcPng);

            // One duplicate of srcJpg, one unique file in outpath
            string outDupJpg = Path.Combine(outDir, "a_dup.jpg");
            string outUniqueJpg = Path.Combine(outDir, "unique.jpg");
            File.Copy(srcJpg, outDupJpg);
            // unique.jpg: write distinct content so hash differs
            byte[] uniqueBytes = [1, 2, 3, 4, 5, 6, 7, 8];
            await File.WriteAllBytesAsync(outUniqueJpg, uniqueBytes);

            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(
                CreateOptions(),
                db,
                outDb,
                null,
                new SkippedExtensionTracker()
            );
            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [srcJpg, srcPng],
                [outDupJpg, outUniqueJpg],
                TestContext.Current.CancellationToken
            );

            indexed.Should().Be(2);
            ignored.Should().Be(0);
            deleted.Should().Be(1);
            kept.Should().Be(1);
            failed.Should().Be(0);
            File.Exists(outDupJpg).Should().BeFalse();
            File.Exists(outUniqueJpg).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
        }
    }

    // -- Incremental: second run reuses existing source index -----------------

    [Fact]
    public async Task ExecuteAsync_IncrementalRun_ReusesExistingSourceIndex()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);

            // Run 1: index source, nothing in outpath
            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(
                CreateOptions(),
                db,
                outDb,
                null,
                new SkippedExtensionTracker()
            );
            await task.ExecuteAsync([srcJpg], [], TestContext.Current.CancellationToken);

            // Run 2: source dir is empty but DB still holds the hash;
            //        a duplicate placed in outpath should still be deleted
            string outJpg = Path.Combine(outDir, "photo_copy.jpg");
            File.Copy(srcJpg, outJpg);

            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [], // no new source files - relies on DB from run 1
                [outJpg],
                TestContext.Current.CancellationToken
            );

            indexed.Should().Be(0);
            ignored.Should().Be(0);
            deleted.Should().Be(1);
            kept.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(outJpg).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
        }
    }

    // -- OutDb: target file hashes are cached in outdb -------------------------

    [Fact]
    public async Task ExecuteAsync_OutDb_CachesTargetHashes()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "photo.jpg");
            string outJpg = Path.Combine(outDir, "unique.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallPngFile), outJpg);

            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(
                CreateOptions(),
                db,
                outDb,
                null,
                new SkippedExtensionTracker()
            );
            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [srcJpg],
                [outJpg],
                TestContext.Current.CancellationToken
            );

            indexed.Should().Be(1);
            kept.Should().Be(1); // unique file kept

            // Verify outdb has a cached record for the target file
            FileRecord? cached = await outDb.GetByPathAsync(outJpg);
            cached.Should().NotBeNull();
            cached.Sha256.Should().NotBeNullOrEmpty();
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OutDb_SecondRunUsesCache()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "photo.jpg");
            string outJpg = Path.Combine(outDir, "unique.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallPngFile), outJpg);

            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(
                CreateOptions(),
                db,
                outDb,
                null,
                new SkippedExtensionTracker()
            );

            // Run 1: computes and caches target hash
            await task.ExecuteAsync([srcJpg], [outJpg], TestContext.Current.CancellationToken);

            FileRecord? afterRun1 = await outDb.GetByPathAsync(outJpg);
            afterRun1.Should().NotBeNull();

            // Run 2: same files, same outdb - should use cached hash
            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [srcJpg],
                [outJpg],
                TestContext.Current.CancellationToken
            );

            kept.Should().Be(1);

            // Outdb record should be unchanged (same hash, same mtime)
            FileRecord? afterRun2 = await outDb.GetByPathAsync(outJpg);
            afterRun2.Should().NotBeNull();
            afterRun2.Sha256.Should().Be(afterRun1.Sha256);
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
        }
    }

    // -- TrashDb: file whose SHA-1 matches trash DB is deleted -----------------

    [Fact]
    public async Task ExecuteAsync_TrashDb_MatchingFileDeleted()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        string trashDbPath = TempDb();
        try
        {
            // Source and target are different files (no SHA-256 duplicate)
            string srcJpg = Path.Combine(srcDir, "source.jpg");
            string outJpg = Path.Combine(outDir, "target.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallPngFile), outJpg);

            // Compute target SHA-1 and seed it into the trash DB
            (string _, string outSha1) = await Database.ComputeHashesAsync(outJpg);
            await using TrashDatabase trashDb = new(trashDbPath);
            await trashDb.InitializeAsync();
            await trashDb.InsertHashAsync(outSha1);

            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(CreateOptions(), db, outDb, trashDb, new());
            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [srcJpg],
                [outJpg],
                TestContext.Current.CancellationToken
            );

            indexed.Should().Be(1);
            deleted.Should().Be(1); // deleted via trash match
            kept.Should().Be(0);
            failed.Should().Be(0);
            File.Exists(outJpg).Should().BeFalse();
            File.Exists(srcJpg).Should().BeTrue(); // source untouched
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
            File.Delete(trashDbPath);
        }
    }

    // -- TrashDb: non-matching file is kept ------------------------------------

    [Fact]
    public async Task ExecuteAsync_TrashDb_NonMatchingFileKept()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        string outDbPath = TempDb();
        string trashDbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "source.jpg");
            string outJpg = Path.Combine(outDir, "target.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallPngFile), outJpg);

            // Seed trash DB with a non-matching hash
            await using TrashDatabase trashDb = new(trashDbPath);
            await trashDb.InitializeAsync();
            await trashDb.InsertHashAsync("0000000000000000000000000000000000000000");

            await using Database db = await OpenDbAsync(dbPath);
            await using Database outDb = await OpenDbAsync(outDbPath);
            DuplicatesTask task = new(CreateOptions(), db, outDb, trashDb, new());
            (int indexed, int ignored, int deleted, int kept, int failed) = await task.ExecuteAsync(
                [srcJpg],
                [outJpg],
                TestContext.Current.CancellationToken
            );

            indexed.Should().Be(1);
            deleted.Should().Be(0);
            kept.Should().Be(1);
            failed.Should().Be(0);
            File.Exists(outJpg).Should().BeTrue(); // not trashed, kept
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
            Directory.Delete(outDir, recursive: true);
            File.Delete(dbPath);
            File.Delete(outDbPath);
            File.Delete(trashDbPath);
        }
    }
}
