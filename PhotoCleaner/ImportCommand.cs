namespace PhotoCleaner;

internal sealed class ImportCommand(
    CommandLine.Options options,
    CancellationToken cancellationToken
)
{
    private readonly SkippedExtensionTracker _skippedExtensions = new();

    internal async Task<int> ExecuteAsync() =>
        await CommandRunner
            .RunAsync(
                "Import",
                async () =>
                {
                    (IReadOnlyList<string> files, int totalCount) = FileEnumerator.Enumerate(
                        options.Path,
                        options.Threads,
                        cancellationToken
                    );

                    (
                        int imported,
                        int ignored,
                        int skipped,
                        int skipDbSkipped,
                        int trashSkipped,
                        int invalid,
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
                                                        ImportTask task = new(
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
                                                    cancellationToken: cancellationToken
                                                )
                                                .ConfigureAwait(false),
                                        readOnly: true,
                                        label: "skip database",
                                        cancellationToken: cancellationToken
                                    )
                                    .ConfigureAwait(false),
                            readOnly: true,
                            cancellationToken: cancellationToken
                        )
                        .ConfigureAwait(false);

                    Log.Information("Total {TotalCount} files", totalCount);
                    Log.Information("Imported {ImportedCount} files", imported);
                    Log.Information("Ignored {IgnoredCount} non-media files", ignored);
                    _skippedExtensions.LogWarnings();
                    Log.Information("Skipped {SkippedCount} files already in collection", skipped);
                    Log.Information(
                        "Skipped {SkipDbSkippedCount} files found in skip database",
                        skipDbSkipped
                    );
                    Log.Information(
                        "Skipped {TrashSkippedCount} files trashed in Immich",
                        trashSkipped
                    );
                    Log.Information("Deleted {DeletedCount} empty directories", deletedDirs);
                    Log.Information("Invalid {InvalidCount} files", invalid);
                    Log.Information("Failed {FailedCount} files", failed);

                    return failed > 0 || invalid > 0 ? ExitCode.Failed : ExitCode.Success;
                }
            )
            .ConfigureAwait(false);
}
