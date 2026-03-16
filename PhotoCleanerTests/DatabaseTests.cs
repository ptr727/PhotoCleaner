using System.Text;
using Microsoft.Data.Sqlite;
using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class DatabaseTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"db_{Path.GetRandomFileName()}.db");

    private static OrganizedFileRecord MakeRecord(string hash) =>
        new(
            hash,
            "/source/photo.jpg",
            "photo.jpg",
            null,
            null,
            1024,
            "image/jpeg",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "/dest/2024-01/photo.jpg"
        );

    [Fact]
    public async Task InitializeAsync_NewFile_CreatesTable()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();

            await using SqliteConnection conn = new($"Data Source={dbPath}");
            await conn.OpenAsync();
            SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='organized_files'";
            object? result = await cmd.ExecuteScalarAsync();
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
            await db1.InitializeAsync();

            await using Database db2 = new(dbPath);
            Func<Task> act = db2.InitializeAsync;
            await act.Should().NotThrowAsync();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExistsAsync_HashNotInDb_ReturnsFalse()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();

            bool exists = await db.ExistsAsync("nonexistenthash");
            exists.Should().BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InsertAsync_NewRecord_ExistsAsyncReturnsTrue()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            string hash = "abc123";
            await db.InsertAsync(MakeRecord(hash));

            bool exists = await db.ExistsAsync(hash);
            exists.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InsertAsync_DuplicateHash_NoException()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            string hash = "duplicate123";
            await db.InsertAsync(MakeRecord(hash));

            Func<Task> act = () => db.InsertAsync(MakeRecord(hash));
            await act.Should().NotThrowAsync();

            bool exists = await db.ExistsAsync(hash);
            exists.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ComputeHashAsync_SameContent_ReturnsSameHash()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("hello photo"));

            string hash1 = await Database.ComputeHashAsync(file);
            string hash2 = await Database.ComputeHashAsync(file);
            hash1.Should().Be(hash2);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ComputeHashAsync_DifferentContent_ReturnsDifferentHash()
    {
        string file1 = Path.GetTempFileName();
        string file2 = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file1, Encoding.UTF8.GetBytes("content A"));
            await File.WriteAllBytesAsync(file2, Encoding.UTF8.GetBytes("content B"));

            string hash1 = await Database.ComputeHashAsync(file1);
            string hash2 = await Database.ComputeHashAsync(file2);
            hash1.Should().NotBe(hash2);
        }
        finally
        {
            File.Delete(file1);
            File.Delete(file2);
        }
    }

    [Fact]
    public async Task GetSourcePathAsync_HashNotInDb_ReturnsNull()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();

            string? sourcePath = await db.GetSourcePathAsync("nonexistenthash");
            sourcePath.Should().BeNull();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task GetSourcePathAsync_HashInDb_ReturnsRecordedSourcePath()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            string hash = "abc456";
            await db.InsertAsync(MakeRecord(hash));

            string? sourcePath = await db.GetSourcePathAsync(hash);
            sourcePath.Should().Be("/source/photo.jpg");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ExistsAsync_ConcurrentCalls_NoDeadlock()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            await db.InsertAsync(MakeRecord("hash-a"));

            Task<bool>[] tasks =
            [
                .. Enumerable
                    .Range(0, 10)
                    .Select(i => db.ExistsAsync(i % 2 == 0 ? "hash-a" : "hash-missing")),
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
