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
                    ) = await TrashDatabaseScope
                        .RunAsync(
                            options.TrashDbFile,
                            async trashDatabase =>
                                await DatabaseScope
                                    .RunAsync(
                                        options.SkipDbFile,
                                        async skipDatabase =>
                                            await DatabaseScope
                                                .RunAsync(
                                                    options.DbFile,
                                                    async database =>
                                                    {
                                                        OrganizeTask task = new(
                                                            options,
                                                            database,
                                                            skipDatabase,
                                                            trashDatabase,
                                                            _skippedExtensions
                                                        );
                                                        return await task.ExecuteAsync(
                                                                files,
                                                                options.Path,
                                                                cancellationToken
                                                            )
                                                            .ConfigureAwait(false);
                                                    },
                                                    cancellationToken
                                                )
                                                .ConfigureAwait(false),
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false),
                            cancellationToken
                        )
                        .ConfigureAwait(false);

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
}
