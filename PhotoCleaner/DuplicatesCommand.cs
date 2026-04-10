namespace PhotoCleaner;

internal sealed class DuplicatesCommand(
    CommandLine.Options options,
    CancellationToken cancellationToken
)
{
    private readonly SkippedExtensionTracker _skippedExtensions = new();

    internal async Task<int> ExecuteAsync() =>
        await CommandRunner
            .RunAsync(
                "Duplicates",
                async () =>
                {
                    (IReadOnlyList<string> sourceFiles, int sourceCount) = FileEnumerator.Enumerate(
                        options.Path,
                        options.Threads,
                        cancellationToken
                    );
                    (IReadOnlyList<string> outFiles, int outCount) = FileEnumerator.Enumerate(
                        options.OutPath!,
                        options.Threads,
                        cancellationToken
                    );
                    int totalCount = sourceCount + outCount;

                    (int indexed, int ignored, int deleted, int kept, int failed) =
                        await TrashDatabaseScope
                            .RunAsync(
                                options.TrashDbFile,
                                async trashDatabase =>
                                    await DatabaseScope
                                        .RunAsync(
                                            options.DbFile,
                                            async database =>
                                                await DatabaseScope
                                                    .RunAsync(
                                                        options.OutDbFile,
                                                        async outDatabase =>
                                                        {
                                                            DuplicatesTask task = new(
                                                                options,
                                                                database!,
                                                                outDatabase!,
                                                                trashDatabase,
                                                                _skippedExtensions
                                                            );
                                                            return await task.ExecuteAsync(
                                                                    sourceFiles,
                                                                    outFiles,
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

                    if (indexed > 0)
                    {
                        Log.Information("Indexed {IndexedCount} source files", indexed);
                    }

                    if (ignored > 0)
                    {
                        Log.Information("Ignored {IgnoredCount} non-media source files", ignored);
                    }

                    _skippedExtensions.LogWarnings();

                    if (deleted > 0)
                    {
                        Log.Information("Deleted {DeletedCount} duplicate files", deleted);
                    }

                    if (kept > 0)
                    {
                        Log.Information("Kept {KeptCount} unique files", kept);
                    }

                    if (failed > 0)
                    {
                        Log.Warning("Failed {FailedCount} files", failed);
                    }
                }
            )
            .ConfigureAwait(false);
}
