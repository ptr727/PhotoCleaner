namespace PhotoCleaner;

internal sealed class OrganizeTask(
    bool dryRun,
    DirectoryInfo outPath,
    string format,
    int threads,
    bool deleteEmpty,
    bool move,
    Database? database
)
{
    internal async Task<(
        int organized,
        int ignored,
        int skippedSamePath,
        int skippedDuplicate,
        int failed,
        int deletedDirs
    )> ExecuteOrganizeAsync(
        IReadOnlyCollection<string> allFiles,
        IReadOnlyCollection<DirectoryInfo> sourceDirs,
        CancellationToken cancellationToken = default
    )
    {
        int organized = 0,
            ignored = 0,
            skippedSamePath = 0,
            skippedDuplicate = 0,
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
                        hash = await Database.ComputeHashAsync(file).ConfigureAwait(false);
                        string? recordedSourcePath = await database
                            .GetSourcePathAsync(hash)
                            .ConfigureAwait(false);
                        Log.Debug(
                            "File '{FilePath}' has hash '{Hash}' (exists in database: {Exists})",
                            file,
                            hash,
                            recordedSourcePath is not null
                        );
                        if (recordedSourcePath is not null)
                        {
                            if (recordedSourcePath != file)
                            {
                                Log.Information(
                                    "Skipping '{FilePath}' (duplicate of already organized '{RecordedSourcePath}')",
                                    file,
                                    recordedSourcePath
                                );
                                _ = Interlocked.Increment(ref skippedDuplicate);
                            }
                            else
                            {
                                Log.Information("Skipping '{FilePath}' (already organized)", file);
                                _ = Interlocked.Increment(ref skippedSamePath);
                            }
                            return;
                        }
                    }

                    ExifToolJson? meta = await GetFileMetaAsync(file).ConfigureAwait(false);
                    string finalDest = BuildDestinationPath(file, meta);

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
                        if (File.Exists(finalDest))
                        {
                            Log.Warning(
                                "Destination exists, overwriting '{DestinationPath}'",
                                finalDest
                            );
                        }

                        if (move)
                        {
                            File.Move(file, finalDest, overwrite: true);
                        }
                        else
                        {
                            File.Copy(file, finalDest, overwrite: true);
                        }

                        File.SetLastWriteTimeUtc(finalDest, sourceInfo.LastWriteTimeUtc);

                        if (hash is not null)
                        {
                            Log.Debug("Recording organized file in database: '{FilePath}'", file);
                            OrganizedFileRecord record = new(
                                hash,
                                file,
                                Path.GetFileName(file),
                                meta?.ContentIdentifier,
                                meta?.GetDateString(),
                                sourceInfo.Length,
                                meta?.MIMEType,
                                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                                finalDest
                            );
                            await database!.InsertAsync(record).ConfigureAwait(false);
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

        int deletedDirs = deleteEmpty ? DeleteEmptyDirectories(sourceDirs) : 0;
        return (organized, ignored, skippedSamePath, skippedDuplicate, failed, deletedDirs);
    }

    private int DeleteEmptyDirectories(IReadOnlyCollection<DirectoryInfo> sourceDirs)
    {
        int count = 0;
        foreach (DirectoryInfo sourceDir in sourceDirs)
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
