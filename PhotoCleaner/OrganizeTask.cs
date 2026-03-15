namespace PhotoCleaner;

internal sealed class OrganizeTask(
    bool dryRun,
    DirectoryInfo outPath,
    string format,
    int threads,
    bool deleteEmpty
)
{
    internal async Task<(int moved, int ignored, int failed, int deletedDirs)> ExecuteOrganizeAsync(
        IReadOnlyCollection<string> allFiles,
        IReadOnlyCollection<DirectoryInfo> sourceDirs,
        CancellationToken cancellationToken = default
    )
    {
        int moved = 0,
            ignored = 0,
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

                    string finalDest = await BuildDestinationPathAsync(file).ConfigureAwait(false);

                    Log.Information(
                        "Moving '{SourcePath}' to '{DestinationPath}'",
                        file,
                        finalDest
                    );
                    if (dryRun)
                    {
                        _ = Interlocked.Increment(ref moved);
                        return;
                    }

                    try
                    {
                        DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(file);
                        _ = Directory.CreateDirectory(Path.GetDirectoryName(finalDest)!);
                        File.Move(file, finalDest);
                        File.SetLastWriteTimeUtc(finalDest, lastWriteTimeUtc);
                        _ = Interlocked.Increment(ref moved);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Error(
                            ex,
                            "Failed to move '{SourcePath}' to '{DestinationPath}'",
                            file,
                            finalDest
                        );
                        _ = Interlocked.Increment(ref failed);
                    }
                }
            )
            .ConfigureAwait(false);

        int deletedDirs = deleteEmpty ? DeleteEmptyDirectories(sourceDirs) : 0;
        return (moved, ignored, failed, deletedDirs);
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

    private async Task<string> BuildDestinationPathAsync(string filePath)
    {
        DateTime date = await GetFileDateAsync(filePath).ConfigureAwait(false);
        string dateDir = date.ToString(format, CultureInfo.InvariantCulture);
        string destPath = Path.Combine(outPath.FullName, dateDir, Path.GetFileName(filePath));
        string finalDest = ProcessTask.GetUniqueFileName(destPath);
        if (!string.Equals(destPath, finalDest, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Destination exists, using '{DestinationPath}'", finalDest);
        }
        return finalDest;
    }

    private static async Task<DateTime> GetFileDateAsync(string filePath)
    {
        try
        {
            ExifToolJson? meta = await ProcessTask
                .GetExifToolJsonAsync(filePath)
                .ConfigureAwait(false);
            DateTime? date = meta?.GetDate();
            if (date.HasValue)
            {
                return date.Value;
            }
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Failed to read metadata for '{FilePath}'", filePath);
        }

        Log.Information("No EXIF date found, using DateTime.MinValue for: '{FilePath}'", filePath);
        return DateTime.MinValue;
    }
}
