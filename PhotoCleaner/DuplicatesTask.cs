namespace PhotoCleaner;

internal sealed class DuplicatesTask(bool dryRun, int threads, bool rehash, Database database)
{
    internal async Task<(
        int indexed,
        int ignored,
        int deleted,
        int kept,
        int failed
    )> ExecuteDuplicatesAsync(
        IReadOnlyCollection<string> sourceFiles,
        IReadOnlyCollection<string> outFiles,
        CancellationToken cancellationToken = default
    )
    {
        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = threads,
            CancellationToken = cancellationToken,
        };

        // Phase 1: hash source files and register them in the database
        Log.Information("Indexing {Count} source files", sourceFiles.Count);
        IndexTask indexTask = new(rehash, database);
        (int inserted, int updated, int unchanged, int ignoredSrc, int indexFailed) =
            await indexTask
                .ExecuteIndexAsync(sourceFiles, threads, cancellationToken)
                .ConfigureAwait(false);
        int indexed = inserted + updated + unchanged;
        Log.Information("Indexed {Count} source files", indexed);

        // Phase 2: scan outpath files and delete those whose hash is in the source index
        int deleted = 0,
            kept = 0,
            deleteFailed = 0;
        Log.Information("Scanning {Count} target files for duplicates", outFiles.Count);
        await Parallel
            .ForEachAsync(
                outFiles,
                parallelOptions,
                async (file, ct) =>
                {
                    if (!ProcessTask.SupportedExtensions.Contains(Path.GetExtension(file)))
                    {
                        return;
                    }

                    Log.Information("Indexing '{FilePath}'", file);
                    string hash = await Database.ComputeHashAsync(file).ConfigureAwait(false);
                    bool isDuplicate = await database.HashExistsAsync(hash).ConfigureAwait(false);
                    if (!isDuplicate)
                    {
                        _ = Interlocked.Increment(ref kept);
                        return;
                    }

                    Log.Information("Deleting duplicate '{FilePath}'", file);
                    if (dryRun)
                    {
                        _ = Interlocked.Increment(ref deleted);
                        return;
                    }

                    try
                    {
                        File.Delete(file);
                        _ = Interlocked.Increment(ref deleted);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Error(ex, "Failed to delete duplicate '{FilePath}'", file);
                        _ = Interlocked.Increment(ref deleteFailed);
                    }
                }
            )
            .ConfigureAwait(false);

        return (indexed, ignoredSrc, deleted, kept, indexFailed + deleteFailed);
    }
}
