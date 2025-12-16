using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Exceptions;
using Serilog;

namespace PhotoCleaner;

public class ProcessTask(
    ConcurrentBag<string> fileNameBag,
    ConcurrentBag<string> unknownExtensionBag,
    FileInfo fileInfo
)
{
    private readonly ConcurrentBag<string> _fileNameBag = fileNameBag;
    private readonly ConcurrentBag<string> _unknownExtensionBag = unknownExtensionBag;
    private FileInfo _fileInfo = fileInfo;

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

    // File types may require remuxing or reencoding
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

    public async Task<bool> Execute()
    {
        if (!_processExtensions.Contains(_fileInfo.Extension.ToLower()))
        {
            if (!_unknownExtensionBag.Contains(_fileInfo.Extension.ToLower()))
            {
                Log.Warning("Skipping non-media file: '{FileName}'.", _fileInfo.FullName);
                _unknownExtensionBag.Add(_fileInfo.Extension.ToLower());
            }
            return false;
        }

        // Get exiftool info
        BufferedCommandResult result = await Cli.Wrap("exiftool")
            .WithArguments(["-groupNames", "-json", _fileInfo.FullName])
            .ExecuteBufferedAsync();
        string json = result.StandardOutput.Trim(' ', '\n', '\r', ' ', '[', ']');
        _exifToolJson = JsonSerializer.Deserialize(
            json,
            SourceGenerationContext.Default.ExifToolJson
        );
        Debug.Assert(_exifToolJson != null, "ExifToolJson should not be null here.");

        // Process files in order of validation to modification to verification
        if (
            !await DetectDoubleExtensions()
            || !await DetectMixedCaseExtensions()
            || !await DetectMismatchedMimeExtension()
            || !await DeleteLivePhotos()
            || !await ConvertVideo()
            || !await SetMissingCreateDate()
            || !await DetectPcmAudio()
            || !await DetectMissingCreateDate()
        )
        {
            return false;
        }
        return true;
    }

    private async Task<bool> DetectDoubleExtensions()
    {
        string[] parts = _fileInfo.Name.ToLower().Split('.');
        string[] extensions = _processExtensions.Select(item => item.Trim('.').ToLower()).ToArray();
        int extensionCount = 0;
        foreach (string part in parts)
        {
            if (extensions.Contains(part))
            {
                extensionCount++;
            }
        }
        if (extensionCount > 1)
        {
            Log.Warning("Multiple extensions detected: '{FileName}'.", _fileInfo.FullName);
            return false;
        }

        return true;
    }

    private async Task<bool> DetectMixedCaseExtensions()
    {
        if (
            _fileInfo.Extension != _fileInfo.Extension.ToLower()
            && _fileInfo.Extension != _fileInfo.Extension.ToUpper()
        )
        {
            Log.Warning(
                "Mixed case extension detected '{Extension}': '{FileName}'.",
                _fileInfo.Extension,
                _fileInfo.FullName
            );
            return false;
        }

        return true;
    }

    private async Task<bool> DetectMismatchedMimeExtension()
    {
        bool match = true;
        string extension = _fileInfo.Extension.ToLower();
        string expectedExtension = extension;
        switch (_exifToolJson!.MIMEType)
        {
            case "image/jpeg":
                string[] jpegExtensions = [".jpg", ".jpeg"];
                match = jpegExtensions.Contains(extension);
                expectedExtension = jpegExtensions[0];
                break;
            case "image/png":
                string[] pngExtensions = [".png"];
                match = pngExtensions.Contains(extension);
                expectedExtension = pngExtensions[0];
                break;
            case "image/heic":
                string[] heicExtensions = [".heic", ".heif"];
                match = heicExtensions.Contains(extension);
                expectedExtension = heicExtensions[0];
                break;
            case "image/tiff":
                string[] tiffExtensions = [".tif", ".tiff"];
                match = tiffExtensions.Contains(extension);
                expectedExtension = tiffExtensions[0];
                break;
            case "image/x-adobe-dng":
                string[] dngExtensions = [".dng"];
                match = dngExtensions.Contains(extension);
                expectedExtension = dngExtensions[0];
                break;
            case "video/mp4":
                string[] mp4Extensions = [".mp4"];
                match = mp4Extensions.Contains(extension);
                expectedExtension = mp4Extensions[0];
                break;
            case "video/quicktime":
                string[] movExtensions = [".mov"];
                match = movExtensions.Contains(extension);
                expectedExtension = movExtensions[0];
                break;
            case "video/x-matroska":
                string[] mkvExtensions = [".mkv"];
                match = mkvExtensions.Contains(extension);
                expectedExtension = mkvExtensions[0];
                break;
        }
        if (!match)
        {
            // Rename extensions to match MIME type
            Log.Warning(
                "MIME type '{MimeType}' does not match file extension '{Extension}' : '{FileName}'.",
                _exifToolJson!.MIMEType,
                extension,
                _fileInfo.FullName
            );
            string outputFile = Path.ChangeExtension(_fileInfo.FullName, expectedExtension);
            Log.Information(
                "Renaming '{OldFileName}' to '{NewFileName}' ...",
                _fileInfo.FullName,
                outputFile
            );
            File.Move(_fileInfo.FullName, outputFile, false);

            // Queue renamed file for further processing
            Log.Information("Queuing '{FileName}' for further processing.", outputFile);
            _fileNameBag.Add(outputFile);
            return false;
        }

        return true;
    }

    private async Task<bool> DetectMissingCreateDate()
    {
        if (!_exifToolJson!.IsDateSet())
        {
            Log.Warning("Missing created date: '{FileName}'.", _fileInfo.FullName);
            return false;
        }

        return true;
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
                _fileInfo.FullName,
            ])
            .ExecuteBufferedAsync();
        string audioFormat = result.StandardOutput.Trim().ToLower();
        return audioFormat.StartsWith("pcm");
    }

    private async Task<bool> DetectPcmAudio()
    {
        string[] audioExtensions = [".mov", ".mp4"];
        if (!audioExtensions.Contains(_fileInfo.Extension.ToLower()))
        {
            return true;
        }

        if (await IsAudioPcm())
        {
            Log.Warning("PCM audio detected: '{FileName}'.", _fileInfo.FullName);
            return false;
        }

        return true;
    }

    private async Task<bool> DeleteLivePhotos()
    {
        string[] liveExtensions = [".mp4", ".mov"];
        if (!liveExtensions.Contains(_fileInfo.Extension.ToLower()))
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
                _fileInfo.FullName,
            ])
            .ExecuteBufferedAsync();
        float duration = float.Parse(result.StandardOutput.Trim());

        // Very short videos
        if (duration <= 0.5)
        {
            Log.Information(
                "{Duration}s video clip detected: '{FileName}'.",
                duration,
                _fileInfo.FullName
            );
            string backupFile = GetBackupFileName(_fileInfo.FullName);
            Log.Information(
                "Renaming '{OldFileName}' to '{NewFileName}' ...",
                _fileInfo.FullName,
                backupFile
            );
            File.Move(_fileInfo.FullName, backupFile, false);
            return false;
        }

        // Live photos, short videos with matching HEIC or JPEG file
        if (duration <= 3.0)
        {
            string[] videoImageExtensions = [".heic", ".jpg", ".jpeg"];
            foreach (string extension in videoImageExtensions)
            {
                if (
                    File.Exists(Path.ChangeExtension(_fileInfo.FullName, extension.ToLower()))
                    || File.Exists(Path.ChangeExtension(_fileInfo.FullName, extension.ToUpper()))
                )
                {
                    Log.Information(
                        "{Duration}s video clip detected with matching image file: '{FileName}'.",
                        duration,
                        _fileInfo.FullName
                    );
                    string backupFile = GetBackupFileName(_fileInfo.FullName);
                    Log.Information(
                        "Renaming '{OldFileName}' to '{NewFileName}' ...",
                        _fileInfo.FullName,
                        backupFile
                    );
                    File.Move(_fileInfo.FullName, backupFile, false);
                    return false;
                }
            }
        }

        return true;
    }

    private async Task<bool> ConvertVideo()
    {
        // Convert to MP4 if needed
        string outputFile = Path.ChangeExtension(_fileInfo.FullName, ".mp4");
        string[] ffmpegArguments;
        if (_remuxExtensions.Contains(_fileInfo.Extension.ToLower()))
        {
            // Remux audio and video
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                _fileInfo.FullName,
                "-c",
                "copy",
                "-movflags",
                "+faststart",
                outputFile,
            ];
        }
        else if (_reencodeExtensions.Contains(_fileInfo.Extension.ToLower()))
        {
            // Reencode audio and video
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                _fileInfo.FullName,
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
        else if (_reencodeAudioExtensions.Contains(_fileInfo.Extension.ToLower()))
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
                _fileInfo.FullName,
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

        // Destination file must not already exist
        Log.Information(
            "Converting '{OldFileName}' to '{NewFileName}' ...",
            _fileInfo.FullName,
            outputFile
        );
        if (File.Exists(outputFile))
        {
            Log.Warning("Target file already exists: '{FileName}'.", outputFile);
            return false;
        }

        // Run ffmpeg
        BufferedCommandResult result = await Cli.Wrap("ffmpeg")
            .WithArguments(ffmpegArguments)
            .ExecuteBufferedAsync();

        // Backup original file
        string backupFile = GetBackupFileName(_fileInfo.FullName);
        Log.Information(
            "Renaming '{OldFileName}' to '{NewFileName}' ...",
            _fileInfo.FullName,
            backupFile
        );
        File.Move(_fileInfo.FullName, backupFile, false);

        // Set timestamps on remuxed file from original timestamps
        string? createdDate = _exifToolJson!.GetDateString();
        if (!string.IsNullOrEmpty(createdDate))
        {
            Console.WriteLine(
                $"INFORMATION: Setting timestamps on '{outputFile}' to '{createdDate}' ..."
            );
            result = await Cli.Wrap("exiftool")
                .WithArguments([
                    "-v2",
                    "-overwrite_original",
                    $"-QuickTime:CreateDate={createdDate}",
                    $"-QuickTime:ModifyDate={createdDate}",
                    outputFile,
                ])
                .ExecuteBufferedAsync();
        }

        // Queue remuxed file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", outputFile);
        _fileNameBag.Add(outputFile);
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
        if (!_setdateExtensions.Contains(_fileInfo.Extension.ToLower()))
        {
            return true;
        }

        // Try to infer the date from the path
        string createdDate = "";
        if (!DateFromPath.InferCreatedDate(_fileInfo.FullName, ref createdDate))
        {
            return true;
        }

        // Backup original file
        string backupFile = GetBackupFileName(_fileInfo.FullName);
        Log.Information(
            "Creating backup '{OldFileName}' to '{NewFileName}' ...",
            _fileInfo.FullName,
            backupFile
        );
        File.Copy(_fileInfo.FullName, backupFile, false);

        // Set the created date using exiftool
        Log.Information(
            "Setting created date to '{CreatedDate}': '{FileName}'.",
            createdDate,
            _fileInfo.FullName
        );
        string[] arguments;
        if (_fileInfo.Extension.ToLower() == ".mp4")
        {
            arguments =
            [
                "-v2",
                "-overwrite_original",
                $"-QuickTime:CreateDate={createdDate}",
                $"-QuickTime:ModifyDate={createdDate}",
                _fileInfo.FullName,
            ];
        }
        else
        {
            arguments =
            [
                "-v2",
                "-overwrite_original",
                $"-EXIF:CreateDate={createdDate}",
                $"-EXIF:DateTimeOriginal={createdDate}",
                _fileInfo.FullName,
            ];
        }
        BufferedCommandResult result = await Cli.Wrap("exiftool")
            .WithArguments(arguments)
            .ExecuteBufferedAsync();

        // Queue file for further processing
        Log.Information("Queuing '{FileName}' for further processing.", _fileInfo.FullName);
        _fileNameBag.Add(_fileInfo.FullName);
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
