using CliWrap;
using CliWrap.Buffered;

namespace PhotoCleaner;

internal sealed class ImportTask(
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

    internal enum ImportResult
    {
        Imported,
        Ignored,
        Skipped,
        SkippedBySkipDb,
        TrashedInImmich,
        Invalid,
        Failed,
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Per-file catch-all logs the file path and continues processing remaining files"
    )]
    internal async Task<(
        int imported,
        int ignored,
        int skipped,
        int skipDbSkipped,
        int trashSkipped,
        int invalid,
        int failed,
        int deletedDirs
    )> ExecuteAsync(
        IReadOnlyCollection<string> allFiles,
        DirectoryInfo sourceDir,
        CancellationToken cancellationToken = default
    )
    {
        int imported = 0,
            ignored = 0,
            skipped = 0,
            skipDbSkipped = 0,
            trashSkipped = 0,
            invalid = 0,
            failed = 0;
        Log.Information("Importing {FileCount} files", allFiles.Count);
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
                        _ = await ImportFileAsync(file, sourceDir, ct).ConfigureAwait(false) switch
                        {
                            ImportResult.Imported => Interlocked.Increment(ref imported),
                            ImportResult.Ignored => Interlocked.Increment(ref ignored),
                            ImportResult.Skipped => Interlocked.Increment(ref skipped),
                            ImportResult.SkippedBySkipDb => Interlocked.Increment(
                                ref skipDbSkipped
                            ),
                            ImportResult.TrashedInImmich => Interlocked.Increment(ref trashSkipped),
                            ImportResult.Invalid => Interlocked.Increment(ref invalid),
                            ImportResult.Failed => Interlocked.Increment(ref failed),
                            _ => throw new NotImplementedException(),
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Every failure counts, including a file missing since it was indexed.
                        // Import never renames its source, so a vanished file means the tree changed.
                        Log.Error(ex, "Failed to import '{FilePath}'", file);
                        _ = Interlocked.Increment(ref failed);
                    }
                }
            )
            .ConfigureAwait(false);

        int deletedDirs = options.DeleteEmpty
            ? DirectoryCleaner.DeleteEmptyDirectories(options.OutPath!, options.DryRun)
            : 0;
        return (
            imported,
            ignored,
            skipped,
            skipDbSkipped,
            trashSkipped,
            invalid,
            failed,
            deletedDirs
        );
    }

    private async Task<ImportResult> ImportFileAsync(
        string file,
        DirectoryInfo sourceDir,
        CancellationToken cancellationToken = default
    )
    {
        Log.Information("Importing '{FilePath}'", file);

        // Skip non-media files
        if (!MediaUtilities.SupportedExtensions.Contains(Path.GetExtension(file)))
        {
            Log.Warning("Skipping non-media file: '{FilePath}'", file);
            skippedExtensions.Track(Path.GetExtension(file));
            return ImportResult.Ignored;
        }

        string? sha256 = null;
        string? sha1 = null;
        FileRecord? cached = null;
        if (database is not null || skipDatabase is not null || trashDatabase is not null)
        {
            // ResolveHashesAsync returns cached hashes when size and mtime still match disk.
            // The cache is keyed by source path because import inserts source paths.
            cached = database is null
                ? null
                : await database.GetByPathAsync(file, cancellationToken).ConfigureAwait(false);
            Log.Debug("Hashing '{FilePath}'", file);
            (string resolvedSha256, string resolvedSha1) = await Database
                .ResolveHashesAsync(file, cached, options.Rehash, cancellationToken)
                .ConfigureAwait(false);
            sha256 = resolvedSha256;
            sha1 = resolvedSha1;
            Log.Debug("File '{FilePath}' has SHA-256 '{Sha256}'", file, sha256);

            // Trash.db outlives Immich's 30-day trash retention.
            // Skipping here is the only thing preventing re-import after Immich purges its trash.
            if (trashDatabase is not null)
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
                    return ImportResult.TrashedInImmich;
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
                    return ImportResult.SkippedBySkipDb;
                }
            }

            // Dedup: skip files already imported (matched by source content hash).
            if (database is not null)
            {
                bool exists = await database
                    .Sha256ExistsAsync(sha256, cancellationToken)
                    .ConfigureAwait(false);
                if (exists)
                {
                    Log.Information(
                        "Skipping already imported '{FilePath}' with SHA-256 '{Sha256}'",
                        file,
                        sha256
                    );
                    return ImportResult.Skipped;
                }
            }
        }

        ExifToolJson? meta = await GetFileMetaAsync(file, cancellationToken).ConfigureAwait(false);

        // Warnings are ignored on purpose, since most healthy files carry them.
        (int validateErrors, _) = ExifToolJson.ParseValidate(meta?.Validate);
        if (validateErrors > 0)
        {
            Log.Error(
                "Skipping import of '{FilePath}': exiftool validation reported {ErrorCount} error(s): {Validate} {Error}",
                file,
                validateErrors,
                meta!.Validate,
                meta.ExifToolError
            );
            return ImportResult.Invalid;
        }

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
            return ImportResult.Imported;
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

            // The row identifies the source file, not the destination.
            // Lookups are by source content hash.
            if (database is not null && sha256 is not null && sha1 is not null)
            {
                if (cached is null)
                {
                    Log.Debug(
                        "Inserting source '{SourcePath}' with SHA-256 '{Sha256}'",
                        file,
                        sha256
                    );
                    await database
                        .InsertAsync(
                            new FileRecord(
                                file,
                                sha256,
                                sha1,
                                sourceInfo.Length,
                                sourceInfo.LastWriteTimeUtc.Ticks,
                                false
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    Log.Debug(
                        "Updating source '{SourcePath}' with SHA-256 '{Sha256}'",
                        file,
                        sha256
                    );
                    await database
                        .UpdateHashesAsync(
                            file,
                            sha256,
                            sha1,
                            sourceInfo.Length,
                            sourceInfo.LastWriteTimeUtc.Ticks,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
            }

            return ImportResult.Imported;
        }
        catch (OperationCanceledException)
        {
            // Only a cancelled copy can leave a partial destination.
            // A move is atomic on one filesystem, and cross-device failure leaves the source intact.
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
            return ImportResult.Failed;
        }
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

    // Remove-then-add per value replaces an existing copy instead of doubling it.
    // -overwrite_original skips exiftool's _original backup.
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
