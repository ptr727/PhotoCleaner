using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Exceptions;

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
                Console.WriteLine($"WARNING: Skipping non-media file: '{_fileInfo.FullName}'.");
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
        string[] parts = _fileInfo!.Name.ToLower().Split('.');
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
            Console.WriteLine($"WARNING: Multiple extensions detected: '{_fileInfo!.FullName}'.");
            return false;
        }

        return true;
    }

    private async Task<bool> DetectMixedCaseExtensions()
    {
        if (
            _fileInfo!.Extension != _fileInfo!.Extension.ToLower()
            && _fileInfo!.Extension != _fileInfo!.Extension.ToUpper()
        )
        {
            Console.WriteLine(
                $"WARNING: Mixed case extension detected '{_fileInfo!.Extension}': '{_fileInfo.FullName}'."
            );
            return false;
        }

        return true;
    }

    private async Task<bool> DetectMismatchedMimeExtension()
    {
        bool match = true;
        string extension = _fileInfo!.Extension.ToLower();
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
            Console.WriteLine(
                $"WARNING: MIME type '{_exifToolJson!.MIMEType}' does not match file extension '{extension}' : '{_fileInfo!.FullName}'."
            );
            string outputFile = Path.ChangeExtension(_fileInfo!.FullName, expectedExtension);
            Console.WriteLine(
                $"INFORMATION: Renaming '{_fileInfo!.FullName}' to '{outputFile}' ..."
            );
            File.Move(_fileInfo!.FullName, outputFile, false);

            // Queue renamed file for further processing
            Console.WriteLine($"INFORMATION: Queuing '{outputFile}' for further processing.");
            _fileNameBag.Add(outputFile);
            return false;
        }

        return true;
    }

    private async Task<bool> DetectMissingCreateDate()
    {
        if (!_exifToolJson!.IsDateSet())
        {
            Console.WriteLine($"WARNING: Missing created date: '{_fileInfo!.FullName}'.");
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
                _fileInfo!.FullName,
            ])
            .ExecuteBufferedAsync();
        string audioFormat = result.StandardOutput.Trim().ToLower();
        return audioFormat.StartsWith("pcm");
    }

    private async Task<bool> DetectPcmAudio()
    {
        string[] audioExtensions = [".mov", ".mp4"];
        if (!audioExtensions.Contains(_fileInfo!.Extension.ToLower()))
        {
            return true;
        }

        if (await IsAudioPcm())
        {
            Console.WriteLine($"WARNING: PCM audio detected: '{_fileInfo!.FullName}'.");
            return false;
        }

        return true;
    }

    private async Task<bool> DeleteLivePhotos()
    {
        string[] liveExtensions = [".mp4", ".mov"];
        if (!liveExtensions.Contains(_fileInfo!.Extension.ToLower()))
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
                _fileInfo!.FullName,
            ])
            .ExecuteBufferedAsync();
        float duration = float.Parse(result.StandardOutput.Trim());

        // Very short videos
        if (duration <= 0.5)
        {
            Console.WriteLine(
                $"INFORMATION: {duration}s video clip detected: '{_fileInfo!.FullName}'."
            );
            string backupFile = GetBackupFileName(_fileInfo!.FullName);
            Console.WriteLine(
                $"INFORMATION: Renaming '{_fileInfo!.FullName}' to '{backupFile}' ..."
            );
            File.Move(_fileInfo!.FullName, backupFile, false);
            return false;
        }

        // Short videos with matching HEIC file
        if (duration <= 3.0)
        {
            string heicFilePathLower = Path.ChangeExtension(_fileInfo.FullName, ".heic");
            string heicFilePathUpper = Path.ChangeExtension(_fileInfo.FullName, ".HEIC");
            if (File.Exists(heicFilePathLower) || File.Exists(heicFilePathUpper))
            {
                Console.WriteLine(
                    $"INFORMATION: {duration}s video clip detected with matching HEIC file: '{_fileInfo!.FullName}'."
                );
                string backupFile = GetBackupFileName(_fileInfo!.FullName);
                Console.WriteLine(
                    $"INFORMATION: Renaming '{_fileInfo!.FullName}' to '{backupFile}' ..."
                );
                File.Move(_fileInfo!.FullName, backupFile, false);
                return false;
            }
        }

        return true;
    }

    private async Task<bool> ConvertVideo()
    {
        // Convert to MP4 if needed
        string outputFile = Path.ChangeExtension(_fileInfo!.FullName, ".mp4");
        string[] ffmpegArguments;
        if (_remuxExtensions.Contains(_fileInfo!.Extension.ToLower()))
        {
            // Remux audio and video
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                _fileInfo!.FullName,
                "-c",
                "copy",
                "-movflags",
                "+faststart",
                outputFile,
            ];
        }
        else if (_reencodeExtensions.Contains(_fileInfo!.Extension.ToLower()))
        {
            // Reencode audio and video
            ffmpegArguments =
            [
                "-nostdin",
                "-y",
                "-i",
                _fileInfo!.FullName,
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
        else if (_reencodeAudioExtensions.Contains(_fileInfo!.Extension.ToLower()))
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
                _fileInfo!.FullName,
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
        Console.WriteLine($"INFORMATION: Converting '{_fileInfo!.FullName}' to '{outputFile}' ...");
        if (File.Exists(outputFile))
        {
            Console.WriteLine($"WARNING: Target file already exists: '{outputFile}'.");
            return false;
        }

        // Run ffmpeg
        BufferedCommandResult result = await Cli.Wrap("ffmpeg")
            .WithArguments(ffmpegArguments)
            .ExecuteBufferedAsync();

        // Backup original file
        string backupFile = GetBackupFileName(_fileInfo!.FullName);
        Console.WriteLine($"INFORMATION: Renaming '{_fileInfo!.FullName}' to '{backupFile}' ...");
        File.Move(_fileInfo!.FullName, backupFile, false);

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
        Console.WriteLine($"INFORMATION: Queuing '{outputFile}' for further processing.");
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
        if (!_setdateExtensions.Contains(_fileInfo!.Extension.ToLower()))
        {
            return true;
        }

        // Try to infer the date from the path
        string createdDate = "";
        if (!InferCreatedDate(ref createdDate))
        {
            return true;
        }

        // Backup original file
        string backupFile = GetBackupFileName(_fileInfo!.FullName);
        Console.WriteLine(
            $"INFORMATION: Creating backup '{_fileInfo!.FullName}' to '{backupFile}' ..."
        );
        File.Copy(_fileInfo!.FullName, backupFile, false);

        // Set the created date using exiftool
        Console.WriteLine(
            $"INFORMATION: Setting created date to '{createdDate}': '{_fileInfo!.FullName}'."
        );
        string[] arguments;
        if (_fileInfo!.Extension.ToLower() == ".mp4")
        {
            arguments =
            [
                "-v2",
                "-overwrite_original",
                $"-QuickTime:CreateDate={createdDate}",
                $"-QuickTime:ModifyDate={createdDate}",
                _fileInfo!.FullName,
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
                _fileInfo!.FullName,
            ];
        }
        BufferedCommandResult result = await Cli.Wrap("exiftool")
            .WithArguments(arguments)
            .ExecuteBufferedAsync();

        // Queue file for further processing
        Console.WriteLine($"INFORMATION: Queuing '{_fileInfo!.FullName}' for further processing.");
        _fileNameBag.Add(_fileInfo!.FullName);
        return false;
    }

    private bool InferCreatedDate(ref string createdDate)
    {
        // Try to extract date from filename
        DateTime? dateFromPath = ExtractDateFromFilename(_fileInfo!.Name);
        if (IsDateValid(dateFromPath))
        {
            createdDate = dateFromPath!.Value.ToString("yyyy:MM:dd HH:mm:ss");
            return true;
        }

        // Try to extract date from directory path
        dateFromPath = ExtractDateFromPath(_fileInfo!.FullName);
        if (IsDateValid(dateFromPath))
        {
            createdDate = dateFromPath!.Value.ToString("yyyy:MM:dd HH:mm:ss");
            return true;
        }

        return false;
    }

    private static bool IsDateValid(DateTime? date)
    {
        return date != null
            && date.HasValue
            && date.Value.Year >= 1900
            && date.Value.Year <= DateTime.Now.Year;
    }

    private static DateTime? ExtractDateFromFilename(string fileName)
    {
        // Pattern 1: YYYYMMDD_HHMMSS format (e.g., 20210502_200152957_iOS-1747.jpg)
        var pattern1 = new Regex(@"(\d{8})_(\d{6,9})");
        var match1 = pattern1.Match(fileName);
        if (match1.Success)
        {
            if (
                DateTime.TryParseExact(
                    match1.Groups[1].Value,
                    "yyyyMMdd",
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime date1
                )
            )
            {
                string timeStr = match1.Groups[2].Value.PadRight(6, '0').Substring(0, 6); // Take first 6 digits for HHMMSS
                if (
                    DateTime.TryParseExact(
                        timeStr,
                        "HHmmss",
                        null,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime time1
                    )
                )
                {
                    return date1.Date.Add(time1.TimeOfDay);
                }
                return date1;
            }
        }

        // Pattern 2: YYYYMMDD format without time (e.g., EX_20030219_3378.jpg, PV_20090709_0081.mp4)
        var pattern2 = new Regex(@"(\d{8})");
        var match2 = pattern2.Match(fileName);
        if (match2.Success)
        {
            if (
                DateTime.TryParseExact(
                    match2.Groups[1].Value,
                    "yyyyMMdd",
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime date2
                )
            )
            {
                return date2;
            }
        }

        // Pattern 3: YYYY-MM-DD format (e.g., PHOTO-2024-06-22-07-56-41, WhatsApp Image 2024-06-30)
        var pattern3 = new Regex(@"(\d{4})-(\d{2})-(\d{2})");
        var match3 = pattern3.Match(fileName);
        if (match3.Success)
        {
            if (
                DateTime.TryParse(
                    $"{match3.Groups[1].Value}-{match3.Groups[2].Value}-{match3.Groups[3].Value}",
                    out DateTime date3
                )
            )
            {
                // Try to extract time if present (HH-MM-SS format)
                var timePattern = new Regex(
                    $@"{Regex.Escape(match3.Value)}-(\d{{2}})-(\d{{2}})-(\d{{2}})"
                );
                var timeMatch = timePattern.Match(fileName);
                if (timeMatch.Success)
                {
                    if (
                        int.TryParse(timeMatch.Groups[1].Value, out int hours)
                        && int.TryParse(timeMatch.Groups[2].Value, out int minutes)
                        && int.TryParse(timeMatch.Groups[3].Value, out int seconds)
                        && hours <= 23
                        && minutes <= 59
                        && seconds <= 59
                    )
                    {
                        return date3.Date.Add(new TimeSpan(hours, minutes, seconds));
                    }
                }
                return date3;
            }
        }

        // Pattern 4: YYYY MM DD format with spaces (e.g., EV 2014 07 03_0003.tif)
        var pattern4 = new Regex(@"(\d{4})\s+(\d{2})\s+(\d{2})");
        var match4 = pattern4.Match(fileName);
        if (match4.Success)
        {
            if (
                DateTime.TryParse(
                    $"{match4.Groups[1].Value}-{match4.Groups[2].Value}-{match4.Groups[3].Value}",
                    out DateTime date4
                )
            )
            {
                return date4;
            }
        }

        // /data/media/Pictures_Archive/Lumia/2015-11-18/garden-photo.jpg
        // /data/media/Pictures_Archive/DV7/MP Navigator EX/2014_07_14/scan 2014 07 03_0003.tif

        return null;
    }

    private static DateTime? ExtractDateFromPath(string fullPath)
    {
        // Extract date from directory structure (e.g., /2021/2021-05-02/)
        var pathPattern = new Regex(@"[/\\](\d{4})[/\\](\d{4})-(\d{2})-(\d{2})[/\\]");
        var pathMatch = pathPattern.Match(fullPath);
        if (pathMatch.Success)
        {
            string yearFromDir = pathMatch.Groups[1].Value;
            string fullDate =
                $"{pathMatch.Groups[2].Value}-{pathMatch.Groups[3].Value}-{pathMatch.Groups[4].Value}";

            if (DateTime.TryParse(fullDate, out DateTime pathDate))
            {
                return pathDate;
            }
        }

        // Extract YYYY-MM-DD format anywhere in the path (e.g., /Lumia/2015-11-18/)
        var dateAnywherePattern = new Regex(@"[/\\](\d{4})-(\d{2})-(\d{2})[/\\]");
        var dateAnywhereMatch = dateAnywherePattern.Match(fullPath);
        if (dateAnywhereMatch.Success)
        {
            string dateString =
                $"{dateAnywhereMatch.Groups[1].Value}-{dateAnywhereMatch.Groups[2].Value}-{dateAnywhereMatch.Groups[3].Value}";
            if (DateTime.TryParse(dateString, out DateTime dateAnywhere))
            {
                return dateAnywhere;
            }
        }

        // Extract YYYY_MM_DD format anywhere in the path (e.g., /MP Navigator EX/2010_01_21/)
        var dateUnderscorePattern = new Regex(@"[/\\](\d{4})_(\d{2})_(\d{2})[/\\]");
        var dateUnderscoreMatch = dateUnderscorePattern.Match(fullPath);
        if (dateUnderscoreMatch.Success)
        {
            string dateString =
                $"{dateUnderscoreMatch.Groups[1].Value}-{dateUnderscoreMatch.Groups[2].Value}-{dateUnderscoreMatch.Groups[3].Value}";
            if (DateTime.TryParse(dateString, out DateTime dateUnderscore))
            {
                return dateUnderscore;
            }
        }

        // Fallback: Try to extract just year from path
        var yearPattern = new Regex(@"[/\\](\d{4})[/\\]");
        var yearMatch = yearPattern.Match(fullPath);
        if (yearMatch.Success)
        {
            if (
                int.TryParse(yearMatch.Groups[1].Value, out int year)
                && year >= 1900
                && year <= DateTime.Now.Year
            )
            {
                return new DateTime(year, 1, 1);
            }
        }

        return null;
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
