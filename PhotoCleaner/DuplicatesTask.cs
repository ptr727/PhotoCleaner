namespace PhotoCleaner;

internal sealed class DuplicatesTask(
    CommandLine.Options options,
    Database database,
    Database outDatabase,
    TrashDatabase? trashDatabase,
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
        (string sha256, string? sha1) = await Database
            .ResolveHashesAsync(file, cached, options.Rehash, cancellationToken)
            .ConfigureAwait(false);
        Log.Debug("File '{FilePath}' has SHA-256 '{Sha256}'", file, sha256);

        // Upsert into outdb for future cache hits (skip during dry run)
        if (!options.DryRun)
        {
            FileInfo info = new(file);
            if (cached is null)
            {
                await outDatabase
                    .InsertAsync(
                        new FileRecord(
                            file,
                            sha256,
                            sha1,
                            info.Length,
                            info.LastWriteTimeUtc.Ticks,
                            false
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            else if (sha256 != cached.Sha256)
            {
                await outDatabase
                    .UpdateHashesAsync(
                        file,
                        sha256,
                        sha1,
                        info.Length,
                        info.LastWriteTimeUtc.Ticks,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            else if (
                sha1 != cached.Sha1
                || info.Length != cached.FileSize
                || info.LastWriteTimeUtc.Ticks != cached.MtimeTicks
            )
            {
                await outDatabase
                    .UpdateMetadataAsync(
                        file,
                        sha1,
                        info.Length,
                        info.LastWriteTimeUtc.Ticks,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }

        bool isDuplicate = await database
            .Sha256ExistsAsync(sha256, cancellationToken)
            .ConfigureAwait(false);

        // Also check trash DB - delete files whose SHA-1 matches a trashed Immich asset
        bool isTrashed = false;
        if (!isDuplicate && trashDatabase is not null && sha1 is not null)
        {
            isTrashed = await trashDatabase
                .Sha1ExistsAsync(sha1, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!isDuplicate && !isTrashed)
        {
            return DuplicateCheckResult.Kept;
        }

        Log.Information(
            isTrashed
                ? "Deleting file trashed in Immich '{FilePath}' with SHA-1 '{Sha1}'"
                : "Deleting duplicate '{FilePath}' with SHA-256 '{Sha256}'",
            file,
            isTrashed ? sha1 : sha256
        );
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
