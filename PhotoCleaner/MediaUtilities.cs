using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;

namespace PhotoCleaner;

internal static class MediaUtilities
{
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
    internal const string StillOutputExtension = ".png";

    internal const double LiveVideoDuration = 4.0;
    internal const double ShortVideoDuration = 1.0;

    // A QuickTime container takes no EXIF, and exiftool reports such a file unchanged with exit 0.
    private static readonly FrozenSet<string> s_quicktimeExtensions = new[]
    {
        ".3gp",
        ".mov",
        ".mp4",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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

    // ImageDataHash and the ImageHashType option arrived together in ExifTool 12.63 (2023-06-08).
    // Source: the ExifTool version history, which lists both under 12.63.
    internal const double MinimumImageHashExifToolVersion = 12.63;

    private static readonly Lazy<Task> s_imageHashSupport = new(CheckImageHashSupportAsync);

    // Fails with a clear message instead of cataloguing every file as having no image hash.
    internal static Task EnsureImageHashSupportAsync() => s_imageHashSupport.Value;

    private static async Task CheckImageHashSupportAsync()
    {
        BufferedCommandResult result = await Cli.Wrap("exiftool")
            .WithArguments(["-ver"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        if (
            !double.TryParse(
                result.StandardOutput.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double version
            )
        )
        {
            throw new NotSupportedException(
                $"Cannot determine the exiftool version from '{result.StandardOutput.Trim()}', version {MinimumImageHashExifToolVersion.ToString(CultureInfo.InvariantCulture)} or newer is required for ImageDataHash."
            );
        }

        if (version < MinimumImageHashExifToolVersion)
        {
            throw new NotSupportedException(
                $"exiftool {version.ToString(CultureInfo.InvariantCulture)} is too old, version {MinimumImageHashExifToolVersion.ToString(CultureInfo.InvariantCulture)} or newer is required for ImageDataHash."
            );
        }
    }

    // A catalog read adds the image data hash to the same exiftool run.
    // Other reads leave it out, because hashing the image data costs extra I/O.
    internal static async Task<ExifToolJson?> GetCatalogExifToolJsonAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        await EnsureImageHashSupportAsync().ConfigureAwait(false);
        return await ReadExifToolJsonAsync(filePath, true, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<ExifToolJson?> GetExifToolJsonAsync(
        string filePath,
        CancellationToken cancellationToken = default
    ) => ReadExifToolJsonAsync(filePath, false, cancellationToken);

    private static async Task<ExifToolJson?> ReadExifToolJsonAsync(
        string filePath,
        bool imageHash,
        CancellationToken cancellationToken
    )
    {
        Log.Debug("exiftool: Getting metadata for '{FilePath}'", filePath);

        // Separates a file this tool cannot open from one it reads but exiftool cannot parse.
        // Both come back from exiftool as an error alongside well formed JSON.
        // Without this they are indistinguishable, so a permission problem reads as damaged media.
        EnsureReadable(filePath);

        // -all is required because -validate alone narrows the output to just that one tag.
        // Measured at no cost over the plain call, so validation rides along instead of a second run.
        // A file whose verdict reports an error also makes exiftool exit non-zero, JSON and all.
        // The parsed output decides, not the exit code.
        BufferedCommandResult result = await Cli.Wrap("exiftool")
            .WithArguments(
                imageHash
                    ?
                    [
                        "-groupNames",
                        "-json",
                        "-validate",
                        "-all",
                        "-ImageDataHash",
                        "-api",
                        "ImageHashType=SHA256",
                        filePath,
                    ]
                    : ["-groupNames", "-json", "-validate", "-all", filePath]
            )
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        ReadOnlySpan<char> json = result.StandardOutput.AsSpan().Trim([' ', '\n', '\r', '[', ']']);
        if (json.IsEmpty)
        {
            throw new InvalidOperationException(
                $"exiftool returned no metadata for '{filePath}' (exit {result.ExitCode}): "
                    + result.StandardError.Trim()
            );
        }

        ExifToolJson? exifToolJson = JsonSerializer.Deserialize(
            json,
            ExifToolJsonContext.Default.ExifToolJson
        );
        ArgumentNullException.ThrowIfNull(exifToolJson);
        return exifToolJson;
    }

    // Reading attributes needs no read permission on the content.
    // Nothing about a file's metadata therefore proves its bytes can be reached.
    // A command that hands the file to another process sees its own lack of access nowhere else.
    internal static void EnsureReadable(string filePath)
    {
        using FileStream probe = File.OpenRead(filePath);
    }

    internal static Task<string?> TrySetCreateDateAsync(
        string createdDate,
        string outputFile,
        string exifTool,
        CancellationToken cancellationToken = default
    )
    {
        Log.Debug(
            "exiftool: Setting created date '{CreatedDate}' on '{DestinationPath}'",
            createdDate,
            outputFile
        );
        return WriteExifToolAsync(
            exifTool,
            CreateDateArguments(createdDate, outputFile),
            cancellationToken
        );
    }

    // Returns exiftool's exit code and output for a failed write, or null for a successful one.
    internal static async Task<string?> WriteExifToolAsync(
        string exifTool,
        string[] arguments,
        CancellationToken cancellationToken = default
    )
    {
        BufferedCommandResult result = await Cli.Wrap(exifTool)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        return IsWriteFailure(result.ExitCode, result.StandardOutput, result.StandardError)
            ? $"exit {result.ExitCode}: {(result.StandardError + result.StandardOutput).Trim()}"
            : null;
    }

    // Returns why a delete failed, or null once the file is gone, so each caller picks its log level.
    internal static Exception? TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex;
        }
    }

    // An exiftool write failure prints an "Error:" line, normally with a non-zero exit.
    internal static bool IsWriteFailure(
        int exitCode,
        string standardOutput,
        string standardError
    ) =>
        exitCode != 0
        || $"{standardError}\n{standardOutput}"
            .Split('\n')
            .Any(line => line.TrimStart().StartsWith("Error", StringComparison.Ordinal));

    private static string[] CreateDateArguments(string createdDate, string outputFile) =>
        s_quicktimeExtensions.Contains(Path.GetExtension(outputFile))
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
}
