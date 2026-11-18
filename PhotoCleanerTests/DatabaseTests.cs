using System.Text;
using Microsoft.Data.Sqlite;
using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class DatabaseTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"db_{Path.GetRandomFileName()}.db");

    private static FileRecord MakeRecord(string path, string sha256) =>
        new(
            path,
            sha256,
            "0000000000000000000000000000000000000000",
            1024L,
            DateTime.UtcNow.Ticks,
            false
        );

    [Fact]
    public async Task InitializeAsync_NewFile_CreatesTable()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            await using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='files'";
            object? result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            Convert.ToInt64(result).Should().Be(1);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InitializeAsync_ExistingFile_IsIdempotent()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db1 = new(dbPath);
            await db1.InitializeAsync(TestContext.Current.CancellationToken);

            await using Database db2 = new(dbPath);
            Func<Task> act = () => db2.InitializeAsync(TestContext.Current.CancellationToken);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sha256ExistsAsync_HashNotInDb_ReturnsFalse()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            bool exists = await db.Sha256ExistsAsync(
                "nonexistenthash",
                cancellationToken: TestContext.Current.CancellationToken
            );
            exists.Should().BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InsertAsync_NewRecord_Sha256ExistsReturnsTrue()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            string sha256 = "abc123";
            await db.InsertAsync(
                MakeRecord("/source/photo.jpg", sha256),
                cancellationToken: TestContext.Current.CancellationToken
            );

            bool exists = await db.Sha256ExistsAsync(
                sha256,
                cancellationToken: TestContext.Current.CancellationToken
            );
            exists.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InsertAsync_DuplicatePath_IsIgnored()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            string path = "/source/photo.jpg";
            await db.InsertAsync(
                MakeRecord(path, "hash1"),
                cancellationToken: TestContext.Current.CancellationToken
            );

            Func<Task> act = () =>
                db.InsertAsync(
                    MakeRecord(path, "hash2"),
                    cancellationToken: TestContext.Current.CancellationToken
                );
            await act.Should().NotThrowAsync();

            // First insert wins due to INSERT OR IGNORE
            FileRecord? record = await db.GetByPathAsync(
                path,
                cancellationToken: TestContext.Current.CancellationToken
            );
            record.Should().NotBeNull();
            record.Sha256.Should().Be("hash1");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task GetByPathAsync_PathNotInDb_ReturnsNull()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            FileRecord? record = await db.GetByPathAsync(
                "/missing/path.jpg",
                cancellationToken: TestContext.Current.CancellationToken
            );
            record.Should().BeNull();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task GetByPathAsync_PathInDb_ReturnsRecord()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            string path = "/source/photo.jpg";
            string sha256 = "abc456";
            string sha1 = "def789";
            long fileSize = 2048L;
            long mtimeTicks = DateTime.UtcNow.Ticks;
            await db.InsertAsync(
                new FileRecord(path, sha256, sha1, fileSize, mtimeTicks, false),
                cancellationToken: TestContext.Current.CancellationToken
            );

            FileRecord? record = await db.GetByPathAsync(
                path,
                cancellationToken: TestContext.Current.CancellationToken
            );
            record.Should().NotBeNull();
            record.Path.Should().Be(path);
            record.Sha256.Should().Be(sha256);
            record.Sha1.Should().Be(sha1);
            record.FileSize.Should().Be(fileSize);
            record.MtimeTicks.Should().Be(mtimeTicks);
            record.IsProcessed.Should().BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task UpdateHashesAsync_ExistingRecord_UpdatesHashesAndResetsIsProcessed()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            string path = "/source/photo.jpg";
            await db.InsertAsync(
                new FileRecord(path, "oldsha256", "oldsha1", 1024L, 0L, false),
                cancellationToken: TestContext.Current.CancellationToken
            );
            await db.SetProcessedAsync(
                path,
                cancellationToken: TestContext.Current.CancellationToken
            );

            long newSize = 2048L;
            long newTicks = DateTime.UtcNow.Ticks;
            await db.UpdateHashesAsync(
                path,
                "newsha256",
                "newsha1",
                newSize,
                newTicks,
                cancellationToken: TestContext.Current.CancellationToken
            );

            FileRecord? record = await db.GetByPathAsync(
                path,
                cancellationToken: TestContext.Current.CancellationToken
            );
            record.Should().NotBeNull();
            record.Sha256.Should().Be("newsha256");
            record.Sha1.Should().Be("newsha1");
            record.FileSize.Should().Be(newSize);
            record.MtimeTicks.Should().Be(newTicks);
            record.IsProcessed.Should().BeFalse(); // reset by UpdateHashesAsync
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task SetProcessedAsync_ExistingRecord_SetsIsProcessed()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            string path = "/source/photo.jpg";
            await db.InsertAsync(
                MakeRecord(path, "hash1"),
                cancellationToken: TestContext.Current.CancellationToken
            );

            await db.SetProcessedAsync(
                path,
                cancellationToken: TestContext.Current.CancellationToken
            );

            FileRecord? record = await db.GetByPathAsync(
                path,
                cancellationToken: TestContext.Current.CancellationToken
            );
            record.Should().NotBeNull();
            record.IsProcessed.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ResolveHashesAsync_CacheHit_ReturnsCachedHashes()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                file,
                Encoding.UTF8.GetBytes("hello photo"),
                cancellationToken: TestContext.Current.CancellationToken
            );
            FileInfo info = new(file);
            FileRecord cached = new(
                file,
                "cached-sha256-value",
                "cached-sha1-value",
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                false
            );

            (string sha256, string? sha1) = await Database.ResolveHashesAsync(
                file,
                cached,
                rehash: false,
                cancellationToken: TestContext.Current.CancellationToken
            );
            sha256.Should().Be("cached-sha256-value");
            sha1.Should().Be("cached-sha1-value");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ResolveHashesAsync_SizeMismatch_RecomputesHashes()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                file,
                Encoding.UTF8.GetBytes("hello photo"),
                cancellationToken: TestContext.Current.CancellationToken
            );
            FileInfo info = new(file);
            // Cached record has wrong size
            FileRecord cached = new(
                file,
                "stale-sha256",
                "stale-sha1",
                info.Length + 1,
                info.LastWriteTimeUtc.Ticks,
                false
            );

            (string sha256, string? sha1) = await Database.ResolveHashesAsync(
                file,
                cached,
                rehash: false,
                cancellationToken: TestContext.Current.CancellationToken
            );
            (string expectedSha256, string expectedSha1) = await Database.ComputeHashesAsync(
                file,
                cancellationToken: TestContext.Current.CancellationToken
            );
            sha256.Should().Be(expectedSha256);
            sha1.Should().Be(expectedSha1);
            sha256.Should().NotBe("stale-sha256");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ResolveHashesAsync_Rehash_AlwaysRecomputes()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                file,
                Encoding.UTF8.GetBytes("hello photo"),
                cancellationToken: TestContext.Current.CancellationToken
            );
            FileInfo info = new(file);
            // Cached record matches size and mtime exactly
            FileRecord cached = new(
                file,
                "stale-sha256",
                "stale-sha1",
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                false
            );

            // Rehashing ignores the cache.
            (string sha256, string? sha1) = await Database.ResolveHashesAsync(
                file,
                cached,
                rehash: true,
                cancellationToken: TestContext.Current.CancellationToken
            );
            (string expectedSha256, string expectedSha1) = await Database.ComputeHashesAsync(
                file,
                cancellationToken: TestContext.Current.CancellationToken
            );
            sha256.Should().Be(expectedSha256);
            sha1.Should().Be(expectedSha1);
            sha256.Should().NotBe("stale-sha256");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ComputeHashesAsync_SameContent_ReturnsSameHashes()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                file,
                Encoding.UTF8.GetBytes("hello photo"),
                cancellationToken: TestContext.Current.CancellationToken
            );

            (string sha256a, string sha1a) = await Database.ComputeHashesAsync(
                file,
                cancellationToken: TestContext.Current.CancellationToken
            );
            (string sha256b, string sha1b) = await Database.ComputeHashesAsync(
                file,
                cancellationToken: TestContext.Current.CancellationToken
            );
            sha256a.Should().Be(sha256b);
            sha1a.Should().Be(sha1b);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ComputeHashesAsync_DifferentContent_ReturnsDifferentHashes()
    {
        string file1 = Path.GetTempFileName();
        string file2 = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                file1,
                Encoding.UTF8.GetBytes("content A"),
                cancellationToken: TestContext.Current.CancellationToken
            );
            await File.WriteAllBytesAsync(
                file2,
                Encoding.UTF8.GetBytes("content B"),
                cancellationToken: TestContext.Current.CancellationToken
            );

            (string sha256a, string sha1a) = await Database.ComputeHashesAsync(
                file1,
                cancellationToken: TestContext.Current.CancellationToken
            );
            (string sha256b, string sha1b) = await Database.ComputeHashesAsync(
                file2,
                cancellationToken: TestContext.Current.CancellationToken
            );
            sha256a.Should().NotBe(sha256b);
            sha1a.Should().NotBe(sha1b);
        }
        finally
        {
            File.Delete(file1);
            File.Delete(file2);
        }
    }

    [Fact]
    public async Task Sha256ExistsAsync_ConcurrentCalls_NoDeadlock()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertAsync(
                MakeRecord("/source/photo.jpg", "hash-a"),
                cancellationToken: TestContext.Current.CancellationToken
            );

            Task<bool>[] tasks =
            [
                .. Enumerable
                    .Range(0, 10)
                    .Select(i =>
                        db.Sha256ExistsAsync(
                            i % 2 == 0 ? "hash-a" : "hash-missing",
                            cancellationToken: TestContext.Current.CancellationToken
                        )
                    ),
            ];
            bool[] results = await Task.WhenAll(tasks);
            results.Count(r => r).Should().Be(5);
            results.Count(r => !r).Should().Be(5);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sha1ExistsAsync_HashInDb_ReturnsTrue()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertAsync(
                new FileRecord("/source/photo.jpg", "sha256val", "sha1val", 1024L, 0L, false),
                cancellationToken: TestContext.Current.CancellationToken
            );

            bool exists = await db.Sha1ExistsAsync(
                "sha1val",
                cancellationToken: TestContext.Current.CancellationToken
            );
            exists.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sha1ExistsAsync_HashNotInDb_ReturnsFalse()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            bool exists = await db.Sha1ExistsAsync(
                "nonexistent",
                cancellationToken: TestContext.Current.CancellationToken
            );
            exists.Should().BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ComputeHashesAsync_BothHashesAreValid()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                file,
                Encoding.UTF8.GetBytes("hello photo"),
                cancellationToken: TestContext.Current.CancellationToken
            );
            (string sha256, string sha1) = await Database.ComputeHashesAsync(
                file,
                cancellationToken: TestContext.Current.CancellationToken
            );

            sha256.Should().HaveLength(64); // SHA-256 is 32 bytes = 64 hex chars
            sha256.Should().MatchRegex("^[0-9a-f]+$");
            sha1.Should().HaveLength(40); // SHA-1 is 20 bytes = 40 hex chars
            sha1.Should().MatchRegex("^[0-9a-f]+$");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task FindPathByImageHashAsync_MatchingRow_ReturnsItsPath()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertAsync(
                new FileRecord(
                    "/source/IMG_1234.HEIC",
                    "sha256-a",
                    "sha1-a",
                    10L,
                    0L,
                    false,
                    ImageHash: "imagehash-a",
                    MetadataRead: true
                ),
                TestContext.Current.CancellationToken
            );

            (
                await db.FindPathByImageHashAsync(
                    "imagehash-a",
                    TestContext.Current.CancellationToken
                )
            )
                .Should()
                .Be("/source/IMG_1234.HEIC");
            (
                await db.FindPathByImageHashAsync(
                    "imagehash-b",
                    TestContext.Current.CancellationToken
                )
            )
                .Should()
                .BeNull();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task FindPathByImageHashAsync_NullOrEmptyHash_NeverMatchesRowsWithoutOne()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertAsync(
                new FileRecord("/source/clip.gif", "sha256-a", "sha1-a", 10L, 0L, false),
                TestContext.Current.CancellationToken
            );

            (await db.FindPathByImageHashAsync(null, TestContext.Current.CancellationToken))
                .Should()
                .BeNull();
            (await db.FindPathByImageHashAsync("", TestContext.Current.CancellationToken))
                .Should()
                .BeNull();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task GetImageHashBySha1Async_ReturnsRecordedHashOrNull()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertAsync(
                new FileRecord(
                    "/processed/IMG_1234.HEIC",
                    "sha256-a",
                    "sha1-a",
                    10L,
                    0L,
                    true,
                    ImageHash: "imagehash-a",
                    MetadataRead: true
                ),
                TestContext.Current.CancellationToken
            );
            await db.InsertAsync(
                new FileRecord("/processed/clip.gif", "sha256-b", "sha1-b", 10L, 0L, true),
                TestContext.Current.CancellationToken
            );

            (await db.GetImageHashBySha1Async("sha1-a", TestContext.Current.CancellationToken))
                .Should()
                .Be("imagehash-a");
            (await db.GetImageHashBySha1Async("sha1-b", TestContext.Current.CancellationToken))
                .Should()
                .BeNull();
            (await db.GetImageHashBySha1Async("sha1-c", TestContext.Current.CancellationToken))
                .Should()
                .BeNull();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ImageHashLookups_OldSchemaDatabaseReadOnly_ReturnNull()
    {
        string dbPath = TempDb();
        try
        {
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
                    INSERT INTO files VALUES ('/processed/IMG_1234.HEIC', 'sha256-a', 'sha1-a', 10, 20, 1)
                    """;
                _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using Database db = new(dbPath, readOnly: true);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            db.HasImageHash.Should().BeFalse();
            (await db.GetImageHashBySha1Async("sha1-a", TestContext.Current.CancellationToken))
                .Should()
                .BeNull();
            (
                await db.FindPathByImageHashAsync(
                    "imagehash-a",
                    TestContext.Current.CancellationToken
                )
            )
                .Should()
                .BeNull();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }
}
