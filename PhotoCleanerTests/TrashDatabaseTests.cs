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
            await db.InitializeAsync();

            long count = await db.GetCountAsync();
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
            await db1.InitializeAsync();

            await using TrashDatabase db2 = new(dbPath);
            Func<Task> act = () => db2.InitializeAsync();
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
            await db.InitializeAsync();

            await db.InsertHashAsync("abc123def456");

            bool exists = await db.Sha1ExistsAsync("abc123def456");
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
            await db.InitializeAsync();

            bool exists = await db.Sha1ExistsAsync("nonexistent");
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
            await db.InitializeAsync();

            await db.InsertHashAsync("abc123");
            Func<Task> act = () => db.InsertHashAsync("abc123");
            await act.Should().NotThrowAsync();

            long count = await db.GetCountAsync();
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
            await db.InitializeAsync();

            await db.InsertHashAsync("hash1");
            await db.InsertHashAsync("hash2");
            await db.InsertHashAsync("hash3");

            long count = await db.GetCountAsync();
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
            await db.InitializeAsync();

            await db.InsertHashAsync("hash1");
            await db.InsertHashAsync("hash2");

            await db.ClearAsync();

            long count = await db.GetCountAsync();
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
            await db.InitializeAsync();
            await db.InsertHashAsync("hash-a");

            Task<bool>[] tasks =
            [
                .. Enumerable
                    .Range(0, 10)
                    .Select(i => db.Sha1ExistsAsync(i % 2 == 0 ? "hash-a" : "hash-missing")),
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
}
