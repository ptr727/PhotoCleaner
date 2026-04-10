using System.Text;
using Microsoft.Data.Sqlite;
using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class DatabaseTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"db_{Path.GetRandomFileName()}.db");

    private static FileRecord MakeRecord(string path, string hash) =>
        new(path, hash, 1024L, DateTime.UtcNow.Ticks, false);

    [Fact]
    public async Task InitializeAsync_NewFile_CreatesTable()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();

            await using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
            await conn.OpenAsync();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='files'";
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
            Func<Task> act = () => db2.InitializeAsync();
            await act.Should().NotThrowAsync();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task HashExistsAsync_HashNotInDb_ReturnsFalse()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();

            bool exists = await db.HashExistsAsync("nonexistenthash");
            exists.Should().BeFalse();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task InsertAsync_NewRecord_HashExistsReturnsTrue()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            string hash = "abc123";
            await db.InsertAsync(MakeRecord("/source/photo.jpg", hash));

            bool exists = await db.HashExistsAsync(hash);
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
            await db.InitializeAsync();
            string path = "/source/photo.jpg";
            await db.InsertAsync(MakeRecord(path, "hash1"));

            Func<Task> act = () => db.InsertAsync(MakeRecord(path, "hash2"));
            await act.Should().NotThrowAsync();

            // First insert wins due to INSERT OR IGNORE
            FileRecord? record = await db.GetByPathAsync(path);
            record.Should().NotBeNull();
            record.Hash.Should().Be("hash1");
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
            await db.InitializeAsync();

            FileRecord? record = await db.GetByPathAsync("/missing/path.jpg");
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
            await db.InitializeAsync();
            string path = "/source/photo.jpg";
            string hash = "abc456";
            long fileSize = 2048L;
            long mtimeTicks = DateTime.UtcNow.Ticks;
            await db.InsertAsync(new FileRecord(path, hash, fileSize, mtimeTicks, false));

            FileRecord? record = await db.GetByPathAsync(path);
            record.Should().NotBeNull();
            record.Path.Should().Be(path);
            record.Hash.Should().Be(hash);
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
    public async Task UpdateHashAsync_ExistingRecord_UpdatesHashAndResetsIsProcessed()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            string path = "/source/photo.jpg";
            await db.InsertAsync(new FileRecord(path, "oldhash", 1024L, 0L, false));
            await db.SetProcessedAsync(path);

            long newSize = 2048L;
            long newTicks = DateTime.UtcNow.Ticks;
            await db.UpdateHashAsync(path, "newhash", newSize, newTicks);

            FileRecord? record = await db.GetByPathAsync(path);
            record.Should().NotBeNull();
            record.Hash.Should().Be("newhash");
            record.FileSize.Should().Be(newSize);
            record.MtimeTicks.Should().Be(newTicks);
            record.IsProcessed.Should().BeFalse(); // reset by UpdateHashAsync
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
            await db.InitializeAsync();
            string path = "/source/photo.jpg";
            await db.InsertAsync(MakeRecord(path, "hash1"));

            await db.SetProcessedAsync(path);

            FileRecord? record = await db.GetByPathAsync(path);
            record.Should().NotBeNull();
            record.IsProcessed.Should().BeTrue();
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ResolveHashAsync_CacheHit_ReturnsCachedHash()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("hello photo"));
            FileInfo info = new(file);
            FileRecord cached = new(
                file,
                "cached-hash-value",
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                false
            );

            string hash = await Database.ResolveHashAsync(file, cached, rehash: false);
            hash.Should().Be("cached-hash-value");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ResolveHashAsync_SizeMismatch_RecomputesHash()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("hello photo"));
            FileInfo info = new(file);
            // Cached record has wrong size
            FileRecord cached = new(
                file,
                "stale-hash",
                info.Length + 1,
                info.LastWriteTimeUtc.Ticks,
                false
            );

            string hash = await Database.ResolveHashAsync(file, cached, rehash: false);
            string expected = await Database.ComputeHashAsync(file);
            hash.Should().Be(expected);
            hash.Should().NotBe("stale-hash");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ResolveHashAsync_Rehash_AlwaysRecomputes()
    {
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("hello photo"));
            FileInfo info = new(file);
            // Cached record matches size and mtime exactly
            FileRecord cached = new(
                file,
                "stale-hash",
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                false
            );

            // rehash=true ignores cache
            string hash = await Database.ResolveHashAsync(file, cached, rehash: true);
            string expected = await Database.ComputeHashAsync(file);
            hash.Should().Be(expected);
            hash.Should().NotBe("stale-hash");
        }
        finally
        {
            File.Delete(file);
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
    public async Task HashExistsAsync_ConcurrentCalls_NoDeadlock()
    {
        string dbPath = TempDb();
        try
        {
            await using Database db = new(dbPath);
            await db.InitializeAsync();
            await db.InsertAsync(MakeRecord("/source/photo.jpg", "hash-a"));

            Task<bool>[] tasks =
            [
                .. Enumerable
                    .Range(0, 10)
                    .Select(i => db.HashExistsAsync(i % 2 == 0 ? "hash-a" : "hash-missing")),
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
