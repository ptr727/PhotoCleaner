namespace PhotoCleaner;

internal sealed class VerifyCommand(
    CommandLine.Options options,
    CancellationToken cancellationToken
)
{
    private readonly SkippedExtensionTracker _skippedExtensions = new();

    internal async Task<int> ExecuteAsync() =>
        await CommandRunner
            .RunAsync(
                "Verify",
                async () =>
                {
                    (IReadOnlyList<string> files, int totalCount) = FileEnumerator.Enumerate(
                        options.Path,
                        options.Threads,
                        cancellationToken
                    );

                    VerifyTask.Counts counts = await DatabaseScope
                        .RunAsync(
                            options.DbFile,
                            async database =>
                            {
                                VerifyTask task = new(options, database, _skippedExtensions);
                                return await task.ExecuteAsync(files, cancellationToken)
                                    .ConfigureAwait(false);
                            },
                            cancellationToken: cancellationToken
                        )
                        .ConfigureAwait(false);

                    Log.Information("Total {TotalCount} files", totalCount);
                    Log.Information("Verified {VerifiedCount} files", counts.Verified);
                    Log.Information(
                        "Skipped {SkippedCount} already verified files",
                        counts.Skipped
                    );
                    Log.Information("Ignored {IgnoredCount} non-media files", counts.Ignored);
                    _skippedExtensions.LogWarnings();
                    Log.Information("Invalid {InvalidCount} files", counts.Invalid);
                    Log.Information("Failed {FailedCount} files", counts.Failed);

                    return counts.Invalid > 0 || counts.Failed > 0
                        ? ExitCode.Failed
                        : ExitCode.Success;
                }
            )
            .ConfigureAwait(false);
}
