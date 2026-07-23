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

            List<(string Sha1Hex, string? OriginalFileName)> batch =
            [
                ("aaa111", "photo1.jpg"),
                ("bbb222", "photo2.jpg"),
                ("ccc333", null),
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

            List<(string Sha1Hex, string? OriginalFileName)> batch =
            [
                ("aaa111", "photo1.jpg"),
                ("aaa111", "photo1_dup.jpg"),
                ("bbb222", "photo2.jpg"),
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
}
