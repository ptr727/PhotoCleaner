namespace PhotoCleaner;

internal static class TrashDatabaseScope
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "TrashDatabase is disposed in the finally block"
    )]
    internal static async Task<T> RunAsync<T>(
        FileInfo? dbFile,
        Func<TrashDatabase?, Task<T>> work,
        bool readOnly = false,
        CancellationToken cancellationToken = default
    )
    {
        if (dbFile is null)
        {
            return await work(null).ConfigureAwait(false);
        }

        if (!readOnly)
        {
            _ = Directory.CreateDirectory(dbFile.DirectoryName!);
        }

        TrashDatabase database = new(dbFile.FullName, readOnly);
        try
        {
            Log.Information("Using trash database '{DbFile}'", dbFile.FullName);
            await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return await work(database).ConfigureAwait(false);
        }
        finally
        {
            await database.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static async Task RunAsync(
        FileInfo? dbFile,
        Func<TrashDatabase?, Task> work,
        bool readOnly = false,
        CancellationToken cancellationToken = default
    ) =>
        await RunAsync<object?>(
                dbFile,
                async db =>
                {
                    await work(db).ConfigureAwait(false);
                    return null;
                },
                readOnly,
                cancellationToken
            )
            .ConfigureAwait(false);
}
