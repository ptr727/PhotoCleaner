using Microsoft.Data.Sqlite;

namespace PhotoCleaner;

internal sealed class TrashDatabase(string dbPath, bool readOnly = false)
    : IDisposable,
        IAsyncDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private SqliteConnection? _connection;

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
        }
        else
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS trash_hashes (
                    sha1                TEXT NOT NULL PRIMARY KEY,
                    original_file_name  TEXT
                )
                """;
            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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

    internal Task InsertHashAsync(
        string sha1Hex,
        string? originalFileName = null,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(
            async cmd =>
            {
                cmd.CommandText =
                    "INSERT OR IGNORE INTO trash_hashes (sha1, original_file_name) VALUES (@sha1, @name)";
                _ = cmd.Parameters.AddWithValue("@sha1", sha1Hex);
                _ = cmd.Parameters.AddWithValue(
                    "@name",
                    originalFileName is not null ? originalFileName : DBNull.Value
                );
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken
        );

    internal Task InsertHashesAsync(
        IReadOnlyList<(string Sha1Hex, string? OriginalFileName)> hashes,
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
                    cmd.CommandText =
                        "INSERT OR IGNORE INTO trash_hashes (sha1, original_file_name) VALUES (@sha1, @name)";
                    SqliteParameter sha1Param = cmd.Parameters.Add("@sha1", SqliteType.Text);
                    SqliteParameter nameParam = cmd.Parameters.Add("@name", SqliteType.Text);

                    foreach ((string sha1Hex, string? originalFileName) in hashes)
                    {
                        sha1Param.Value = sha1Hex;
                        nameParam.Value = originalFileName is not null
                            ? originalFileName
                            : DBNull.Value;
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
