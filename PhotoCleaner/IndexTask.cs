namespace PhotoCleaner;

internal enum IndexStatus
{
    Inserted,
    Updated,
    Unchanged,
}

internal sealed class IndexTask(CommandLine.Options options, Database database)
{
    internal async Task<(IndexStatus status, string hash, bool wasProcessed)> IndexFileAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        Log.Information("Indexing '{FilePath}'", filePath);
        FileInfo info = new(filePath);
        FileRecord? cached = await database
            .GetByPathAsync(filePath, cancellationToken)
            .ConfigureAwait(false);
        string hash = await Database
            .ResolveHashAsync(filePath, cached, options.Rehash, cancellationToken)
            .ConfigureAwait(false);
        if (cached is null)
        {
            Log.Debug("Inserting '{FilePath}' with hash '{Hash}'", filePath, hash);
            await database
                .InsertAsync(
                    new FileRecord(filePath, hash, info.Length, info.LastWriteTimeUtc.Ticks, false),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return (IndexStatus.Inserted, hash, false);
        }

        if (hash != cached.Hash)
        {
            Log.Debug("Updating '{FilePath}' with new hash '{Hash}'", filePath, hash);
            await database
                .UpdateHashAsync(
                    filePath,
                    hash,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return (IndexStatus.Updated, hash, false);
        }

        return (IndexStatus.Unchanged, hash, cached.IsProcessed);
    }

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
                        _ = Interlocked.Increment(ref ignored);
                        return;
                    }

                    try
                    {
                        (IndexStatus status, _, _) = await IndexFileAsync(file, ct)
                            .ConfigureAwait(false);
                        _ = status switch
                        {
                            IndexStatus.Inserted => Interlocked.Increment(ref inserted),
                            IndexStatus.Updated => Interlocked.Increment(ref updated),
                            IndexStatus.Unchanged => Interlocked.Increment(ref unchanged),
                            _ => throw new NotImplementedException(),
                        };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
