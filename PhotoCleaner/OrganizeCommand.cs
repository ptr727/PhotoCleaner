namespace PhotoCleaner;

internal sealed class OrganizeCommand(
    CommandLine.Options options,
    CancellationToken cancellationToken
)
{
    private readonly SkippedExtensionTracker _skippedExtensions = new();

    internal async Task<int> ExecuteAsync() =>
        await CommandRunner
            .RunAsync(
                "Organize",
                async () =>
                {
                    (IReadOnlyList<string> files, int totalCount) = FileEnumerator.Enumerate(
                        options.Path,
                        options.Threads,
                        cancellationToken
                    );

                    (
                        int organized,
                        int ignored,
                        int skipped,
                        int skipDbSkipped,
                        int trashSkipped,
                        int failed,
                        int deletedDirs
                    ) = await RunWithDatabasesAsync(files).ConfigureAwait(false);

                    Log.Information("Total {TotalCount} files", totalCount);

                    if (organized > 0)
                    {
                        Log.Information("Organized {OrganizedCount} files", organized);
                    }

                    if (ignored > 0)
                    {
                        Log.Information("Ignored {IgnoredCount} non-media files", ignored);
                    }

                    _skippedExtensions.LogWarnings();

                    if (skipped > 0)
                    {
                        Log.Information(
                            "Skipped {SkippedCount} files already in collection",
                            skipped
                        );
                    }

                    if (skipDbSkipped > 0)
                    {
                        Log.Information(
                            "Skipped {SkipDbSkippedCount} files found in skip database",
                            skipDbSkipped
                        );
                    }

                    if (trashSkipped > 0)
                    {
                        Log.Information(
                            "Skipped {TrashSkippedCount} files trashed in Immich",
                            trashSkipped
                        );
                    }

                    if (deletedDirs > 0)
                    {
                        Log.Information("Deleted {DeletedCount} empty directories", deletedDirs);
                    }

                    if (failed > 0)
                    {
                        Log.Warning("Failed {FailedCount} files", failed);
                    }
                }
            )
            .ConfigureAwait(false);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "All databases are disposed in the finally block"
    )]
    private async Task<(
        int organized,
        int ignored,
        int skipped,
        int skipDbSkipped,
        int trashSkipped,
        int failed,
        int deletedDirs
    )> RunWithDatabasesAsync(IReadOnlyList<string> files)
    {
        TrashDatabase? trashDatabase = null;
        Database? skipDatabase = null;
        Database? database = null;
        try
        {
            trashDatabase = await OpenTrashDatabaseAsync(
                    options.TrashDbFile,
                    readOnly: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            skipDatabase = await OpenDatabaseAsync(
                    options.SkipDbFile,
                    readOnly: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            database = await OpenDatabaseAsync(options.DbFile, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            OrganizeTask task = new(
                options,
                database,
                skipDatabase,
                trashDatabase,
                _skippedExtensions
            );
            return await task.ExecuteAsync(files, options.Path, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (database is not null)
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }

            if (skipDatabase is not null)
            {
                await skipDatabase.DisposeAsync().ConfigureAwait(false);
            }

            if (trashDatabase is not null)
            {
                await trashDatabase.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<Database?> OpenDatabaseAsync(
        FileInfo? dbFile,
        bool readOnly = false,
        CancellationToken cancellationToken = default
    )
    {
        if (dbFile is null)
        {
            return null;
        }

        if (!readOnly)
        {
            _ = Directory.CreateDirectory(dbFile.DirectoryName!);
        }

        Database database = new(dbFile.FullName, readOnly);
        try
        {
            Log.Information("Using database '{DbFile}'", dbFile.FullName);
            await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return database;
        }
        catch
        {
            await database.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<TrashDatabase?> OpenTrashDatabaseAsync(
        FileInfo? dbFile,
        bool readOnly = false,
        CancellationToken cancellationToken = default
    )
    {
        if (dbFile is null)
        {
            return null;
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
            return database;
        }
        catch
        {
            await database.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
