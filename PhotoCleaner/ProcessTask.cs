using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Serilog;

namespace PhotoCleaner;

public class ProcessTask(
    ConcurrentBag<string> fileNames,
    ConcurrentDictionary<string, byte> unknownExtensions,
    FileInfo fileInfo,
    bool dryRun
)
{
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

    private static readonly FrozenSet<string> s_processExtensions = new[]
    {
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
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_remuxExtensions = new[]
    {
        ".mts",
        ".m2ts",
        ".mkv",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_reencodeExtensions = new[]
    {
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
        ".jpeg",
        ".jpg",
        ".mov",
        ".mp4",
        ".png",
        ".psd",
        ".tif",
        ".tiff",
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
        ".jpeg",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_quicktimeExtensions = new[]
    {
        ".mp4",
        ".mov",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_jpegExtensions = new[]
    {
        ".jpg",
        ".jpeg",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_pngExtensions = new[] { ".png" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_heicExtensions = new[]
    {
        ".heic",
        ".heif",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_heifExtensions = s_heicExtensions;
    private static readonly FrozenSet<string> s_tiffExtensions = new[]
    {
        ".tif",
        ".tiff",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_dngExtensions = new[] { ".dng" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_mp4Extensions = new[] { ".mp4" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_mkvExtensions = new[] { ".mkv" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_cr2Extensions = new[] { ".cr2" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_nefExtensions = new[] { ".nef" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_orfExtensions = new[] { ".orf" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_wmvExtensions = new[] { ".wmv" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_3gpExtensions = new[] { ".3gp" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_aviExtensions = new[] { ".avi" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_gifExtensions = new[] { ".gif" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly FrozenSet<string> s_psdExtensions = new[] { ".psd" }.ToFrozenSet(
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly Dictionary<string, FrozenSet<string>> s_mimeTypeExtensions = new()
    {
        { "application/vnd.adobe.photoshop", s_psdExtensions },
        { "image/gif", s_gifExtensions },
        { "image/heic", s_heicExtensions },
        { "image/heif", s_heifExtensions },
        { "image/jpeg", s_jpegExtensions },
        { "image/png", s_pngExtensions },
        { "image/tiff", s_tiffExtensions },
        { "image/x-adobe-dng", s_dngExtensions },
        { "image/x-canon-cr2", s_cr2Extensions },
        { "image/x-nikon-nef", s_nefExtensions },
        { "image/x-olympus-orf", s_orfExtensions },
        { "video/3gpp", s_3gpExtensions },
        { "video/mp4", s_mp4Extensions },
        { "video/quicktime", s_quicktimeExtensions },
        { "video/x-matroska", s_mkvExtensions },
        { "video/x-ms-asf", s_wmvExtensions },
        { "video/x-msvideo", s_aviExtensions },
    };

    public static ProcessResult Execute(
        ConcurrentBag<string> fileNames,
        ConcurrentDictionary<string, byte> unknownExtensions,
        FileInfo fileInfo,
        bool dryRun
    )
    {
        ProcessTask processTask = new(fileNames, unknownExtensions, fileInfo, dryRun);
        return processTask.ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<ProcessResult> ExecuteAsync()
    {
        // Skip non-media files
        if (!s_processExtensions.Contains(fileInfo.Extension))
        {
            if (!unknownExtensions.ContainsKey(fileInfo.Extension))
            {
                Log.Warning("Skipping non-media file: '{FileName}'.", fileInfo.FullName);
                _ = unknownExtensions.TryAdd(fileInfo.Extension, 0);
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
        bool hasLower = false,
            hasUpper = false;
        foreach (char c in fileInfo.Extension)
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
                break;
            }
        }
        if (!(hasLower && hasUpper))
        {
            return true;
        }

        Log.Warning(
            "Mixed case extension detected '{Extension}': '{FileName}'.",
            fileInfo.Extension,
            fileInfo.FullName
        );
        if (IsDryRun())
        {
            return false;
        }

        // Rename using lowercase extensions
        _modified = true;
        string outputFile = Path.ChangeExtension(fileInfo.FullName, fileInfo.Extension.ToLower());
        MoveFile(fileInfo.FullName, outputFile);

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
            // Add the missing type?
            Log.Error(
                "Unknown MIME type '{MimeType}' for file: '{FileName}'.",
                _exifToolJson!.MIMEType,
                fileInfo.FullName
            );
            return false;
        }

        // Get the normalized media extension for the file
        GetFileMediaExtension(fileInfo.FullName, out string baseName, out string mediaExtensions);

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
                fileInfo.FullName
            );
        }
        // Is it the preferred extension for this MIME type?
        else if (
            !mediaExtensions.Equals(
                expectedExtensions.First(),
                StringComparison.CurrentCultureIgnoreCase
            )
        )
        {
            rename = true;
            Log.Warning(
                "File extension '{Extension}' is not preferred for MIME type '{MimeType}' '{Extensions}': '{FileName}'.",
                mediaExtensions,
                _exifToolJson!.MIMEType,
                expectedExtensions,
                fileInfo.FullName
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
        MoveFile(fileInfo.FullName, outputFile);

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
                fileInfo.FullName,
            ])
            .ExecuteBufferedAsync();
        return result
            .StandardOutput.Trim()
            .StartsWith("pcm", StringComparison.CurrentCultureIgnoreCase);
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
                fileInfo.FullName,
            ])
            .ExecuteBufferedAsync();
        return float.Parse(result.StandardOutput.Trim());
    }

    private async Task<bool> DeleteLivePhotosAsync()
    {
        if (!s_liveVideoExtensions.Contains(fileInfo.Extension))
        {
            return true;
        }

        // Long videos
        float duration = await GetDurationAsync();
        if (duration > 3.0)
        {
            return true;
        }

        // Very short videos, always delete
        if (duration <= 0.5)
        {
            Log.Warning(
                "Deleting {Duration}s short video clip: '{FileName}'.",
                duration,
                fileInfo.FullName
            );
            if (IsDryRun())
            {
                return false;
            }

            _modified = true;
            BackupFile(fileInfo.FullName, false);
            return false;
        }

        // Live photos, <=3s short videos with matching HEIC or JPEG file
        if (
            !s_liveVideoImageExtensions.Any(extension =>
                File.Exists(Path.ChangeExtension(fileInfo.FullName, extension.ToLower()))
                || File.Exists(Path.ChangeExtension(fileInfo.FullName, extension.ToUpper()))
            )
        )
        {
            return true;
        }
        if (IsDryRun())
        {
            return false;
        }

        _modified = true;
        Log.Warning(
            "Deleting {Duration}s video clip with matching image file: '{FileName}'.",
            duration,
            fileInfo.FullName
        );
        BackupFile(fileInfo.FullName, false);
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
                "Remuxing audio and video by file extension: '{FileName}'.",
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
                "Reencode audio and video by file extension: '{FileName}'.",
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
            if (!await IsPcmAudioAsync())
            {
                return true;
            }

            // Reencode audio and remux video
            Log.Information("Reencode PCM audio and remux video: '{FileName}'.", fileInfo.FullName);
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
        BackupFile(fileInfo.FullName, false);

        // Rename temp output to MP4
        string outputFile = Path.ChangeExtension(fileInfo.FullName, ".mp4");
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
        if (!s_setdateExtensions.Contains(fileInfo.Extension))
        {
            // Not supported for this file type
            return true;
        }

        // Try to infer the date from the path
        string createdDate = "";
        if (!DateFromPath.InferCreatedDate(fileInfo.FullName, ref createdDate))
        {
            // No date inferred from path
            Log.Warning("Missing created date: '{FileName}'.", fileInfo.FullName);
            return true;
        }
        Log.Information(
            "Inferred created date from path '{CreatedDate}': '{FileName}'.",
            createdDate,
            fileInfo.FullName
        );
        if (IsDryRun())
        {
            return false;
        }

        // Backup original file and keep original
        _modified = true;
        BackupFile(fileInfo.FullName, true);

        // Set the created date using exiftool
        return await SetCreateDateAsync(fileInfo.FullName, createdDate)
            && ReProcess(fileInfo.FullName);
    }

    private static async Task<bool> SetCreateDateAsync(string outputFile, string createdDate)
    {
        // Set the created date using exiftool
        // Output file will be overwritten
        string[] arguments = s_quicktimeExtensions.Contains(Path.GetExtension(outputFile).ToLower())
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
            .WithArguments(["-groupNames", "-json", fileInfo.FullName])
            .ExecuteBufferedAsync();
        string json = result.StandardOutput.Trim(' ', '\n', '\r', ' ', '[', ']');
        return JsonSerializer.Deserialize(json, SourceGenerationContext.Default.ExifToolJson);
    }

    private bool ReProcess(string fileName)
    {
        // Queue file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", fileName);
        fileNames.Add(fileName);
        _reprocess = true;
        return false;
    }

    private static string GetBackupFileName(string originalFileName)
    {
        string backupFileName = originalFileName + ".bak";
        int counter = 1;
        while (File.Exists(backupFileName))
        {
            backupFileName = originalFileName + $".bak{counter}";
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
        string directory = Path.GetDirectoryName(filePath) ?? "";

        // Split filename by dots
        string[] parts = fileName.Split('.');

        // If no dots, return original path as base with empty extension
        if (parts.Length <= 1)
        {
            baseName = filePath;
            mediaExtension = "";
            return;
        }

        // Work backwards from the end, collecting consecutive media extensions
        List<string> mediaExtensions = [];
        for (int i = parts.Length - 1; i >= 1; i--)
        {
            // If this is a known media extension, add it to our collection
            string candidateExtension = "." + parts[i].ToLower();
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
            mediaExtension = "";
            return;
        }

        // Reconstruct base name by removing media extensions from the end
        string[] baseParts = parts[..^mediaExtensions.Count];
        string baseFileName = string.Join(".", baseParts);
        baseName = string.IsNullOrEmpty(directory)
            ? baseFileName
            : Path.Combine(directory, baseFileName);
        mediaExtension = string.Join("", mediaExtensions);
    }

    private bool IsDryRun(
        [System.Runtime.CompilerServices.CallerMemberName] string function = "unknown"
    )
    {
        if (dryRun)
        {
            Log.Information("Dry run enabled, skipping action in {Function}.", function);
        }
        return dryRun;
    }
}
