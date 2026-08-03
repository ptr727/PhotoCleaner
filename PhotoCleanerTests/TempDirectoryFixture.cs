using CliWrap;
using PhotoCleaner;

namespace PhotoCleanerTests;

public sealed class TempDirectoryFixture : IAsyncLifetime
{
    // Test duration constants - expressed as offsets from ProcessTask thresholds
    internal const double TooShortDuration = MediaUtilities.ShortVideoDuration - 0.5;
    internal const double LiveDuration = MediaUtilities.LiveVideoDuration - 1.0;
    internal const double LongDuration = MediaUtilities.LiveVideoDuration + 6.0;

    // DNG version strings
    internal const string DngVersion14 = "1.4.0.0";
    internal const string DngVersion15 = "1.5.0.0";

    // Generated source file names
    internal const string SmallJpegFile = "1x1.jpg";
    internal const string SmallPngFile = "1x1.png";
    internal const string DngV14File = "dng_v1_4.dng";
    internal const string DngV15File = "dng_v1_5.dng";
    internal const string ShortVideoFile = "short.mp4";
    internal const string LiveVideoFile = "live.mp4";
    internal const string LongVideoFile = "long.mp4";
    internal const string MtsFile = "remux.mts";
    internal const string M2tsFile = "remux.m2ts";
    internal const string MkvFile = "remux.mkv";
    internal const string AviFile = "reencode.avi";
    internal const string WmvFile = "reencode.wmv";
    internal const string ThreeGpFile = "reencode.3gp";
    internal const string GifFile = "reencode.gif";
    internal const string PcmMovFile = "pcm.mov";
    internal const string AacMovFile = "aac.mov";
    internal const string PcmMp4File = "pcm.mp4";
    internal const string AacMp4File = "aac.mp4";

    public string SourceDir { get; private set; } = string.Empty;
    public bool IsAvailable { get; private set; } = true;
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        SourceDir = Path.Combine(
            Path.GetTempPath(),
            $"PhotoCleanerTests_{Path.GetRandomFileName()}"
        );
        Directory.CreateDirectory(SourceDir);
        try
        {
            await GenerateImagesAsync();
            await GenerateDngFilesAsync();
            await GenerateVideoFilesAsync();
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception
                || (ex.InnerException is System.ComponentModel.Win32Exception)
            )
        {
            IsAvailable = false;
            SkipReason =
                "Required external tools (ffmpeg/exiftool) are not installed or not in PATH.";
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(SourceDir))
        {
            Directory.Delete(SourceDir, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    internal string SourceFile(string fileName) => Path.Combine(SourceDir, fileName);

    internal static string CreateWorkDir()
    {
        string workDir = Path.Combine(
            Path.GetTempPath(),
            $"PhotoCleanerTest_{Path.GetRandomFileName()}"
        );
        Directory.CreateDirectory(workDir);
        return workDir;
    }

    internal static void DeleteWorkDir(string workDir)
    {
        if (Directory.Exists(workDir))
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // Probed at runtime because Linux is case-sensitive while Windows NTFS and macOS HFS+ are not.
    internal static bool IsFileSystemCaseSensitive(string directory)
    {
        string upper = Path.Combine(directory, "_CASE_PROBE_");
        File.WriteAllBytes(upper, []);
        try
        {
            return !File.Exists(Path.Combine(directory, "_case_probe_"));
        }
        finally
        {
            File.Delete(upper);
        }
    }

    private async Task GenerateImagesAsync()
    {
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=red:s=1x1",
            "-frames:v",
            "1",
            SourceFile(SmallJpegFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=blue:s=1x1",
            "-frames:v",
            "1",
            SourceFile(SmallPngFile),
        ]);
    }

    private async Task GenerateDngFilesAsync()
    {
        // Create a TIFF, then set DNGVersion so exiftool identifies it as image/x-adobe-dng
        string tifPath = Path.Combine(SourceDir, "tmp.tif");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "color=c=red:s=1x1", "-frames:v", "1", tifPath]);

        string dng14Path = SourceFile(DngV14File);
        File.Copy(tifPath, dng14Path);
        await RunExiftoolAsync(["-DNGVersion=1 4 0 0", "-overwrite_original", dng14Path]);

        string dng15Path = SourceFile(DngV15File);
        File.Copy(tifPath, dng15Path);
        await RunExiftoolAsync(["-DNGVersion=1 5 0 0", "-overwrite_original", dng15Path]);

        File.Delete(tifPath);
    }

