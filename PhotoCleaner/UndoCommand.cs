namespace PhotoCleaner;

internal sealed class UndoCommand(CommandLine.Options options, CancellationToken cancellationToken)
{
    internal async Task<int> ExecuteAsync() =>
        await CommandRunner
            .RunAsync(
                "Undo",
                () =>
                {
                    (IReadOnlyList<string> files, int totalCount) = FileEnumerator.Enumerate(
                        options.Path,
                        options.Threads,
                        cancellationToken
                    );

                    UndoTask undoTask = new(options);
                    (int restored, int deleted, int failed) = undoTask.Execute(
                        files,
                        cancellationToken
                    );

                    Log.Information("Total {TotalCount} files", totalCount);

                    if (restored > 0)
                    {
                        Log.Information("Restored {RestoredCount} files", restored);
                    }

                    if (deleted > 0)
                    {
                        Log.Information("Deleted {DeletedCount} files", deleted);
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
