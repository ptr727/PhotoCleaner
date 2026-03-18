using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace PhotoCleaner;

internal sealed record FileRecord(
    string Path,
    string Hash,
    long FileSize,
    long MtimeTicks,
    bool IsProcessed
);

internal sealed class Database(string dbPath) : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private SqliteConnection? _connection;

    internal async Task InitializeAsync()
    {
        Log.Debug("Initializing database at '{DbPath}'", dbPath);
        _connection = new SqliteConnection($"Data Source={dbPath}");
        await _connection.OpenAsync().ConfigureAwait(false);

        SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS files (
                path         TEXT NOT NULL PRIMARY KEY,
                hash         TEXT NOT NULL,
                file_size    INTEGER NOT NULL,
                mtime_ticks  INTEGER NOT NULL,
                is_processed INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_files_hash ON files (hash)
            """;
        _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    internal async Task<FileRecord?> GetByPathAsync(string path)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            SqliteCommand cmd = _connection!.CreateCommand();
            cmd.CommandText =
                "SELECT path, hash, file_size, mtime_ticks, is_processed FROM files WHERE path = @path LIMIT 1";
            _ = cmd.Parameters.AddWithValue("@path", path);
            SqliteDataReader reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            try
            {
                return !await reader.ReadAsync().ConfigureAwait(false)
                    ? null
                    : new FileRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4) != 0
                    );
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    internal async Task<bool> HashExistsAsync(string hash)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            SqliteCommand cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files WHERE hash = @hash";
            _ = cmd.Parameters.AddWithValue("@hash", hash);
            object? result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result is long count && count > 0;
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    internal async Task InsertAsync(FileRecord record)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            SqliteCommand cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO files (path, hash, file_size, mtime_ticks, is_processed)
                VALUES (@path, @hash, @fileSize, @mtimeTicks, @isProcessed)
                """;
            _ = cmd.Parameters.AddWithValue("@path", record.Path);
            _ = cmd.Parameters.AddWithValue("@hash", record.Hash);
            _ = cmd.Parameters.AddWithValue("@fileSize", record.FileSize);
            _ = cmd.Parameters.AddWithValue("@mtimeTicks", record.MtimeTicks);
            _ = cmd.Parameters.AddWithValue("@isProcessed", record.IsProcessed ? 1 : 0);
            _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    internal async Task UpdateHashAsync(string path, string hash, long fileSize, long mtimeTicks)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            SqliteCommand cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                UPDATE files
                SET hash = @hash, file_size = @fileSize, mtime_ticks = @mtimeTicks, is_processed = 0
                WHERE path = @path
                """;
            _ = cmd.Parameters.AddWithValue("@path", path);
            _ = cmd.Parameters.AddWithValue("@hash", hash);
            _ = cmd.Parameters.AddWithValue("@fileSize", fileSize);
            _ = cmd.Parameters.AddWithValue("@mtimeTicks", mtimeTicks);
            _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    internal async Task SetProcessedAsync(string path)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            SqliteCommand cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE files SET is_processed = 1 WHERE path = @path";
            _ = cmd.Parameters.AddWithValue("@path", path);
            _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    internal static async Task<string> ResolveHashAsync(
        string filePath,
        FileRecord? cached,
        bool rehash
    )
    {
        if (!rehash && cached is not null)
        {
            FileInfo info = new(filePath);
            if (cached.FileSize == info.Length && cached.MtimeTicks == info.LastWriteTimeUtc.Ticks)
            {
                return cached.Hash;
            }
        }

        return await ComputeHashAsync(filePath).ConfigureAwait(false);
    }

    internal static async Task<string> ComputeHashAsync(string filePath)
    {
        using FileStream fs = File.OpenRead(filePath);
        byte[] hashBytes = await SHA256.HashDataAsync(fs).ConfigureAwait(false);
        return Convert.ToHexStringLower(hashBytes);
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _semaphore.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _semaphore.Dispose();
    }
}
