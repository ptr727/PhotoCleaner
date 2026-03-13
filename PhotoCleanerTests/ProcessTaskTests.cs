using System.Collections.Concurrent;
using CliWrap;
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
    public async Task ExecuteAsync_UnknownExtension_ReturnsUnknownExtension()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "file.xyz");
            File.WriteAllBytes(filePath, []);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

            // Assert
            result.Should().Be(ProcessTask.ProcessResult.UnknownExtension);
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_MixedCaseExtension_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "photo.Jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_MismatchedMimeExtension_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // JPEG bytes saved with .png extension — MIME mismatch
            string filePath = Path.Combine(workDir, "photo.png");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_MultipleExtensions_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // JPEG bytes with compound extension .heic.jpg — GetFileMediaExtension strips it
            string filePath = Path.Combine(workDir, "photo.heic.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_NonPreferredExtension_RenamesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            // JPEG bytes with .jpeg extension — .jpg is the preferred extension
            string filePath = Path.Combine(workDir, "photo.jpeg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_ShortVideo_DeletesFile()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.ShortVideoFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.ShortVideoFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_LivePhotoVideo_DeletesVideoWithMatchingContentIdentifier()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "livephoto.mp4");
            string imagePath = Path.Combine(workDir, "livephoto.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);
            // Use a copy of the video file as companion (ISOBMFF container supports ContentIdentifier)
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(videoPath, "test-uuid-livephoto");
            await SetContentIdentifierAsync(imagePath, "test-uuid-livephoto");

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(videoPath)
            );

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
    public async Task ExecuteAsync_LivePhotoHevcSuffix_DeletesVideoWithMatchingContentIdentifier()
    {
        // Arrange — new iPhone naming: IMG_1234_HEVC.mov pairs with IMG_1234.heic
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "img_1234_hevc.mp4");
            string imagePath = Path.Combine(workDir, "img_1234.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(videoPath, "test-uuid-hevc");
            await SetContentIdentifierAsync(imagePath, "test-uuid-hevc");

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(videoPath)
            );

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
    public async Task ExecuteAsync_LivePhotoContentIdentifierMismatch_KeepsVideo()
    {
        // Arrange — same name, different ContentIdentifier: not a live photo pair
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "photo.mp4");
            string imagePath = Path.Combine(workDir, "photo.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(videoPath, "uuid-video-AAA");
            await SetContentIdentifierAsync(imagePath, "uuid-image-BBB");

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(videoPath)
            );

            // Assert — mismatch means not a live pair, video kept
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
            File.Exists(videoPath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LivePhotoNoContentIdentifier_KeepsVideo()
    {
        // Arrange — companion image found by name but no ContentIdentifier on either file
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "photo.mp4");
            string imagePath = Path.Combine(workDir, "photo.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), imagePath);

            // Act — no ContentIdentifier set on either file
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(videoPath)
            );

            // Assert — cannot confirm pair without ContentIdentifier, video kept
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
            File.Exists(videoPath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LongVideoWithMatchingImage_KeepsVideo()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "longvideo.mp4");
            string imagePath = Path.Combine(workDir, "longvideo.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LongVideoFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(videoPath, "test-uuid-long");
            await SetContentIdentifierAsync(imagePath, "test-uuid-long");

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(videoPath)
            );

            // Assert — long video is always kept regardless of ContentIdentifier match
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(videoPath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LiveDurationVideoNoMatchingImage_KeepsVideo()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "solo.mp4");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), videoPath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(videoPath)
            );

            // Assert — no matching image candidate, so video is kept
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
    public async Task ExecuteAsync_MtsFile_RemuxesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.MtsFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.MtsFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_M2tsFile_RenamesExtensionToMts()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.M2tsFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.M2tsFile), filePath);

            // Act — first pass: .m2ts is not the preferred extension for video/mpeg (.mts is)
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_MkvFile_RemuxesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.MkvFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.MkvFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_AviFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.AviFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.AviFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_WmvFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.WmvFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.WmvFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_3gpFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.ThreeGpFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.ThreeGpFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_GifFile_ReencodesToMp4()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.GifFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.GifFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_MovWithPcmAudio_ReencodesAudio()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.PcmMovFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.PcmMovFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_MovWithAacAudio_NoConversion()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.AacMovFile);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.AacMovFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_Mp4WithPcmAudio_ReencodesAudio()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.PcmMp4File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.PcmMp4File), filePath);

            // Act — second pass: .mp4 with PCM audio triggers audio reencode
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_Mp4WithAacAudio_ReturnsSuccess()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, TempDirectoryFixture.AacMp4File);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.AacMp4File), filePath);

            // Act — second pass: .mp4 with AAC audio requires no conversion
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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

    [Fact]
    public async Task ExecuteAsync_PcmMovWithContentIdentifier_PreservesContentIdentifierAfterConversion()
    {
        // Arrange — live photo MOV with PCM audio: first pass re-encodes audio, second pass deletes
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string videoPath = Path.Combine(workDir, "livephoto.mov");
            string imagePath = Path.Combine(workDir, "livephoto.heic");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.PcmMovFile), videoPath);
            File.Copy(fixture.SourceFile(TempDirectoryFixture.LiveVideoFile), imagePath);
            await SetContentIdentifierAsync(videoPath, "test-uuid-pcm-preserved");
            await SetContentIdentifierAsync(imagePath, "test-uuid-pcm-preserved");

            // Act — first pass: re-encodes PCM audio to AAC, queues .mp4 for reprocess
            ProcessTask.ProcessResult firstResult = await ProcessTask.ExecuteAsync(
                CreateContext(videoPath)
            );

            // Assert — first pass queues for reprocess
            firstResult.Should().Be(ProcessTask.ProcessResult.Reprocess);
            string mp4Path = Path.ChangeExtension(videoPath, ".mp4");
            File.Exists(mp4Path).Should().BeTrue();

            // Act — second pass: live photo detection on the converted .mp4
            ProcessTask.ProcessResult secondResult = await ProcessTask.ExecuteAsync(
                CreateContext(mp4Path)
            );

            // Assert — ContentIdentifier was preserved through conversion, video is deleted
            secondResult.Should().Be(ProcessTask.ProcessResult.Failure);
            File.Exists(mp4Path + ".bak").Should().BeTrue();
            File.Exists(mp4Path).Should().BeFalse();
            File.Exists(imagePath).Should().BeTrue();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // ── DNG version warning ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DngV1_4_NoWarning()
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
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_DngV1_5_LogsWarning()
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
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

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
    public async Task ExecuteAsync_JpegMissingDateInPath_SetsDate()
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
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath, dateFromPath: true)
            );

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
    public async Task ExecuteAsync_JpegMissingDateNoPath_ReturnsSuccess()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string filePath = Path.Combine(workDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

            // Assert — no date in path, warning logged but no modification
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(filePath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_JpegMissingDateInPath_DateFromPathDisabled_ReturnsSuccess()
    {
        // Arrange
        string workDir = TempDirectoryFixture.CreateWorkDir();
        try
        {
            string datedDir = Path.Combine(workDir, "2024", "01", "15");
            Directory.CreateDirectory(datedDir);
            string filePath = Path.Combine(datedDir, "photo.jpg");
            File.Copy(fixture.SourceFile(TempDirectoryFixture.SmallJpegFile), filePath);

            // Act — dateFromPath defaults to false
            ProcessTask.ProcessResult result = await ProcessTask.ExecuteAsync(
                CreateContext(filePath)
            );

            // Assert — date inference skipped, file unmodified
            result.Should().Be(ProcessTask.ProcessResult.Success);
            File.Exists(filePath + ".bak").Should().BeFalse();
        }
        finally
        {
            TempDirectoryFixture.DeleteWorkDir(workDir);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task SetContentIdentifierAsync(string filePath, string contentId) =>
        // exiftool may exit with code 1 for warnings; suppress validation
        await Cli.Wrap("exiftool")
            .WithArguments([
                $"-Keys:ContentIdentifier={contentId}",
                "-overwrite_original",
                filePath,
            ])
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();

    private static ProcessTask.Context CreateContext(
        string filePath,
        bool dryRun = false,
        bool dateFromPath = false
    ) =>
        new()
        {
            FileInfo = new FileInfo(filePath),
            DryRun = dryRun,
            DateFromPath = dateFromPath,
            ReProcessNames = [],
            UnknownExtensions = new ConcurrentDictionary<string, byte>(),
        };
}
