using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class DuplicatesTaskTests(TempDirectoryFixture fixture)
    : IClassFixture<TempDirectoryFixture>
{
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
    public async Task ExecuteDuplicatesAsync_UnsupportedExtension_Ignored()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string srcTxt = Path.Combine(srcDir, "notes.txt");
            string outTxt = Path.Combine(outDir, "notes.txt");
            await File.WriteAllTextAsync(srcTxt, "hello");
            File.Copy(srcTxt, outTxt);

            await using Database db = await OpenDbAsync(dbPath);
            DuplicatesTask task = new(dryRun: false, threads: 1, rehash: false, db);
            (int indexed, int ignored, int deleted, int kept, int failed) =
                await task.ExecuteDuplicatesAsync(
                    [srcTxt],
                    [outTxt],
                    TestContext.Current.CancellationToken
                );

            indexed.Should().Be(0);
            ignored.Should().Be(1); // .txt not a supported media extension
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
        }
    }

    // -- NoDuplicates: unique outpath files are kept --------------------------

    [Fact]
    public async Task ExecuteDuplicatesAsync_NoDuplicates_NoFilesDeleted()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "source.jpg");
            string outJpg = Path.Combine(outDir, "other.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallPngFile), outJpg);

            await using Database db = await OpenDbAsync(dbPath);
            DuplicatesTask task = new(dryRun: false, threads: 1, rehash: false, db);
            (int indexed, int ignored, int deleted, int kept, int failed) =
                await task.ExecuteDuplicatesAsync(
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
        }
    }

    // -- DuplicateFound: matching outpath file is deleted ---------------------

    [Fact]
    public async Task ExecuteDuplicatesAsync_DuplicateFound_FileDeleted()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "photo.jpg");
            string outJpg = Path.Combine(outDir, "photo_copy.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(srcJpg, outJpg); // identical content

            await using Database db = await OpenDbAsync(dbPath);
            DuplicatesTask task = new(dryRun: false, threads: 1, rehash: false, db);
            (int indexed, int ignored, int deleted, int kept, int failed) =
                await task.ExecuteDuplicatesAsync(
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
        }
    }

    // -- DryRun: duplicate reported but not deleted ---------------------------

    [Fact]
    public async Task ExecuteDuplicatesAsync_DryRun_ReportsCountButLeavesFiles()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "photo.jpg");
            string outJpg = Path.Combine(outDir, "photo_copy.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);
            File.Copy(srcJpg, outJpg);

            await using Database db = await OpenDbAsync(dbPath);
            DuplicatesTask task = new(dryRun: true, threads: 1, rehash: false, db);
            (int indexed, int ignored, int deleted, int kept, int failed) =
                await task.ExecuteDuplicatesAsync(
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
        }
    }

    // -- MultipleSourceFiles: all indexed, only duplicates deleted -------------

    [Fact]
    public async Task ExecuteDuplicatesAsync_MultipleSourceFiles_AllIndexed()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
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
            DuplicatesTask task = new(dryRun: false, threads: 1, rehash: false, db);
            (int indexed, int ignored, int deleted, int kept, int failed) =
                await task.ExecuteDuplicatesAsync(
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
        }
    }

    // -- Incremental: second run reuses existing source index -----------------

    [Fact]
    public async Task ExecuteDuplicatesAsync_IncrementalRun_ReusesExistingSourceIndex()
    {
        string srcDir = TempDir();
        string outDir = TempDir();
        string dbPath = TempDb();
        try
        {
            string srcJpg = Path.Combine(srcDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), srcJpg);

            // Run 1: index source, nothing in outpath
            await using Database db = await OpenDbAsync(dbPath);
            DuplicatesTask task = new(dryRun: false, threads: 1, rehash: false, db);
            await task.ExecuteDuplicatesAsync([srcJpg], [], TestContext.Current.CancellationToken);

            // Run 2: source dir is empty but DB still holds the hash;
            //        a duplicate placed in outpath should still be deleted
            string outJpg = Path.Combine(outDir, "photo_copy.jpg");
            File.Copy(srcJpg, outJpg);

            (int indexed, int ignored, int deleted, int kept, int failed) =
                await task.ExecuteDuplicatesAsync(
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
        }
    }
}
