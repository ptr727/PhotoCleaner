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

    internal const double LiveVideoDuration = 4.0;
    internal const double ShortVideoDuration = 1.0;

    private static readonly FrozenSet<string> s_quicktimeExtensions = new[]
    {
        ".mp4",
        ".mov",
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

    internal static async Task<ExifToolJson?> GetExifToolJsonAsync(
        string filePath,
        CancellationToken cancellationToken = default
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
            .WithArguments(["-groupNames", "-json", "-validate", "-all", filePath])
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

    internal static async Task SetCreateDateAsync(
        string createdDate,
        string outputFile,
        CancellationToken cancellationToken = default
    )
    {
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
        _ = await Cli.Wrap("exiftool")
            .WithArguments(arguments)
            .ExecuteBufferedAsync(cancellationToken);
    }
}
