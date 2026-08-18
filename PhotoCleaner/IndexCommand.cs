namespace PhotoCleaner;

internal sealed class IndexCommand(CommandLine.Options options, CancellationToken cancellationToken)
{
    private readonly SkippedExtensionTracker _skippedExtensions = new();

    internal async Task<int> ExecuteAsync() =>
        await CommandRunner
            .RunAsync(
                "Index",
                async () =>
                {
                    (IReadOnlyList<string> files, int totalCount) = FileEnumerator.Enumerate(
                        options.Path,
                        options.Threads,
                        cancellationToken
                    );

                    (int inserted, int updated, int unchanged, int ignored, int failed) =
                        await DatabaseScope
                            .RunAsync(
                                options.DbFile,
                                async database =>
                                {
                                    IndexTask task = new(options, database!, _skippedExtensions);
                                    Log.Information("Indexing {FileCount} files", files.Count);
                                    return await task.ExecuteAsync(files, cancellationToken)
                                        .ConfigureAwait(false);
                                },
                                cancellationToken: cancellationToken
                            )
                            .ConfigureAwait(false);

                    Log.Information("Total {TotalCount} files", totalCount);
                    Log.Information("Inserted {InsertedCount} new files", inserted);
                    Log.Information("Updated {UpdatedCount} changed files", updated);
                    Log.Information("Unchanged {UnchangedCount} files", unchanged);
                    Log.Information("Ignored {IgnoredCount} non-media files", ignored);
                    _skippedExtensions.LogWarnings();
                    Log.Information("Failed {FailedCount} files", failed);

                    return failed > 0 ? ExitCode.Failed : ExitCode.Success;
                }
            )
            .ConfigureAwait(false);
}
