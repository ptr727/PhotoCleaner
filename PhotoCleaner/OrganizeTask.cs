using CliWrap;
using CliWrap.Buffered;

namespace PhotoCleaner;

internal sealed class OrganizeTask(
    CommandLine.Options options,
    Database? database,
    Database? skipDatabase,
    TrashDatabase? trashDatabase,
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
        SkippedBySkipDb,
        TrashedInImmich,
        Failed,
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Per-file catch-all logs the file path and continues processing remaining files"
    )]
    internal async Task<(
        int organized,
        int ignored,
        int skipped,
        int skipDbSkipped,
        int trashSkipped,
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
            skipDbSkipped = 0,
            trashSkipped = 0,
            failed = 0;
        Log.Information("Organizing {FileCount} files", allFiles.Count);
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
                    try
                    {
                        _ = await OrganizeFileAsync(file, sourceDir, ct)
                            .ConfigureAwait(false) switch
                        {
                            OrganizeResult.Organized => Interlocked.Increment(ref organized),
                            OrganizeResult.Ignored => Interlocked.Increment(ref ignored),
                            OrganizeResult.Skipped => Interlocked.Increment(ref skipped),
                            OrganizeResult.SkippedBySkipDb => Interlocked.Increment(
                                ref skipDbSkipped
                            ),
                            OrganizeResult.TrashedInImmich => Interlocked.Increment(
                                ref trashSkipped
                            ),
                            OrganizeResult.Failed => Interlocked.Increment(ref failed),
                            _ => throw new NotImplementedException(),
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to organize '{FilePath}'", file);
                        _ = Interlocked.Increment(ref failed);
                    }
                }
            )
            .ConfigureAwait(false);

        int deletedDirs = options.DeleteEmpty ? DeleteEmptyDirectories(sourceDir) : 0;
        return (organized, ignored, skipped, skipDbSkipped, trashSkipped, failed, deletedDirs);
    }

    private async Task<OrganizeResult> OrganizeFileAsync(
        string file,
        DirectoryInfo sourceDir,
        CancellationToken cancellationToken = default
    )
    {
        Log.Information("Organizing '{FilePath}'", file);

        // Skip non-media files
        if (!MediaUtilities.SupportedExtensions.Contains(Path.GetExtension(file)))
        {
            Log.Warning("Skipping non-media file: '{FilePath}'", file);
            skippedExtensions.Track(Path.GetExtension(file));
            return OrganizeResult.Ignored;
        }

        string? sha256 = null;
        string? sha1 = null;
        if (database is not null || skipDatabase is not null || trashDatabase is not null)
        {
            Log.Debug("Hashing '{FilePath}'", file);
            (string resolvedSha256, string? resolvedSha1) = await Database
                .ResolveHashesAsync(
                    file,
                    null,
                    options.Rehash,
                    needsSha1: trashDatabase is not null,
                    cancellationToken
                )
                .ConfigureAwait(false);
            sha256 = resolvedSha256;
            sha1 = resolvedSha1;
            Log.Debug("File '{FilePath}' has SHA-256 '{Sha256}'", file, sha256);

            // Check trash DB first - skip files that were trashed in Immich
            if (trashDatabase is not null && sha1 is not null)
            {
                bool trashed = await trashDatabase
                    .Sha1ExistsAsync(sha1, cancellationToken)
                    .ConfigureAwait(false);
                if (trashed)
                {
                    Log.Information(
                        "Skipping file trashed in Immich '{FilePath}' with SHA-1 '{Sha1}'",
                        file,
                        sha1
                    );
                    return OrganizeResult.TrashedInImmich;
                }
            }

            // Check skip DB (read-only)
            if (skipDatabase is not null)
            {
                bool inSkipDb = await skipDatabase
                    .Sha256ExistsAsync(sha256, cancellationToken)
                    .ConfigureAwait(false);
                if (inSkipDb)
                {
                    Log.Information(
                        "Skipping file found in skip database '{FilePath}' with SHA-256 '{Sha256}'",
                        file,
                        sha256
                    );
                    return OrganizeResult.SkippedBySkipDb;
                }
            }

            // Check dedup DB - skip files already organized
            if (database is not null)
            {
                bool exists = await database
                    .Sha256ExistsAsync(sha256, cancellationToken)
                    .ConfigureAwait(false);
                if (exists)
                {
                    Log.Information(
                        "Skipping already organized '{FilePath}' with SHA-256 '{Sha256}'",
                        file,
                        sha256
                    );
                    return OrganizeResult.Skipped;
                }
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

            if (database is not null && sha256 is not null)
            {
                Log.Debug("Inserting '{FilePath}' with SHA-256 '{Sha256}'", finalDest, sha256);
                FileInfo destInfo = new(finalDest);
                await database
                    .InsertAsync(
                        new FileRecord(
                            finalDest,
                            sha256,
                            sha1,
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
