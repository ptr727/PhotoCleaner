namespace PhotoCleaner;

internal sealed class OrganizeTask(
    bool dryRun,
    DirectoryInfo outPath,
    string format,
    int threads,
    bool deleteEmpty,
    bool move,
    bool rehash,
    Database? database
)
{
    internal async Task<(
        int organized,
        int ignored,
        int skipped,
        int failed,
        int deletedDirs
    )> ExecuteOrganizeAsync(
        IReadOnlyCollection<string> allFiles,
        DirectoryInfo sourceDir,
        CancellationToken cancellationToken = default
    )
    {
        int organized = 0,
            ignored = 0,
            skipped = 0,
            failed = 0;
        await Parallel
            .ForEachAsync(
                allFiles,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = threads,
                    CancellationToken = cancellationToken,
                },
                async (file, ct) =>
                {
                    if (!ProcessTask.SupportedExtensions.Contains(Path.GetExtension(file)))
                    {
                        _ = Interlocked.Increment(ref ignored);
                        return;
                    }

                    string? hash = null;
                    if (!dryRun && database is not null)
                    {
                        Log.Information("Indexing '{FilePath}'", file);
                        hash = await Database
                            .ResolveHashAsync(file, null, rehash)
                            .ConfigureAwait(false);
                        Log.Debug("File '{FilePath}' has hash '{Hash}'", file, hash);
                        bool exists = await database.HashExistsAsync(hash).ConfigureAwait(false);
                        if (exists)
                        {
                            Log.Information("Skipping '{FilePath}' (already in collection)", file);
                            _ = Interlocked.Increment(ref skipped);
                            return;
                        }
                    }

                    ExifToolJson? meta = await GetFileMetaAsync(file).ConfigureAwait(false);
                    string requestedDest = BuildDestinationPath(file, meta);
                    string finalDest = ProcessTask.GetUniqueFileName(requestedDest);

                    Log.Information(
                        move
                            ? "Moving '{SourcePath}' to '{DestinationPath}'"
                            : "Copying '{SourcePath}' to '{DestinationPath}'",
                        file,
                        finalDest
                    );
                    if (dryRun)
                    {
                        _ = Interlocked.Increment(ref organized);
                        return;
                    }

                    try
                    {
                        FileInfo sourceInfo = new(file);
                        _ = Directory.CreateDirectory(Path.GetDirectoryName(finalDest)!);
                        if (finalDest != requestedDest)
                        {
                            Log.Warning(
                                "Destination '{RequestedPath}' already exists, using '{FinalPath}'",
                                requestedDest,
                                finalDest
                            );
                        }

                        if (move)
                        {
                            File.Move(file, finalDest);
                        }
                        else
                        {
                            File.Copy(file, finalDest);
                        }

                        File.SetLastWriteTimeUtc(finalDest, sourceInfo.LastWriteTimeUtc);

                        if (hash is not null)
                        {
                            Log.Debug("Inserting '{FilePath}' with hash {Hash}", finalDest, hash);
                            FileInfo destInfo = new(finalDest);
                            await database!
                                .InsertAsync(
                                    new FileRecord(
                                        finalDest,
                                        hash,
                                        destInfo.Length,
                                        destInfo.LastWriteTimeUtc.Ticks,
                                        false
                                    )
                                )
                                .ConfigureAwait(false);
                        }

                        _ = Interlocked.Increment(ref organized);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Error(
                            ex,
                            move
                                ? "Failed to move '{SourcePath}' to '{DestinationPath}'"
                                : "Failed to copy '{SourcePath}' to '{DestinationPath}'",
                            file,
                            finalDest
                        );
                        _ = Interlocked.Increment(ref failed);
                    }
                }
            )
            .ConfigureAwait(false);

        int deletedDirs = deleteEmpty ? DeleteEmptyDirectories(sourceDir) : 0;
        return (organized, ignored, skipped, failed, deletedDirs);
    }

    private int DeleteEmptyDirectories(DirectoryInfo sourceDir)
    {
        int count = 0;
        {
            // Order deepest-first so children are removed before their parents
            List<string> subdirs =
            [
                .. Directory
                    .EnumerateDirectories(sourceDir.FullName, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)),
            ];

            foreach (string dir in subdirs)
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    continue;
                }

                Log.Information("Deleting empty directory: '{DirectoryPath}'", dir);
                if (dryRun)
                {
                    count++;
                    continue;
                }

                try
                {
                    Directory.Delete(dir);
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(ex, "Failed to delete empty directory: '{DirectoryPath}'", dir);
                }
            }
        }

        return count;
    }

    private string BuildDestinationPath(string filePath, ExifToolJson? meta)
    {
        DateTime date = meta?.GetDate() ?? DateTime.MinValue;
        string dateDir = date.ToString(format, CultureInfo.InvariantCulture)
            .Replace('/', Path.DirectorySeparatorChar);
        string destPath = Path.Combine(outPath.FullName, dateDir, Path.GetFileName(filePath));
        return destPath;
    }

    private static async Task<ExifToolJson?> GetFileMetaAsync(string filePath)
    {
        try
        {
            return await ProcessTask.GetExifToolJsonAsync(filePath).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Failed to read metadata for '{FilePath}'", filePath);
            return null;
        }
    }
}
