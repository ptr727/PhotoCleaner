using System.Collections.Concurrent;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using Serilog;

namespace PhotoCleaner;

public class ProcessTask(
    ConcurrentBag<string> fileNameBag,
    List<string> unknownExtensionsList,
    Lock unknownExtensionsListLock,
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

    private static readonly string[] s_processExtensions =
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
    ];
    private static readonly string[] s_remuxExtensions = [".mts", ".m2ts", ".mkv"];
    private static readonly string[] s_reencodeExtensions = [".wmv", ".avi", ".3gp", ".gif"];
    private static readonly string[] s_reencodeAudioExtensions = [".mov"];
    private static readonly string[] s_setdateExtensions =
    [
        ".heic",
        ".jpeg",
        ".jpg",
        ".mov",
        ".mp4",
        ".png",
        ".psd",
        ".tif",
        ".tiff",
    ];
    private static readonly string[] s_liveVideoExtensions = [".mp4", ".mov"];
    private static readonly string[] s_liveVideoImageExtensions = [".heic", ".jpg", ".jpeg"];
    private static readonly string[] s_quicktimeExtensions = [".mov", ".mp4"];
    private static readonly string[] s_jpegExtensions = [".jpg", ".jpeg"];
    private static readonly string[] s_pngExtensions = [".png"];
    private static readonly string[] s_heicExtensions = [".heic", ".heif"];
    private static readonly string[] s_tiffExtensions = [".tif", ".tiff"];
    private static readonly string[] s_dngExtensions = [".dng"];
    private static readonly string[] s_mp4Extensions = [".mp4"];
    private static readonly string[] s_mkvExtensions = [".mkv"];

    public static ProcessResult Execute(
        ConcurrentBag<string> fileNameBag,
        List<string> unknownExtensionsList,
        Lock unknownExtensionsListLock,
        FileInfo fileInfo,
        bool dryRun
    )
    {
        ProcessTask processTask = new(
            fileNameBag,
            unknownExtensionsList,
            unknownExtensionsListLock,
            fileInfo,
            dryRun
        );
        return processTask.ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<ProcessResult> ExecuteAsync()
    {
        if (!s_processExtensions.Contains(fileInfo.Extension.ToLower()))
        {
            unknownExtensionsListLock.Enter();
            if (!unknownExtensionsList.Contains(fileInfo.Extension.ToLower()))
            {
                Log.Warning("Skipping non-media file: '{FileName}'.", fileInfo.FullName);
                unknownExtensionsList.Add(fileInfo.Extension.ToLower());
            }
            unknownExtensionsListLock.Exit();
            return ProcessResult.UnknownExtension;
        }

        // Get exiftool info
        _exifToolJson = await GetExifToolJsonAsync();
        ArgumentNullException.ThrowIfNull(_exifToolJson);

        // Special handling for Foo.HEIC.JPG with HEIC MIME type
        // Need to manually correct double extensions
        if (!RenameDoubleExtensions() || !DetectDoubleExtensions())
        {
            return ProcessResult.DoubleExtensions;
        }

        // Process files
        if (
            !RenameMixedCaseExtensions()
            || !RenameMismatchedMimeExtensions()
            || !RenamePreferredExtensions()
            || !await DeleteLivePhotosAsync()
            || !await ConvertVideoAsync()
            || !await SetMissingCreateDateAsync()
        )
        {
            return _reprocess ? ProcessResult.Reprocess : ProcessResult.Failure;
        }
        return _modified ? ProcessResult.Modified : ProcessResult.Success;
    }

    private bool RenameDoubleExtensions()
    {
        // Special handling for Foo.HEIC.JPG with HEIC MIME type
        string outputFile = fileInfo.FullName;
        if (
            fileInfo.FullName.ToLower().EndsWith(".heic.jpg")
            && _exifToolJson!.MIMEType == "image/heic"
        )
        {
            Log.Warning(
                ".HEIC.JPG extension in HEIC format detected: '{FileName}'.",
                fileInfo.FullName
            );

            // Rename to .heic.jpg to .heic
            outputFile = fileInfo.FullName[..^4];
        }
        // Special handling for Foo.MOV.3GP with Quicktime MIME type
        else if (
            fileInfo.FullName.ToLower().EndsWith(".mov.3gp")
            && _exifToolJson!.MIMEType == "video/quicktime"
        )
        {
            Log.Warning(
                ".MOV.3GP extension in Quicktime format detected: '{FileName}'.",
                fileInfo.FullName
            );
            if (dryRun)
            {
                return false;
            }

            // Rename to .mov.3gp to .mov
            outputFile = fileInfo.FullName[..^4];
        }
        else
        {
            return true;
        }
        if (dryRun)
        {
            return false;
        }

        _modified = true;
        MoveFile(fileInfo.FullName, outputFile);

        // Queue renamed file for further processing
        return ReProcess(outputFile);
    }

    private bool DetectDoubleExtensions()
    {
        string[] parts = fileInfo.Name.ToLower().Split('.');
        string[] extensions = [.. s_processExtensions.Select(item => item.Trim('.').ToLower())];
        int extensionCount = parts.Count(part => extensions.Contains(part));
        if (extensionCount <= 1)
        {
            return true;
        }

        Log.Warning("Multiple extensions detected: '{FileName}'.", fileInfo.FullName);
        return false;
    }

    private bool RenameMixedCaseExtensions()
    {
        if (!(fileInfo.Extension.Any(char.IsLower) && fileInfo.Extension.Any(char.IsUpper)))
        {
            return true;
        }

        Log.Warning(
            "Mixed case extension detected '{Extension}': '{FileName}'.",
            fileInfo.Extension,
            fileInfo.FullName
        );
        if (dryRun)
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
        bool match = true;
        string extension = fileInfo.Extension.ToLower();
        string expectedExtension = extension;
        switch (_exifToolJson!.MIMEType)
        {
            case "image/jpeg":
                match = s_jpegExtensions.Contains(extension);
                expectedExtension = s_jpegExtensions[0];
                break;
            case "image/png":
                match = s_pngExtensions.Contains(extension);
                expectedExtension = s_pngExtensions[0];
                break;
            case "image/heic":
                match = s_heicExtensions.Contains(extension);
                expectedExtension = s_heicExtensions[0];
                break;
            case "image/tiff":
                match = s_tiffExtensions.Contains(extension);
                expectedExtension = s_tiffExtensions[0];
                break;
            case "image/x-adobe-dng":
                match = s_dngExtensions.Contains(extension);
                expectedExtension = s_dngExtensions[0];
                break;
            case "video/mp4":
                match = s_mp4Extensions.Contains(extension);
                expectedExtension = s_mp4Extensions[0];
                break;
            case "video/quicktime":
                match = s_quicktimeExtensions.Contains(extension);
                expectedExtension = s_quicktimeExtensions[0];
                break;
            case "video/x-matroska":
                match = s_mkvExtensions.Contains(extension);
                expectedExtension = s_mkvExtensions[0];
                break;
        }
        if (match)
        {
            return true;
        }
        Log.Warning(
            "MIME type '{MimeType}' does not match file extension '{Extension}': '{FileName}'.",
            _exifToolJson!.MIMEType,
            extension,
            fileInfo.FullName
        );
        if (dryRun)
        {
            return false;
        }

        // Rename extensions to match MIME type
        _modified = true;
        string outputFile = Path.ChangeExtension(fileInfo.FullName, expectedExtension);
        MoveFile(fileInfo.FullName, outputFile);

        // Queue renamed file for further processing
        return ReProcess(outputFile);
    }

    private bool RenamePreferredExtensions()
    {
        // .jpeg and .jpg
        string extension = fileInfo.Extension.ToLower();
        string preferredExtension = extension;
        if (s_jpegExtensions.Contains(extension) && extension != s_jpegExtensions[0])
        {
            // .jpg
            preferredExtension = s_jpegExtensions[0];
        }

        // .tiff and .tif
        if (s_tiffExtensions.Contains(extension) && extension != s_tiffExtensions[0])
        {
            // .tif
            preferredExtension = s_tiffExtensions[0];
        }

        // Good
        if (extension == preferredExtension)
        {
            return true;
        }

        // Extensions not preferred type
        Log.Warning(
            "Extension '{Extension}' does not match preferred extension '{PreferredExtension}': '{FileName}'.",
            fileInfo.Extension,
            preferredExtension,
            fileInfo.FullName
        );
        if (dryRun)
        {
            return false;
        }

        // Rename extensions to match preferred type
        _modified = true;
        string outputFile = Path.ChangeExtension(fileInfo.FullName, preferredExtension);
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
        string audioFormat = result.StandardOutput.Trim().ToLower();
        return audioFormat.StartsWith("pcm");
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
        if (!s_liveVideoExtensions.Contains(fileInfo.Extension.ToLower()))
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
            if (dryRun)
            {
                return false;
            }

            _modified = true;
            BackupFile(fileInfo.FullName);
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
        if (dryRun)
        {
            return false;
        }

        _modified = true;
        Log.Warning(
            "Deleting {Duration}s video clip with matching image file: '{FileName}'.",
            duration,
            fileInfo.FullName
        );
        BackupFile(fileInfo.FullName);
        return false;
    }

    private async Task<bool> ConvertVideoAsync()
    {
        // Convert to MP4 if needed
        string outputFile = Path.ChangeExtension(fileInfo.FullName, ".mp4");
        string[] ffmpegArguments;
        if (s_remuxExtensions.Contains(fileInfo.Extension.ToLower()))
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
                outputFile,
            ];
        }
        else if (s_reencodeExtensions.Contains(fileInfo.Extension.ToLower()))
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
                outputFile,
            ];
        }
        else if (s_reencodeAudioExtensions.Contains(fileInfo.Extension.ToLower()))
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
                outputFile,
            ];
        }
        else
        {
            // Nothing to do
            return true;
        }
        if (dryRun)
        {
            return false;
        }

        // Backup target file if it exists
        _modified = true;
        if (File.Exists(outputFile))
        {
            BackupFile(outputFile);
        }

        // Run ffmpeg
        _ = await Cli.Wrap("ffmpeg").WithArguments(ffmpegArguments).ExecuteBufferedAsync();

        // Backup original file
        BackupFile(fileInfo.FullName);

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
        if (!s_setdateExtensions.Contains(fileInfo.Extension.ToLower()))
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
        if (dryRun)
        {
            return false;
        }

        // Backup original file
        _modified = true;
        string backupFile = GetBackupFileName(fileInfo.FullName);
        Log.Information(
            "Creating backup '{OldFileName}' to '{NewFileName}' ...",
            fileInfo.FullName,
            backupFile
        );
        File.Copy(fileInfo.FullName, backupFile, false);

        // Set the created date using exiftool
        return await SetCreateDateAsync(fileInfo.FullName, createdDate)
            && ReProcess(fileInfo.FullName);
    }

    private static async Task<bool> SetCreateDateAsync(string outputFile, string createdDate)
    {
        // Set the created date using exiftool
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

    private bool ReProcess(string fileName)
    {
        // Queue file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", fileName);
        fileNameBag.Add(fileName);
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

    private static void BackupFile(string fileName)
    {
        string backupFileName = GetBackupFileName(fileName);
        Log.Information(
            "Renaming '{OldFileName}' to '{NewFileName}' ...",
            fileName,
            backupFileName
        );
        File.Move(fileName, backupFileName, false);
    }

    private static void MoveFile(string sourceFileName, string targetFileName)
    {
        // Backup target if it exists
        if (File.Exists(targetFileName))
        {
            BackupFile(targetFileName);
        }

        Log.Information(
            "Renaming '{OldFileName}' to '{NewFileName}' ...",
            sourceFileName,
            targetFileName
        );
        File.Move(sourceFileName, targetFileName, false);
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
}
