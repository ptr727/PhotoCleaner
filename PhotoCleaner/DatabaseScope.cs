namespace PhotoCleaner;

internal static class DatabaseScope
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Database is disposed in the finally block"
    )]
    internal static async Task<T> RunAsync<T>(
        FileInfo? dbFile,
        Func<Database?, Task<T>> work,
        CancellationToken cancellationToken = default
    )
    {
        if (dbFile is null)
        {
            return await work(null).ConfigureAwait(false);
        }

        _ = Directory.CreateDirectory(dbFile.DirectoryName!);
        Database database = new(dbFile.FullName);
        try
        {
            Log.Information("Using database '{DbFile}'", dbFile.FullName);
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
        Func<Database?, Task> work,
        CancellationToken cancellationToken = default
    ) =>
        await RunAsync<object?>(
                dbFile,
                async db =>
                {
                    await work(db).ConfigureAwait(false);
                    return null;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
}
