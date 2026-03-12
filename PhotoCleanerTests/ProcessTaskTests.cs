using System.Collections.Concurrent;
using PhotoCleaner;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PhotoCleanerTests;

public sealed class ProcessTaskTests(TempDirectoryFixture fixture)
    : IClassFixture<TempDirectoryFixture>
{
    private sealed class InMemorySink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

    // ── Extension / MIME handling ────────────────────────────────────────────

    [Fact]
    public void Execute_UnknownExtension_ReturnsUnknownExtension()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "file.xyz");
            File.WriteAllBytes(filePath, []);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.UnknownExtension);
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_MixedCaseExtension_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "photo.Jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(Path.Combine(workDir, "photo.jpg")).Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_MismatchedMimeExtension_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // JPEG bytes saved with .png extension — MIME mismatch
            string filePath = Path.Combine(workDir, "photo.png");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(Path.Combine(workDir, "photo.jpg")).Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_MultipleExtensions_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // JPEG bytes with compound extension .heic.jpg — GetFileMediaExtension strips it
            string filePath = Path.Combine(workDir, "photo.heic.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(Path.Combine(workDir, "photo.jpg")).Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_NonPreferredExtension_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // JPEG bytes with .jpeg extension — .jpg is the preferred extension
            string filePath = Path.Combine(workDir, "photo.jpeg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(Path.Combine(workDir, "photo.jpg")).Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // ── Live photos / short videos ───────────────────────────────────────────

    [Fact]
    public void Execute_ShortVideo_DeletesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.ShortVideoFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.ShortVideoFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Failure);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(filePath).Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_LivePhotoVideo_DeletesVideoWithMatchingImage()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "livephoto.mp4");
            string imagePath = Path.Combine(workDir, "livephoto.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), imagePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(videoPath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Failure);
            File.Exists(videoPath + ".bak").Should().BeTrue();
            File.Exists(videoPath).Should().BeFalse();
            File.Exists(imagePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_LongVideoWithMatchingImage_KeepsVideo()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "longvideo.mp4");
            string imagePath = Path.Combine(workDir, "longvideo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LongVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), imagePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(videoPath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_LiveDurationVideoNoMatchingImage_KeepsVideo()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "solo.mp4");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(videoPath));

            // Assert — no matching image, so video is kept
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // ── Video conversion — remux ─────────────────────────────────────────────

    [Fact]
    public void Execute_MtsFile_RemuxesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.MtsFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.MtsFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_M2tsFile_RenamesExtensionToMts()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.M2tsFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.M2tsFile), filePath);

            // Act — first pass: .m2ts is not the preferred extension for video/mpeg (.mts is)
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert — file is renamed to .mts and queued for reprocess (remux happens on second pass)
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath).Should().BeFalse();
            File.Exists(Path.ChangeExtension(filePath, ".mts")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_MkvFile_RemuxesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.MkvFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.MkvFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // ── Video conversion — reencode ──────────────────────────────────────────

    [Fact]
    public void Execute_AviFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.AviFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.AviFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_WmvFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.WmvFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.WmvFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_3gpFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.ThreeGpFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.ThreeGpFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_GifFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.GifFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.GifFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // ── Video conversion — PCM audio ─────────────────────────────────────────

    [Fact]
    public void Execute_MovWithPcmAudio_ReencodesAudio()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.PcmMovFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.PcmMovFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert — first pass: .mov is not the preferred extension for video/quicktime (.mp4 is),
            // so file is renamed to .mp4 and queued for reprocess; PCM reencode happens on second pass
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath).Should().BeFalse();
            File.Exists(filePath + ".bak").Should().BeFalse();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_MovWithAacAudio_NoConversion()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.AacMovFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.AacMovFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert — first pass: .mov renamed to .mp4 (preferred extension); no audio reencode triggered
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath).Should().BeFalse();
            File.Exists(Path.ChangeExtension(filePath, ".mp4")).Should().BeTrue();
            File.Exists(filePath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_Mp4WithPcmAudio_ReencodesAudio()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.PcmMp4File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.PcmMp4File), filePath);

            // Act — second pass: .mp4 with PCM audio triggers audio reencode
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert — original backed up, re-encoded output has same path
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
            File.Exists(filePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_Mp4WithAacAudio_ReturnsSuccess()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.AacMp4File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.AacMp4File), filePath);

            // Act — second pass: .mp4 with AAC audio requires no conversion
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(filePath).Should().BeTrue();
            File.Exists(filePath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // ── DNG version warning ───────────────────────────────────────────────────

    [Fact]
    public void Execute_DngV1_4_NoWarning()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        InMemorySink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Sink(sink)
                .CreateLogger();

            string filePath = Path.Combine(workDir, TempDirectoryFixture.DngV14File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.DngV14File), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Success);
            sink.Events.Where(e => e.Level == LogEventLevel.Warning)
                .Should()
                .NotContain(e =>
                    e.RenderMessage().Contains("DNG version", StringComparison.Ordinal)
                );
        }
        finally
        {
            Log.Logger = savedLogger;
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_DngV1_5_LogsWarning()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        InMemorySink sink = new();
        ILogger savedLogger = Log.Logger;
        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Sink(sink)
                .CreateLogger();

            string filePath = Path.Combine(workDir, TempDirectoryFixture.DngV15File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.DngV15File), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.Success);
            sink.Events.Should()
                .Contain(e =>
                    e.Level == LogEventLevel.Warning
                    && e.RenderMessage().Contains("DNG version", StringComparison.Ordinal)
                    && e.RenderMessage()
                        .Contains(TempDirectoryFixture.DngVersion15, StringComparison.Ordinal)
                );
        }
        finally
        {
            Log.Logger = savedLogger;
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // ── Date inference ───────────────────────────────────────────────────────

    [Fact]
    public void Execute_JpegMissingDateInPath_SetsDate()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // Create dated directory structure that DateFromPath can extract
            string datedDir = Path.Combine(workDir, "2024", "01", "15");
            Directory.CreateDirectory(datedDir);
            string filePath = Path.Combine(datedDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert — date was inferred from path, file was modified and queued for reprocess
            result.Should().Be(ProcessTask.ProcessResult.Reprocess);
            File.Exists(filePath + ".bak").Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public void Execute_JpegMissingDateNoPath_ReturnsSuccess()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = ProcessTask.Execute(CreateContext(filePath));

            // Assert — no date in path, warning logged but no modification
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(filePath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcessTask.Context CreateContext(string filePath, bool dryRun = false) =>
        new()
        {
            FileInfo = new FileInfo(filePath),
            DryRun = dryRun,
            ReProcessNames = [],
            UnknownExtensions = new ConcurrentDictionary<string, byte>(),
        };
}
