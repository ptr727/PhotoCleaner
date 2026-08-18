namespace PhotoCleaner;

internal enum IndexStatus
{
    Inserted,
    Updated,
    Unchanged,
}

internal sealed class IndexTask(
    CommandLine.Options options,
    Database database,
    SkippedExtensionTracker skippedExtensions
)
{
    internal async Task<(
        IndexStatus status,
        string sha256,
        string sha1,
        bool wasProcessed
    )> IndexFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Log.Information("Indexing '{FilePath}'", filePath);
        FileInfo info = new(filePath);
        FileRecord? cached = await database
            .GetByPathAsync(filePath, cancellationToken)
            .ConfigureAwait(false);
        (string sha256, string sha1) = await Database
            .ResolveHashesAsync(filePath, cached, options.Rehash, cancellationToken)
            .ConfigureAwait(false);
        if (cached is null)
        {
            Log.Debug("Inserting '{FilePath}' with SHA-256 '{Sha256}'", filePath, sha256);
            await database
                .InsertAsync(
                    new FileRecord(
                        filePath,
                        sha256,
                        sha1,
                        info.Length,
                        info.LastWriteTimeUtc.Ticks,
                        options.MarkProcessed
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return (IndexStatus.Inserted, sha256, sha1, options.MarkProcessed);
        }

        if (sha256 != cached.Sha256)
        {
            // Content changed - update hashes and reset is_processed
            Log.Debug("Updating '{FilePath}' with new SHA-256 '{Sha256}'", filePath, sha256);
            await database
                .UpdateHashesAsync(
                    filePath,
                    sha256,
                    sha1,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return (IndexStatus.Updated, sha256, sha1, false);
        }

        if (info.Length != cached.FileSize || info.LastWriteTimeUtc.Ticks != cached.MtimeTicks)
        {
            Log.Debug("Updating metadata for '{FilePath}'", filePath);
            await database
                .UpdateMetadataAsync(
                    filePath,
                    sha1,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return (
                cached.IsProcessed ? IndexStatus.Unchanged : IndexStatus.Updated,
                sha256,
                sha1,
                cached.IsProcessed
            );
        }

        return (IndexStatus.Unchanged, sha256, sha1, cached.IsProcessed);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Per-file catch-all logs the file path and continues processing remaining files"
    )]
    internal async Task<(
        int inserted,
        int updated,
        int unchanged,
        int ignored,
        int failed
    )> ExecuteAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken = default
    )
    {
        int inserted = 0,
            updated = 0,
            unchanged = 0,
            ignored = 0,
            failed = 0;
        await Parallel
            .ForEachAsync(
                files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.Threads,
                    CancellationToken = cancellationToken,
                },
                async (file, ct) =>
                {
                    if (!MediaUtilities.SupportedExtensions.Contains(Path.GetExtension(file)))
                    {
                        Log.Warning("Skipping non-media file: '{FilePath}'", file);
                        skippedExtensions.Track(Path.GetExtension(file));
                        _ = Interlocked.Increment(ref ignored);
                        return;
                    }

                    try
                    {
                        (IndexStatus status, _, _, _) = await IndexFileAsync(file, ct)
                            .ConfigureAwait(false);
                        _ = status switch
                        {
                            IndexStatus.Inserted => Interlocked.Increment(ref inserted),
                            IndexStatus.Updated => Interlocked.Increment(ref updated),
                            IndexStatus.Unchanged => Interlocked.Increment(ref unchanged),
                            _ => throw new NotImplementedException(),
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to index '{FilePath}'", file);
                        _ = Interlocked.Increment(ref failed);
                    }
                }
            )
            .ConfigureAwait(false);
        return (inserted, updated, unchanged, ignored, failed);
    }
}
