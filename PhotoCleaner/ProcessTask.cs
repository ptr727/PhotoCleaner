using System.Collections.Concurrent;
using CliWrap;
using CliWrap.Buffered;

namespace PhotoCleaner;

internal sealed class ProcessTask(
    CommandLine.Options options,
    FileInfo fileInfo,
    Database? database,
    ConcurrentBag<string> reProcessNames,
    ConcurrentDictionary<string, byte> unknownExtensions,
    CancellationToken cancellationToken
)
{
    public enum ProcessResult
    {
        Success,
        Failure,
        Deleted,
        Reprocess,
        Modified,
        UnknownExtension,
        Skipped,
    }

    private ExifToolJson? _exifToolJson;
    private bool _modified;
    private bool _reprocess;
    private bool _deleted;

    private static readonly FrozenSet<string> s_remuxExtensions = new[]
    {
        ".m2t",
        ".mkv",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_reencodeExtensions = new[]
    {
        ".asf",
        ".wmv",
        ".avi",
        ".3gp",
        ".gif",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_reencodeAudioExtensions = new[]
    {
        ".mov",
        ".mp4",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_liveVideoExtensions = new[]
    {
        ".mp4",
        ".mov",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_liveVideoImageExtensions = new[]
    {
        ".heic",
        ".jpg",
        ".jpeg", // Non-canonical version required, matching file may not yet have been normalized
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static Task<ProcessResult> ExecuteAsync(
        CommandLine.Options options,
        FileInfo fileInfo,
        Database? database,
        ConcurrentBag<string> reProcessNames,
        ConcurrentDictionary<string, byte> unknownExtensions,
        CancellationToken cancellationToken = default
    )
    {
        ProcessTask processTask = new(
            options,
            fileInfo,
            database,
            reProcessNames,
            unknownExtensions,
            cancellationToken
        );
        return processTask.ExecuteAsync();
    }

    private async Task<ProcessResult> ExecuteAsync()
    {
        // Skip files that no longer exist
        if (!fileInfo.Exists)
        {
            Log.Warning("Skipping file that no longer exists: '{FilePath}'", fileInfo.FullName);
            return ProcessResult.Success;
        }

        // Skip non-media files
        if (!MediaUtilities.SupportedExtensions.Contains(fileInfo.Extension))
        {
            Log.Warning("Skipping non-media file: '{FilePath}'", fileInfo.FullName);
            _ = unknownExtensions.TryAdd(fileInfo.Extension, 0);
            return ProcessResult.UnknownExtension;
        }

        // Hash file before any modification for DB deduplication
        string? preProcessHash = null;
        if (database is not null)
        {
            IndexTask indexTask = new(options, database);
            (IndexStatus status, string hash, bool wasProcessed) = await indexTask
                .IndexFileAsync(fileInfo.FullName, cancellationToken)
                .ConfigureAwait(false);
            preProcessHash = hash;
            Log.Debug(
                "File '{FilePath}' has pre-process hash '{Hash}'",
                fileInfo.FullName,
                preProcessHash
            );
            if (status == IndexStatus.Unchanged && wasProcessed && !options.Reprocess)
            {
                Log.Information(
                    "Skipping already processed '{FilePath}' with hash '{Hash}'",
                    fileInfo.FullName,
                    preProcessHash
                );
                return ProcessResult.Skipped;
            }
        }

        // Get exiftool info
        _exifToolJson = await MediaUtilities
            .GetExifToolJsonAsync(fileInfo.FullName, cancellationToken)
            .ConfigureAwait(false);

        // Process files
        ProcessResult result =
            !RenameMismatchedMimeExtensions()
            || !RenameMixedCaseExtensions()
            || !await DeleteLivePhotosAsync().ConfigureAwait(false)
            || !await ConvertVideoAsync().ConfigureAwait(false)
            || !WarnDngVersion()
                ? _reprocess
                    ? ProcessResult.Reprocess
                    : _deleted
                        ? ProcessResult.Deleted
                        : ProcessResult.Failure
                : _modified
                    ? ProcessResult.Modified
                    : ProcessResult.Success;

        // Record terminal results in DB so re-runs skip already processed files
        if (preProcessHash is not null && result is ProcessResult.Success or ProcessResult.Modified)
        {
            Log.Debug(
                "Recording processed file in database: '{FilePath}' with hash '{Hash}'",
                fileInfo.FullName,
                preProcessHash
            );
            await database!
                .SetProcessedAsync(fileInfo.FullName, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    private bool RenameMixedCaseExtensions()
    {
        if (!IsMixedCaseExtension(fileInfo.Extension.AsSpan()))
        {
            return true;
        }

        Log.Warning(
            "Mixed case extension detected '{Extension}': '{FilePath}'",
            fileInfo.Extension,
            fileInfo.FullName
        );
        if (options.DryRun)
        {
            return false;
        }

        // Rename using lowercase extensions
        _modified = true;
        string outputFile = Path.ChangeExtension(
            fileInfo.FullName,
            fileInfo.Extension.ToLowerInvariant()
        );
        MoveFile(fileInfo.FullName, outputFile, options.SkipBackup);

        // Queue renamed file for further processing
        return ReProcess(outputFile);
    }

    private bool RenameMismatchedMimeExtensions()
    {
        // Use the canonical extension reported by exiftool for this file's actual content
        if (string.IsNullOrEmpty(_exifToolJson!.FileTypeExtension))
        {
            Log.Warning(
                "No FileTypeExtension returned by exiftool for '{FilePath}'; skipping extension check",
                fileInfo.FullName
            );
            return true;
        }
        Log.Debug(
            "Exiftool MIME details for '{FilePath}': '{FileDetails}'",
            fileInfo.FullName,
            _exifToolJson.FileDetails
        );

        // Only rename if extensions is in process list
        string expectedExtension = "." + _exifToolJson!.FileTypeExtension.ToLowerInvariant();
        if (!MediaUtilities.SupportedExtensions.Contains(expectedExtension))
        {
            Log.Warning(
                "Exiftool FileTypeExtension '{ExpectedExtension}' is not in process list for '{FilePath}'; skipping extension check",
                expectedExtension,
                fileInfo.FullName
            );
            return true;
        }

        // Get the normalized media extension for the file
        GetFileMediaExtension(fileInfo.FullName, out string baseName, out string mediaExtension);
        if (string.Equals(mediaExtension, expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Log.Warning(
            "File extension '{Extension}' does not match exiftool expected '{Expected}': '{FilePath}'",
            mediaExtension,
            expectedExtension,
            fileInfo.FullName
        );
        if (options.DryRun)
        {
            return false;
        }

        // Rename extension to match exiftool's canonical extension
        // Note; no backup is taken, undo cannot restore
        _modified = true;
        string outputFile = baseName + expectedExtension;
        MoveFile(fileInfo.FullName, outputFile, options.SkipBackup);

        // Queue renamed file for further processing
        return ReProcess(outputFile);
    }

    private async Task<bool> IsPcmAudioAsync()
    {
        Log.Debug("ffprobe: Checking for PCM audio stream in '{FilePath}'", fileInfo.FullName);
        BufferedCommandResult result = await Cli.Wrap("ffprobe")
            .WithArguments([
                "-loglevel",
                "error",
                "-select_streams",
                "a:0",
                "-show_entries",
                "stream=codec_name",
                "-of",
                "default=nw=1:nk=1",
                fileInfo.FullName,
            ])
            .ExecuteBufferedAsync(cancellationToken);
        return result
            .StandardOutput.AsSpan()
            .Trim()
            .StartsWith("pcm", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<float> GetDurationAsync()
    {
        Log.Debug("ffprobe: Getting play duration for '{FilePath}'", fileInfo.FullName);
        BufferedCommandResult result = await Cli.Wrap("ffprobe")
            .WithArguments([
                "-loglevel",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "format=duration",
                "-of",
                "default=nw=1:nk=1",
                fileInfo.FullName,
            ])
            .ExecuteBufferedAsync(cancellationToken);
        return float.Parse(result.StandardOutput.AsSpan().Trim(), CultureInfo.InvariantCulture);
    }

    private async Task<bool> DeleteLivePhotosAsync()
    {
        // Live photos are MOV or MP4 about ~3s long with a matching HEIC or JPEG file
        if (!s_liveVideoExtensions.Contains(fileInfo.Extension))
        {
            return true;
        }

        // Short videos, always delete
        float duration = await GetDurationAsync().ConfigureAwait(false);
        if (duration <= options.ShortVideoDuration)
        {
            Log.Warning(
                "Deleting {Duration}s short video clip: '{FilePath}'",
                duration,
                fileInfo.FullName
            );
            if (options.DryRun)
            {
                return false;
            }

            _modified = true;
            _deleted = true;
            if (options.SkipBackup)
            {
                File.Delete(fileInfo.FullName);
            }
            else
            {
                _ = BackupFile(fileInfo.FullName, false);
            }
            return false;
        }

        // No matching image candidate then skip
        string? companionPath = FindCompanionImagePath();
        if (companionPath == null)
        {
            return true;
        }

        // Long videos
        if (duration >= MediaUtilities.LiveVideoDuration)
        {
            // Warn in case the video length threshold needs adjustment
            Log.Warning(
                "Long {Duration}s video clip has matching image file: '{FilePath}'",
                duration,
                fileInfo.FullName
            );
            return true;
        }

        // Verify ContentIdentifier tags match to confirm this is a live photo pair
        string? videoContentId = _exifToolJson?.ContentIdentifier;
        string? imageContentId = (
            await MediaUtilities
                .GetExifToolJsonAsync(companionPath, cancellationToken)
                .ConfigureAwait(false)
        )?.ContentIdentifier;
        Log.Debug(
            "ContentIdentifier tag for video: '{FilePath}' = '{ContentIdentifier}'",
            fileInfo.FullName,
            videoContentId
        );
        Log.Debug(
            "ContentIdentifier tag for image: '{FilePath}' = '{ContentIdentifier}'",
            companionPath,
            imageContentId
        );

        if (string.IsNullOrEmpty(videoContentId) || string.IsNullOrEmpty(imageContentId))
        {
            Log.Warning(
                "Cannot confirm live photo pair, ContentIdentifier missing: '{FilePath}'",
                fileInfo.FullName
            );
            return true;
        }

        if (!string.Equals(videoContentId, imageContentId, StringComparison.Ordinal))
        {
            Log.Warning(
                "ContentIdentifier mismatch, not a live photo pair: '{FilePath}'",
                fileInfo.FullName
            );
            return true;
        }

        // Delete live video with confirmed matching image
        if (options.DryRun)
        {
            return false;
        }

        _modified = true;
        _deleted = true;
        Log.Warning(
            "Deleting {Duration}s live photo video with matching image file: '{FilePath}'",
            duration,
            fileInfo.FullName
        );
        if (options.SkipBackup)
        {
            File.Delete(fileInfo.FullName);
        }
        else
        {
            _ = BackupFile(fileInfo.FullName, false);
        }
        return false;
    }

    private async Task<bool> ConvertVideoAsync()
    {
        // Output to temp file
        string tempFile = Path.ChangeExtension(fileInfo.FullName, ".temp");
        string[] ffmpegArguments;
        if (s_remuxExtensions.Contains(fileInfo.Extension))
        {
            // Remux audio and video
            Log.Information(
                "ffmpeg: Remuxing audio and video by file extension: '{FilePath}'",
                fileInfo.FullName
            );
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                fileInfo.FullName,
                "-c",
                "copy",
                "-movflags",
                "+faststart",
                "-f",
                "mp4",
                tempFile,
            ];
        }
        else if (s_reencodeExtensions.Contains(fileInfo.Extension))
        {
            // Reencode audio and video
            Log.Information(
                "ffmpeg: Reencode audio and video by file extension: '{FilePath}'",
                fileInfo.FullName
            );
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                fileInfo.FullName,
                "-c:v",
                "libx264",
                "-crf",
                "21",
                "-preset",
                "medium",
                "-pix_fmt",
                "yuv420p",
                "-c:a",
                "aac",
                "-b:a",
                "128k",
                "-movflags",
                "+faststart",
                "-f",
                "mp4",
                tempFile,
            ];
        }
        else if (s_reencodeAudioExtensions.Contains(fileInfo.Extension))
        {
            // Only if audio is PCM
            if (!await IsPcmAudioAsync().ConfigureAwait(false))
            {
                return true;
            }

            // Reencode audio and remux video
            Log.Information(
                "ffmpeg: Reencode PCM audio and remux video: '{FilePath}'",
                fileInfo.FullName
            );
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                fileInfo.FullName,
                "-c:v",
                "copy",
                "-c:a",
                "aac",
                "-b:a",
                "128k",
                "-movflags",
                "+faststart",
                "-f",
                "mp4",
                tempFile,
            ];
        }
        else
        {
            // Nothing to do
            return true;
        }
        if (options.DryRun)
        {
            return false;
        }

        DateTime sourceLastWriteTimeUtc = fileInfo.LastWriteTimeUtc;

        // Delete temp output if it exists
        if (File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }

        try
        {
            // Run ffmpeg
            _ = await Cli.Wrap("ffmpeg")
                .WithArguments(ffmpegArguments)
                .ExecuteBufferedAsync(cancellationToken);

            // Backup original file (or keep it on disk if skipbackup, to read metadata from)
            _modified = true;
            string sourceForMetadata = options.SkipBackup
                ? fileInfo.FullName
                : BackupFile(fileInfo.FullName, false);

            // Rename temp output to MP4; use a unique name if target already exists
            string outputFile = MediaUtilities.GetUniqueFileName(
                Path.ChangeExtension(fileInfo.FullName, MediaUtilities.VideoOutputExtension)
            );
            if (!options.SkipBackup)
            {
                await File.WriteAllTextAsync(
                        sourceForMetadata + ".out",
                        outputFile,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            MoveFile(tempFile, outputFile, options.SkipBackup);

            // Copy compatible metadata from the original to the converted file
            await CopyMetadataAsync(sourceForMetadata, outputFile, cancellationToken)
                .ConfigureAwait(false);
            if (options.SkipBackup)
            {
                Log.Information(
                    "Deleting original after conversion: '{FilePath}'",
                    sourceForMetadata
                );
                File.Delete(sourceForMetadata);
            }

            // Set timestamps on remuxed file from original timestamps
            string? createdDate = _exifToolJson!.GetDateString();
            if (!string.IsNullOrEmpty(createdDate))
            {
                await MediaUtilities
                    .SetCreateDateAsync(createdDate, outputFile, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Restore original file's modified timestamp on the converted output
            File.SetLastWriteTimeUtc(outputFile, sourceLastWriteTimeUtc);

            return ReProcess(outputFile);
        }
        finally
        {
            // Clean up temp file if conversion did not complete (rename did not happen)
            if (File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (IOException)
                {
                    Log.Warning("Failed to clean up temp file: '{FilePath}'", tempFile);
                }
            }
        }
    }

    private bool WarnDngVersion()
    {
        if (!fileInfo.Extension.Equals(".dng", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ExifToolJson.IsDngVersionNewer(_exifToolJson!.EXIFDNGVersion))
        {
            Log.Warning(
                "DNG version {DngVersion} is newer than v1.4, file may not render correctly: '{FilePath}'",
                _exifToolJson!.EXIFDNGVersion,
                fileInfo.FullName
            );
        }

        return true;
    }

    private static async Task CopyMetadataAsync(
        string sourceFile,
        string outputFile,
        CancellationToken cancellationToken = default
    )
    {
        Log.Debug(
            "exiftool: Copying metadata from '{SourcePath}' to '{DestinationPath}'",
            sourceFile,
            outputFile
        );
        _ = await Cli.Wrap("exiftool")
            .WithArguments([
                "-TagsFromFile",
                sourceFile,
                "-all:all",
                "-overwrite_original",
                outputFile,
            ])
            // Ignore errors
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
    }

    private bool ReProcess(string fileName)
    {
        // Queue file for further processing
        Log.Information("Queuing '{FilePath}' for further processing", fileName);
        reProcessNames.Add(fileName);
        _reprocess = true;
        return false;
    }

    internal static string GetBackupFileName(string fileName)
    {
        // Append .bak or .bakN to make unique backup file name
        string backupFileName = fileName + ".bak";
        int counter = 1;
        while (File.Exists(backupFileName))
        {
            backupFileName = fileName + $".bak{counter}";
            counter++;
        }
        return backupFileName;
    }

    private static string BackupFile(string fileName, bool copy)
    {
        string backupFileName = GetBackupFileName(fileName);
        if (copy)
        {
            Log.Information(
                "Copying '{SourcePath}' to '{DestinationPath}'",
                fileName,
                backupFileName
            );
            File.Copy(fileName, backupFileName, false);
        }
        else
        {
            Log.Information(
                "Renaming '{SourcePath}' to '{DestinationPath}'",
                fileName,
                backupFileName
            );
            File.Move(fileName, backupFileName, false);
        }

        return backupFileName;
    }

    private static void MoveFile(
        string sourceFileName,
        string targetFileName,
        bool skipBackup = false
    )
    {
        // Backup or delete target if it exists
        if (File.Exists(targetFileName))
        {
            if (skipBackup)
            {
                Log.Information(
                    "Deleting conflicting file (no backup): '{FilePath}'",
                    targetFileName
                );
                File.Delete(targetFileName);
            }
            else
            {
                _ = BackupFile(targetFileName, false);
            }
        }

        Log.Information(
            "Renaming '{SourcePath}' to '{DestinationPath}'",
            sourceFileName,
            targetFileName
        );
        File.Move(sourceFileName, targetFileName, false);
    }

    private string? FindCompanionImagePath()
    {
        // e.g. IMG_1234.mov -> IMG_1234.heic / IMG_1234.HEIC
        foreach (string extension in s_liveVideoImageExtensions)
        {
            string candidate = Path.ChangeExtension(fileInfo.FullName, extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string candidateUpper = Path.ChangeExtension(
                fileInfo.FullName,
                extension.ToUpperInvariant()
            );
            if (File.Exists(candidateUpper))
            {
                return candidateUpper;
            }
        }

        // e.g. IMG_1234_HEVC.mov -> IMG_1234.heic / IMG_1234.HEIC
        string nameNoExt = Path.GetFileNameWithoutExtension(fileInfo.FullName);
        if (nameNoExt.EndsWith("_hevc", StringComparison.OrdinalIgnoreCase))
        {
            string dir = fileInfo.DirectoryName!;
            string stripped = nameNoExt[..^"_hevc".Length];
            foreach (string extension in s_liveVideoImageExtensions)
            {
                string candidate = Path.Combine(dir, stripped + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                string candidateUpper = Path.Combine(dir, stripped + extension.ToUpperInvariant());
                if (File.Exists(candidateUpper))
                {
                    return candidateUpper;
                }
            }
        }

        return null;
    }

    internal static bool IsMixedCaseExtension(ReadOnlySpan<char> extension)
    {
        bool hasLower = false,
            hasUpper = false;
        foreach (char c in extension)
        {
            if (char.IsLower(c))
            {
                hasLower = true;
            }
            else if (char.IsUpper(c))
            {
                hasUpper = true;
            }
            if (hasLower && hasUpper)
            {
                return true;
            }
        }
        return false;
    }

    internal static void GetFileMediaExtension(
        string filePath,
        out string baseName,
        out string mediaExtension
    )
    {
        // Get the base name and all media extensions as one extension
        // E.g.:
        // /path/to/file.ext.heic.jpg -> "/path/to/file.ext", ".heic.jpg"
        // /path/to/file.heic.jpg.ext -> "/path/to/file.heic.jpg.ext", ""
        // /file -> "/file", ""
        // /file.ext -> "/file.ext", ""
        // /path/file.jpeg.ext -> "/path/file.jpeg.ext", ""

        string fileName = Path.GetFileName(filePath);
        string directory = Path.GetDirectoryName(filePath) ?? string.Empty;

        // Split filename by dots
        ReadOnlySpan<char> fileNameSpan = fileName.AsSpan();
        List<Range> parts = [.. fileNameSpan.Split('.')];

        // If no dots, return original path as base with empty extension
        if (parts.Count <= 1)
        {
            baseName = filePath;
            mediaExtension = string.Empty;
            return;
        }

        // Work backwards from the end, collecting consecutive media extensions
        List<string> mediaExtensions = [];
        for (int i = parts.Count - 1; i >= 1; i--)
        {
            // If this is a known media extension, add it to our collection
            ReadOnlySpan<char> partSpan = fileNameSpan[parts[i]];
            string candidateExtension = "." + new string(partSpan);
            if (MediaUtilities.SupportedExtensions.Contains(candidateExtension))
            {
                mediaExtensions.Insert(0, candidateExtension);
            }
            else
            {
                // Stop at first non-media extension
                break;
            }
        }

        // Build the results
        if (mediaExtensions.Count == 0)
        {
            // No media extensions found
            baseName = filePath;
            mediaExtension = string.Empty;
            return;
        }

        // Reconstruct base name by removing media extensions from the end
        int basePartsCount = parts.Count - mediaExtensions.Count;
        string baseFileName = string.Join(".", parts.Take(basePartsCount).Select(r => fileName[r]));
        baseName = string.IsNullOrEmpty(directory)
            ? baseFileName
            : Path.Combine(directory, baseFileName);
        mediaExtension = string.Join("", mediaExtensions);
    }
}
