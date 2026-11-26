using System.Collections.Concurrent;

namespace PhotoCleaner;

internal sealed class ImportTask(
    CommandLine.Options options,
    Database? database,
    Database? skipDatabase,
    TrashDatabase? trashDatabase,
    SkippedExtensionTracker skippedExtensions
)
{
    private readonly ConcurrentDictionary<string, Claim> _claims = new(StringComparer.Ordinal);

    internal string ExifToolCommand { get; init; } = "exiftool";

    internal sealed record Claim(string File)
    {
        internal TaskCompletionSource<bool> Imported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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

    // A file that does not import releases its claims, and a file waiting on one of them tries again.
    private async Task<ImportResult> ImportFileAsync(
        string file,
        DirectoryInfo sourceDir,
        CancellationToken cancellationToken = default
    )
    {
        List<(string Key, Claim Claim)> claimed = [];
        ImportResult result = ImportResult.Failed;
        try
        {
            result = await ImportFileAsync(file, sourceDir, claimed, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            ReleaseClaims(claimed, result == ImportResult.Imported);
        }
    }

    internal void ReleaseClaims(List<(string Key, Claim Claim)> claimed, bool imported)
    {
        foreach ((string key, Claim claim) in claimed)
        {
            if (!imported)
            {
                _ = _claims.TryRemove(new KeyValuePair<string, Claim>(key, claim));
            }

            _ = claim.Imported.TrySetResult(imported);
        }
    }

    private async Task<ImportResult> ImportFileAsync(
        string file,
        DirectoryInfo sourceDir,
        List<(string Key, Claim Claim)> claimed,
        CancellationToken cancellationToken
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
        ExifToolJson? meta = null;
        bool metaRead = false;
        string? imageHash = null;
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

            // Opt-in, since equal image data with different metadata is treated as one file.
            if (options.ImageHash && (trashDatabase is not null || database is not null))
            {
                meta = await GetFileMetaAsync(file, true, cancellationToken).ConfigureAwait(false);
                metaRead = true;
                imageHash = FileMetadata.FromExifTool(meta)?.ImageHash;
                ImportResult? imageHashResult = await CheckImageHashAsync(
                        file,
                        imageHash,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (imageHashResult is not null)
                {
                    return imageHashResult.Value;
                }
            }

            // Parallel files that match each other both pass the checks above before either row exists.
            // The first to claim a hash goes on, and the rest wait for its outcome, so the counts match a sequential run.
            // Which of the matching files claims first depends on timing when more than one thread runs.
            // SHA-256 is always claimed before the image hash, and no file waits while holding an image hash.
            if (database is not null)
            {
                string? claimedBy = await ClaimedByAsync(
                        "sha256:" + sha256,
                        file,
                        claimed,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                    ?? (
                        options.ImageHash && !string.IsNullOrEmpty(imageHash)
                            ? await ClaimedByAsync(
                                    "image:" + imageHash,
                                    file,
                                    claimed,
                                    cancellationToken
                                )
                                .ConfigureAwait(false)
                            : null
                    );
                if (claimedBy is not null)
                {
                    Log.Information(
                        "Skipping '{FilePath}' matching '{KnownPath}' earlier in this run",
                        file,
                        claimedBy
                    );
                    return ImportResult.Skipped;
                }
            }
        }

        if (!metaRead)
        {
            meta = await GetFileMetaAsync(file, database is not null, cancellationToken)
                .ConfigureAwait(false);
        }

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
            // FileInfo reads lazily, and a move leaves nothing at the source path to read.
            FileInfo sourceInfo = new(file);
            sourceInfo.Refresh();
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
            bool written = true;
            if (!string.IsNullOrEmpty(inferredDateStr) && canWriteTags)
            {
                written = ReportWrite(
                    await MediaUtilities
                        .TrySetCreateDateAsync(
                            inferredDateStr,
                            finalDest,
                            ExifToolCommand,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
                    "created date",
                    file,
                    finalDest
                );
            }

            // Apply XMP Subject tags before restoring mtime so exiftool does not clobber it
            if (written && allTags.Length > 0)
            {
                written = ReportWrite(
                    await ApplyTagsAsync(allTags, finalDest, cancellationToken)
                        .ConfigureAwait(false),
                    "tags",
                    file,
                    finalDest
                );
            }

            if (!written)
            {
                RollBackDestination(file, finalDest, sourceInfo.LastWriteTimeUtc);
                return ImportResult.Failed;
            }

            // Restore source mtime last - after any exiftool writes
            File.SetLastWriteTimeUtc(finalDest, sourceInfo.LastWriteTimeUtc);

            // The row identifies the source file, not the destination.
            // Lookups are by source content hash.
            if (database is not null && sha256 is not null && sha1 is not null)
            {
                FileMetadata? metadata = FileMetadata.FromExifTool(meta);
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
                                false,
                                metadata?.ContentIdentifier,
                                metadata?.ImageHash,
                                metadata is not null
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
                            metadata,
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
                _ = DeleteCopy(finalDest);
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

    internal async Task<string?> ClaimedByAsync(
        string key,
        string file,
        List<(string Key, Claim Claim)> claimed,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            Claim mine = new(file);
            Claim owner = _claims.GetOrAdd(key, mine);
            if (ReferenceEquals(owner, mine))
            {
                claimed.Add((key, mine));
                return null;
            }

            if (await owner.Imported.Task.WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                return owner.File;
            }
        }
    }

    private async Task<ImportResult?> CheckImageHashAsync(
        string file,
        string? imageHash,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(imageHash))
        {
            Log.Debug("No image data hash for '{FilePath}'", file);
            return null;
        }

        if (
            trashDatabase is not null
            && await trashDatabase
                .ImageHashExistsAsync(imageHash, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            Log.Information(
                "Skipping file trashed in Immich '{FilePath}' with image data hash '{ImageHash}'",
                file,
                imageHash
            );
            return ImportResult.TrashedInImmich;
        }

        string? knownPath = database is null
            ? null
            : await database
                .FindPathByImageHashAsync(imageHash, cancellationToken)
                .ConfigureAwait(false);
        if (knownPath is not null)
        {
            Log.Information(
                "Skipping already imported '{FilePath}' with image data hash '{ImageHash}' matching '{KnownPath}'",
                file,
                imageHash,
                knownPath
            );
            return ImportResult.Skipped;
        }

        return null;
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

    // Only a run holding a database or matching by image hash asks for the image hash.
    private static async Task<ExifToolJson?> GetFileMetaAsync(
        string filePath,
        bool imageHash,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return imageHash
                ? await MediaUtilities
                    .GetCatalogExifToolJsonAsync(filePath, cancellationToken)
                    .ConfigureAwait(false)
                : await MediaUtilities
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
    private Task<string?> ApplyTagsAsync(
        string[] tags,
        string destFile,
        CancellationToken cancellationToken
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
        return MediaUtilities.WriteExifToolAsync(ExifToolCommand, [.. args], cancellationToken);
    }

    // A caller may delete the source after an import succeeds, so a failed write fails the file.
    private static bool ReportWrite(
        string? failure,
        string what,
        string sourceFile,
        string destFile
    )
    {
        if (failure is null)
        {
            return true;
        }

        Log.Error(
            "Failed to write {What} to '{DestinationPath}' imported from '{SourcePath}' ({Failure})",
            what,
            destFile,
            sourceFile,
            failure
        );
        return false;
    }

    // A copy is removed, so the untouched source imports cleanly on the next run.
    // A moved file has no source left to return to, so it stays where it landed.
    private void RollBackDestination(string sourceFile, string destFile, DateTime lastWriteTimeUtc)
    {
        if (options.Move)
        {
            File.SetLastWriteTimeUtc(destFile, lastWriteTimeUtc);
            Log.Warning(
                "Kept '{DestinationPath}' moved from '{SourcePath}' after the failed write",
                destFile,
                sourceFile
            );
            return;
        }

        if (DeleteCopy(destFile))
        {
            Log.Warning(
                "Removed '{DestinationPath}' so '{SourcePath}' can be imported again",
                destFile,
                sourceFile
            );
        }
    }

    private static bool DeleteCopy(string destFile)
    {
        Exception? failure = MediaUtilities.TryDeleteFile(destFile);
        if (failure is not null)
        {
            Log.Warning(failure, "Failed to remove '{DestinationPath}'", destFile);
        }

        return failure is null;
    }
}
