using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;

namespace PhotoCleaner;

internal sealed class ProcessTask(ProcessTask.Context processContext)
{
    internal sealed class Context
    {
        public required ConcurrentBag<string> ReProcessNames { get; init; }
        public required ConcurrentDictionary<string, byte> UnknownExtensions { get; init; }
        public required FileInfo FileInfo { get; init; }
        public required bool DryRun { get; init; }
        public required bool DateFromPath { get; init; }
        public required bool SkipBackup { get; init; }
    }

    public enum ProcessResult
    {
        Success,
        Failure,
        Deleted,
        Reprocess,
        Modified,
        UnknownExtension,
    }

    internal const double LiveVideoDuration = 4.0;
    internal const double ShortVideoDuration = 1.0;

    private ExifToolJson? _exifToolJson;
    private bool _modified;
    private bool _reprocess;
    private bool _deleted;

    internal static FrozenSet<string> SupportedExtensions { get; } =
        new[]
        {
            ".3gp",
            ".arw",
            ".asf",
            ".avi",
            ".cr2",
            ".dng",
            ".gif",
            ".heic",
            ".heif",
            ".jpeg",
            ".jpg",
            ".m2t",
            ".m2ts",
            ".mkv",
            ".mov",
            ".mp4",
            ".mts",
            ".nef",
            ".orf",
            ".png",
            ".psd",
            ".rw2",
            ".tif",
            ".tiff",
            ".wmv",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal const string VideoOutputExtension = ".mp4";

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
    private static readonly FrozenSet<string> s_setdateExtensions = new[]
    {
        ".heic",
        ".heif",
        ".jpg",
        ".mov",
        ".mp4",
        ".png",
        ".psd",
        ".tif",
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
    private static readonly FrozenSet<string> s_quicktimeExtensions = new[]
    {
        ".mp4",
        ".mov",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static Task<ProcessResult> ExecuteAsync(Context processContext)
    {
        ProcessTask processTask = new(processContext);
        return processTask.ExecuteAsync();
    }

    private async Task<ProcessResult> ExecuteAsync()
    {
        // Skip files that no longer exist
        if (!processContext.FileInfo.Exists)
        {
            Log.Warning(
                "Skipping file that no longer exists: '{FilePath}'",
                processContext.FileInfo.FullName
            );
            return ProcessResult.Success;
        }

        // Skip non-media files
        if (!SupportedExtensions.Contains(processContext.FileInfo.Extension))
        {
            Log.Debug("Skipping non-media file: '{FilePath}'", processContext.FileInfo.FullName);
            if (!processContext.UnknownExtensions.ContainsKey(processContext.FileInfo.Extension))
            {
                _ = processContext.UnknownExtensions.TryAdd(processContext.FileInfo.Extension, 0);
            }
            return ProcessResult.UnknownExtension;
        }

        // Get exiftool info
        _exifToolJson = await GetExifToolJsonAsync(processContext.FileInfo.FullName)
            .ConfigureAwait(false);

        // Process files
        return
            !RenameMismatchedMimeExtensions()
            || !RenameMixedCaseExtensions()
            || !await DeleteLivePhotosAsync().ConfigureAwait(false)
            || !await ConvertVideoAsync().ConfigureAwait(false)
            || !await SetMissingCreateDateAsync().ConfigureAwait(false)
            || !WarnDngVersion()
            ? _reprocess
                ? ProcessResult.Reprocess
                : _deleted
                    ? ProcessResult.Deleted
                    : ProcessResult.Failure
            : _modified
                ? ProcessResult.Modified
                : ProcessResult.Success;
    }

    private bool RenameMixedCaseExtensions()
    {
        if (!IsMixedCaseExtension(processContext.FileInfo.Extension.AsSpan()))
        {
            return true;
        }

        Log.Warning(
            "Mixed case extension detected '{Extension}': '{FilePath}'",
            processContext.FileInfo.Extension,
            processContext.FileInfo.FullName
        );
        if (IsDryRun())
        {
            return false;
        }

        // Rename using lowercase extensions
        _modified = true;
        string outputFile = Path.ChangeExtension(
            processContext.FileInfo.FullName,
            processContext.FileInfo.Extension.ToLowerInvariant()
        );
        MoveFile(processContext.FileInfo.FullName, outputFile, processContext.SkipBackup);

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
                processContext.FileInfo.FullName
            );
            return true;
        }
        Log.Debug(
            "Exiftool MIME details for '{FilePath}': '{FileDetails}'",
            processContext.FileInfo.FullName,
            _exifToolJson.FileDetails
        );

        // Only rename if extensions is in process list
        string expectedExtension = "." + _exifToolJson!.FileTypeExtension.ToLowerInvariant();
        if (!SupportedExtensions.Contains(expectedExtension))
        {
            Log.Warning(
                "Exiftool FileTypeExtension '{ExpectedExtension}' is not in process list for '{FilePath}'; skipping extension check",
                expectedExtension,
                processContext.FileInfo.FullName
            );
            return true;
        }

        // Get the normalized media extension for the file
        GetFileMediaExtension(
            processContext.FileInfo.FullName,
            out string baseName,
            out string mediaExtension
        );
        if (string.Equals(mediaExtension, expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Log.Warning(
            "File extension '{Extension}' does not match exiftool expected '{Expected}': '{FilePath}'",
            mediaExtension,
            expectedExtension,
            processContext.FileInfo.FullName
        );
        if (IsDryRun())
        {
            return false;
        }

        // Rename extension to match exiftool's canonical extension
        // Note; no backup is taken, undo cannot restore
        _modified = true;
        string outputFile = baseName + expectedExtension;
        MoveFile(processContext.FileInfo.FullName, outputFile, processContext.SkipBackup);

        // Queue renamed file for further processing
        return ReProcess(outputFile);
    }

    private async Task<bool> IsPcmAudioAsync()
    {
        Log.Debug(
            "ffprobe: Checking for PCM audio stream in '{FilePath}'",
            processContext.FileInfo.FullName
        );
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
                processContext.FileInfo.FullName,
            ])
            .ExecuteBufferedAsync();
        return result
            .StandardOutput.AsSpan()
            .Trim()
            .StartsWith("pcm", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<float> GetDurationAsync()
    {
        Log.Debug(
            "ffprobe: Getting play duration for '{FilePath}'",
            processContext.FileInfo.FullName
        );
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
                processContext.FileInfo.FullName,
            ])
            .ExecuteBufferedAsync();
        return float.Parse(result.StandardOutput.AsSpan().Trim(), CultureInfo.InvariantCulture);
    }

    private async Task<bool> DeleteLivePhotosAsync()
    {
        // Live photos are MOV or MP4 about ~3s long with a matching HEIC or JPEG file
        if (!s_liveVideoExtensions.Contains(processContext.FileInfo.Extension))
        {
            return true;
        }

        // Short videos, always delete
        float duration = await GetDurationAsync().ConfigureAwait(false);
        if (duration <= ShortVideoDuration)
        {
            Log.Warning(
                "Deleting {Duration}s short video clip: '{FilePath}'",
                duration,
                processContext.FileInfo.FullName
            );
            if (IsDryRun())
            {
                return false;
            }

            _modified = true;
            _deleted = true;
            if (processContext.SkipBackup)
            {
                File.Delete(processContext.FileInfo.FullName);
            }
            else
            {
                _ = BackupFile(processContext.FileInfo.FullName, false);
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
        if (duration >= LiveVideoDuration)
        {
            // Warn in case the video length threshold needs adjustment
            Log.Warning(
                "Long {Duration}s video clip has matching image file: '{FilePath}'",
                duration,
                processContext.FileInfo.FullName
            );
            return true;
        }

        // Verify ContentIdentifier tags match to confirm this is a live photo pair
        string? videoContentId = _exifToolJson?.ContentIdentifier;
        string? imageContentId = (
            await GetExifToolJsonAsync(companionPath).ConfigureAwait(false)
        )?.ContentIdentifier;
        Log.Debug(
            "ContentIdentifier tag for video: '{FilePath}' = '{ContentIdentifier}'",
            processContext.FileInfo.FullName,
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
                processContext.FileInfo.FullName
            );
            return true;
        }

        if (!string.Equals(videoContentId, imageContentId, StringComparison.Ordinal))
        {
            Log.Warning(
                "ContentIdentifier mismatch, not a live photo pair: '{FilePath}'",
                processContext.FileInfo.FullName
            );
            return true;
        }

        // Delete live video with confirmed matching image
        if (IsDryRun())
        {
            return false;
        }

        _modified = true;
        _deleted = true;
        Log.Warning(
            "Deleting {Duration}s live photo video with matching image file: '{FilePath}'",
            duration,
            processContext.FileInfo.FullName
        );
        if (processContext.SkipBackup)
        {
            File.Delete(processContext.FileInfo.FullName);
        }
        else
        {
            _ = BackupFile(processContext.FileInfo.FullName, false);
        }
        return false;
    }

    private async Task<bool> ConvertVideoAsync()
    {
        // Output to temp file
        string tempFile = Path.ChangeExtension(processContext.FileInfo.FullName, ".temp");
        string[] ffmpegArguments;
        if (s_remuxExtensions.Contains(processContext.FileInfo.Extension))
        {
            // Remux audio and video
            Log.Information(
                "ffmpeg: Remuxing audio and video by file extension: '{FilePath}'",
                processContext.FileInfo.FullName
            );
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                processContext.FileInfo.FullName,
                "-c",
                "copy",
                "-movflags",
                "+faststart",
                "-f",
                "mp4",
                tempFile,
            ];
        }
        else if (s_reencodeExtensions.Contains(processContext.FileInfo.Extension))
        {
            // Reencode audio and video
            Log.Information(
                "ffmpeg: Reencode audio and video by file extension: '{FilePath}'",
                processContext.FileInfo.FullName
            );
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                processContext.FileInfo.FullName,
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
        else if (s_reencodeAudioExtensions.Contains(processContext.FileInfo.Extension))
        {
            // Only if audio is PCM
            if (!await IsPcmAudioAsync().ConfigureAwait(false))
            {
                return true;
            }

            // Reencode audio and remux video
            Log.Information(
                "ffmpeg: Reencode PCM audio and remux video: '{FilePath}'",
                processContext.FileInfo.FullName
            );
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                processContext.FileInfo.FullName,
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
        if (IsDryRun())
        {
            return false;
        }

        // Delete temp output if it exists
        if (File.Exists(tempFile))
        {
            File.Delete(tempFile);
        }

        // Run ffmpeg
        _ = await Cli.Wrap("ffmpeg").WithArguments(ffmpegArguments).ExecuteBufferedAsync();

        // Backup original file (or keep it on disk if skipbackup, to read metadata from)
        _modified = true;
        string sourceForMetadata = processContext.SkipBackup
            ? processContext.FileInfo.FullName
            : BackupFile(processContext.FileInfo.FullName, false);

        // Rename temp output to MP4; use a unique name if target already exists
        string outputFile = GetUniqueFileName(
            Path.ChangeExtension(processContext.FileInfo.FullName, VideoOutputExtension)
        );
        if (!processContext.SkipBackup)
        {
            await File.WriteAllTextAsync(sourceForMetadata + ".out", outputFile)
                .ConfigureAwait(false);
        }
        MoveFile(tempFile, outputFile, processContext.SkipBackup);

        // Copy compatible metadata from the original to the converted file
        await CopyMetadataAsync(sourceForMetadata, outputFile).ConfigureAwait(false);
        if (processContext.SkipBackup)
        {
            Log.Information("Deleting original after conversion: '{FilePath}'", sourceForMetadata);
            File.Delete(sourceForMetadata);
        }

        // Set timestamps on remuxed file from original timestamps
        string? createdDate = _exifToolJson!.GetDateString();
        if (!string.IsNullOrEmpty(createdDate))
        {
            await SetCreateDateAsync(createdDate, outputFile).ConfigureAwait(false);
        }

        return ReProcess(outputFile);
    }

    private async Task<bool> SetMissingCreateDateAsync()
    {
        // Already have a date
        if (_exifToolJson!.IsDateSet())
        {
            return true;
        }
        Log.Warning("Created date is missing: '{FilePath}'", processContext.FileInfo.FullName);

        // Date inference from path is opt-in
        if (!processContext.DateFromPath)
        {
            return true;
        }

        // Only some file types are supported
        if (!s_setdateExtensions.Contains(processContext.FileInfo.Extension))
        {
            Log.Warning(
                "Setting created date not supported for file type: '{FilePath}'",
                processContext.FileInfo.FullName
            );
            return true;
        }

        // Try to infer the date from the path
        string createdDate = string.Empty;
        if (!DateFromPath.InferCreatedDate(processContext.FileInfo.FullName, ref createdDate))
        {
            Log.Warning(
                "Failed to infer date from path: '{FilePath}'",
                processContext.FileInfo.FullName
            );
            return true;
        }

        Log.Information(
            "Inferred created date from path '{CreatedDate}': '{FilePath}'",
            createdDate,
            processContext.FileInfo.FullName
        );
        if (IsDryRun())
        {
            return false;
        }

        // Backup original file (skip if --skipbackup; file is modified in-place either way)
        _modified = true;
        if (!processContext.SkipBackup)
        {
            _ = BackupFile(processContext.FileInfo.FullName, true);
        }

        // Set the created date using exiftool
        await SetCreateDateAsync(createdDate, processContext.FileInfo.FullName)
            .ConfigureAwait(false);

        return ReProcess(processContext.FileInfo.FullName);
    }

    private bool WarnDngVersion()
    {
        if (!processContext.FileInfo.Extension.Equals(".dng", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ExifToolJson.IsDngVersionNewer(_exifToolJson!.EXIFDNGVersion))
        {
            Log.Warning(
                "DNG version {DngVersion} is newer than v1.4, file may not render correctly: '{FilePath}'",
                _exifToolJson!.EXIFDNGVersion,
                processContext.FileInfo.FullName
            );
        }

        return true;
    }

    private static async Task CopyMetadataAsync(string sourceFile, string outputFile)
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
            .ExecuteBufferedAsync();
    }

    private static async Task SetCreateDateAsync(string createdDate, string outputFile)
    {
        // Use tag appropriate to file type to set date
        Log.Debug(
            "exiftool: Setting created date '{CreatedDate}' on '{DestinationPath}'",
            createdDate,
            outputFile
        );
        string[] arguments = s_quicktimeExtensions.Contains(Path.GetExtension(outputFile))
            ?
            [
                "-overwrite_original",
                $"-QuickTime:CreateDate={createdDate}",
                $"-QuickTime:ModifyDate={createdDate}",
                outputFile,
            ]
            :
            [
                "-overwrite_original",
                $"-EXIF:CreateDate={createdDate}",
                $"-EXIF:DateTimeOriginal={createdDate}",
                outputFile,
            ];
        _ = await Cli.Wrap("exiftool").WithArguments(arguments).ExecuteBufferedAsync();
    }

    internal static async Task<ExifToolJson?> GetExifToolJsonAsync(string filePath)
    {
        // Get exiftool info
        Log.Debug("exiftool: Getting metadata for '{FilePath}'", filePath);
        BufferedCommandResult result = await Cli.Wrap("exiftool")
            .WithArguments(["-groupNames", "-json", filePath])
            .ExecuteBufferedAsync();
        ExifToolJson? exifToolJson = JsonSerializer.Deserialize(
            result.StandardOutput.AsSpan().Trim([' ', '\n', '\r', '[', ']']),
            SourceGenerationContext.Default.ExifToolJson
        );
        ArgumentNullException.ThrowIfNull(exifToolJson);
        return exifToolJson;
    }

    private bool ReProcess(string fileName)
    {
        // Queue file for further processing
        Log.Information("Queuing '{FilePath}' for further processing", fileName);
        processContext.ReProcessNames.Add(fileName);
        _reprocess = true;
        return false;
    }

    internal static string GetUniqueFileName(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return filePath;
        }

        string directory = Path.GetDirectoryName(filePath)!;
        string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);
        int counter = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{nameWithoutExt}_{counter}{ext}");
            counter++;
        } while (File.Exists(candidate));
        return candidate;
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
            string candidate = Path.ChangeExtension(processContext.FileInfo.FullName, extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string candidateUpper = Path.ChangeExtension(
                processContext.FileInfo.FullName,
                extension.ToUpperInvariant()
            );
            if (File.Exists(candidateUpper))
            {
                return candidateUpper;
            }
        }

        // e.g. IMG_1234_HEVC.mov -> IMG_1234.heic / IMG_1234.HEIC
        string nameNoExt = Path.GetFileNameWithoutExtension(processContext.FileInfo.FullName);
        if (nameNoExt.EndsWith("_hevc", StringComparison.OrdinalIgnoreCase))
        {
            string dir = processContext.FileInfo.DirectoryName!;
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
            if (SupportedExtensions.Contains(candidateExtension))
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

    private bool IsDryRun([CallerMemberName] string function = "unknown")
    {
        if (processContext.DryRun)
        {
            Log.Verbose("Dry run enabled, skipping action in {Function}", function);
        }
        return processContext.DryRun;
    }
}
