using Microsoft.Data.Sqlite;

namespace PhotoCleaner;

internal sealed class TrashDatabase(string dbPath, bool readOnly = false)
    : IDisposable,
        IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private SqliteConnection? _connection;

    /// <summary>
    /// Whether the trash_hashes table has the image_hash column, which a read-only open of an older database lacks.
    /// </summary>
    internal bool HasImageHash { get; private set; }

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Log.Debug(
            "Initializing trash database at '{DbPath}' (readOnly={ReadOnly})",
            dbPath,
            readOnly
        );
        string mode = readOnly ? "Mode=ReadOnly;" : "";
        _connection = new SqliteConnection($"Data Source={dbPath};{mode}Pooling=False");
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (readOnly)
        {
            await ValidateSchemaAsync(cancellationToken).ConfigureAwait(false);
            HasImageHash = await HasImageHashColumnAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS trash_hashes (
                    sha1                TEXT NOT NULL PRIMARY KEY,
                    original_file_name  TEXT,
                    image_hash          TEXT
                )
                """;
            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (!await HasImageHashColumnAsync(cancellationToken).ConfigureAwait(false))
            {
                // A database created before the image_hash column existed gains it in place.
                Log.Information("Upgrading trash database schema, adding the image_hash column");
                using SqliteCommand alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE trash_hashes ADD COLUMN image_hash TEXT";
                _ = await alterCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using SqliteCommand indexCmd = _connection.CreateCommand();
            indexCmd.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_trash_hashes_image_hash ON trash_hashes (image_hash)";
            _ = await indexCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            HasImageHash = true;
        }
    }

    internal void WarnIfImageHashMissing()
    {
        if (!HasImageHash)
        {
            Log.Warning(
                "Trash database '{DbPath}' has no image_hash column, so it matches by SHA-1 only until 'trash' upgrades it",
                dbPath
            );
        }
    }

    private async Task<bool> HasImageHashColumnAsync(CancellationToken cancellationToken)
    {
        using SqliteCommand cmd = _connection!.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('trash_hashes') WHERE name = 'image_hash'";
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count && count > 0;
    }

    private async Task ValidateSchemaAsync(CancellationToken cancellationToken)
    {
        using SqliteCommand cmd = _connection!.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='trash_hashes'";
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not long count || count == 0)
        {
            throw new InvalidOperationException(
                $"Database '{dbPath}' does not contain the expected 'trash_hashes' table. "
                    + "Run the 'trash' command first to create it."
            );
        }
    }

    // An existing row keeps its name and gains an image hash it lacked, so a later sync can backfill it.
    private const string InsertSql = """
        INSERT INTO trash_hashes (sha1, original_file_name, image_hash) VALUES (@sha1, @name, @imageHash)
        ON CONFLICT (sha1) DO UPDATE SET image_hash = COALESCE(trash_hashes.image_hash, excluded.image_hash)
        """;

    private static object DbValue(string? value) =>
        string.IsNullOrEmpty(value) ? DBNull.Value : value;

    internal Task InsertHashAsync(
        string sha1Hex,
        string? originalFileName = null,
        string? imageHash = null,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = InsertSql;
                _ = cmd.Parameters.AddWithValue("@sha1", sha1Hex);
                _ = cmd.Parameters.AddWithValue(
                    "@name",
                    originalFileName is not null ? originalFileName : DBNull.Value
                );
                _ = cmd.Parameters.AddWithValue("@imageHash", DbValue(imageHash));
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );

    internal Task InsertHashesAsync(
        IReadOnlyList<(string Sha1Hex, string? OriginalFileName, string? ImageHash)> hashes,
        CancellationToken cancellationToken = default
    ) =>
        hashes.Count == 0
            ? Task.CompletedTask
            : ExecuteAsync(
                async cmd =>
                {
                    using SqliteTransaction transaction = (SqliteTransaction)
                        await _connection!
                            .BeginTransactionAsync(cancellationToken)
                            .ConfigureAwait(false);
                    cmd.Transaction = transaction;
                    cmd.CommandText = InsertSql;
                    SqliteParameter sha1Param = cmd.Parameters.Add("@sha1", SqliteType.Text);
                    SqliteParameter nameParam = cmd.Parameters.Add("@name", SqliteType.Text);
                    SqliteParameter imageHashParam = cmd.Parameters.Add(
                        "@imageHash",
                        SqliteType.Text
                    );

                    foreach (
                        (string sha1Hex, string? originalFileName, string? imageHash) in hashes
                    )
                    {
                        sha1Param.Value = sha1Hex;
                        nameParam.Value = originalFileName is not null
                            ? originalFileName
                            : DBNull.Value;
                        imageHashParam.Value = DbValue(imageHash);
                        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken
            );

    internal Task<bool> Sha1ExistsAsync(
        string sha1Hex,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = "SELECT COUNT(*) FROM trash_hashes WHERE sha1 = @sha1";
                _ = cmd.Parameters.AddWithValue("@sha1", sha1Hex);
                object? result = await cmd.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                return result is long count && count > 0;
            },
            cancellationToken
        );

    /// <summary>
    /// Whether a trashed asset carries the given image data hash, which is never true for a null or empty hash.
    /// </summary>
    internal Task<bool> ImageHashExistsAsync(
        string? imageHash,
        CancellationToken cancellationToken = default
    ) =>
        string.IsNullOrEmpty(imageHash) || !HasImageHash
            ? Task.FromResult(false)
            : ExecuteAsync(
                async cmd =>
                {
                    cmd.CommandText =
                        "SELECT COUNT(*) FROM trash_hashes WHERE image_hash = @imageHash";
                    _ = cmd.Parameters.AddWithValue("@imageHash", imageHash);
                    object? result = await cmd.ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return result is long count && count > 0;
                },
                cancellationToken
            );

    internal Task<long> GetCountAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = "SELECT COUNT(*) FROM trash_hashes";
                object? result = await cmd.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                return result is long count ? count : 0L;
            },
            cancellationToken
        );

    internal Task ClearAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText = "DELETE FROM trash_hashes";
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
