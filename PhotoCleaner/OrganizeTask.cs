using CliWrap;
using CliWrap.Buffered;

namespace PhotoCleaner;

internal sealed class OrganizeTask(
    CommandLine.Options options,
    Database? database,
    SkippedExtensionTracker skippedExtensions
)
{
    private static readonly FrozenSet<string> s_exiftoolWriteExtensions = new[]
    {
        ".3gp",
        ".arw",
        ".cr2",
        ".dng",
        ".gif",
        ".heic",
        ".heif",
        ".jpeg",
        ".jpg",
        ".mov",
        ".mp4",
        ".nef",
        ".orf",
        ".png",
        ".psd",
        ".rw2",
        ".tif",
        ".tiff",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal enum OrganizeResult
    {
        Organized,
        Ignored,
        Skipped,
        Failed,
    }

    internal async Task<(
        int organized,
        int ignored,
        int skipped,
        int failed,
        int deletedDirs
    )> ExecuteAsync(
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
                    MaxDegreeOfParallelism = options.Threads,
                    CancellationToken = cancellationToken,
                },
                async (file, ct) =>
                {
                    _ = await OrganizeFileAsync(file, sourceDir, ct).ConfigureAwait(false) switch
                    {
                        OrganizeResult.Organized => Interlocked.Increment(ref organized),
                        OrganizeResult.Ignored => Interlocked.Increment(ref ignored),
                        OrganizeResult.Skipped => Interlocked.Increment(ref skipped),
                        OrganizeResult.Failed => Interlocked.Increment(ref failed),
                        _ => throw new NotImplementedException(),
                    };
                }
            )
            .ConfigureAwait(false);

        int deletedDirs = options.DeleteEmpty ? DeleteEmptyDirectories(sourceDir) : 0;
        return (organized, ignored, skipped, failed, deletedDirs);
    }

    private async Task<OrganizeResult> OrganizeFileAsync(
        string file,
        DirectoryInfo sourceDir,
        CancellationToken cancellationToken = default
    )
    {
        // Skip non-media files
        if (!MediaUtilities.SupportedExtensions.Contains(Path.GetExtension(file)))
        {
            Log.Warning("Skipping non-media file: '{FilePath}'", file);
            skippedExtensions.Track(Path.GetExtension(file));
            return OrganizeResult.Ignored;
        }

        string? hash = null;
        if (!options.DryRun && database is not null)
        {
            Log.Information("Indexing '{FilePath}'", file);
            hash = await Database
                .ResolveHashAsync(file, null, options.Rehash, cancellationToken)
                .ConfigureAwait(false);
            Log.Debug("File '{FilePath}' has hash '{Hash}'", file, hash);
            bool exists = await database
                .HashExistsAsync(hash, cancellationToken)
                .ConfigureAwait(false);
            if (exists)
            {
                Log.Information(
                    "Skipping already processed '{FilePath}' with hash '{Hash}'",
                    file,
                    hash
                );
                return OrganizeResult.Skipped;
            }
        }

        ExifToolJson? meta = await GetFileMetaAsync(file, cancellationToken).ConfigureAwait(false);

        string inferredDateStr = string.Empty;
        DateTime? inferredDate = null;
        if (options.DatePath && !(meta?.IsDateSet() ?? false))
        {
            if (DateFromPath.InferCreatedDate(file, ref inferredDateStr))
            {
                _ = DateTime.TryParseExact(
                    inferredDateStr,
                    "yyyy:MM:dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsedDate
                );
                inferredDate = parsedDate == default ? null : parsedDate;
                Log.Information(
                    "Inferred created date from path '{CreatedDate}': '{FilePath}'",
                    inferredDateStr,
                    file
                );
            }
            else
            {
                Log.Warning("Failed to infer date from path: '{FilePath}'", file);
            }
        }

        string requestedDest = BuildDestinationPath(file, meta, inferredDate);
        string finalDest = MediaUtilities.GetUniqueFileName(requestedDest);

        bool canWriteTags = s_exiftoolWriteExtensions.Contains(
            "." + (meta?.FileTypeExtension ?? string.Empty)
        );
        string[] pathTags =
            (options.TagPath && canWriteTags) ? ComputePathTags(file, sourceDir) : [];
        string[] explicitTags =
            (!string.IsNullOrEmpty(options.Tags) && canWriteTags)
                ? options.Tags.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                : [];
        string[] allTags = [.. pathTags, .. explicitTags];
        if (allTags.Length > 0)
        {
            Log.Information(
                "Tags to apply [{Tags}] to '{FilePath}'",
                string.Join(", ", allTags),
                finalDest
            );
        }

        Log.Information(
            options.Move
                ? "Moving '{SourcePath}' to '{DestinationPath}'"
                : "Copying '{SourcePath}' to '{DestinationPath}'",
            file,
            finalDest
        );
        if (options.DryRun)
        {
            return OrganizeResult.Organized;
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

            if (options.Move)
            {
                File.Move(file, finalDest);
            }
            else
            {
                File.Copy(file, finalDest);
            }

            // Write inferred date to destination before restoring mtime
            if (
                !string.IsNullOrEmpty(inferredDateStr)
                && s_exiftoolWriteExtensions.Contains(
                    "." + (meta?.FileTypeExtension ?? string.Empty)
                )
            )
            {
                await MediaUtilities
                    .SetCreateDateAsync(inferredDateStr, finalDest, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Apply XMP Subject tags before restoring mtime so exiftool does not clobber it
            if (allTags.Length > 0)
            {
                await ApplyTagsAsync(allTags, finalDest, cancellationToken).ConfigureAwait(false);
            }

            // Restore source mtime last - after any exiftool writes
            File.SetLastWriteTimeUtc(finalDest, sourceInfo.LastWriteTimeUtc);

            if (hash is not null)
            {
                Log.Debug("Inserting '{FilePath}' with hash '{Hash}'", finalDest, hash);
                FileInfo destInfo = new(finalDest);
                await database!
                    .InsertAsync(
                        new FileRecord(
                            finalDest,
                            hash,
                            destInfo.Length,
                            destInfo.LastWriteTimeUtc.Ticks,
                            false
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            return OrganizeResult.Organized;
        }
        catch (OperationCanceledException)
        {
            // Clean up partial destination file on cancelled copy (not move - moves are atomic
            // on the same filesystem; for cross-device moves .NET leaves the source intact on
            // failure, so no orphan risk)
            if (!options.Move && File.Exists(finalDest))
            {
                try
                {
                    File.Delete(finalDest);
                }
                catch (IOException)
                {
                    Log.Warning("Failed to clean up partial file: '{FilePath}'", finalDest);
                }
            }

            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(
                ex,
                options.Move
                    ? "Failed to move '{SourcePath}' to '{DestinationPath}'"
                    : "Failed to copy '{SourcePath}' to '{DestinationPath}'",
                file,
                finalDest
            );
            return OrganizeResult.Failed;
        }
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
                if (options.DryRun)
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

    private string BuildDestinationPath(
        string filePath,
        ExifToolJson? meta,
        DateTime? overrideDate = null
    )
    {
        DateTime date = overrideDate ?? meta?.GetDate() ?? DateTime.MinValue;
        string dateDir = date.ToString(options.Format, CultureInfo.InvariantCulture)
            .Replace('/', Path.DirectorySeparatorChar);
        string destPath = Path.Combine(
            options.OutPath!.FullName,
            dateDir,
            Path.GetFileName(filePath)
        );
        return destPath;
    }

    private static async Task<ExifToolJson?> GetFileMetaAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await MediaUtilities
                .GetExifToolJsonAsync(filePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Failed to read metadata for '{FilePath}'", filePath);
            return null;
        }
    }

    // Returns the directory sub-path components of sourceFile relative to sourceDir.
    // Returns [] when the file is directly inside sourceDir (relative path is ".").
    internal static string[] ComputePathTags(string sourceFile, DirectoryInfo sourceDir)
    {
        string fileDir = Path.GetDirectoryName(sourceFile) ?? string.Empty;
        string relPath = Path.GetRelativePath(sourceDir.FullName, fileDir);
        return relPath == "."
            ? []
            : relPath.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
    }

    // Adds each tag to XMP:Subject on destFile without creating duplicates.
    // Uses remove-then-add (-= then +=) per value so existing copies are replaced
    // rather than doubled. -overwrite_original skips exiftool's _original backup.
    private static async Task ApplyTagsAsync(
        string[] tags,
        string destFile,
        CancellationToken cancellationToken = default
    )
    {
        List<string> args = new((tags.Length * 2) + 2);
        foreach (string tag in tags)
        {
            args.Add($"-XMP:Subject-={tag}");
            args.Add($"-XMP:Subject+={tag}");
        }

        args.Add("-overwrite_original");
        args.Add(destFile);

        Log.Information(
            "Applying path tags [{Tags}] to '{FilePath}'",
            string.Join(", ", tags),
            destFile
        );
        Log.Debug("exiftool: Setting XMP:Subject on '{FilePath}'", destFile);
        _ = await Cli.Wrap("exiftool")
            .WithArguments([.. args])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
    }
}
