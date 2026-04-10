namespace PhotoCleaner;

internal sealed class DuplicatesTask(
    CommandLine.Options options,
    Database database,
    Database outDatabase,
    SkippedExtensionTracker skippedExtensions
)
{
    private enum DuplicateCheckResult
    {
        Ignored,
        Kept,
        Deleted,
        Failed,
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Per-file catch-all logs the file path and continues processing remaining files"
    )]
    internal async Task<(int indexed, int ignored, int deleted, int kept, int failed)> ExecuteAsync(
        IReadOnlyCollection<string> sourceFiles,
        IReadOnlyCollection<string> outFiles,
        CancellationToken cancellationToken = default
    )
    {
        // Phase 1: hash source files and register them in the database
        Log.Information("Indexing {FileCount} source files", sourceFiles.Count);
        IndexTask indexTask = new(options, database, skippedExtensions);
        (int inserted, int updated, int unchanged, int ignoredSrc, int indexFailed) =
            await indexTask.ExecuteAsync(sourceFiles, cancellationToken).ConfigureAwait(false);
        int indexed = inserted + updated + unchanged;
        Log.Information("Indexed {FileCount} source files", indexed);

        // Phase 2: scan outpath files and delete those whose hash is in the source index
        int deleted = 0,
            kept = 0,
            ignored = 0,
            deleteFailed = 0;
        Log.Information("Scanning {FileCount} target files for duplicates", outFiles.Count);
        await Parallel
            .ForEachAsync(
                outFiles,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.Threads,
                    CancellationToken = cancellationToken,
                },
                async (file, ct) =>
                {
                    try
                    {
                        _ = await CheckDuplicateFileAsync(file, ct).ConfigureAwait(false) switch
                        {
                            DuplicateCheckResult.Ignored => Interlocked.Increment(ref ignored),
                            DuplicateCheckResult.Kept => Interlocked.Increment(ref kept),
                            DuplicateCheckResult.Deleted => Interlocked.Increment(ref deleted),
                            DuplicateCheckResult.Failed => Interlocked.Increment(ref deleteFailed),
                            _ => throw new NotImplementedException(),
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to check duplicate '{FilePath}'", file);
                        _ = Interlocked.Increment(ref deleteFailed);
                    }
                }
            )
            .ConfigureAwait(false);

        return (indexed, ignoredSrc + ignored, deleted, kept, indexFailed + deleteFailed);
    }

    private async Task<DuplicateCheckResult> CheckDuplicateFileAsync(
        string file,
        CancellationToken cancellationToken = default
    )
    {
        if (!MediaUtilities.SupportedExtensions.Contains(Path.GetExtension(file)))
        {
            Log.Warning("Skipping non-media file: '{FilePath}'", file);
            skippedExtensions.Track(Path.GetExtension(file));
            return DuplicateCheckResult.Ignored;
        }

        Log.Information("Checking duplicate '{FilePath}'", file);
        FileRecord? cached = await outDatabase
            .GetByPathAsync(file, cancellationToken)
            .ConfigureAwait(false);
        string hash = await Database
            .ResolveHashAsync(file, cached, options.Rehash, cancellationToken)
            .ConfigureAwait(false);
        Log.Debug("File '{FilePath}' has hash '{Hash}'", file, hash);

        // Upsert into outdb for future cache hits
        FileInfo info = new(file);
        if (cached is null)
        {
            await outDatabase
                .InsertAsync(
                    new FileRecord(file, hash, info.Length, info.LastWriteTimeUtc.Ticks, false),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else if (hash != cached.Hash)
        {
            await outDatabase
                .UpdateHashAsync(
                    file,
                    hash,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        bool isDuplicate = await database
            .HashExistsAsync(hash, cancellationToken)
            .ConfigureAwait(false);
        if (!isDuplicate)
        {
            return DuplicateCheckResult.Kept;
        }

        Log.Information("Deleting duplicate '{FilePath}' with hash '{Hash}'", file, hash);
        if (options.DryRun)
        {
            return DuplicateCheckResult.Deleted;
        }

        try
        {
            File.Delete(file);
            return DuplicateCheckResult.Deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Failed to delete duplicate '{FilePath}'", file);
            return DuplicateCheckResult.Failed;
        }
    }
}
