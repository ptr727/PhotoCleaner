using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Serilog;

namespace PhotoCleaner;

public class ProcessTask(ProcessTask.Context processContext)
{
    public class Context
    {
        public required ConcurrentBag<string> ReProcessNames { get; init; }
        public required ConcurrentDictionary<string, byte> UnknownExtensions { get; init; }
        public required FileInfo FileInfo { get; init; }
        public required bool DryRun { get; init; }
    }

    public enum ProcessResult
    {
        Success,
        Failure,
        Reprocess,
        Modified,
        UnknownExtension,
        DoubleExtensions,
    }

    private ExifToolJson? _exifToolJson;
    private bool _modified;
    private bool _reprocess;

    private static readonly FrozenSet<string> s_processExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [
            ".3gp",
            ".arw",
            ".avi",
            ".cr2",
            ".dng",
            ".gif",
            ".heic",
            ".heif",
            ".jpeg",
            ".jpg",
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
        ]
    );
    private static readonly FrozenSet<string> s_remuxExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".mts", ".m2ts", ".mkv"]
    );
    private static readonly FrozenSet<string> s_reencodeExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".wmv", ".avi", ".3gp", ".gif"]
    );
    private static readonly FrozenSet<string> s_reencodeAudioExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".mov", ".mp4"]
    );
    private static readonly FrozenSet<string> s_setdateExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".heic", ".heif", ".jpeg", ".jpg", ".mov", ".mp4", ".png", ".psd", ".tif", ".tiff"]
    );
    private static readonly FrozenSet<string> s_liveVideoExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".mp4", ".mov"]
    );
    private static readonly FrozenSet<string> s_liveVideoImageExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".heic", ".jpg", ".jpeg"]
    );
    private static readonly FrozenSet<string> s_quicktimeExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".mp4", ".mov"]
    );
    private static readonly FrozenSet<string> s_jpegExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".jpg", ".jpeg"]
    );
    private static readonly FrozenSet<string> s_heicExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".heic", ".heif"]
    );
    private static readonly FrozenSet<string> s_tiffExtensions = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        [".tif", ".tiff"]
    );

    private static readonly FrozenDictionary<string, FrozenSet<string>> s_mimeTypeExtensions =
        new Dictionary<string, FrozenSet<string>>
        {
            {
                "application/vnd.adobe.photoshop",
                FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".psd"])
            },
            { "image/gif", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".gif"]) },
            { "image/heic", s_heicExtensions },
            { "image/heif", s_heicExtensions },
            { "image/jpeg", s_jpegExtensions },
            { "image/png", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".png"]) },
            { "image/tiff", s_tiffExtensions },
            { "image/x-adobe-dng", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".dng"]) },
            { "image/x-canon-cr2", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".cr2"]) },
            { "image/x-nikon-nef", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".nef"]) },
            { "image/x-olympus-orf", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".orf"]) },
            { "video/3gpp", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".3gp"]) },
            { "video/mp4", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".mp4"]) },
            { "video/quicktime", s_quicktimeExtensions },
            { "video/x-matroska", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".mkv"]) },
            { "video/x-ms-asf", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".wmv"]) },
            { "video/x-msvideo", FrozenSet.Create(StringComparer.OrdinalIgnoreCase, [".avi"]) },
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public async Task<ProcessResult> ExecuteAsync()
    {
        // Skip non-media files
        if (!s_processExtensions.Contains(processContext.FileInfo.Extension))
        {
            Log.Debug("Skipping non-media file: '{FileName}'.", processContext.FileInfo.FullName);
            if (!processContext.UnknownExtensions.ContainsKey(processContext.FileInfo.Extension))
            {
                _ = processContext.UnknownExtensions.TryAdd(processContext.FileInfo.Extension, 0);
            }
            return ProcessResult.UnknownExtension;
        }

        // Get exiftool info
        _exifToolJson = await GetExifToolJsonAsync();
        ArgumentNullException.ThrowIfNull(_exifToolJson);

        // Process files
        return
            !RenameMismatchedMimeExtensions()
            || !RenameMixedCaseExtensions()
            || !await DeleteLivePhotosAsync()
            || !await ConvertVideoAsync()
            || !await SetMissingCreateDateAsync()
            ? _reprocess
                ? ProcessResult.Reprocess
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
            "Mixed case extension detected '{Extension}': '{FileName}'.",
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
        MoveFile(processContext.FileInfo.FullName, outputFile);

        // Queue renamed file for further processing
        return ReProcess(outputFile);
    }

    private bool RenameMismatchedMimeExtensions()
    {
        // Lookup extension list from MIME type
        if (
            !s_mimeTypeExtensions.TryGetValue(
                _exifToolJson!.MIMEType!,
                out FrozenSet<string>? expectedExtensions
            )
        )
        {
            // Add MIME type to list
            throw new InvalidOperationException(
                $"Unknown MIME type '{_exifToolJson!.MIMEType}' for file: '{processContext.FileInfo.FullName}'."
            );
        }

        // Get the normalized media extension for the file
        GetFileMediaExtension(
            processContext.FileInfo.FullName,
            out string baseName,
            out string mediaExtensions
        );

        // Does the extension match the MIME type?
        bool rename = false;
        if (!expectedExtensions.Contains(mediaExtensions))
        {
            rename = true;
            Log.Warning(
                "File extension '{Extension}' does not match MIME type '{MimeType}' '{Extensions}': '{FileName}'.",
                mediaExtensions,
                _exifToolJson!.MIMEType,
                expectedExtensions,
                processContext.FileInfo.FullName
            );
        }
        // Is it the preferred extension for this MIME type?
        else if (
            !mediaExtensions.Equals(expectedExtensions.First(), StringComparison.OrdinalIgnoreCase)
        )
        {
            rename = true;
            Log.Warning(
                "File extension '{Extension}' is not preferred for MIME type '{MimeType}' '{Extensions}': '{FileName}'.",
                mediaExtensions,
                _exifToolJson!.MIMEType,
                expectedExtensions,
                processContext.FileInfo.FullName
            );
        }
        if (!rename)
        {
            return true;
        }
        if (IsDryRun())
        {
            return false;
        }

        // Rename extension to match MIME type
        _modified = true;
        string outputFile = baseName + expectedExtensions.First();
        MoveFile(processContext.FileInfo.FullName, outputFile);

        // Queue renamed file for further processing
        return ReProcess(outputFile);
    }

    private async Task<bool> IsPcmAudioAsync()
    {
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
        // Get duration in fractions of a second
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
        return float.Parse(result.StandardOutput.AsSpan().Trim());
    }

    private async Task<bool> DeleteLivePhotosAsync()
    {
        // Live photos are MOV or MP4 about ~3s long with a matching HEIC or JPEG file
        const double liveVideoDuration = 4.0;
        const double shortVideoDuration = 1.0;
        if (!s_liveVideoExtensions.Contains(processContext.FileInfo.Extension))
        {
            return true;
        }

        // Get duration
        float duration = await GetDurationAsync();

        // Short videos, always delete
        if (duration <= shortVideoDuration)
        {
            Log.Warning(
                "Deleting {Duration}s short video clip: '{FileName}'.",
                duration,
                processContext.FileInfo.FullName
            );
            if (IsDryRun())
            {
                return false;
            }

            _modified = true;
            BackupFile(processContext.FileInfo.FullName, false);
            return false;
        }

        // No matching image then skip
        if (!IsVideoMatchingImageExtension())
        {
            return true;
        }

        // Long videos
        if (duration >= liveVideoDuration)
        {
            // Warn in case the video length threshold needs adjustment
            Log.Warning(
                "Long {Duration}s video clip has matching image file: '{FileName}'.",
                duration,
                processContext.FileInfo.FullName
            );
            return true;
        }

        // Delete live video with matching image
        if (IsDryRun())
        {
            return false;
        }

        _modified = true;
        Log.Warning(
            "Deleting {Duration}s video clip with matching image file: '{FileName}'.",
            duration,
            processContext.FileInfo.FullName
        );
        BackupFile(processContext.FileInfo.FullName, false);
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
                "Remuxing audio and video by file extension: '{FileName}'.",
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
                "Reencode audio and video by file extension: '{FileName}'.",
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
            if (!await IsPcmAudioAsync())
            {
                return true;
            }

            // Reencode audio and remux video
            Log.Information(
                "Reencode PCM audio and remux video: '{FileName}'.",
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

        // Backup original file
        _modified = true;
        BackupFile(processContext.FileInfo.FullName, false);

        // Rename temp output to MP4
        string outputFile = Path.ChangeExtension(processContext.FileInfo.FullName, ".mp4");
        MoveFile(tempFile, outputFile);

        // Set timestamps on remuxed file from original timestamps
        string? createdDate = _exifToolJson!.GetDateString();
        return !string.IsNullOrEmpty(createdDate)
            ? await SetCreateDateAsync(outputFile, createdDate) && ReProcess(outputFile)
            : ReProcess(outputFile);
    }

    private async Task<bool> SetMissingCreateDateAsync()
    {
        // Already have a date
        if (_exifToolJson!.IsDateSet())
        {
            return true;
        }

        // Only some file types are supported
        if (!s_setdateExtensions.Contains(processContext.FileInfo.Extension))
        {
            // Not supported for this file type
            return true;
        }

        // Try to infer the date from the path
        string createdDate = string.Empty;
        if (!DateFromPath.InferCreatedDate(processContext.FileInfo.FullName, ref createdDate))
        {
            // No date inferred from path
            Log.Warning("Missing created date: '{FileName}'.", processContext.FileInfo.FullName);
            return true;
        }
        Log.Information(
            "Inferred created date from path '{CreatedDate}': '{FileName}'.",
            createdDate,
            processContext.FileInfo.FullName
        );
        if (IsDryRun())
        {
            return false;
        }

        // Backup original file and keep original
        _modified = true;
        BackupFile(processContext.FileInfo.FullName, true);

        // Set the created date using exiftool
        return await SetCreateDateAsync(processContext.FileInfo.FullName, createdDate)
            && ReProcess(processContext.FileInfo.FullName);
    }

    private static async Task<bool> SetCreateDateAsync(string outputFile, string createdDate)
    {
        // Set the created date using exiftool
        // Output file will be overwritten
        string[] arguments = s_quicktimeExtensions.Contains(Path.GetExtension(outputFile))
            ?
            [
                // "-v2",
                "-overwrite_original",
                $"-QuickTime:CreateDate={createdDate}",
                $"-QuickTime:ModifyDate={createdDate}",
                outputFile,
            ]
            :
            [
                // "-v2",
                "-overwrite_original",
                $"-EXIF:CreateDate={createdDate}",
                $"-EXIF:DateTimeOriginal={createdDate}",
                outputFile,
            ];
        _ = await Cli.Wrap("exiftool").WithArguments(arguments).ExecuteBufferedAsync();
        return true;
    }

    private async Task<ExifToolJson?> GetExifToolJsonAsync()
    {
        // Get exiftool info
        BufferedCommandResult result = await Cli.Wrap("exiftool")
            .WithArguments(["-groupNames", "-json", processContext.FileInfo.FullName])
            .ExecuteBufferedAsync();
        return JsonSerializer.Deserialize(
            result.StandardOutput.AsSpan().Trim([' ', '\n', '\r', '[', ']']),
            ExifToolJsonContext.Default.ExifToolJson
        );
    }

    private bool ReProcess(string fileName)
    {
        // Queue file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", fileName);
        processContext.ReProcessNames.Add(fileName);
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

    private static void BackupFile(string fileName, bool copy)
    {
        string backupFileName = GetBackupFileName(fileName);
        if (copy)
        {
            Log.Information(
                "Copying '{OldFileName}' to '{NewFileName}' ...",
                fileName,
                backupFileName
            );
            File.Copy(fileName, backupFileName, false);
        }
        else
        {
            Log.Information(
                "Renaming '{OldFileName}' to '{NewFileName}' ...",
                fileName,
                backupFileName
            );
            File.Move(fileName, backupFileName, false);
        }
    }

    private static void MoveFile(string sourceFileName, string targetFileName)
    {
        // Backup target if it exists
        if (File.Exists(targetFileName))
        {
            BackupFile(targetFileName, false);
        }

        Log.Information(
            "Renaming '{OldFileName}' to '{NewFileName}' ...",
            sourceFileName,
            targetFileName
        );
        File.Move(sourceFileName, targetFileName, false);
    }

    private bool IsVideoMatchingImageExtension() =>
        // Find matching HEIC or JPEG file
        // Extension on disk must be all lowercase or all uppercase
        // Static extension list is already lowercase
        s_liveVideoImageExtensions.Any(extension =>
            File.Exists(Path.ChangeExtension(processContext.FileInfo.FullName, extension))
            || File.Exists(
                Path.ChangeExtension(processContext.FileInfo.FullName, extension.ToUpper())
            )
        );

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
            if (s_processExtensions.Contains(candidateExtension))
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
            Log.Verbose("Dry run enabled, skipping action in {Function}.", function);
        }
        return processContext.DryRun;
    }
}
