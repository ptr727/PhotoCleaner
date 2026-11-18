using Microsoft.Data.Sqlite;
using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class TrashDatabaseTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"trashdb_{Path.GetRandomFileName()}.db");

    [Fact]
    public async Task InitializeAsync_NewFile_CreatesTable()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(0);
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
            await using TrashDatabase db1 = new(dbPath);
            await db1.InitializeAsync(TestContext.Current.CancellationToken);

            await using TrashDatabase db2 = new(dbPath);
            Func<Task> act = () => db2.InitializeAsync(TestContext.Current.CancellationToken);
            await act.Should().NotThrowAsync();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InsertHashAsync_NewHash_Sha1ExistsReturnsTrue()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            await db.InsertHashAsync(
                "abc123def456",
                cancellationToken: TestContext.Current.CancellationToken
            );

            bool exists = await db.Sha1ExistsAsync(
                "abc123def456",
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
            await using TrashDatabase db = new(dbPath);
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
    public async Task InsertHashAsync_DuplicateHash_IsIgnored()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            await db.InsertHashAsync(
                "abc123",
                cancellationToken: TestContext.Current.CancellationToken
            );
            Func<Task> act = () =>
                db.InsertHashAsync(
                    "abc123",
                    cancellationToken: TestContext.Current.CancellationToken
                );
            await act.Should().NotThrowAsync();

            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(1);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task GetCountAsync_MultipleHashes_ReturnsCorrectCount()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            await db.InsertHashAsync(
                "hash1",
                cancellationToken: TestContext.Current.CancellationToken
            );
            await db.InsertHashAsync(
                "hash2",
                cancellationToken: TestContext.Current.CancellationToken
            );
            await db.InsertHashAsync(
                "hash3",
                cancellationToken: TestContext.Current.CancellationToken
            );

            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(3);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ClearAsync_RemovesAllHashes()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            await db.InsertHashAsync(
                "hash1",
                cancellationToken: TestContext.Current.CancellationToken
            );
            await db.InsertHashAsync(
                "hash2",
                cancellationToken: TestContext.Current.CancellationToken
            );

            await db.ClearAsync(TestContext.Current.CancellationToken);

            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(0);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Sha1ExistsAsync_ConcurrentCalls_NoDeadlock()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertHashAsync(
                "hash-a",
                cancellationToken: TestContext.Current.CancellationToken
            );

            Task<bool>[] tasks =
            [
                .. Enumerable
                    .Range(0, 10)
                    .Select(i =>
                        db.Sha1ExistsAsync(
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
    public async Task InsertHashesAsync_BatchInsert_AllHashesPresent()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            List<(string Sha1Hex, string? OriginalFileName, string? ImageHash)> batch =
            [
                ("aaa111", "photo1.jpg", null),
                ("bbb222", "photo2.jpg", null),
                ("ccc333", null, null),
            ];
            await db.InsertHashesAsync(
                batch,
                cancellationToken: TestContext.Current.CancellationToken
            );

            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(3);
            (
                await db.Sha1ExistsAsync(
                    "aaa111",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
                .Should()
                .BeTrue();
            (
                await db.Sha1ExistsAsync(
                    "bbb222",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
                .Should()
                .BeTrue();
            (
                await db.Sha1ExistsAsync(
                    "ccc333",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
                .Should()
                .BeTrue();
            (
                await db.Sha1ExistsAsync(
                    "ddd444",
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
                .Should()
                .BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InsertHashesAsync_DuplicatesInBatch_Ignored()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            List<(string Sha1Hex, string? OriginalFileName, string? ImageHash)> batch =
            [
                ("aaa111", "photo1.jpg", null),
                ("aaa111", "photo1_dup.jpg", null),
                ("bbb222", "photo2.jpg", null),
            ];
            await db.InsertHashesAsync(
                batch,
                cancellationToken: TestContext.Current.CancellationToken
            );

            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(2); // duplicate ignored
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InsertHashesAsync_EmptyBatch_NoError()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            await db.InsertHashesAsync(
                [],
                cancellationToken: TestContext.Current.CancellationToken
            );

            long count = await db.GetCountAsync(TestContext.Current.CancellationToken);
            count.Should().Be(0);
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InitializeAsync_OldSchemaDatabase_AddsImageHashColumnKeepingRows()
    {
        string dbPath = TempDb();
        try
        {
            // Arrange - a trash database in the shape written before the image_hash column existed
            await CreateOldSchemaAsync(dbPath);

            // Act
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            // Assert - the row survives, and a later sync backfills its image hash
            db.HasImageHash.Should().BeTrue();
            (await db.Sha1ExistsAsync("aaa111", TestContext.Current.CancellationToken))
                .Should()
                .BeTrue();
            (await db.ImageHashExistsAsync("imagehash-a", TestContext.Current.CancellationToken))
                .Should()
                .BeFalse();
            await db.InsertHashesAsync(
                [("aaa111", "IMG_1234.HEIC", "imagehash-a")],
                TestContext.Current.CancellationToken
            );
            (await db.GetCountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
            (await db.ImageHashExistsAsync("imagehash-a", TestContext.Current.CancellationToken))
                .Should()
                .BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InitializeAsync_OldSchemaDatabaseReadOnly_MatchesBySha1Only()
    {
        string dbPath = TempDb();
        try
        {
            await CreateOldSchemaAsync(dbPath);

            await using TrashDatabase db = new(dbPath, readOnly: true);
            await db.InitializeAsync(TestContext.Current.CancellationToken);

            db.HasImageHash.Should().BeFalse();
            (await db.Sha1ExistsAsync("aaa111", TestContext.Current.CancellationToken))
                .Should()
                .BeTrue();
            (await db.ImageHashExistsAsync("imagehash-a", TestContext.Current.CancellationToken))
                .Should()
                .BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ImageHashExistsAsync_NullOrEmptyHash_NeverMatches()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertHashesAsync(
                [("aaa111", "IMG_1234.HEIC", null), ("bbb222", "IMG_5678.HEIC", "")],
                TestContext.Current.CancellationToken
            );

            (await db.ImageHashExistsAsync(null, TestContext.Current.CancellationToken))
                .Should()
                .BeFalse();
            (await db.ImageHashExistsAsync("", TestContext.Current.CancellationToken))
                .Should()
                .BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InsertHashAsync_WithImageHash_ImageHashExistsReturnsTrue()
    {
        string dbPath = TempDb();
        try
        {
            await using TrashDatabase db = new(dbPath);
            await db.InitializeAsync(TestContext.Current.CancellationToken);
            await db.InsertHashAsync(
                "aaa111",
                "IMG_1234.HEIC",
                "imagehash-a",
                TestContext.Current.CancellationToken
            );

            (await db.ImageHashExistsAsync("imagehash-a", TestContext.Current.CancellationToken))
                .Should()
                .BeTrue();
            (await db.ImageHashExistsAsync("imagehash-b", TestContext.Current.CancellationToken))
                .Should()
                .BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    private static async Task CreateOldSchemaAsync(string dbPath)
    {
        await using SqliteConnection connection = new($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE trash_hashes (
                sha1                TEXT NOT NULL PRIMARY KEY,
                original_file_name  TEXT
            );
            INSERT INTO trash_hashes VALUES ('aaa111', 'IMG_1234.HEIC')
            """;
        _ = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
