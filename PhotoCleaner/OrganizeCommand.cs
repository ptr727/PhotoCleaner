using System.Collections.Concurrent;

namespace PhotoCleaner;

internal sealed class OrganizeCommand(
    CommandLine.Options options,
    CancellationToken cancellationToken
)
{
    private readonly ConcurrentDictionary<string, byte> _unknownExtensions = new(
        StringComparer.OrdinalIgnoreCase
    );

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

                    (int organized, int ignored, int skipped, int failed, int deletedDirs) =
                        await DatabaseScope
                            .RunAsync(
                                options.DbFile,
                                async database =>
                                {
                                    OrganizeTask task = new(options, database, _unknownExtensions);
                                    return await task.ExecuteAsync(
                                            files,
                                            options.Path,
                                            cancellationToken
                                        )
                                        .ConfigureAwait(false);
                                },
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

                    if (!_unknownExtensions.IsEmpty)
                    {
                        List<string> unknownExtensionsList = [.. _unknownExtensions.Keys];
                        unknownExtensionsList.Sort();
                        foreach (string extension in unknownExtensionsList)
                        {
                            Log.Warning("Unknown file extension: '{Extension}'", extension);
                        }
                    }

                    if (skipped > 0)
                    {
                        Log.Information(
                            "Skipped {SkippedCount} files already in collection",
                            skipped
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
