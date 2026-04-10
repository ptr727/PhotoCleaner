namespace PhotoCleaner;

internal sealed class CleanupCommand(
    CommandLine.Options options,
    CancellationToken cancellationToken
)
{
    internal async Task<int> ExecuteAsync() =>
        await CommandRunner
            .RunAsync(
                "Cleanup",
                () =>
                {
                    (IReadOnlyList<string> files, int totalCount) = FileEnumerator.Enumerate(
                        options.Path,
                        options.Threads,
                        cancellationToken
                    );

                    (int deleted, int failed) = new CleanupTask(options).Execute(
                        files,
                        cancellationToken
                    );

                    int kept = totalCount - deleted - failed;
                    Log.Information("Total {TotalCount} files", totalCount);

                    if (kept > 0)
                    {
                        Log.Information("Kept {KeptCount} media files", kept);
                    }

                    if (deleted > 0)
                    {
                        Log.Information("Deleted {DeletedCount} non-media files", deleted);
                    }

                    if (failed > 0)
                    {
                        Log.Warning("Failed {FailedCount} files", failed);
                    }

                    return Task.CompletedTask;
                }
            )
            .ConfigureAwait(false);
}