    private async Task GenerateVideoFilesAsync()
    {
        string shortD = TooShortDuration.ToString(CultureInfo.InvariantCulture);
        string liveD = LiveDuration.ToString(CultureInfo.InvariantCulture);
        string longD = LongDuration.ToString(CultureInfo.InvariantCulture);

        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-t",
            shortD,
            "-c:v",
            "libx264",
            "-an",
            SourceFile(ShortVideoFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-t",
            liveD,
            "-c:v",
            "libx264",
            "-an",
            SourceFile(LiveVideoFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-t",
            longD,
            "-c:v",
            "libx264",
            "-an",
            SourceFile(LongVideoFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-t",
            liveD,
            "-c:v",
            "mpeg2video",
            "-b:v",
            "1M",
            "-an",
            "-f",
            "mpegts",
            SourceFile(MtsFile),
        ]);
        File.Copy(SourceFile(MtsFile), SourceFile(M2tsFile));
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-t",
            liveD,
            "-c:v",
            "libx264",
            "-an",
            "-f",
            "matroska",
            SourceFile(MkvFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-t",
            liveD,
            "-c:v",
            "libx264",
            "-an",
            "-f",
            "avi",
            SourceFile(AviFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-t",
            liveD,
            "-c:v",
            "wmv2",
            "-an",
            "-f",
            "asf",
            SourceFile(WmvFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-t",
            liveD,
            "-c:v",
            "libx264",
            "-an",
            "-f",
            "3gp",
            SourceFile(ThreeGpFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            "color=c=red:s=64x64:r=5",
            "-t",
            "2",
            SourceFile(GifFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            $"sine=frequency=440:duration={liveD}",
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-map",
            "0:a",
            "-map",
            "1:v",
            "-t",
            liveD,
            "-c:v",
            "libx264",
            "-c:a",
            "pcm_s16le",
            "-f",
            "mov",
            SourceFile(PcmMovFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            $"sine=frequency=440:duration={liveD}",
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-map",
            "0:a",
            "-map",
            "1:v",
            "-t",
            liveD,
            "-c:v",
            "libx264",
            "-c:a",
            "aac",
            "-f",
            "mov",
            SourceFile(AacMovFile),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            $"sine=frequency=440:duration={liveD}",
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-map",
            "0:a",
            "-map",
            "1:v",
            "-t",
            liveD,
            "-c:v",
            "libx264",
            "-c:a",
            "pcm_s16le",
            "-f",
            "mp4",
            SourceFile(PcmMp4File),
        ]);
        await RunFfmpegAsync([
            "-f",
            "lavfi",
            "-i",
            $"sine=frequency=440:duration={liveD}",
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x240:r=25",
            "-map",
            "0:a",
            "-map",
            "1:v",
            "-t",
            liveD,
            "-c:v",
            "libx264",
            "-c:a",
            "aac",
            "-f",
            "mp4",
            SourceFile(AacMp4File),
        ]);
    }

    private static async Task RunFfmpegAsync(string[] arguments) =>
        await Cli.Wrap("ffmpeg").WithArguments(["-nostdin", "-y", .. arguments]).ExecuteAsync();

    private static async Task RunExiftoolAsync(string[] arguments) =>
        // Warnings such as writing DNG tags to TIFF make exiftool exit 1.
        // The write still succeeds, so exit code validation is suppressed.
        await Cli.Wrap("exiftool")
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();
}
