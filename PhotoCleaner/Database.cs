using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace PhotoCleaner;

internal sealed record MediaFileRecord(
    string SourceHash,
    string SourcePath,
    string FileName,
    string? ContentIdentifier,
    string? ExifDate,
    long FileSize,
    string? MimeType,
    string OrganizedAt,
    string OrganizedTo
);

internal sealed class Database(string dbPath) : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private SqliteConnection? _connection;

    internal async Task InitializeAsync()
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        await _connection.OpenAsync().ConfigureAwait(false);

        SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS media_files (
                source_hash        TEXT NOT NULL PRIMARY KEY,
                source_path        TEXT NOT NULL,
                file_name          TEXT NOT NULL,
                content_identifier TEXT,
                exif_date          TEXT,
                file_size          INTEGER NOT NULL,
                mime_type          TEXT,
                organized_at       TEXT NOT NULL,
                organized_to       TEXT NOT NULL
            )
            """;
        _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    internal async Task<string?> GetSourcePathAsync(string hash)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            SqliteCommand cmd = _connection!.CreateCommand();
            cmd.CommandText =
                "SELECT source_path FROM media_files WHERE source_hash = @hash LIMIT 1";
            _ = cmd.Parameters.AddWithValue("@hash", hash);
            object? result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result as string;
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    internal async Task<bool> ExistsAsync(string hash)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            SqliteCommand cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM media_files WHERE source_hash = @hash";
            _ = cmd.Parameters.AddWithValue("@hash", hash);
            object? result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return result is long count && count > 0;
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    internal async Task InsertAsync(MediaFileRecord record)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            SqliteCommand cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO media_files
                    (source_hash, source_path, file_name, content_identifier,
                     exif_date, file_size, mime_type, organized_at, organized_to)
                VALUES
                    (@hash, @sourcePath, @fileName, @contentIdentifier,
                     @exifDate, @fileSize, @mimeType, @organizedAt, @organizedTo)
                """;
            _ = cmd.Parameters.AddWithValue("@hash", record.SourceHash);
            _ = cmd.Parameters.AddWithValue("@sourcePath", record.SourcePath);
            _ = cmd.Parameters.AddWithValue("@fileName", record.FileName);
            _ = cmd.Parameters.AddWithValue(
                "@contentIdentifier",
                (object?)record.ContentIdentifier ?? DBNull.Value
            );
            _ = cmd.Parameters.AddWithValue("@exifDate", (object?)record.ExifDate ?? DBNull.Value);
            _ = cmd.Parameters.AddWithValue("@fileSize", record.FileSize);
            _ = cmd.Parameters.AddWithValue("@mimeType", (object?)record.MimeType ?? DBNull.Value);
            _ = cmd.Parameters.AddWithValue("@organizedAt", record.OrganizedAt);
            _ = cmd.Parameters.AddWithValue("@organizedTo", record.OrganizedTo);
            _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _ = _semaphore.Release();
        }
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
