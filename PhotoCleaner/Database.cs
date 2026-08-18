using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace PhotoCleaner;

internal sealed record FileRecord(
    string Path,
    string Sha256,
    string Sha1,
    long FileSize,
    long MtimeTicks,
    bool IsProcessed
);

internal sealed class Database(string dbPath, bool readOnly = false) : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private SqliteConnection? _connection;

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Log.Debug("Initializing database at '{DbPath}' (readOnly={ReadOnly})", dbPath, readOnly);
        string mode = readOnly ? "Mode=ReadOnly;" : "";
        _connection = new SqliteConnection($"Data Source={dbPath};{mode}Pooling=False");
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (readOnly)
        {
            await ValidateSchemaAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using SqliteCommand createCmd = _connection.CreateCommand();
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS files (
                    path         TEXT NOT NULL PRIMARY KEY,
                    sha256       TEXT NOT NULL,
                    sha1         TEXT NOT NULL,
                    file_size    INTEGER NOT NULL,
                    mtime_ticks  INTEGER NOT NULL,
                    is_processed INTEGER NOT NULL DEFAULT 0
                )
                """;
            _ = await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            using SqliteCommand indexCmd = _connection.CreateCommand();
            indexCmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_files_sha256 ON files (sha256);
                CREATE INDEX IF NOT EXISTS idx_files_sha1 ON files (sha1)
                """;
            _ = await indexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ValidateSchemaAsync(CancellationToken cancellationToken)
    {
        using SqliteCommand cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='files'";
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not string schema || string.IsNullOrEmpty(schema))
        {
            throw new InvalidOperationException(
                $"Database '{dbPath}' does not contain the expected 'files' table."
            );
        }

        if (
            !schema.Contains("sha256", StringComparison.OrdinalIgnoreCase)
            || !schema.Contains("sha1", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                $"Database '{dbPath}' has an unsupported schema (missing 'sha256' or 'sha1' column)."
            );
        }
    }

    internal Task<FileRecord?> GetByPathAsync(
        string path,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText =
                    "SELECT path, sha256, sha1, file_size, mtime_ticks, is_processed FROM files WHERE path = @path LIMIT 1";
                _ = cmd.Parameters.AddWithValue("@path", path);
                SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    return !await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                        ? null
                        : new FileRecord(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetInt64(3),
                            reader.GetInt64(4),
                            reader.GetInt64(5) != 0
                        );
                }
                finally
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
            },
            cancellationToken
        );

    internal Task<bool> Sha256ExistsAsync(
        string sha256,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = "SELECT COUNT(*) FROM files WHERE sha256 = @sha256";
                _ = cmd.Parameters.AddWithValue("@sha256", sha256);
                object? result = await cmd.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                return result is long count && count > 0;
            },
            cancellationToken
        );

    internal Task<bool> Sha1ExistsAsync(
        string sha1,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = "SELECT COUNT(*) FROM files WHERE sha1 = @sha1";
                _ = cmd.Parameters.AddWithValue("@sha1", sha1);
                object? result = await cmd.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                return result is long count && count > 0;
            },
            cancellationToken
        );

    internal Task InsertAsync(FileRecord record, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = """
                INSERT OR IGNORE INTO files (path, sha256, sha1, file_size, mtime_ticks, is_processed)
                VALUES (@path, @sha256, @sha1, @fileSize, @mtimeTicks, @isProcessed)
                """;
                _ = cmd.Parameters.AddWithValue("@path", record.Path);
                _ = cmd.Parameters.AddWithValue("@sha256", record.Sha256);
                _ = cmd.Parameters.AddWithValue("@sha1", record.Sha1);
                _ = cmd.Parameters.AddWithValue("@fileSize", record.FileSize);
                _ = cmd.Parameters.AddWithValue("@mtimeTicks", record.MtimeTicks);
                _ = cmd.Parameters.AddWithValue("@isProcessed", record.IsProcessed ? 1 : 0);
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );

    internal Task UpdateHashesAsync(
        string path,
        string sha256,
        string sha1,
        long fileSize,
        long mtimeTicks,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = """
                UPDATE files
                SET sha256 = @sha256, sha1 = @sha1, file_size = @fileSize, mtime_ticks = @mtimeTicks, is_processed = 0
                WHERE path = @path
                """;
                _ = cmd.Parameters.AddWithValue("@path", path);
                _ = cmd.Parameters.AddWithValue("@sha256", sha256);
                _ = cmd.Parameters.AddWithValue("@sha1", sha1);
                _ = cmd.Parameters.AddWithValue("@fileSize", fileSize);
                _ = cmd.Parameters.AddWithValue("@mtimeTicks", mtimeTicks);
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );

    internal Task UpdateMetadataAsync(
        string path,
        string sha1,
        long fileSize,
        long mtimeTicks,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText =
                    "UPDATE files SET sha1 = @sha1, file_size = @size, mtime_ticks = @mtime WHERE path = @path";
                _ = cmd.Parameters.AddWithValue("@path", path);
                _ = cmd.Parameters.AddWithValue("@sha1", sha1);
                _ = cmd.Parameters.AddWithValue("@size", fileSize);
                _ = cmd.Parameters.AddWithValue("@mtime", mtimeTicks);
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );

    internal Task SetProcessedAsync(string path, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = "UPDATE files SET is_processed = 1 WHERE path = @path";
                _ = cmd.Parameters.AddWithValue("@path", path);
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );

    internal Task DeleteByPathAsync(string path, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = "DELETE FROM files WHERE path = @path";
                _ = cmd.Parameters.AddWithValue("@path", path);
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );

    private async Task<T> ExecuteAsync<T>(
        Func<SqliteCommand, Task<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using SqliteCommand cmd = _connection!.CreateCommand();
            return await operation(cmd).ConfigureAwait(false);
        }
        finally
        {
            _ = _semaphore.Release();
        }
    }

    private async Task ExecuteAsync(
        Func<SqliteCommand, Task> operation,
        CancellationToken cancellationToken = default
    ) =>
        await ExecuteAsync<object?>(
                async cmd =>
                {
                    await operation(cmd).ConfigureAwait(false);
                    return null;
                },
                cancellationToken
            )
            .ConfigureAwait(false);

    internal static async Task<(string sha256, string sha1)> ResolveHashesAsync(
        string filePath,
        FileRecord? cached,
        bool rehash,
        CancellationToken cancellationToken = default
    )
    {
        if (!rehash && cached is not null)
        {
            FileInfo info = new(filePath);
            if (cached.FileSize == info.Length && cached.MtimeTicks == info.LastWriteTimeUtc.Ticks)
            {
                return (cached.Sha256, cached.Sha1);
            }
        }

        return await ComputeHashesAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "SHA-1 required for Immich compatibility - Immich uses SHA-1 checksums"
    )]
    internal static async Task<(string sha256, string sha1)> ComputeHashesAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        using IncrementalHash sha256Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using IncrementalHash sha1Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        byte[] buffer = new byte[81920];
        using FileStream fs = File.OpenRead(filePath);
        int bytesRead;
        while (
            (
                bytesRead = await fs.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false)
            ) > 0
        )
        {
            sha256Hasher.AppendData(buffer, 0, bytesRead);
            sha1Hasher.AppendData(buffer, 0, bytesRead);
        }

        return (
            Convert.ToHexStringLower(sha256Hasher.GetHashAndReset()),
            Convert.ToHexStringLower(sha1Hasher.GetHashAndReset())
        );
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
