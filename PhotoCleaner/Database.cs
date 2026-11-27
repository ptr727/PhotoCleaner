using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace PhotoCleaner;

internal sealed record FileRecord(
    string Path,
    string Sha256,
    string Sha1,
    long FileSize,
    long MtimeTicks,
    bool IsProcessed,
    string? ContentIdentifier = null,
    string? ImageHash = null,
    bool MetadataRead = false
);

/// <summary>
/// Catalog metadata read from one file, where a null member means the tag is absent.
/// </summary>
internal sealed record FileMetadata(string? ContentIdentifier, string? ImageHash)
{
    /// <summary>
    /// Maps a catalog read to a row's metadata, or null when exiftool reported an error so the row stays unread.
    /// </summary>
    internal static FileMetadata? FromExifTool(ExifToolJson? json) =>
        json is null || !string.IsNullOrEmpty(json.ExifToolError)
            ? null
            : new FileMetadata(
                string.IsNullOrEmpty(json.ContentIdentifier) ? null : json.ContentIdentifier,
                string.IsNullOrEmpty(json.ImageDataHash) ? null : json.ImageDataHash
            );
}

internal sealed class Database(string dbPath, bool readOnly = false, bool inMemory = false)
    : IDisposable,
        IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private SqliteConnection? _connection;

    /// <summary>
    /// Whether the files table has the image_hash column, which a read-only open of an older database lacks.
    /// </summary>
    internal bool HasImageHash { get; private set; }

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Log.Debug(
            "Initializing database at '{DbPath}' (readOnly={ReadOnly}, inMemory={InMemory})",
            dbPath,
            readOnly,
            inMemory
        );
        if (inMemory)
        {
            _connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (File.Exists(dbPath))
            {
                SqliteConnection source = new($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
                await using (source.ConfigureAwait(false))
                {
                    await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                    source.BackupDatabase(_connection);
                }
            }
        }
        else
        {
            string mode = readOnly ? "Mode=ReadOnly;" : "";
            _connection = new SqliteConnection($"Data Source={dbPath};{mode}Pooling=False");
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        if (readOnly)
        {
            await ValidateSchemaAsync(cancellationToken).ConfigureAwait(false);
            HasImageHash = (
                await GetColumnNamesAsync(_connection, "files", cancellationToken)
                    .ConfigureAwait(false)
            ).Contains("image_hash");
        }
        else
        {
            if (!inMemory)
            {
                await CheckWritableAsync(cancellationToken).ConfigureAwait(false);
            }

            HasImageHash = true;
            using SqliteCommand createCmd = _connection.CreateCommand();
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS files (
                    path         TEXT NOT NULL PRIMARY KEY,
                    sha256       TEXT NOT NULL,
                    sha1         TEXT NOT NULL,
                    file_size    INTEGER NOT NULL,
                    mtime_ticks  INTEGER NOT NULL,
                    is_processed INTEGER NOT NULL DEFAULT 0,
                    content_identifier TEXT,
                    image_hash   TEXT,
                    metadata_read INTEGER NOT NULL DEFAULT 0
                )
                """;
            _ = await createCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await UpgradeSchemaAsync(cancellationToken).ConfigureAwait(false);

            using SqliteCommand indexCmd = _connection.CreateCommand();
            indexCmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_files_sha256 ON files (sha256);
                CREATE INDEX IF NOT EXISTS idx_files_sha1 ON files (sha1);
                CREATE INDEX IF NOT EXISTS idx_files_content_identifier ON files (content_identifier);
                CREATE INDEX IF NOT EXISTS idx_files_image_hash ON files (image_hash)
                """;
            _ = await indexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // A file the user cannot write still opens, so the run proves a write before any file moves.
    private async Task CheckWritableAsync(CancellationToken cancellationToken)
    {
        try
        {
            DbTransaction dbTransaction = await _connection!
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (dbTransaction.ConfigureAwait(false))
            {
                // Creating a table dirties a page, so the rollback journal must be writable too.
                using SqliteCommand cmd = _connection.CreateCommand();
                cmd.Transaction = (SqliteTransaction)dbTransaction;
                cmd.CommandText = "CREATE TABLE write_check (id INTEGER)";
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await dbTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"Database '{dbPath}' cannot be written: {ex.Message}",
                ex
            );
        }
    }

    internal static async Task<HashSet<string>> GetColumnNamesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken
    )
    {
        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info(@table)";
        _ = cmd.Parameters.AddWithValue("@table", table);
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static readonly string[] s_catalogColumns =
    [
        "content_identifier",
        "image_hash",
        "metadata_read",
    ];

    private async Task UpgradeSchemaAsync(CancellationToken cancellationToken)
    {
        HashSet<string> columns = await GetColumnNamesAsync(
                _connection!,
                "files",
                cancellationToken
            )
            .ConfigureAwait(false);
        int present = s_catalogColumns.Count(columns.Contains);
        if (present == 3)
        {
            return;
        }

        // A database created before the catalog columns existed gains them in place.
        // Its rows keep metadata_read at 0, so the next catalog backfills the ones still on disk.
        Log.Information("Upgrading database schema, adding the catalog columns");
        DbTransaction dbTransaction = await _connection!
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        using SqliteCommand alterCmd = _connection.CreateCommand();
        alterCmd.Transaction = (SqliteTransaction)dbTransaction;
        alterCmd.CommandText = """
            ALTER TABLE files ADD COLUMN content_identifier TEXT;
            ALTER TABLE files ADD COLUMN image_hash TEXT;
            ALTER TABLE files ADD COLUMN metadata_read INTEGER NOT NULL DEFAULT 0
            """;
        _ = await alterCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await dbTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await dbTransaction.DisposeAsync().ConfigureAwait(false);
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
                    "SELECT path, sha256, sha1, file_size, mtime_ticks, is_processed, content_identifier, image_hash, metadata_read FROM files WHERE path = @path LIMIT 1";
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
                            reader.GetInt64(5) != 0,
                            await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                                ? null
                                : reader.GetString(6),
                            await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                                ? null
                                : reader.GetString(7),
                            reader.GetInt64(8) != 0
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

    /// <summary>
    /// Finds a row whose image data hash equals the given one, or null when there is none.
    /// </summary>
    internal Task<string?> FindPathByImageHashAsync(
        string imageHash,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText =
                    "SELECT path FROM files WHERE image_hash = @imageHash ORDER BY path LIMIT 1";
                _ = cmd.Parameters.AddWithValue("@imageHash", imageHash);
                object? result = await cmd.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                return result as string;
            },
            cancellationToken
        );

    /// <summary>
    /// Returns the image data hash recorded for a file with the given SHA-1, or null when none is recorded.
    /// </summary>
    internal Task<string?> GetImageHashBySha1Async(
        string sha1,
        CancellationToken cancellationToken = default
    ) =>
        !HasImageHash
            ? Task.FromResult<string?>(null)
            : ExecuteAsync(
                async cmd =>
                {
                    cmd.CommandText =
                        "SELECT image_hash FROM files WHERE sha1 = @sha1 AND image_hash IS NOT NULL ORDER BY path LIMIT 1";
                    _ = cmd.Parameters.AddWithValue("@sha1", sha1);
                    object? result = await cmd.ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return result as string;
                },
                cancellationToken
            );

    internal Task InsertAsync(FileRecord record, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = """
                    INSERT OR IGNORE INTO files (path, sha256, sha1, file_size, mtime_ticks, is_processed, content_identifier, image_hash, metadata_read)
                    VALUES (@path, @sha256, @sha1, @fileSize, @mtimeTicks, @isProcessed, @contentIdentifier, @imageHash, @metadataRead)
                    """;
                _ = cmd.Parameters.AddWithValue(
                    "@contentIdentifier",
                    (object?)record.ContentIdentifier ?? DBNull.Value
                );
                _ = cmd.Parameters.AddWithValue(
                    "@imageHash",
                    (object?)record.ImageHash ?? DBNull.Value
                );
                _ = cmd.Parameters.AddWithValue("@metadataRead", record.MetadataRead ? 1 : 0);
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
        FileMetadata? metadata = null,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                // New content invalidates the old metadata, so a null argument leaves the row unread.
                cmd.CommandText = """
                    UPDATE files
                    SET sha256 = @sha256, sha1 = @sha1, file_size = @fileSize, mtime_ticks = @mtimeTicks, is_processed = 0,
                        content_identifier = @contentIdentifier, image_hash = @imageHash, metadata_read = @metadataRead
                    WHERE path = @path
                    """;
                _ = cmd.Parameters.AddWithValue(
                    "@contentIdentifier",
                    (object?)metadata?.ContentIdentifier ?? DBNull.Value
                );
                _ = cmd.Parameters.AddWithValue(
                    "@imageHash",
                    (object?)metadata?.ImageHash ?? DBNull.Value
                );
                _ = cmd.Parameters.AddWithValue("@metadataRead", metadata is null ? 0 : 1);
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

    internal Task SetMetadataAsync(
        string path,
        FileMetadata metadata,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = """
                    UPDATE files
                    SET content_identifier = @contentIdentifier, image_hash = @imageHash, metadata_read = 1
                    WHERE path = @path
                    """;
                _ = cmd.Parameters.AddWithValue("@path", path);
                _ = cmd.Parameters.AddWithValue(
                    "@contentIdentifier",
                    (object?)metadata.ContentIdentifier ?? DBNull.Value
                );
                _ = cmd.Parameters.AddWithValue(
                    "@imageHash",
                    (object?)metadata.ImageHash ?? DBNull.Value
                );
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );

    /// <summary>
    /// Finds a cataloged image row carrying the given ContentIdentifier, whether or not its file still exists.
    /// </summary>
    internal Task<string?> FindImagePathByContentIdentifierAsync(
        string contentIdentifier,
        IReadOnlyCollection<string> imageExtensions,
        string? excludeSha256,
        string? excludeImageHash,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                // Rows sharing one ContentIdentifier are few, so the extension filter runs here.
                cmd.CommandText =
                    "SELECT path, sha256, image_hash FROM files WHERE content_identifier = @contentIdentifier ORDER BY path";
                _ = cmd.Parameters.AddWithValue("@contentIdentifier", contentIdentifier);
                SqliteDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        string path = reader.GetString(0);

                        // A row with the video's own bytes or media data is the video, not a still.
                        if (
                            reader.GetString(1) == excludeSha256
                            || (
                                excludeImageHash is not null
                                && !await reader
                                    .IsDBNullAsync(2, cancellationToken)
                                    .ConfigureAwait(false)
                                && reader.GetString(2) == excludeImageHash
                            )
                        )
                        {
                            continue;
                        }

                        if (
                            imageExtensions.Contains(
                                System.IO.Path.GetExtension(path),
                                StringComparer.OrdinalIgnoreCase
                            )
                        )
                        {
                            return path;
                        }
                    }

                    return null;
                }
                finally
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
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
