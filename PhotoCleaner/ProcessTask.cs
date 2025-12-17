using System.Collections.Concurrent;
using System.Diagnostics;
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
    private ExifToolJson? _exifToolJson;

    // File types to process
    private readonly string[] _processExtensions =
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
        ".rw2",
        ".tif",
        ".tiff",
        ".wmv",
    ];
    private readonly string[] _remuxExtensions = [".mts", ".m2ts", ".mkv"];
    private readonly string[] _reencodeExtensions = [".wmv", ".avi", ".3gp", ".gif"];
    private readonly string[] _reencodeAudioExtensions = [".mov"];
    private readonly string[] _setdateExtensions =
    [
        ".jpg",
        ".jpeg",
        ".mp4",
        ".png",
        ".tif",
        ".tiff",
        ".heic",
    ];
    private readonly string[] _liveVideoExtensions = [".mp4", ".mov"];
    private readonly string[] _liveVideoImageExtensions = [".heic", ".jpg", ".jpeg"];
    private readonly string[] _pcmAudioVideoExtensions = [".mov", ".mp4"];
    private readonly string[] _jpegExtensions = [".jpg", ".jpeg"];
    private readonly string[] _pngExtensions = [".png"];
    private readonly string[] _heicExtensions = [".heic", ".heif"];
    private readonly string[] _tiffExtensions = [".tif", ".tiff"];
    private readonly string[] _dngExtensions = [".dng"];
    private readonly string[] _mp4Extensions = [".mp4"];
    private readonly string[] _movExtensions = [".mov"];
    private readonly string[] _mkvExtensions = [".mkv"];

    public static bool Execute(
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

    private async Task<bool> ExecuteAsync()
    {
        if (!_processExtensions.Contains(fileInfo.Extension.ToLower()))
        {
            unknownExtensionsListLock.Enter();
            if (!unknownExtensionsList.Contains(fileInfo.Extension.ToLower()))
            {
                Log.Warning("Skipping non-media file: '{FileName}'.", fileInfo.FullName);
                unknownExtensionsList.Add(fileInfo.Extension.ToLower());
            }
            unknownExtensionsListLock.Exit();
            return false;
        }

        // Get exiftool info
        BufferedCommandResult result = await Cli.Wrap("exiftool")
            .WithArguments(["-groupNames", "-json", fileInfo.FullName])
            .ExecuteBufferedAsync();
        string json = result.StandardOutput.Trim(' ', '\n', '\r', ' ', '[', ']');
        _exifToolJson = JsonSerializer.Deserialize(
            json,
            SourceGenerationContext.Default.ExifToolJson
        );
        ArgumentNullException.ThrowIfNull(_exifToolJson);

        // Process files
        return DetectDoubleExtensions() // Need to manually correct double extensions
            && RenameMixedCaseExtensions()
            && RenameMismatchedMimeExtensions()
            && RenamePreferredExtensions()
            && await DeleteLivePhotos()
            && await ConvertVideo()
            && await SetMissingCreateDate()
            && await DetectPcmAudio() // Should already be fixed
            && DetectMissingCreateDate(); // Could not determine a date from the path
    }

    private bool DetectDoubleExtensions()
    {
        string[] parts = fileInfo.Name.ToLower().Split('.');
        string[] extensions = _processExtensions.Select(item => item.Trim('.').ToLower()).ToArray();
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
        string outputFile = Path.ChangeExtension(fileInfo.FullName, fileInfo.Extension.ToLower());
        Log.Information(
            "Renaming '{OldFileName}' to '{NewFileName}' ...",
            fileInfo.FullName,
            outputFile
        );
        File.Move(fileInfo.FullName, outputFile, false);

        // Queue renamed file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", outputFile);
        fileNameBag.Add(outputFile);
        return false;
    }

    private bool RenameMismatchedMimeExtensions()
    {
        bool match = true;
        string extension = fileInfo.Extension.ToLower();
        string expectedExtension = extension;
        switch (_exifToolJson!.MIMEType)
        {
            case "image/jpeg":
                match = _jpegExtensions.Contains(extension);
                expectedExtension = _jpegExtensions[0];
                break;
            case "image/png":
                match = _pngExtensions.Contains(extension);
                expectedExtension = _pngExtensions[0];
                break;
            case "image/heic":
                match = _heicExtensions.Contains(extension);
                expectedExtension = _heicExtensions[0];
                break;
            case "image/tiff":
                match = _tiffExtensions.Contains(extension);
                expectedExtension = _tiffExtensions[0];
                break;
            case "image/x-adobe-dng":

                match = _dngExtensions.Contains(extension);
                expectedExtension = _dngExtensions[0];
                break;
            case "video/mp4":

                match = _mp4Extensions.Contains(extension);
                expectedExtension = _mp4Extensions[0];
                break;
            case "video/quicktime":

                match = _movExtensions.Contains(extension);
                expectedExtension = _movExtensions[0];
                break;
            case "video/x-matroska":

                match = _mkvExtensions.Contains(extension);
                expectedExtension = _mkvExtensions[0];
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
        string outputFile = Path.ChangeExtension(fileInfo.FullName, expectedExtension);
        Log.Information(
            "Renaming '{OldFileName}' to '{NewFileName}' ...",
            fileInfo.FullName,
            outputFile
        );
        File.Move(fileInfo.FullName, outputFile, false);

        // Queue renamed file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", outputFile);
        fileNameBag.Add(outputFile);
        return false;
    }

    private bool RenamePreferredExtensions()
    {
        // .jpeg and .jpg
        string extension = fileInfo.Extension.ToLower();
        string preferredExtension = extension;
        if (_jpegExtensions.Contains(extension) && extension != _jpegExtensions[0])
        {
            // .jpg
            preferredExtension = _jpegExtensions[0];
        }

        // .tiff and .tif
        if (_tiffExtensions.Contains(extension) && extension != _tiffExtensions[0])
        {
            // .tif
            preferredExtension = _tiffExtensions[0];
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

        // Target file must not already exist
        string outputFile = Path.ChangeExtension(fileInfo.FullName, preferredExtension);
        if (File.Exists(outputFile))
        {
            Log.Error("Target file already exists: '{FileName}'.", outputFile);
            return false;
        }

        // Rename extensions to match preferred type
        Log.Information(
            "Renaming '{OldFileName}' to '{NewFileName}' ...",
            fileInfo.FullName,
            outputFile
        );
        File.Move(fileInfo.FullName, outputFile, false);

        // Queue renamed file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", outputFile);
        fileNameBag.Add(outputFile);
        return false;
    }

    private bool DetectMissingCreateDate()
    {
        if (_exifToolJson!.IsDateSet())
        {
            return true;
        }

        Log.Warning("Missing created date: '{FileName}'.", fileInfo.FullName);
        return false;
    }

    private async Task<bool> IsAudioPcm()
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

    private async Task<bool> DetectPcmAudio()
    {
        if (!_pcmAudioVideoExtensions.Contains(fileInfo.Extension.ToLower()))
        {
            return true;
        }

        if (!await IsAudioPcm())
        {
            return true;
        }

        Log.Warning("PCM audio detected: '{FileName}'.", fileInfo.FullName);
        return false;
    }

    private async Task<bool> DeleteLivePhotos()
    {
        if (!_liveVideoExtensions.Contains(fileInfo.Extension.ToLower()))
        {
            return true;
        }

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
        float duration = float.Parse(result.StandardOutput.Trim());

        // Long videos
        if (duration > 3.0)
        {
            return true;
        }

        // Very short videos, always delete
        string backupFile = GetBackupFileName(fileInfo.FullName);
        if (duration <= 0.5)
        {
            Log.Information(
                "{Duration}s video clip detected: '{FileName}'.",
                duration,
                fileInfo.FullName
            );
            if (dryRun)
            {
                return false;
            }

            Log.Information(
                "Renaming '{OldFileName}' to '{NewFileName}' ...",
                fileInfo.FullName,
                backupFile
            );
            File.Move(fileInfo.FullName, backupFile, false);
            return false;
        }

        // Live photos, <=3s short videos with matching HEIC or JPEG file
        if (
            !_liveVideoImageExtensions.Any(extension =>
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

        Log.Information(
            "{Duration}s video clip detected with matching image file: '{FileName}'.",
            duration,
            fileInfo.FullName
        );
        Log.Information(
            "Renaming '{OldFileName}' to '{NewFileName}' ...",
            fileInfo.FullName,
            backupFile
        );
        File.Move(fileInfo.FullName, backupFile, false);
        return false;
    }

    private async Task<bool> ConvertVideo()
    {
        // Convert to MP4 if needed
        string outputFile = Path.ChangeExtension(fileInfo.FullName, ".mp4");
        string[] ffmpegArguments;
        if (_remuxExtensions.Contains(fileInfo.Extension.ToLower()))
        {
            // Remux audio and video
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
        else if (_reencodeExtensions.Contains(fileInfo.Extension.ToLower()))
        {
            // Reencode audio and video
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
        else if (_reencodeAudioExtensions.Contains(fileInfo.Extension.ToLower()))
        {
            // Only if audio is PCM
            if (!await IsAudioPcm())
            {
                return true;
            }

            // Reencode audio and remux video
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

        // Target file must not already exist
        Log.Information(
            "Converting '{OldFileName}' to '{NewFileName}' ...",
            fileInfo.FullName,
            outputFile
        );
        if (File.Exists(outputFile))
        {
            Log.Error("Target file already exists: '{FileName}'.", outputFile);
            return false;
        }
        if (dryRun)
        {
            return false;
        }

        // Run ffmpeg
        _ = await Cli.Wrap("ffmpeg").WithArguments(ffmpegArguments).ExecuteAsync();

        // Backup original file
        string backupFile = GetBackupFileName(fileInfo.FullName);
        Log.Information(
            "Renaming '{OldFileName}' to '{NewFileName}' ...",
            fileInfo.FullName,
            backupFile
        );
        File.Move(fileInfo.FullName, backupFile, false);

        // Set timestamps on remuxed file from original timestamps
        string? createdDate = _exifToolJson!.GetDateString();
        if (!string.IsNullOrEmpty(createdDate))
        {
            Console.WriteLine(
                $"INFORMATION: Setting timestamps on '{outputFile}' to '{createdDate}' ..."
            );
            _ = await Cli.Wrap("exiftool")
                .WithArguments([
                    // "-v2",
                    "-overwrite_original",
                    $"-QuickTime:CreateDate={createdDate}",
                    $"-QuickTime:ModifyDate={createdDate}",
                    outputFile,
                ])
                .ExecuteAsync();
        }

        // Queue remuxed file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", outputFile);
        fileNameBag.Add(outputFile);
        return false;
    }

    private async Task<bool> SetMissingCreateDate()
    {
        // Already have a date
        if (_exifToolJson!.IsDateSet())
        {
            return true;
        }

        // Only some file types are supported
        if (!_setdateExtensions.Contains(fileInfo.Extension.ToLower()))
        {
            return true;
        }

        // Try to infer the date from the path
        string createdDate = "";
        if (!DateFromPath.InferCreatedDate(fileInfo.FullName, ref createdDate))
        {
            return true;
        }
        Log.Information(
            "Setting created date to '{CreatedDate}': '{FileName}'.",
            createdDate,
            fileInfo.FullName
        );
        if (dryRun)
        {
            return false;
        }

        // Backup original file
        string backupFile = GetBackupFileName(fileInfo.FullName);
        Log.Information(
            "Creating backup '{OldFileName}' to '{NewFileName}' ...",
            fileInfo.FullName,
            backupFile
        );
        File.Copy(fileInfo.FullName, backupFile, false);

        // Set the created date using exiftool
        string[] arguments = fileInfo.Extension.Equals(
            ".mp4",
            StringComparison.CurrentCultureIgnoreCase
        )
            ?
            [
                // "-v2",
                "-overwrite_original",
                $"-QuickTime:CreateDate={createdDate}",
                $"-QuickTime:ModifyDate={createdDate}",
                fileInfo.FullName,
            ]
            :
            [
                // "-v2",
                "-overwrite_original",
                $"-EXIF:CreateDate={createdDate}",
                $"-EXIF:DateTimeOriginal={createdDate}",
                fileInfo.FullName,
            ];
        _ = await Cli.Wrap("exiftool").WithArguments(arguments).ExecuteAsync();

        // Queue file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", fileInfo.FullName);
        fileNameBag.Add(fileInfo.FullName);
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
}
